using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RefreshRateOverlay.WPF.Services;

/// <summary>
/// Registers a system-wide hotkey and invokes a callback on WM_HOTKEY.
/// The AHK version used Win+Shift+R, but that combo is already claimed by
/// something else on this machine (confirmed via a standalone RegisterHotKey
/// probe), so this uses Win+Alt+Shift+R instead.
/// </summary>
internal sealed class HotkeyService : NativeWindow, IDisposable
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId  = 1;

    private const uint MOD_ALT   = 0x0001;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_WIN   = 0x0008;
    private const uint VK_R      = 0x52;

    private readonly Action _onHotkey;
    private readonly bool   _registered;

    public HotkeyService(Action onHotkey)
    {
        _onHotkey = onHotkey;
        CreateHandle(new CreateParams { Style = 0 });

        _registered = RegisterHotKey(Handle, HotkeyId, MOD_WIN | MOD_ALT | MOD_SHIFT, VK_R);
        if (!_registered)
            Logger.Log("Failed to register Win+Alt+Shift+R hotkey (already in use by another app?).");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
            try { _onHotkey(); }
            catch (Exception ex) { Logger.LogException(ex); }
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_registered) UnregisterHotKey(Handle, HotkeyId);
        if (Handle != IntPtr.Zero) DestroyHandle();
    }
}
