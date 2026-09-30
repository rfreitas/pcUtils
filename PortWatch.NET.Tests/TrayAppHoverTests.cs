using System.Reflection;
using System.Windows.Forms;
using PortWatch;
using Xunit;

public class TrayAppHoverTests
{
    [Fact]
    public void Hover_then_timer_ticks_do_not_throw()
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var app = new TrayApp();
                var move = typeof(TrayApp).GetMethod("OnIconMouseMove", BindingFlags.NonPublic | BindingFlags.Instance)!;
                move.Invoke(app, [null, new MouseEventArgs(MouseButtons.None, 0, 0, 0, 0)]);

                var popup = (HoverPopup)typeof(TrayApp).GetField("_popup", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(app)!;
                Assert.True(popup.IsVisible);

                var end = DateTime.UtcNow.AddSeconds(2);
                while (DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(20); }
                Assert.False(popup.IsVisible);
                app.Dispose();
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
        Assert.True(error is null, error?.ToString());
    }
}
