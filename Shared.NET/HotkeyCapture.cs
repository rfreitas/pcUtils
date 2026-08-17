using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Shared;

/// <summary>
/// Low-level keyboard hook used only while a "record a new shortcut" UI is
/// active. WPF's routed KeyDown/Keyboard.Modifiers can't reliably see the
/// Windows key (the shell intercepts it to open the Start menu), so this
/// tracks modifier state directly from WH_KEYBOARD_LL and swallows the Win
/// key's default action while recording.
/// </summary>
internal sealed class HotkeyCapture : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN     = 0x0100;
    private const int WM_SYSKEYDOWN  = 0x0104;
    private const int WM_KEYUP       = 0x0101;
    private const int WM_SYSKEYUP    = 0x0105;

    private const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const int VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3;
    private const int VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1;
    private const int VK_LMENU = 0xA4, VK_RMENU = 0xA5;
    private const int VK_ESCAPE = 0x1B; // reserved by the settings UI to cancel recording

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private readonly LowLevelKeyboardProc _proc; // keep a live reference so the delegate isn't GC'd while hooked
    private IntPtr _hookHandle = IntPtr.Zero;
    private bool _winDown, _ctrlDown, _shiftDown, _altDown;

    /// <summary>
    /// Fired for any non-modifier keydown (Escape excluded, reserved for cancel).
    /// Modifiers may be 0 - a single key with no modifier is a valid capture.
    /// </summary>
    public event Action<uint, uint>? Captured; // (modifiers, vk) using HotkeyService.MOD_* bits

    public HotkeyCapture() => _proc = HookCallback;

    public void Start()
    {
        if (_hookHandle != IntPtr.Zero) return;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule  = curProcess.MainModule!;
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(curModule.ModuleName), 0);
    }

    public void Stop()
    {
        if (_hookHandle == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
        _winDown = _ctrlDown = _shiftDown = _altDown = false;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int vk = (int)data.vkCode;
            bool isDown = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
            bool isUp   = wParam == WM_KEYUP   || wParam == WM_SYSKEYUP;

            bool isModifier = vk == VK_LWIN || vk == VK_RWIN
                            || vk == VK_LCONTROL || vk == VK_RCONTROL
                            || vk == VK_LSHIFT || vk == VK_RSHIFT
                            || vk == VK_LMENU || vk == VK_RMENU;

            if (isDown || isUp)
            {
                bool state = isDown;
                if (vk == VK_LWIN || vk == VK_RWIN) _winDown = state;
                else if (vk == VK_LCONTROL || vk == VK_RCONTROL) _ctrlDown = state;
                else if (vk == VK_LSHIFT || vk == VK_RSHIFT) _shiftDown = state;
                else if (vk == VK_LMENU || vk == VK_RMENU) _altDown = state;
            }

            if (isDown && !isModifier && vk != VK_ESCAPE && IsPlausibleKey(vk))
            {
                uint mods = 0;
                if (_winDown)   mods |= HotkeyService.MOD_WIN;
                if (_ctrlDown)  mods |= HotkeyService.MOD_CONTROL;
                if (_shiftDown) mods |= HotkeyService.MOD_SHIFT;
                if (_altDown)   mods |= HotkeyService.MOD_ALT;

                // A single key with no modifier is allowed - it just means the key
                // becomes globally intercepted (RegisterHotKey supports mods=0 fine).
                Captured?.Invoke(mods, (uint)vk);
            }

            // Swallow the Windows key while recording so Start Menu doesn't pop open.
            if (vk == VK_LWIN || vk == VK_RWIN)
                return (IntPtr)1;
        }
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    /// <summary>Rejects vk codes no real keyboard ever reports — this hook sees
    /// synthetic/injected key events the same as physical ones (SendInput/
    /// keybd_event, exactly what a controller-to-keyboard mapping layer or a
    /// game's own input handling can emit), and capturing one of these as "the
    /// hotkey" produces a binding that can never correspond to an actual
    /// keypress again.
    ///
    /// 0xFF was deliberately left off this list after getting it wrong once:
    /// Microsoft's Virtual-Key table documents it as "reserved," but a MacBook
    /// keyboard's Windows driver evidently maps a real key to it (confirmed
    /// directly), so rejecting it here would have blocked legitimate hardware,
    /// not just synthetic input. If something during a game also happens to
    /// emit synthetic events on the same vk a user's real keyboard uses, no
    /// capture-time filter can distinguish the two — that's a genuine
    /// collision, not invalid input, and the fix is picking a different key,
    /// not rejecting the value here.</summary>
    private static bool IsPlausibleKey(int vk) => vk switch
    {
        0x00 => false, // undefined — no real keyboard hook ever reports this
        0x07 => false, // reserved
        _ => true,
    };

    public void Dispose() => Stop();
}
