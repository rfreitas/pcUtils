using System;
using System.Windows;
using System.Windows.Input;

namespace Shared;

/// <summary>
/// Base for a borderless "flyout" window opened from a tray icon (like the
/// Windows volume/network flyouts) — dismisses on click-away or Escape, and
/// wires up the click-away/deactivate handling every such window needs.
///
/// Toggling one of these from a NotifyIcon click is trickier than
/// "if (IsVisible) Hide(); else Show();": clicking the tray icon while the
/// flyout is open steals focus, which fires Deactivated -> Hide() *before*
/// the tray icon's own Click handler runs. By the time that handler checks
/// IsVisible, it's already false, so a naive toggle just reopens the window
/// instead of leaving it closed. ToggleShow() debounces that race by ignoring
/// a reopen request that arrives immediately after an auto-hide.
/// </summary>
public class TrayFlyoutWindow : Window
{
    private static readonly TimeSpan ReopenDebounce = TimeSpan.FromMilliseconds(250);
    private DateTime _autoHiddenAtUtc = DateTime.MinValue;

    protected TrayFlyoutWindow()
    {
        WindowStyle           = WindowStyle.None;
        ResizeMode            = ResizeMode.NoResize;
        Topmost               = true;
        ShowInTaskbar         = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Deactivated    += (_, _) => { _autoHiddenAtUtc = DateTime.UtcNow; Hide(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
    }

    /// <summary>
    /// Call from the tray icon's click handler. Closes the flyout if it's
    /// currently open; otherwise invokes <paramref name="performShow"/> to
    /// position and show it — unless the window auto-hid (via click-away)
    /// moments ago, in which case this click is the *same* click that caused
    /// that, and the flyout should just stay closed.
    /// </summary>
    public void ToggleShow(Action performShow)
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        if (DateTime.UtcNow - _autoHiddenAtUtc < ReopenDebounce)
            return;

        performShow();
    }
}
