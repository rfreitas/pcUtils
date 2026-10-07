using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Media;
using RefreshRateOverlay.WPF.Services;

namespace RefreshRateOverlay.WPF.Tests;

/// <summary>Overlay optimizer row (same dot/Apply shape as the other settings)
/// and the side panel's rendering of a report.</summary>
public class OptimizerOverlayAndPanelTests
{
    private static OverlayWindow CreateWindow(OptimizerMode mode = OptimizerMode.Off, OptimizerMode stored = OptimizerMode.Off) =>
        new OverlayWindow(
            activeApp: "Desktop",
            availableRates: new List<int> { 60, 120 },
            preselectRate: 60,
            hdrSupported: true,
            hdrEnabled: false,
            hasProfile: false,
            windowMode: WindowMode.Windowed,
            preselectDsxProfile: null,
            gsyncMode: null,
            appVrrState: null,
            runningApps: new List<string>(),
            storedRate: 60,
            storedHdr: false,
            optimizerMode: mode,
            storedOptimizerMode: stored);

    private static Color Dot(OverlayWindow w) => ((SolidColorBrush)w.OptimizerSyncDot.Fill).Color;

    [Fact]
    public void Row_PreselectsMode_AndDotIsGreen() => StaTestHelper.Run(() =>
    {
        var w = CreateWindow(OptimizerMode.DryRun, OptimizerMode.DryRun);
        Assert.Equal(OptimizerMode.DryRun, w.SelectedOptimizerMode);
        Assert.Equal(new[] { "Off", "Dry run" }, w.OptimizerDropDown.Items.Cast<string>());
        Assert.Equal(Brushes.LimeGreen.Color, Dot(w));
    });

    [Fact]
    public void UserEdit_TurnsDotRed_ApplyReflectionTurnsItGreen() => StaTestHelper.Run(() =>
    {
        var w = CreateWindow();
        w.OptimizerDropDown.SelectedIndex = (int)OptimizerMode.DryRun;
        Assert.Equal(Brushes.Red.Color, Dot(w));

        w.ReflectOptimizerApplied(OptimizerMode.DryRun);
        Assert.Equal(Brushes.LimeGreen.Color, Dot(w));
    });

    [Fact]
    public void PanelButton_RaisesPanelRequested() => StaTestHelper.Run(() =>
    {
        var w = CreateWindow();
        int raised = 0;
        w.OptimizerPanelRequested += (_, _) => raised++;
        w.OptimizerPanelButton.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal(1, raised);
    });

    private static OptimizerReport SampleReport()
    {
        var roots = GameOptimizerPlanner.OpenXrRoots(@"C:\Program Files\Virtual Desktop Streamer\OpenXR\virtualdesktop-openxr.json");
        var procs = new List<ProcessSnapshot>
        {
            new("eldenring", 1, @"D:\Games\eldenring.exe", 1, ProcessPriorityClass.Normal, 5.2),
            new("rustc", 2, @"C:\rust\rustc.exe", 1, ProcessPriorityClass.Normal, 1.4),
            new("VirtualDesktop.Streamer", 3, @"C:\Program Files\Virtual Desktop Streamer\VirtualDesktop.Streamer.exe", 1, ProcessPriorityClass.Normal, 0.8),
            new("MsMpEng", 4, null, 0, null, null),
        };
        for (int i = 0; i < 20; i++)
            procs.Add(new($"idle{i}", 100 + i, $@"C:\x\idle{i}.exe", 1, ProcessPriorityClass.Normal, 0.0));
        return GameOptimizerPlanner.Plan("eldenring.exe", new DateTime(2026, 10, 7, 21, 0, 0), procs, 999, roots);
    }

    private static IEnumerable<string> AllText(System.Windows.DependencyObject root)
    {
        if (root is TextBlock tb) yield return tb.Text;
        int n = VisualTreeHelper.GetChildrenCount(root);
        if (n == 0 && root is Panel p)
        {
            foreach (System.Windows.UIElement c in p.Children)
                foreach (var t in AllText(c)) yield return t;
            yield break;
        }
        for (int i = 0; i < n; i++)
            foreach (var t in AllText(VisualTreeHelper.GetChild(root, i))) yield return t;
    }

