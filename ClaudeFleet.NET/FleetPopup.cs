using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Shared;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;
using Panel = System.Windows.Controls.Panel;
using Rectangle = System.Windows.Shapes.Rectangle;
using Size = System.Windows.Size;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Cursors = System.Windows.Input.Cursors;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;

namespace ClaudeFleet;

/// <summary>
/// Click-to-open flyout: one line per session, with its subagents, headless child sessions and process tree
/// nested under it. Fixed width and fixed slots, so live CPU updates only recolour meters (UX guide §7).
/// </summary>
internal sealed class FleetPopup : TrayFlyoutWindow
{
    private static readonly Brush Bg     = Frozen("#2d2d2d");
    private static readonly Brush Fg     = Frozen("#e6e6e6");
    private static readonly Brush Dim    = Frozen("#888888");
    private static readonly Brush Accent = Frozen("#7fc8ff");
    private static readonly Brush Warn   = Frozen("#ffb454");
    private static readonly Brush Done   = Frozen("#6fcf97");
    private static readonly Brush Head   = Frozen("#c39bff");
    private static readonly Brush HlRow  = Frozen("#3b4a5e");
    private static readonly Brush Unlit  = Frozen("#4a4a4a");
    private static readonly Brush Clear  = Brushes.Transparent;

    public const double FlyoutWidth = 580;
    private const double Indent = 16;

    /// <summary>A row action: "focus", "resume", "folder", "transcript", "kill", "copycmd" on a <see cref="SessionView"/> or <see cref="ProcView"/>.</summary>
    public Action<string, object>? OnAction { get; set; }

    /// <summary>Show MCP server processes in the trees. Read by <see cref="SetFleet"/>.</summary>
    public bool ShowMcp { get; set; }

    private readonly StackPanel _rows = new();
    private readonly TextBlock _footer = new() { Margin = new Thickness(10, 4, 10, 6), Foreground = Dim, FontSize = 11 };
    private readonly Dictionary<string, (SegmentBar Bar, FrameworkElement Row)> _meters = new();
    private readonly List<TextBlock> _texts = new();
    private readonly HashSet<int> _toggled = new();   // sessions whose default expansion the user flipped
    private SegmentBar? _totalBar;

    public FleetPopup()
    {
        SizeToContent = SizeToContent.Height;
        Width         = FlyoutWidth;
        MaxHeight     = 560;
        Background    = Bg;
        FontFamily    = new FontFamily("Segoe UI");
        FontSize      = 12;
        Left = -10000;
        Top  = -10000;
        new WindowInteropHelper(this).EnsureHandle();

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(6, 6, 6, 4),
            Content = _rows,
        };
        Grid.SetRow(_footer, 1);
        grid.Children.Add(scroll);
        grid.Children.Add(_footer);

