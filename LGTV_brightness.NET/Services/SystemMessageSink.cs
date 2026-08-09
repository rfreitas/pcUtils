using System;
using System.Windows.Forms;

namespace LgtvBrightness.Services;

/// <summary>
/// Hidden NativeWindow that receives WM_DISPLAYCHANGE. Defers the callback via
/// a one-shot Timer, debounced to let a refresh-rate switch settle (matches the
/// AHK script's 2 s debounce before re-reading the rate).
/// </summary>
internal sealed class SystemMessageSink : NativeWindow, IDisposable
{
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int DebounceMs = 2000;

    private readonly Action _onDisplayChange;

    public SystemMessageSink(Action onDisplayChange)
    {
        _onDisplayChange = onDisplayChange;
        CreateHandle(new CreateParams { Style = 0 });
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DISPLAYCHANGE)
        {
            Logger.Log("WM_DISPLAYCHANGE received. Deferring sync.");
            ScheduleDeferred(_onDisplayChange, DebounceMs);
        }
        base.WndProc(ref m);
    }

    private static void ScheduleDeferred(Action action, int delayMs)
    {
        var t = new System.Windows.Forms.Timer { Interval = Math.Max(1, delayMs) };
        t.Tick += (_, _) =>
        {
            t.Stop(); t.Dispose();
            try { action(); }
            catch (Exception ex) { Logger.LogException(ex); }
        };
        t.Start();
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
            DestroyHandle();
    }
}
