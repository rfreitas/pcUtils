using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AggressiveScreensaver.Forms;

namespace AggressiveScreensaver.NET.Tests;

/// <summary>
/// Captures TimeoutSliderForm using PrintWindow(PW_RENDERFULLCONTENT) — renders
/// the window directly to a DC without needing focus or topmost, so the
/// Deactivate→Hide() handler can't interfere.
/// Run with: dotnet test --filter SliderRenderTest
/// Output: AggressiveScreensaver.NET.Tests/bin/.../slider_render.png
/// </summary>
public class SliderRenderTest
{
    // PrintWindow renders a window into a DC regardless of visibility / z-order
    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

    // PW_RENDERFULLCONTENT: render everything including native Win32 controls
    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    [Fact]
    public void RenderSlider_SavesToPng()
    {
        int[] steps = [15, 30, 60, 120, 180, 300, 600, 900, 1200, 1800];
        string? outPath = null;

        var thread = new Thread(() =>
        {
            using var form = new TimeoutSliderForm(30, steps, _ => { });
            form.ClientSize = new Size(60, 210);
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(50, 50);

            // Timer fires on the real message loop after controls have painted
            var timer = new System.Windows.Forms.Timer { Interval = 400 };
            timer.Tick += (_, _) =>
            {
                timer.Stop();

                // PrintWindow captures from the window DC directly — no DoEvents,
                // no focus changes, no Deactivate trigger
                var bmp = new Bitmap(form.Width, form.Height);
                using (var g = Graphics.FromImage(bmp))
                {
                    var hdc = g.GetHdc();
                    PrintWindow(form.Handle, hdc, PW_RENDERFULLCONTENT);
                    g.ReleaseHdc(hdc);
                }

                outPath = Path.Combine(
                    Path.GetDirectoryName(typeof(SliderRenderTest).Assembly.Location)!,
                    "slider_render.png");
                bmp.Save(outPath, ImageFormat.Png);
                bmp.Dispose();

                Console.WriteLine($"Slider render saved to: {outPath}");
                form.Close();
            };
            timer.Start();

            Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.True(File.Exists(outPath), $"Expected PNG at {outPath}");
    }
}
