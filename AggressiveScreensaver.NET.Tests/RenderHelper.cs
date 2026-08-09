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
    // Central DPI scale list — edit here to affect ALL render tests.
    public static TheoryData<float, string> ScaleFactors { get; } = new()
    {
        { 1.0f, "100" },
        { 1.5f, "150" },
    };

    // Set DPI mode exactly once per process before any form is created.
    // PerMonitorV2 allows us to simulate different DPIs per-window via P/Invoke.
    static RenderHelper()
    {
        try 
        { 
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
        } catch { }
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
    /// Shows the form returned by <paramref name="formFactory"/> off-screen on an STA thread,
    /// waits for it to fully paint, captures it with PrintWindow, then closes it.
    /// Returns the absolute path of the saved PNG.
    /// </summary>
    public static string CaptureForm(Func<Form> formFactory, string fileNameWithoutExtension, int settleMs = 400, float scaleFactor = 1.0f)
    {
        string? outPath = null;

        var thread = new Thread(() =>
        {
            SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            
            using Form form = formFactory();
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(OFF_SCREEN_X, OFF_SCREEN_Y);

            // Simulate high DPI scaling manually if requested, since the thread DPI context
            // trick alone doesn't trigger WinForms auto-scaling for off-screen test forms.
            if (scaleFactor != 1.0f)
            {
                form.Scale(new SizeF(scaleFactor, scaleFactor));
                form.Font = new Font(form.Font.FontFamily, form.Font.Size * scaleFactor, form.Font.Style);
            }

            // Force a layout pass so things like AutoSize labels don't get cropped
            form.Show();
            Application.DoEvents();

            var timer = new System.Windows.Forms.Timer { Interval = Math.Max(50, settleMs) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Application.DoEvents(); // One last pump before snapping
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

            // Manual scale to simulate High DPI
            if (scaleFactor != 1.0f)
            {
                menu.Scale(new SizeF(scaleFactor, scaleFactor));
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

    /// <summary>
    /// Shows the WPF window returned by <paramref name="factory"/> off-screen on its
    /// own STA thread with a Dispatcher message loop, waits for it to settle, captures
    /// it with PrintWindow, then closes it. Sizing uses GetClientRect (physical pixels)
    /// rather than ActualWidth/Height (device-independent) — WPF's DIU-to-physical
    /// mapping isn't a simple multiply once thread DPI awareness is involved.
    /// </summary>
    public static string CaptureWpfWindow(Func<System.Windows.Window> factory, string name, int settleMs = 400, float scaleFactor = 1.0f)
    {
        string? outPath = null;

        var thread = new Thread(() =>
        {
            SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

            var window = factory();
            window.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            window.Left = OFF_SCREEN_X;
            window.Top  = OFF_SCREEN_Y;

            if (scaleFactor != 1.0f)
            {
                if (window.Content is System.Windows.FrameworkElement root)
                    root.LayoutTransform = new System.Windows.Media.ScaleTransform(scaleFactor, scaleFactor);
                if (!double.IsNaN(window.Width))  window.Width  *= scaleFactor;
                if (!double.IsNaN(window.Height)) window.Height *= scaleFactor;
            }

            window.Show();

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(50, settleMs)),
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                outPath = SnapHwnd(hwnd, name);
                window.Close();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            };
            timer.Start();

            System.Windows.Threading.Dispatcher.Run();
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return outPath ?? throw new InvalidOperationException("WPF capture did not produce a file.");
    }

    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    private static string SnapHwnd(IntPtr hwnd, string name)
    {
        GetClientRect(hwnd, out RECT rect);
        var bmp = new Bitmap(rect.Right - rect.Left, rect.Bottom - rect.Top);
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT);
            g.ReleaseHdc(hdc);
        }
        return Save(bmp, name);
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
