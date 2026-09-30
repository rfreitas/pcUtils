using System.Drawing;
using System.Windows.Forms;

namespace PortWatch;

/// <summary>
/// NotifyIcon + hover popup. WinForms raises NotifyIcon.MouseMove while the
/// cursor is over the icon but has no "mouse left" event, so a short timer
/// hides the popup once the cursor is neither moving over the icon nor
/// inside the popup itself.
/// </summary>
internal sealed class TrayApp : IDisposable
{
    private static readonly TimeSpan HoverGrace = TimeSpan.FromMilliseconds(400);

    private readonly NotifyIcon _tray;
    private readonly HoverPopup _popup = new();
    private readonly System.Windows.Forms.Timer _hideTimer = new() { Interval = 150 };
    private bool _showing;
    private DateTime _lastIconMoveUtc = DateTime.MinValue;

    public TrayApp()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Exit", null, (_, _) => System.Windows.Application.Current.Shutdown());

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Information,
            Text = "PortWatch",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.MouseMove += OnIconMouseMove;

        _hideTimer.Tick += (_, _) => HideIfCursorAway();
        _hideTimer.Start();
    }

    /// <summary>Same code path as a real hover, callable without a mouse.</summary>
    public void SimulateHover() => OnIconMouseMove(null, new MouseEventArgs(MouseButtons.None, 0, 0, 0, 0));

    /// <summary>Hover with the cursor treated as being at <paramref name="cursorPx"/> (e.g. over the tray icon).</summary>
    public void SimulateHoverAt(System.Drawing.Point cursorPx)
    {
        _lastIconMoveUtc = DateTime.UtcNow;
        _popup.SetRows(PortGrouper.Group(PortScanner.Scan()));
        _popup.ShowNear(cursorPx);
    }

    public HoverPopup Popup => _popup;

    /// <summary>Posts <paramref name="count"/> genuine tray mouse-move messages to the NotifyIcon's window (diagnostics).</summary>
    public void PostTrayMouseMoves(int count)
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var window = (NativeWindow)typeof(NotifyIcon).GetField("_window", flags)!.GetValue(_tray)!;
        var id = Convert.ToInt32(typeof(NotifyIcon).GetField("_id", flags)!.GetValue(_tray)!);
        for (int i = 0; i < count; i++)
            PostMessage(window.Handle, 0x800 /* WM_USER+1024 tray callback */, (IntPtr)id, (IntPtr)0x200 /* WM_MOUSEMOVE */);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

    public bool PopupVisible => _popup.IsVisible;

    private void OnIconMouseMove(object? sender, MouseEventArgs e)
    {
        _lastIconMoveUtc = DateTime.UtcNow;
        // Show() pumps messages while it builds the window, and a hover is a burst of
        // mouse-move events, so guard against re-entering mid-creation.
        if (_popup.IsVisible || _showing) return;

        _showing = true;
        try
        {
            _popup.SetRows(PortGrouper.Group(PortScanner.Scan()));
            _popup.ShowNear(Cursor.Position);
        }
        finally { _showing = false; }
    }

    private void HideIfCursorAway()
    {
        if (!_popup.IsVisible) return;
        if (DateTime.UtcNow - _lastIconMoveUtc < HoverGrace) return;
        if (_popup.ContainsPixel(Cursor.Position)) return;
        _popup.Hide();
    }

    public void Dispose()
    {
        _hideTimer.Dispose();
        _popup.Close();
        _tray.Visible = false;
        _tray.Dispose();
    }
}
