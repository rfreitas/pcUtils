using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RefreshRateOverlay.NET.Tests;

/// <summary>
/// Renders WinForms controls/forms to PNG for visual regression testing.
/// Mirrors AggressiveScreensaver.NET.Tests/RenderHelper.cs.
/// </summary>
internal static class RenderHelper
{
    public static TheoryData<float, string> ScaleFactors { get; } = new()
    {
        { 1.0f, "100" },
        { 1.5f, "150" },
    };

    static RenderHelper()
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
        }
        catch { }
    }

    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (IntPtr)(-4);
    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private const int  OFF_SCREEN_X = -4096;
    private const int  OFF_SCREEN_Y = -4096;

    public static string CaptureForm(Func<Form> formFactory, string name, int settleMs = 400, float scaleFactor = 1.0f)
    {
        string? outPath = null;
        var thread = new Thread(() =>
        {
            SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            using Form form = formFactory();
            // Disable AutoScaleMode so the test's explicit scaleFactor is the only scaling applied.
            // AutoScaleMode.Dpi would otherwise scale by the system DPI, double-scaling form.Scale().
            form.AutoScaleMode = AutoScaleMode.None;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(OFF_SCREEN_X, OFF_SCREEN_Y);

            if (scaleFactor != 1.0f)
            {
                form.Scale(new SizeF(scaleFactor, scaleFactor));
                form.Font = new Font(form.Font.FontFamily, form.Font.Size * scaleFactor, form.Font.Style);
            }

            form.Show();
            Application.DoEvents();

            var timer = new System.Windows.Forms.Timer { Interval = Math.Max(50, settleMs) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Application.DoEvents();
                outPath = Snap(form, name);
                form.Close();
            };
            timer.Start();
            Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return outPath ?? throw new InvalidOperationException("Capture failed.");
    }

    public static string CaptureMenu(ContextMenuStrip menu, string name, int settleMs = 400, float scaleFactor = 1.0f)
    {
        string? outPath = null;
        var thread = new Thread(() =>
        {
            SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

            if (scaleFactor != 1.0f)
            {
                menu.Scale(new SizeF(scaleFactor, scaleFactor));
                menu.Font = new Font(menu.Font.FontFamily, menu.Font.Size * scaleFactor, menu.Font.Style);
            }

            using var host = new Form
            {
                Width = 1, Height = 1,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(OFF_SCREEN_X, OFF_SCREEN_Y),
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.None,
            };

            host.Shown  += (_, _) => menu.Show(host, new Point(0, 0));
            menu.Opened += (_, _) =>
            {
                var t = new System.Windows.Forms.Timer { Interval = settleMs };
                t.Tick += (_, _) =>
                {
                    t.Stop();
                    outPath = SnapControl(menu, name);
                    menu.Close();
                    host.Close();
                };
                t.Start();
            };
            Application.Run(host);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return outPath ?? throw new InvalidOperationException("Menu capture failed.");
    }

    // -------------------------------------------------------------------------

    private static string Snap(Control control, string name)
    {
        // PrintWindow writes physical pixels; size the bitmap to match.
        float dpiScale = control.DeviceDpi > 0 ? control.DeviceDpi / 96f : 1f;
        int physW = (int)Math.Ceiling(control.Width  * dpiScale);
        int physH = (int)Math.Ceiling(control.Height * dpiScale);
        var bmp = new Bitmap(physW, physH);
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            PrintWindow(control.Handle, hdc, PW_RENDERFULLCONTENT);
            g.ReleaseHdc(hdc);
        }
        return Save(bmp, name);
    }

    private static string SnapControl(ToolStrip strip, string name)
    {
        var bmp = new Bitmap(strip.Width, strip.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            PrintWindow(strip.Handle, hdc, PW_RENDERFULLCONTENT);
            g.ReleaseHdc(hdc);
        }
        return Save(bmp, name);
    }

    private static string Save(Bitmap bmp, string name)
    {
        string path = Path.Combine(
            Path.GetDirectoryName(typeof(RenderHelper).Assembly.Location)!,
            name + ".png");
        bmp.Save(path, ImageFormat.Png);
        bmp.Dispose();
        Console.WriteLine($"[RenderHelper] Saved: {path}");
        return path;
    }
}
