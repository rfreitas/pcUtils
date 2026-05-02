using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AggressiveScreensaver.NET.Tests;

/// <summary>
/// Renders a WinForms control/form to a PNG without needing focus or screen visibility.
/// Uses PrintWindow(PW_RENDERFULLCONTENT) — same technique as SliderRenderTest.
/// </summary>
internal static class RenderHelper
{
    // Set DPI mode exactly once per process before any form is created.
    // PerMonitorV2 allows us to simulate different DPIs per-window via P/Invoke.
    static RenderHelper()
    {
        try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); } catch { }
    }

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (IntPtr)(-4);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    // Off-screen position so nothing flickers on screen
    private const int OFF_SCREEN_X = -4096;
    private const int OFF_SCREEN_Y = -4096;

    /// <summary>
    /// Shows <paramref name="form"/> off-screen on an STA thread, waits for it to
    /// fully paint, captures it with PrintWindow, then closes it.
    /// Returns the absolute path of the saved PNG.
    /// </summary>
    public static string CaptureForm(Form form, string fileNameWithoutExtension, int settleMs = 400)
    {
        string? outPath = null;

        var thread = new Thread(() =>
        {
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(OFF_SCREEN_X, OFF_SCREEN_Y);

            var timer = new System.Windows.Forms.Timer { Interval = settleMs };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                outPath = Snap(form, fileNameWithoutExtension);
                form.Close();
            };
            timer.Start();

            Application.Run(form);
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return outPath ?? throw new InvalidOperationException("Capture did not produce a file.");
    }

    /// <summary>
    /// Shows a ContextMenuStrip on a tiny invisible host form, waits for it to
    /// render, then captures the menu popup.
    /// <param name="scaleFactor">E.g. 1.0f for 96 DPI, 1.5f for 144 DPI (150%).</param>
    /// </summary>
    public static string CaptureMenu(ContextMenuStrip menu, string fileNameWithoutExtension, int settleMs = 400, float scaleFactor = 1.0f)
    {
        string? outPath = null;

        var thread = new Thread(() =>
        {
            // Force the thread into PerMonitorV2 mode so the window can accept custom DPI scaling
            SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

            // If a scale is requested, we scale the default 96-DPI font manually.
            // (Simulating a monitor DPI change for a borderless invisible window is notoriously
            // hard in WinForms test threads, so we simulate the *effect* of high DPI on the menu).
            if (scaleFactor != 1.0f)
            {
                menu.Font = new Font(menu.Font.FontFamily, menu.Font.Size * scaleFactor, menu.Font.Style);
            }

            // Tiny invisible host form to anchor the popup
            using var host = new Form
            {
                Width         = 1,
                Height        = 1,
                StartPosition = FormStartPosition.Manual,
                Location      = new Point(OFF_SCREEN_X, OFF_SCREEN_Y),
                ShowInTaskbar = false,
                FormBorderStyle = FormBorderStyle.None,
            };

            host.Shown += (_, _) =>
            {
                // Show the popup — it creates its own HWND
                menu.Show(host, new Point(0, 0));
            };

            menu.Opened += (_, _) =>
            {
                var timer = new System.Windows.Forms.Timer { Interval = settleMs };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    outPath = SnapControl(menu, fileNameWithoutExtension);
                    menu.Close();
                    host.Close();
                };
                timer.Start();
            };

            Application.Run(host);
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return outPath ?? throw new InvalidOperationException("Menu capture did not produce a file.");
    }

    // -------------------------------------------------------------------------
    // Internals
    // -------------------------------------------------------------------------

    private static string Snap(Control control, string name)
    {
        var bmp = new Bitmap(control.Width, control.Height);
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
        // ContextMenuStrip has its own HWND when shown; use its bounds
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
        string outPath = Path.Combine(
            Path.GetDirectoryName(typeof(RenderHelper).Assembly.Location)!,
            name + ".png");
        bmp.Save(outPath, ImageFormat.Png);
        bmp.Dispose();
        Console.WriteLine($"[RenderHelper] Saved: {outPath}");
        return outPath;
    }
}
