using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AggressiveScreensaver.Services;

namespace AggressiveScreensaver.Services;

/// <summary>
/// Creates / destroys per-monitor black full-screen overlay windows and manages
/// taskbar + cursor visibility. Ported from ShowBlackOverlay/RemoveBlackOverlay
/// in index.ahk.
///
/// IMPORTANT: All public methods must be called on the UI thread.
/// </summary>
internal sealed class BlackOverlayManager
{
    // -------------------------------------------------------------------------
    // P/Invoke
    // -------------------------------------------------------------------------
    [DllImport("user32.dll")] private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern int ShowCursor(bool bShow);

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------
    private readonly List<Form> _overlays = [];
    private bool _isBlanked;
    private readonly Action _onActivityReset;   // called after cleanup

    public bool IsBlanked => _isBlanked;

    public BlackOverlayManager(Action onActivityReset)
    {
        _onActivityReset = onActivityReset;
    }

    // -------------------------------------------------------------------------
    // Show
    // -------------------------------------------------------------------------

    public void Show()
    {
        if (_isBlanked) return;

        Logger.Log("Entering blanking mode...");
        DestroyAll();

        try
        {
            foreach (Screen monitor in Screen.AllScreens)
            {
                try
                {
                    Rectangle b = monitor.Bounds;
                    var f = new Form
                    {
                        FormBorderStyle = FormBorderStyle.None,
                        BackColor       = Color.Black,
                        TopMost         = true,
                        ShowInTaskbar   = false,
                        StartPosition   = FormStartPosition.Manual,
                        Bounds          = b,
                        Cursor          = Cursors.Default,
                    };
                    // Prevent click-through: we want to block the mouse (same as AHK overlay)
                    f.Show();
                    _overlays.Add(f);
                }
                catch (Exception ex)
                {
                    Logger.LogException(ex);
                }
            }

            if (_overlays.Count > 0)
            {
                ShowCursor(false);
                SetTaskbarsVisible(false);
                _isBlanked = true;
            }
        }
        catch (Exception ex)
        {
            Logger.LogException(ex, severe: true);
            Remove(); // defensive cleanup
        }
    }

    // -------------------------------------------------------------------------
    // Remove
    // -------------------------------------------------------------------------

    public void Remove()
    {
        // Reset activity timer so we don't immediately re-blank
        _onActivityReset();

        if (_overlays.Count == 0 && !_isBlanked) return;

        Logger.Log($"Leaving blanking mode. (Overlays: {_overlays.Count})");
        DestroyAll();
        ShowCursor(true);
        SetTaskbarsVisible(true);
        _isBlanked = false;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void DestroyAll()
    {
        foreach (var f in _overlays)
        {
            try { f.Close(); f.Dispose(); }
            catch { /* swallow */ }
        }
        _overlays.Clear();
    }

    private static void SetTaskbarsVisible(bool visible)
    {
        int cmd = visible ? SW_SHOW : SW_HIDE;
        foreach (string cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
        {
            IntPtr hwnd = FindWindow(cls, null);
            if (hwnd != IntPtr.Zero)
                ShowWindow(hwnd, cmd);
        }
    }
}
