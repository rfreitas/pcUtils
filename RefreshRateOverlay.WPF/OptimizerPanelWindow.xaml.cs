using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using RefreshRateOverlay.WPF.Services;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using ComboBox = System.Windows.Controls.ComboBox;
using Panel = System.Windows.Controls.Panel;
using Orientation = System.Windows.Controls.Orientation;

namespace RefreshRateOverlay.WPF;

/// <summary>
/// Side panel next to the overlay that shows the game optimizer's dry-run
/// classification. Purely a view: TrayApp owns sampling and calls Update.
/// </summary>
public partial class OptimizerPanelWindow : Window
{
    private static readonly Dictionary<OptimizerRole, Color> RoleColors = new()
    {
        [OptimizerRole.Game]          = Color.FromRgb(0x4C, 0xC2, 0xFF),
        [OptimizerRole.GameChain]     = Color.FromRgb(0x4C, 0xC2, 0xFF),
        [OptimizerRole.VrPipeline]    = Color.FromRgb(0x4C, 0xC2, 0xFF),
        [OptimizerRole.WouldThrottle] = Color.FromRgb(0xFF, 0xB8, 0x4D),
        [OptimizerRole.Protected]     = Color.FromRgb(0x6C, 0xCB, 0x5F),
        [OptimizerRole.LeftAlone]     = Color.FromRgb(0x88, 0x88, 0x88),
    };

    // Left alone is usually hundreds of idle processes; only its busiest rows
    // are worth showing, the rest is a count.
    internal const int LeftAloneRowLimit = 8;

    // Pickers live outside the 2.5 s rebuild: built once, re-parented into their
    // section on each Update, so a selection or an open dropdown survives.
    private readonly ComboBox _gamePriority, _gameCores, _throttlePriority, _throttleCores;
    private readonly FrameworkElement _gamePickers, _throttlePickers;
    private bool _suppressPickerEvents;

    /// <summary>Raised when the user picks a new priority or core type.</summary>
    internal event EventHandler<OptimizerActions>? ActionsChanged;

    internal OptimizerActions Actions { get; private set; } = OptimizerActions.Default;

    public OptimizerPanelWindow()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();

