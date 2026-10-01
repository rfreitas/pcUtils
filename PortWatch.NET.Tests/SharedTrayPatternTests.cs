using System.Windows.Forms;
using Shared;
using Xunit;

/// <summary>Tests for the shared tray-app building blocks in Shared.NET (TRAY_APP_UX.md).</summary>
public class TrayMenuTests
{
    [Fact]
    public void Menu_is_dark_with_margins_off()
    {
        var menu = TrayMenu.Create();
        Assert.IsType<DarkMenuRenderer>(menu.Renderer);
        Assert.False(menu.ShowCheckMargin);
        Assert.False(menu.ShowImageMargin);
    }

    [Fact]
    public void Check_item_prefixes_label_and_tracks_state()
    {
        var item = TrayMenu.CheckItem("Option", isChecked: true);
        Assert.Equal("✓ Option", item.Text);

        item.Checked = false;
        Assert.Equal("   Option", item.Text);
    }

    [Fact]
    public void Check_item_reports_the_new_state()
    {
        bool? seen = null;
        var item = TrayMenu.CheckItem("Option", false, on => { seen = on; return true; });

        item.Checked = true;

        Assert.True(seen);
        Assert.True(item.Checked);
        Assert.Equal("✓ Option", item.Text);
    }

    [Fact]
    public void Check_item_reverts_when_the_callback_returns_false()
    {
        var item = TrayMenu.CheckItem("Option", false, _ => false);

        item.Checked = true;

        Assert.False(item.Checked);
        Assert.Equal("   Option", item.Text);
    }

    [Fact]
    public void Revert_does_not_call_the_callback_again()
    {
        int calls = 0;
        var item = TrayMenu.CheckItem("Option", false, _ => { calls++; return false; });

        item.Checked = true;

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Header_is_disabled_and_actions_are_aligned_with_check_rows()
    {
        Assert.False(TrayMenu.Header("Title").Enabled);
        Assert.Equal("   Do it", TrayMenu.Action("Do it", () => { }).Text);
        Assert.Equal("   Exit", TrayMenu.ExitItem(() => { }).Text);
    }

    [Fact]
    public void Exit_item_invokes_the_callback()
    {
        bool exited = false;
        TrayMenu.ExitItem(() => exited = true).PerformClick();
        Assert.True(exited);
    }
}

public class TrayMenuRadioTests
{
    private static IReadOnlyList<ToolStripMenuItem> Make(int selected, Action<int>? on = null) =>
        TrayMenu.RadioGroup(["Ports", "Processes", "Both"], selected, on ?? (_ => { }));

    [Fact]
    public void The_selected_row_is_ticked_and_the_others_are_aligned()
    {
        var items = Make(1);
        Assert.Equal(["   Ports", "✓ Processes", "   Both"], items.Select(i => i.Text));
        Assert.Equal([false, true, false], items.Select(i => i.Checked));
    }

    [Fact]
    public void Clicking_another_row_moves_the_tick_and_reports_the_index()
    {
        int? chosen = null;
        var items = Make(0, i => chosen = i);

        items[2].PerformClick();

        Assert.Equal(2, chosen);
        Assert.Equal(["   Ports", "   Processes", "✓ Both"], items.Select(i => i.Text));
        Assert.Equal([false, false, true], items.Select(i => i.Checked));
    }

    [Fact]
    public void Clicking_the_selected_row_again_does_nothing()
    {
        int calls = 0;
        var items = Make(0, _ => calls++);

        items[0].PerformClick();

        Assert.Equal(0, calls);
        Assert.True(items[0].Checked);
    }

    [Fact]
    public void Exactly_one_row_is_ticked_after_any_sequence_of_clicks()
    {
        var items = Make(0);
        foreach (int i in new[] { 2, 1, 1, 0, 2 }) items[i].PerformClick();

        Assert.Single(items.Where(i => i.Checked));
        Assert.Single(items.Where(i => i.Text.StartsWith("✓")));
    }
}

public class SingleInstanceGuardTests
{
    [Fact]
    public void A_second_holder_is_refused_until_the_first_disposes()
    {
        string name = @"Local\PortWatchTest_" + Guid.NewGuid().ToString("N");
        using var first = SingleInstanceGuard.TryAcquire(name, "NoSuchProcess", replaceExisting: false);
        Assert.NotNull(first);

        // Mutex ownership is per thread, so the competing holder must run on another thread.
        SingleInstanceGuard? second = null;
        var t = new Thread(() => second = SingleInstanceGuard.TryAcquire(name, "NoSuchProcess", replaceExisting: false));
        t.Start(); t.Join();
        Assert.Null(second);

        first!.Dispose();

        SingleInstanceGuard? third = null;
        var t2 = new Thread(() => { third = SingleInstanceGuard.TryAcquire(name, "NoSuchProcess", replaceExisting: false); third?.Dispose(); });
        t2.Start(); t2.Join();
        Assert.NotNull(third);
    }
}
