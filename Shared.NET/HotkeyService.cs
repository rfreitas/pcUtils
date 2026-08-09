using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Input;

namespace Shared;

/// <summary>
/// Registers a system-wide hotkey and invokes a callback on WM_HOTKEY.
/// The combination is rebindable at runtime (see Rebind) so the user can
/// change it from a "record new shortcut" UI (see HotkeySettingsWindow).
/// </summary>
internal sealed class HotkeyService : NativeWindow, IDisposable
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId  = 1;

    public const uint MOD_ALT     = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT   = 0x0004;
    public const uint MOD_WIN     = 0x0008;

    private readonly Action _onHotkey;
    private readonly Action<string>? _log;

    public uint Modifiers   { get; private set; }
    public uint Vk           { get; private set; }
    public bool IsRegistered { get; private set; }

    public HotkeyService(Action onHotkey, uint modifiers, uint vk, Action<string>? log = null)
    {
        _onHotkey = onHotkey;
        _log = log;
        CreateHandle(new CreateParams { Style = 0 });
        Rebind(modifiers, vk);
    }

    /// <summary>Unregisters the current binding (if any) and registers the new one.</summary>
    public bool Rebind(uint modifiers, uint vk)
    {
        if (IsRegistered) UnregisterHotKey(Handle, HotkeyId);

        Modifiers = modifiers;
        Vk        = vk;
        IsRegistered = RegisterHotKey(Handle, HotkeyId, modifiers, vk);
        if (!IsRegistered)
            _log?.Invoke($"Failed to register hotkey {Describe(modifiers, vk)} (already in use by another app?).");
        return IsRegistered;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
            try { _onHotkey(); }
            catch (Exception ex) { _log?.Invoke($"HotkeyService callback threw: {ex.Message}\n{ex.StackTrace}"); }
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (IsRegistered) UnregisterHotKey(Handle, HotkeyId);
        if (Handle != IntPtr.Zero) DestroyHandle();
    }

    public static string Describe(uint modifiers, uint vk)
    {
        var parts = new List<string>();
        if ((modifiers & MOD_WIN)     != 0) parts.Add("Win");
        if ((modifiers & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & MOD_ALT)     != 0) parts.Add("Alt");
        if ((modifiers & MOD_SHIFT)   != 0) parts.Add("Shift");
        parts.Add(VkToString(vk));
        return string.Join(" + ", parts);
    }

    private static string VkToString(uint vk)
    {
        // VK codes for '0'-'9' and 'A'-'Z' coincide with their ASCII chars.
        if (vk is >= 0x30 and <= 0x39) return ((char)vk).ToString();
        if (vk is >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        return KeyInterop.KeyFromVirtualKey((int)vk).ToString();
    }
}