        Content = new Border
        {
            Background = Bg, BorderBrush = Frozen("#555555"), BorderThickness = new Thickness(1),
            Child = grid,
        };
    }

    // ---------------------------------------------------------------- building

    /// <summary>Rebuilds every row (on open, or when sessions/agents/processes appear or go).</summary>
    public void SetFleet(FleetSnapshot fleet)
    {
        _rows.Children.Clear();
        _meters.Clear();
        _texts.Clear();

        var header = Line(0, "", Clear, $"Claude · {fleet.Count} sessions · {fleet.Busy} busy · {fleet.RunningAgents} agents",
            null, null, Fg, bold: true, primaryWidth: double.NaN);
        _totalBar = new SegmentBar();
        AttachBar(header, _totalBar);
        header.Margin = new Thickness(0, 0, 0, 4);
        _meters["total"] = (_totalBar, header);
        _rows.Children.Add(header);

        foreach (var s in fleet.Sessions)
            _rows.Children.Add(SessionBlock(s, depth: 0));

        FillFooter(fleet.IsMock);
        UpdateCpu(fleet);
    }

    private FrameworkElement SessionBlock(SessionView s, int depth)
    {
        var children = new StackPanel();
        bool expandedByDefault = s.Status == SessionStatus.Busy;
        bool expanded = expandedByDefault ^ _toggled.Contains(s.Pid);
        bool hasChildren = s.Agents.Count > 0 || s.Children.Count > 0 || s.Processes.Any(p => ShowMcp || !p.IsMcp);

        string chevron = !hasChildren ? " " : expanded ? "▾" : "▸";
        var (glyph, glyphBrush) = s.Headless ? ("◆", Head)
                                : s.Status == SessionStatus.Busy ? ("●", Warn) : ("○", Dim);
        string detail = s.Headless ? $"headless · {s.Model}" : FleetFormat.ShortCwd(s.Cwd, ReposRoot);

        var row = Line(depth, glyph, glyphBrush, s.Name, detail, FleetFormat.Age(s.Age), Fg, bold: true, chevron: chevron);
        var bar = new SegmentBar();
        AttachBar(row, bar);
        _meters[$"s{s.Pid}"] = (bar, row);
        Hover(row);
        row.ContextMenu = SessionMenu(s);
        if (hasChildren)
        {
            row.Cursor = Cursors.Hand;
            row.MouseLeftButtonUp += (_, _) =>
            {
                if (!_toggled.Remove(s.Pid)) _toggled.Add(s.Pid);
                bool open = children.Visibility != Visibility.Visible;
                children.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                ((TextBlock)row.Children[0]).Text = open ? "▾" : "▸";
            };
        }

        foreach (var a in s.Agents)
        {
            var (ag, agBrush) = a.State == AgentState.Running ? ("▶", Accent) : ("✓", Done);
            string when = a.State == AgentState.Running ? FleetFormat.Age(a.Elapsed) : "done";
            var line = Line(depth + 1, ag, agBrush, a.Type, $"{a.Description}{(a.Background ? "  (background)" : "")}", when,
                a.State == AgentState.Running ? Fg : Dim, model: a.Model);
            Hover(line);
            children.Children.Add(line);
        }
        foreach (var c in s.Children)
            children.Children.Add(SessionBlock(c, depth + 1));
        foreach (var p in s.Processes.Where(p => ShowMcp || !p.IsMcp))
            AddProcess(children, p, depth + 1, top: true);

        children.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        return new StackPanel { Children = { row, children } };
    }

    private void AddProcess(Panel into, ProcView p, int depth, bool top)
    {
        var line = Line(depth, top ? "⚙" : "·", Dim, p.Label, null, null, p.IsMcp ? Dim : Fg, primaryWidth: double.NaN);
        var bar = new SegmentBar();
        AttachBar(line, bar);
        _meters[$"p{p.Pid}"] = (bar, line);
        Hover(line);
        line.ContextMenu = ProcessMenu(p);
        into.Children.Add(line);
        foreach (var c in p.Children)
            AddProcess(into, c, depth + 1, top: false);
    }

    /// <summary>
    /// One line: [chevron][indent][glyph][primary][secondary (fills)][model][right][meter slot].
    /// Every column but "secondary" has a fixed width, so the line never changes size.
    /// </summary>
    private Grid Line(int depth, string glyph, Brush glyphBrush, string primary, string? secondary, string? right, Brush fg,
                      bool bold = false, string chevron = " ", string? model = null, double primaryWidth = 160)
    {
        var g = new Grid { Background = Clear, Margin = new Thickness(0, 1, 0, 1) };
        void Col(double w) => g.ColumnDefinitions.Add(new ColumnDefinition { Width = double.IsNaN(w) ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
        Col(14);                          // 0 chevron
        Col(depth * Indent);              // 1 indent
        Col(18);                          // 2 glyph
        Col(double.IsNaN(primaryWidth) ? double.NaN : primaryWidth); // 3 primary
        Col(double.IsNaN(primaryWidth) ? 0 : double.NaN);              // 4 secondary
        Col(model is null ? 0 : 52);      // 5 model
        Col(38);                          // 6 right (age / state)
        Col(SegmentBar.SlotWidth);        // 7 meter slot (empty for agents)

        var chev = Add(g, 0, chevron, Dim);
        chev.Margin = new Thickness(0);
        chev.TextTrimming = TextTrimming.None;
        Add(g, 2, glyph, glyphBrush);
        Add(g, 3, primary, fg, bold);
        if (secondary is not null) Add(g, 4, secondary, Dim);
        if (model is not null) Add(g, 5, model, Dim);
        if (right is not null) Add(g, 6, right, Dim, align: TextAlignment.Right).Margin = new Thickness(0, 0, 8, 0);
        return g;
    }

    private TextBlock Add(Grid g, int col, string text, Brush fg, bool bold = false, TextAlignment align = TextAlignment.Left)
    {
        var tb = new TextBlock
        {
            Text = text, Foreground = fg, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = align,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        Grid.SetColumn(tb, col);
        g.Children.Add(tb);
        _texts.Add(tb);
        return tb;
    }

    private static void AttachBar(Grid line, SegmentBar bar)
    {
        Grid.SetColumn(bar, 7);
        line.Children.Add(bar);
    }

    private static void Hover(Panel row)
    {
        row.MouseEnter += (_, _) => row.Background = HlRow;
        row.MouseLeave += (_, _) => row.Background = Clear;
    }

    // ---------------------------------------------------------------- live update

    /// <summary>Applies CPU to every meter by key. Colour/segments and tooltips only: no text, no size changes.</summary>
    public void UpdateCpu(FleetSnapshot fleet)
    {
        Set("total", fleet.TotalCpu, fleet.Sessions.Sum(s => s.TreeMemory), null);
        foreach (var s in fleet.All())
        {
            Set($"s{s.Pid}", s.TreeCpu, s.TreeMemory, s.Pid);
            foreach (var p in s.Processes) Walk(p);
        }

        void Walk(ProcView p)
        {
            Set($"p{p.Pid}", p.TreeCpu, p.TreeMemory, p.Pid);
            foreach (var c in p.Children) Walk(c);
        }
        void Set(string key, double cpu, long mem, int? pid)
        {
            if (!_meters.TryGetValue(key, out var m)) return;
            m.Bar.SetLevel(FleetFormat.MeterLevel(cpu));
            m.Row.ToolTip = pid is int id ? FleetFormat.Detail(cpu, mem, id) : $"CPU {cpu:0.0}% · {FleetFormat.Memory(mem)}";
        }
    }

    /// <summary>Lit segments of a meter ("total", "s&lt;pid&gt;", "p&lt;pid&gt;"), or -1 if absent (tests).</summary>
    public int MeterLevelOf(string key) => _meters.TryGetValue(key, out var m) ? m.Bar.Level : -1;

    /// <summary>Every character of text in the flyout, in order (tests prove CPU updates never change it).</summary>
    public string AllText() => string.Join("|", _texts.Select(t => t.Text)) + "|" + _footer.Text;

    // ---------------------------------------------------------------- menus / footer

    private ContextMenu? SessionMenu(SessionView s)
    {
        if (OnAction is null) return null;
        var m = DarkMenu();
        m.Items.Add(Item("Focus terminal", () => OnAction("focus", s)));
        m.Items.Add(Item($"Copy  claude --resume {s.SessionId[..8]}…", () => OnAction("resume", s)));
        m.Items.Add(Item("Open folder", () => OnAction("folder", s)));
        m.Items.Add(Item("Open transcript", () => OnAction("transcript", s)));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Kill process tree…", () => OnAction("kill", s)));
        return m;
    }

    private ContextMenu? ProcessMenu(ProcView p)
    {
        if (OnAction is null) return null;
        var m = DarkMenu();
        m.Items.Add(Item("Copy command line", () => OnAction("copycmd", p)));
        m.Items.Add(new Separator());
        m.Items.Add(Item("Kill process tree…", () => OnAction("kill", p)));
        return m;
    }

    private static ContextMenu DarkMenu() => new()
    {
        Background = Bg, Foreground = Fg, BorderBrush = Frozen("#555555"), FontFamily = new FontFamily("Segoe UI"), FontSize = 12,
    };

    private static MenuItem Item(string header, Action onClick)
    {
        var item = new MenuItem { Header = header, Foreground = Fg, Background = Bg };
        item.Click += (_, _) => onClick();
        return item;
    }

    private void FillFooter(bool isMock)
    {
        _footer.Inlines.Clear();
        void Key(string glyph, Brush b, string label)
        {
            _footer.Inlines.Add(new System.Windows.Documents.Run(glyph + " ") { Foreground = b });
            _footer.Inlines.Add(label + "   ");
        }
        Key("●", Warn, "busy");
        Key("○", Dim, "idle");
        Key("▶", Accent, "agent running");
        Key("✓", Done, "done");
        Key("◆", Head, "headless");
        Key("⚙", Dim, "process");
        if (isMock)
            _footer.Inlines.Add(new System.Windows.Documents.Run("MOCK DATA") { Foreground = Warn, FontWeight = FontWeights.SemiBold });
    }

    private static string ReposRoot => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Repos");

    // ---------------------------------------------------------------- placement / diagnostics

    public void ShowNear(System.Drawing.Point cursorPx) => TrayPopupPlacement.ShowAbove(this, cursorPx);

    public System.Drawing.Rectangle WindowRectPx() => TrayPopupPlacement.WindowRect(this);

    /// <summary>Renders the content to a PNG without showing the window (for agents/tests).</summary>
    public void RenderToPng(string path)
    {
        var root = (FrameworkElement)Content;
        root.Measure(new Size(FlyoutWidth, double.PositiveInfinity));
        root.Arrange(new Rect(new Size(FlyoutWidth, root.DesiredSize.Height)));
        root.UpdateLayout();
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(FlyoutWidth), (int)Math.Ceiling(root.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = System.IO.File.Create(path);
        enc.Save(fs);
    }

    /// <summary>Desired size of the content at the flyout width (tests compare it before/after updates).</summary>
    public Size MeasureContent()
    {
        var root = (FrameworkElement)Content;
        root.Measure(new Size(FlyoutWidth, double.PositiveInfinity));
        return root.DesiredSize;
    }

    /// <summary>Captures the live window via PrintWindow (works unfocused / occluded). For diagnostics.</summary>
    public void CaptureWindowPng(string path)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        GetWindowRect(hwnd, out var r);
        using var bmp = new System.Drawing.Bitmap(r.R - r.L, r.B - r.T);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            PrintWindow(hwnd, hdc, 2); // PW_RENDERFULLCONTENT
            g.ReleaseHdc(hdc);
        }
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static SolidColorBrush Frozen(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);

    /// <summary>Fixed-size 5-segment meter; only segment colours change.</summary>
    private sealed class SegmentBar : StackPanel
    {
        public const double SlotWidth = 44;
        private readonly Rectangle[] _segs = new Rectangle[FleetFormat.MeterSteps];
        public int Level { get; private set; } = -1;

        public SegmentBar()
        {
            Orientation = Orientation.Horizontal;
            VerticalAlignment = VerticalAlignment.Center;
            HorizontalAlignment = HorizontalAlignment.Right;
            for (int i = 0; i < _segs.Length; i++)
            {
                _segs[i] = new Rectangle { Width = 6, Height = 9, Margin = new Thickness(1, 0, 1, 0), Fill = Unlit };
                Children.Add(_segs[i]);
            }
            SetLevel(0);
        }

        public void SetLevel(int level)
        {
            if (level == Level) return;
            Level = level;
            var lit = Frozen(FleetFormat.MeterColor(level));
            for (int i = 0; i < _segs.Length; i++) _segs[i].Fill = i < level ? lit : Unlit;
        }
    }
}
