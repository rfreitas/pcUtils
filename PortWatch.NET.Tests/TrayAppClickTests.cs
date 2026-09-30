using System.Windows.Forms;
using PortWatch;
using Xunit;

public class TrayAppClickTests
{
    private static void OnSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
        Assert.True(error is null, error?.ToString());
    }

    private static void Pump(int ms)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(20); }
    }

    [Fact]
    public void Click_opens_the_popup_and_a_second_click_closes_it()
    {
        OnSta(() =>
        {
            var app = new TrayApp();
            Assert.False(app.PopupVisible);

            app.OnIconClick();
            Assert.True(app.PopupVisible);

            app.OnIconClick();
            Assert.False(app.PopupVisible);

            Pump(400);   // past TrayFlyoutWindow's 250 ms reopen debounce
            app.OnIconClick();
            Assert.True(app.PopupVisible);
            app.Dispose();
        });
    }

    [Fact]
    public void Open_popup_is_not_closed_by_a_timer()
    {
        OnSta(() =>
        {
            var app = new TrayApp();
            app.OnIconClick();
            Pump(1500);   // no hover-style auto hide any more
            Assert.True(app.PopupVisible);
            app.Dispose();
        });
    }

    [Fact]
    public void Real_tray_click_message_opens_the_popup()
    {
        OnSta(() =>
        {
            var app = new TrayApp();
            app.PostTrayLeftClick();   // WM_LBUTTONDOWN + WM_LBUTTONUP through the NotifyIcon's own window
            Pump(500);
            Assert.True(app.PopupVisible);
            app.Dispose();
        });
    }

    [Fact]
    public void Mouse_movement_over_the_icon_does_nothing()
    {
        OnSta(() =>
        {
            var app = new TrayApp();
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var tray = (NotifyIcon)typeof(TrayApp).GetField("_tray", flags)!.GetValue(app)!;
            var window = (NativeWindow)typeof(NotifyIcon).GetField("_window", flags)!.GetValue(tray)!;
            var id = Convert.ToInt32(typeof(NotifyIcon).GetField("_id", flags)!.GetValue(tray)!);
            for (int i = 0; i < 30; i++) PostMessage(window.Handle, 0x800, (IntPtr)id, (IntPtr)0x200 /* WM_MOUSEMOVE */);
            Pump(800);
            Assert.False(app.PopupVisible);
            app.Dispose();
        });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
}
