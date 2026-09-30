using PortWatch;
using Xunit;

public class HoverPopupTests
{
    [Fact]
    public void Popup_shows_real_ports_without_throwing()
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var popup = new HoverPopup();
                popup.SetRows(PortGrouper.Group(PortScanner.Scan()));
                popup.ShowNear(new System.Drawing.Point(1800, 1000));
                Assert.True(popup.IsVisible);
                popup.Close();
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
        Assert.Null(error);
    }
}
