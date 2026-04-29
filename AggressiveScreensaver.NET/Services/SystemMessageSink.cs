using System;
using System.Windows.Forms;
using AggressiveScreensaver.Services;

namespace AggressiveScreensaver.Services;

/// <summary>
/// Hidden NativeWindow that receives WM_DISPLAYCHANGE and WM_POWERBROADCAST.
/// All overlay cleanup is deferred via WinForms Timers to avoid synchronous
/// GUI destruction inside WndProc, which crashes CoreMessaging.dll (AV in ntdll).
///
/// Mirrors the AHK pattern:
///   OnMessage(0x007E, OnDisplayChange)  => SetTimer(-10)
///   OnMessage(0x0218, OnPowerMessage)   => SetTimer(-10), -500, -2000
/// </summary>
internal sealed class SystemMessageSink : NativeWindow, IDisposable
{
    private const int WM_DISPLAYCHANGE   = 0x007E;
    private const int WM_POWERBROADCAST  = 0x0218;
    private const int PBT_APMRESUMESUSPEND   = 7;
    private const int PBT_APMRESUMEAUTOMATIC = 18;

    private readonly Action _onDisplayChange;
    private readonly Action _onWake;

    public SystemMessageSink(Action onDisplayChange, Action onWake)
    {
        _onDisplayChange = onDisplayChange;
        _onWake          = onWake;

        // Create an invisible message-only window
        CreateHandle(new CreateParams { Style = 0 });
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_DISPLAYCHANGE:
                Logger.Log("Display change detected. Deferring cleanup.");
                ScheduleDeferred(_onDisplayChange, 10);
                break;

            case WM_POWERBROADCAST when
                (int)m.WParam == PBT_APMRESUMESUSPEND ||
                (int)m.WParam == PBT_APMRESUMEAUTOMATIC:
                Logger.Log("System wake detected. Deferring cleanup.");
                ScheduleDeferred(_onWake, 10);
                ScheduleDeferred(_onWake, 500);
                ScheduleDeferred(_onWake, 2000);
                break;
        }

        base.WndProc(ref m);
    }

    /// <summary>
    /// Fires <paramref name="action"/> once after <paramref name="delayMs"/> ms
    /// using a self-disposing WinForms Timer (UI-thread-safe).
    /// </summary>
    private static void ScheduleDeferred(Action action, int delayMs)
    {
        var t = new System.Windows.Forms.Timer { Interval = Math.Max(1, delayMs) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            t.Dispose();
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
