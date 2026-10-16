using ClaudeFleet;
using Xunit;

public class FleetPopupTests
{
    private static void OnSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start(); t.Join();
        if (error is not null) throw new Exception("STA body failed", error);
    }

    [Fact]
    public void Cpu_updates_change_meters_but_never_text_or_size()
    {
        OnSta(() =>
        {
            var mock = new MockFleet();
            var popup = new FleetPopup();
            popup.SetFleet(mock.Snapshot());
            var size = popup.MeasureContent();
            string text = popup.AllText();
            var levels = new HashSet<int>();

            for (int i = 0; i < 20; i++)
            {
                mock.Tick();
                popup.UpdateCpu(mock.Snapshot());
                levels.Add(popup.MeterLevelOf("p47900"));
                Assert.Equal(size, popup.MeasureContent());
                Assert.Equal(text, popup.AllText());
            }
            Assert.True(levels.Count > 1, "the mock should move at least one meter");
            popup.Close();
        });
    }

    [Fact]
    public void Busy_sessions_are_expanded_and_mcp_hidden_by_default()
    {
        OnSta(() =>
        {
            var popup = new FleetPopup();
            popup.SetFleet(new MockFleet().Snapshot());
            string text = popup.AllText();
            Assert.Contains("find grip clamp references", text);    // agent of a busy session
            Assert.Contains("rustc bevy_pbr", text);                // process of a busy session
            Assert.DoesNotContain("whiteboard mcp", text);
            Assert.True(popup.MeterLevelOf("total") >= 0);
            popup.Close();
        });
    }

    [Fact]
    public void ShowMcp_adds_mcp_server_rows()
    {
        OnSta(() =>
        {
            var popup = new FleetPopup { ShowMcp = true };
            popup.SetFleet(new MockFleet().Snapshot());
            Assert.Contains("whiteboard mcp", popup.AllText());
            popup.Close();
        });
    }
}