    [Fact]
    public void Panel_ShowsDryRunBanner_SectionsAndRows() => StaTestHelper.Run(() =>
    {
        var panel = new OptimizerPanelWindow();
        panel.Update(SampleReport(), OptimizerMode.DryRun);

        var text = AllText(panel.SectionsPanel).ToList();
        Assert.Contains("Nothing is changed", panel.ModeText.Text);
        Assert.Contains("Game (1)", text);
        Assert.Contains("Would throttle (1)", text);
        Assert.Contains("Protected (2)", text);
        Assert.Contains("Left alone (20)", text);
        Assert.Contains("rustc", text);
        Assert.Contains($"+ {20 - OptimizerPanelWindow.LeftAloneRowLimit} more idle processes", text);
    });

    [Fact]
    public void Panel_WhenModeOff_SaysPreviewOnly() => StaTestHelper.Run(() =>
    {
        var panel = new OptimizerPanelWindow();
        panel.Update(SampleReport(), OptimizerMode.Off);
        Assert.Contains("Preview only", panel.ModeText.Text);
    });

    private static void Click(System.Windows.UIElement e) =>
        e.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
        { RoutedEvent = System.Windows.UIElement.MouseLeftButtonUpEvent });

    [Fact]
    public void Panel_ProtectedIsGroupedByRule_CollapsedByDefault() => StaTestHelper.Run(() =>
    {
        var panel = new OptimizerPanelWindow();
        panel.Update(SampleReport(), OptimizerMode.DryRun);

        Assert.Equal(new[] { "OpenXR runtime", "Windows / service" }, panel.GroupHeaders.Keys);
        Assert.False(panel.IsGroupExpanded("OpenXR runtime"));
        var text = AllText(panel.SectionsPanel).ToList();
        Assert.Contains("OpenXR runtime (1)", text);
    });

    [Fact]
    public void Panel_ClickingAGroupExpandsIt_AndItStaysOpenAcrossRefreshes() => StaTestHelper.Run(() =>
    {
        var panel = new OptimizerPanelWindow();
        panel.Update(SampleReport(), OptimizerMode.DryRun);

        Click(panel.GroupHeaders["OpenXR runtime"]);
        Assert.True(panel.IsGroupExpanded("OpenXR runtime"));

        panel.Update(SampleReport(), OptimizerMode.DryRun); // periodic refresh rebuilds everything
        Assert.True(panel.IsGroupExpanded("OpenXR runtime"));

        Click(panel.GroupHeaders["OpenXR runtime"]);
        Assert.False(panel.IsGroupExpanded("OpenXR runtime"));
    });

    [Fact]
    public void Panel_ShowsLaunchChainUnderTheGame() => StaTestHelper.Run(() =>
    {
        var procs = new List<ProcessSnapshot>
        {
            new("explorer", 10, @"C:\Windows\explorer.exe", 1, ProcessPriorityClass.Normal, 0.0),
            new("steam", 20, @"C:\Steam\steam.exe", 1, ProcessPriorityClass.Normal, 0.2, 10),
            new("eldenring", 30, @"D:\Games\eldenring.exe", 1, ProcessPriorityClass.Normal, 5.0, 20),
        };
        var report = GameOptimizerPlanner.Plan("eldenring.exe", DateTime.Now, procs, 999, Array.Empty<string>());
        var panel = new OptimizerPanelWindow();
        panel.Update(report, OptimizerMode.DryRun);

        var text = AllText(panel.SectionsPanel).ToList();
        Assert.Contains("Launch chain · not changed", text);
        int steam = text.IndexOf("↳ steam"), explorer = text.IndexOf("↳ explorer");
        Assert.True(steam >= 0 && explorer > steam);
        Assert.DoesNotContain(text, t => t.StartsWith("Started the game"));
    });
}
