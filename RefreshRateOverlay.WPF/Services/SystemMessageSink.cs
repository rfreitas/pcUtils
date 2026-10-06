using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RefreshRateOverlay.WPF.Services;

/// <summary>
/// Hidden NativeWindow that receives WM_DISPLAYCHANGE and, for any power
/// settings registered via RegisterPowerSettings, WM_POWERBROADCAST /
/// PBT_POWERSETTINGCHANGE. Defers each callback via a one-shot Timer to avoid
/// synchronous GUI destruction inside WndProc.
/// </summary>
internal sealed class SystemMessageSink : NativeWindow, IDisposable
{
    private const int WM_DISPLAYCHANGE = 0x007E;
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_POWERSETTINGCHANGE = 0x8013;
    private const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0;

    private readonly Action _onDisplayChange;
    private readonly Action? _onPowerSettingChange;
    private readonly List<IntPtr> _powerRegistrations = new();

    // Several notifications usually arrive together (the active plan changing
    // also touches the settings inside it); one deferred callback covers them.
    private bool _powerCallbackPending;

    public SystemMessageSink(Action onDisplayChange, Action? onPowerSettingChange = null)
    {
        _onDisplayChange = onDisplayChange;
        _onPowerSettingChange = onPowerSettingChange;
        CreateHandle(new CreateParams { Style = 0 });
    }

    /// <summary>Asks Windows to post PBT_POWERSETTINGCHANGE here whenever one of
    /// these power settings changes. Failures are logged, not thrown: the
    /// caller's startup enforcement still ran, only live re-assertion is lost.</summary>
    public void RegisterPowerSettings(IEnumerable<Guid> settings)
    {
        foreach (Guid setting in settings)
        {
            Guid g = setting;
            IntPtr reg = RegisterPowerSettingNotification(Handle, ref g, DEVICE_NOTIFY_WINDOW_HANDLE);
            if (reg == IntPtr.Zero)
                Logger.Log($"RegisterPowerSettingNotification failed for {setting} (error {Marshal.GetLastWin32Error()}).");
            else
                _powerRegistrations.Add(reg);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_DISPLAYCHANGE)
        {
            Logger.Log("WM_DISPLAYCHANGE received. Deferring sync.");
            ScheduleDeferred(_onDisplayChange, 500);
        }
        else if (m.Msg == WM_POWERBROADCAST && (int)m.WParam == PBT_POWERSETTINGCHANGE
                 && _onPowerSettingChange is { } onPower && !_powerCallbackPending)
        {
            _powerCallbackPending = true;
            ScheduleDeferred(() => { _powerCallbackPending = false; onPower(); }, 500);
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
        foreach (IntPtr reg in _powerRegistrations) UnregisterPowerSettingNotification(reg);
        _powerRegistrations.Clear();
        if (Handle != IntPtr.Zero)
            DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, ref Guid powerSettingGuid, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
}