        _gamePriority = Picker(OptimizerActions.GamePriorities.Select(OptimizerActions.Label));
        _gameCores = Picker(OptimizerActions.Cores.Select(OptimizerActions.Label));
        _throttlePriority = Picker(OptimizerActions.ThrottlePriorities.Select(OptimizerActions.Label));
        _throttleCores = Picker(OptimizerActions.Cores.Select(OptimizerActions.Label));
        _gamePickers = PickerRow(_gamePriority, _gameCores,
            "Priority for the focused game. Unchanged is safest in VR: a High game outranks the streamer.");
        _throttlePickers = PickerRow(_throttlePriority, _throttleCores,
            "Priority for throttled processes. Idle only runs them when nothing else wants the CPU.");
        SetActions(OptimizerActions.Default);
    }

    internal ComboBox GamePriorityPicker => _gamePriority;
    internal ComboBox GameCoresPicker => _gameCores;
    internal ComboBox ThrottlePriorityPicker => _throttlePriority;
    internal ComboBox ThrottleCoresPicker => _throttleCores;

    /// <summary>Shows the stored choices without raising ActionsChanged.</summary>
    internal void SetActions(OptimizerActions actions)
    {
        Actions = actions.Normalized();
        _suppressPickerEvents = true;
        _gamePriority.SelectedIndex = Array.IndexOf(OptimizerActions.GamePriorities, Actions.GamePriority);
        _gameCores.SelectedIndex = Array.IndexOf(OptimizerActions.Cores, Actions.GameCores);
        _throttlePriority.SelectedIndex = Array.IndexOf(OptimizerActions.ThrottlePriorities, Actions.ThrottlePriority);
        _throttleCores.SelectedIndex = Array.IndexOf(OptimizerActions.Cores, Actions.ThrottleCores);
        _suppressPickerEvents = false;
    }

    private ComboBox Picker(IEnumerable<string> labels)
    {
        var box = new ComboBox { FontSize = 11, Height = 22, MinWidth = 96, Margin = new Thickness(4, 0, 10, 0) };
        foreach (string l in labels) box.Items.Add(l);
        box.SelectionChanged += (_, _) => OnPickerChanged();
        return box;
    }

    private static FrameworkElement PickerRow(ComboBox priority, ComboBox cores, string tip)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 4), ToolTip = tip };
        row.Children.Add(PickerLabel("Priority"));
        row.Children.Add(priority);
        row.Children.Add(PickerLabel("Cores"));
        row.Children.Add(cores);
        return row;
    }

    private static TextBlock PickerLabel(string text) =>
        new() { Text = text, Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };

    private void OnPickerChanged()
    {
        if (_suppressPickerEvents) return;
        if (_gamePriority.SelectedIndex < 0 || _gameCores.SelectedIndex < 0 || _throttlePriority.SelectedIndex < 0 || _throttleCores.SelectedIndex < 0) return;
        Actions = new OptimizerActions(
            OptimizerActions.GamePriorities[_gamePriority.SelectedIndex],
            OptimizerActions.Cores[_gameCores.SelectedIndex],
            OptimizerActions.ThrottlePriorities[_throttlePriority.SelectedIndex],
            OptimizerActions.Cores[_throttleCores.SelectedIndex]);
        ActionsChanged?.Invoke(this, Actions);
    }

    private bool PickerOpen =>
        _gamePriority.IsDropDownOpen || _gameCores.IsDropDownOpen || _throttlePriority.IsDropDownOpen || _throttleCores.IsDropDownOpen;

    private static void Reparent(FrameworkElement child, Panel into)
    {
        if (child.Parent is Panel old) old.Children.Remove(child);
        into.Children.Add(child);
    }

    /// <summary>Last report shown — test-facing.</summary>
    internal OptimizerReport? Report { get; private set; }

    private OptimizerMode _mode;
    private Dictionary<string, string> _failedByName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>live: true only when this report is the app the Live session is
    /// actually applying to; failures are the enforcer's per-pid problems.</summary>
    internal void Update(OptimizerReport report, OptimizerMode mode, bool live = false,
        IReadOnlyDictionary<int, string>? failures = null)
    {
        if (PickerOpen) return; // don't rebuild under an open dropdown; the next refresh catches up
        Report = report;
        _mode = live ? OptimizerMode.Live : mode;
        _failedByName = new(StringComparer.OrdinalIgnoreCase);
        if (failures is not null)
            foreach (var d in report.Decisions)
                if (failures.TryGetValue(d.Pid, out string? why)) _failedByName[d.Name] = why;

        ModeText.Text = _mode switch
        {
            OptimizerMode.Live => "Live: these changes are applied now and undone when you leave this app.",
            OptimizerMode.DryRun => "Dry run: shows what it would do. Nothing is changed.",
            _ when mode == OptimizerMode.Live => "Live for this app, but not applied: it isn't the focused app.",
            _ => "Preview only: the optimizer is Off for this app. Nothing is changed.",
        };
        SubtitleText.Text = $"For {report.App} · {report.At:HH:mm:ss} · {report.Decisions.Count} processes";

        SectionsPanel.Children.Clear();
        foreach (var section in GameOptimizerPlanner.Summarize(report))
            SectionsPanel.Children.Add(BuildSection(section));
    }

    // Protected groups the user has opened. Kept across Update calls: the panel
    // rebuilds every 2.5 s and an open group must not snap shut.
    private readonly HashSet<string> _expandedGroups = new(StringComparer.Ordinal);

    /// <summary>Test-facing: the group headers currently built, by reason.</summary>
    internal Dictionary<string, Border> GroupHeaders { get; } = new(StringComparer.Ordinal);

    internal bool IsGroupExpanded(string reason) => _expandedGroups.Contains(reason);

    private UIElement BuildSection(OptimizerSection section)
    {
        if (section.Role == OptimizerRole.Protected && section.Rows.Count > 0)
            return BuildGroupedSection(section);

        var color = RoleColors[section.Role];
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        string title = _mode == OptimizerMode.Live && section.Role == OptimizerRole.WouldThrottle ? "Throttled" : section.Title;
        panel.Children.Add(new TextBlock
        {
            Text = $"{title} ({section.ProcessCount})",
            Foreground = new SolidColorBrush(color),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 3),
        });
        if (section.Role == OptimizerRole.Game) Reparent(_gamePickers, panel);
        else if (section.Role == OptimizerRole.WouldThrottle) Reparent(_throttlePickers, panel);

        IEnumerable<OptimizerRow> rows = section.Rows;
        int hidden = 0;
        if (section.Role == OptimizerRole.LeftAlone && section.Rows.Count > LeftAloneRowLimit)
        {
            hidden = section.Rows.Skip(LeftAloneRowLimit).Sum(r => r.Count);
            rows = section.Rows.Take(LeftAloneRowLimit);
        }

        if (section.Rows.Count == 0)
            panel.Children.Add(Muted("None"));

        foreach (var row in rows)
            panel.Children.Add(_failedByName.TryGetValue(row.Name, out string? why)
                ? BuildRow(row with { Reason = $"{row.Reason} · failed: {why}" })
                : BuildRow(row));

        if (section.ChainRows is { Count: > 0 } chain)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Launch chain · not changed",
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                FontSize = 11,
                Margin = new Thickness(18, 4, 0, 1),
                ToolTip = "Processes that started the game, nearest first. A child inherits a lowered priority " +
                          "and CPU restriction from its parent, so these are never throttled.",
            });
            foreach (var row in chain)
                panel.Children.Add(BuildChainRow(row));
        }

        if (section.PipelineRows is { Count: > 0 } pipeline)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "VR pipeline · not changed",
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                FontSize = 11,
                Margin = new Thickness(18, 4, 0, 1),
                ToolTip = "The VR runtime and streamer: the game's frame still has to pass through them, " +
                          "so they are never throttled and the game is never raised above them.",
            });
            foreach (var row in pipeline)
                panel.Children.Add(BuildChainRow(row, marker: "• "));
        }

        if (hidden > 0)
            panel.Children.Add(Muted($"+ {hidden} more idle processes"));

        return panel;
    }

    /// <summary>Protected: one collapsible group per rule. The header shows the
    /// rule, process count and summed CPU; clicking it shows the processes.</summary>
    private UIElement BuildGroupedSection(OptimizerSection section)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(new TextBlock
        {
            Text = $"{section.Title} ({section.ProcessCount})",
            Foreground = new SolidColorBrush(RoleColors[section.Role]),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 3),
        });

        GroupHeaders.Clear();
        foreach (var group in GameOptimizerPlanner.GroupByReason(section))
        {
            bool expanded = _expandedGroups.Contains(group.Reason);
            var rowsPanel = new StackPanel { Visibility = expanded ? Visibility.Visible : Visibility.Collapsed, Margin = new Thickness(10, 0, 0, 2) };
            foreach (var row in group.Rows)
                rowsPanel.Children.Add(BuildRow(row, showReason: false));

            var chevron = new TextBlock
            {
                Text = expanded ? "" : "", // ChevronDown / ChevronRight
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            var title = new TextBlock { Text = $"{group.Reason} ({group.ProcessCount})", Foreground = Brushes.White, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
            var cpu = new TextBlock
            {
                Text = group.CpuCores is { } c ? $"{c:0.0} cores" : "",
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(chevron, 0); Grid.SetColumn(title, 1); Grid.SetColumn(cpu, 2);
            headerGrid.Children.Add(chevron); headerGrid.Children.Add(title); headerGrid.Children.Add(cpu);

            var header = new Border
            {
                Child = headerGrid,
                Background = Brushes.Transparent, // so the whole row is clickable, not just the text
                Cursor = System.Windows.Input.Cursors.Hand,
                Padding = new Thickness(8, 2, 14, 2),
                ToolTip = expanded ? "Hide processes" : "Show processes",
            };
            string reason = group.Reason;
            header.MouseLeftButtonUp += (_, _) =>
            {
                bool open = !_expandedGroups.Remove(reason);
                if (open) _expandedGroups.Add(reason);
                rowsPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                chevron.Text = open ? "" : "";
                header.ToolTip = open ? "Hide processes" : "Show processes";
            };
            GroupHeaders[reason] = header;

            panel.Children.Add(header);
            panel.Children.Add(rowsPanel);
        }
        return panel;
    }

    private static UIElement BuildRow(OptimizerRow row) => BuildRow(row, showReason: true);

    private static UIElement BuildChainRow(OptimizerRow row, string marker = "↳ ")
    {
        var g = new Grid { Margin = new Thickness(18, 0, 14, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var n = new TextBlock
        {
            Text = marker + (row.Count > 1 ? $"{row.Name} ×{row.Count}" : row.Name),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var cpuText = new TextBlock
        {
            Text = row.CpuCores is { } v ? $"{v:0.0} cores" : "",
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize = 11,
            Margin = new Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(n, 0); Grid.SetColumn(cpuText, 1);
        g.Children.Add(n); g.Children.Add(cpuText);
        return g;
    }

    private static UIElement BuildRow(OptimizerRow row, bool showReason)
    {
        if (!showReason)
        {
            var g = new Grid { Margin = new Thickness(8, 1, 14, 1) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var n = new TextBlock { Text = row.Count > 1 ? $"{row.Name} ×{row.Count}" : row.Name, Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
            var cpuText = new TextBlock { Text = row.CpuCores is { } v ? $"{v:0.0} cores" : "", Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)), FontSize = 11, Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(n, 0); Grid.SetColumn(cpuText, 1);
            g.Children.Add(n); g.Children.Add(cpuText);
            return g;
        }

        var grid = new Grid { Margin = new Thickness(8, 1, 14, 1) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        string name = row.Count > 1 ? $"{row.Name} ×{row.Count}" : row.Name;
        left.Children.Add(new TextBlock { Text = name, Foreground = Brushes.White, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
        string detail = row.Action is null ? row.Reason : $"{row.Reason} → {row.Action}";
        left.Children.Add(new TextBlock { Text = detail, Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)), FontSize = 11, TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        var cpu = new TextBlock
        {
            Text = row.CpuCores is { } c ? $"{c:0.0} cores" : "",
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize = 11,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(cpu, 1);
        grid.Children.Add(cpu);
        return grid;
    }

    private static TextBlock Muted(string text) =>
        new() { Text = text, Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)), FontSize = 11, Margin = new Thickness(8, 1, 0, 1) };

    /// <summary>Places this panel beside the overlay: right edge if it fits on the
    /// overlay's own monitor, otherwise the left edge. Height matches the overlay,
    /// with a floor so the list is usable on a short overlay.</summary>
    internal void AnchorTo(Window overlay)
    {
        var work = OverlayMonitorWorkArea(overlay);
        Height = Math.Max(overlay.ActualHeight, 420);
        double right = overlay.Left + overlay.ActualWidth + 6;
        Left = right + Width <= work.Right ? right : Math.Max(work.Left, overlay.Left - Width - 6);
        Top = Math.Min(overlay.Top, Math.Max(work.Top, work.Bottom - Height));
    }

    private static Rect OverlayMonitorWorkArea(Window overlay)
    {
        IntPtr hwnd = new WindowInteropHelper(overlay).Handle;
        if (hwnd == IntPtr.Zero) return SystemParameters.WorkArea;

        var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
        var source = PresentationSource.FromVisual(overlay);
        Matrix fromDevice = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var wa = screen.WorkingArea;
        Point tl = fromDevice.Transform(new Point(wa.Left, wa.Top));
        Point br = fromDevice.Transform(new Point(wa.Right, wa.Bottom));
        return new Rect(tl, br);
    }
}
