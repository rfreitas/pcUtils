using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Shared;

namespace PortWatch;

/// <summary>
/// Click-to-open flyout: one line per process, ports as hoverable tokens. Hovering a port
/// highlights every process bound to it; shared ports carry a "+N" badge (amber for TCP,
/// where sharing is unusual); system processes get their own colour. Dismissal (click-away / Escape) comes from TrayFlyoutWindow.
/// </summary>
internal sealed class PortPopup : TrayFlyoutWindow
{
    private static readonly System.Windows.Media.Brush Bg     = Frozen("#2d2d2d");
    private static readonly System.Windows.Media.Brush Fg     = Frozen("#e6e6e6");
    private static readonly System.Windows.Media.Brush SysFg  = Frozen("#c39bff");
    private static readonly System.Windows.Media.Brush Dim    = Frozen("#888888");
    private static readonly System.Windows.Media.Brush PortFg = Frozen("#7fc8ff");
    private static readonly System.Windows.Media.Brush Warn   = Frozen("#ffb454");
    private static readonly System.Windows.Media.Brush HlRow  = Frozen("#3b4a5e");
    private static readonly System.Windows.Media.Brush HlSrc  = Frozen("#4f6680");
    private static readonly System.Windows.Media.Brush Clear  = System.Windows.Media.Brushes.Transparent;
    private static readonly System.Windows.Media.Brush InFg   = Frozen("#6fcf97");   // receiving
    private static readonly System.Windows.Media.Brush OutFg  = Frozen("#ff8fa3");   // sending
    private static readonly System.Windows.Media.Brush Active = Frozen("#ffffff");   // a port with live traffic

    private sealed record Token(string Protocol, PortSegment Segment, System.Windows.Documents.Run Port, System.Windows.Documents.Run In, System.Windows.Documents.Run Out, ActivityLatch Latch);
    private sealed record RowView(ProcessRow Data, Border Container, List<Token> Tokens, System.Windows.Documents.Run In, System.Windows.Documents.Run Out, ActivityLatch Latch);

    private readonly StackPanel _rows = new();
    private readonly TextBlock _footer = new() { Margin = new Thickness(10, 4, 10, 6), Foreground = Dim, FontSize = 11 };
    private readonly List<RowView> _views = new();

    /// <summary>How long an arrow stays lit after its traffic stops.</summary>
    public const long HoldMs = 2000;

    /// <summary>Where arrows are drawn. Read by <see cref="SetRows"/>, so set it before the rows are built.</summary>
    public ArrowTarget Target { get; set; } = ArrowTarget.Port;

    private readonly Func<long> _clockMs;

    public PortPopup(Func<long>? clockMs = null)
    {
        _clockMs = clockMs ?? (() => Environment.TickCount64);
        // TrayFlyoutWindow already sets WindowStyle/ResizeMode/Topmost/ShowInTaskbar/Manual location.
        SizeToContent = SizeToContent.WidthAndHeight;
        MaxHeight     = 420;
        Background    = Bg;
        FontFamily    = new System.Windows.Media.FontFamily("Segoe UI");
        FontSize      = 12;
        // Off-screen until ShowNear repositions it, so there is no flash at WPF's default spot.
        Left = -10000;
        Top  = -10000;
        new WindowInteropHelper(this).EnsureHandle();   // pay window-creation cost up front, not during a hover

        FillFooter(null);

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

    public void SetRows(IReadOnlyList<ProcessRow> rows)
    {
        _rows.Children.Clear();
        _views.Clear();
        var counts = PortGrouper.ProcessCounts(rows);

        foreach (var r in rows)
        {
            var line = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 440, Foreground = Fg };
            line.Inlines.Add(new System.Windows.Documents.Run(r.Name)
            {
                FontWeight = FontWeights.SemiBold,
                Foreground = r.IsSystem ? SysFg : Fg,
            });
            string pids = r.Pids.Count == 1 ? $" ({r.Pids[0]})" : $" (\u00D7{r.Pids.Count})";
            line.Inlines.Add(new System.Windows.Documents.Run(pids) { Foreground = Dim });

            // Arrows live on EITHER the process or its ports (never both). Only the chosen level reserves slots,
            // so the other stays compact; the choice is fixed while the flyout is open, so nothing ever moves.
            var inRun  = Slot("\u2193");
            var outRun = Slot("\u2191");
            if (Target == ArrowTarget.Process)
            {
                line.Inlines.Add(" ");
                line.Inlines.Add(inRun);
                line.Inlines.Add(outRun);
            }

            var view = new RowView(r, new Border { Padding = new Thickness(4, 2, 4, 2), Child = line, Background = Clear },
                new List<Token>(), inRun, outRun, new ActivityLatch());
            AddPorts(line, view, "TCP", r.Tcp, counts);
            AddPorts(line, view, "UDP", r.Udp, counts);
            _rows.Children.Add(view.Container);
            _views.Add(view);
        }
    }

    private void AddPorts(TextBlock line, RowView view, string proto, IReadOnlyList<PortSegment> segs, Dictionary<(string Protocol, int Port), int> counts)
    {
        if (segs.Count == 0) return;
        line.Inlines.Add(new System.Windows.Documents.Run($"   {proto} ") { Foreground = Dim });
        for (int i = 0; i < segs.Count; i++)
        {
            var seg = segs[i];
            var run = new System.Windows.Documents.Run(seg.Text) { Foreground = PortFg };
            var inArrow  = Slot("\u2193");
            var outArrow = Slot("\u2191");
            view.Tokens.Add(new Token(proto, seg, run, inArrow, outArrow, new ActivityLatch()));
            run.MouseEnter += (_, _) => Highlight(proto, seg.Ports, view);
            run.MouseLeave += (_, _) => ClearHighlight();

            // Arrow slot first, then the number: commas stay attached to their port and the reserved gap sits
            // between "UDP" and the number instead of inside the list.
            if (Target == ArrowTarget.Port)
            {
                line.Inlines.Add(inArrow);
                line.Inlines.Add(outArrow);
                line.Inlines.Add(" ");
            }
            line.Inlines.Add(run);

            int others = PortGrouper.OtherSharers(proto, seg, counts);
            if (others > 0)
                line.Inlines.Add(new System.Windows.Documents.Run($"+{others}") { Foreground = proto == "TCP" ? Warn : Dim, FontSize = 10 });
            if (i < segs.Count - 1) line.Inlines.Add(", ");
        }
    }

    private void Highlight(string proto, IReadOnlyList<int> ports, RowView source)
    {
        foreach (var v in _views)
        {
            var segs = proto == "TCP" ? v.Data.Tcp : v.Data.Udp;
            bool match = segs.Any(s => s.Ports.Any(ports.Contains));
            v.Container.Background = v == source ? HlSrc : match ? HlRow : Clear;
        }
    }

    private void ClearHighlight()
    {
        foreach (var v in _views) v.Container.Background = Clear;
    }

    /// <summary>Footer legend; with <paramref name="trafficError"/> set, says why live traffic is missing instead.</summary>
    public void SetTrafficStatus(string? trafficError) => FillFooter(trafficError);

    private void FillFooter(string? trafficError)
    {
        _footer.Inlines.Clear();
        _footer.Inlines.Add(new System.Windows.Documents.Run("\u25A0 ") { Foreground = SysFg });
        _footer.Inlines.Add("system   ");
        _footer.Inlines.Add(new System.Windows.Documents.Run("+N ") { Foreground = Warn });
        _footer.Inlines.Add("shared   ");
        if (trafficError is null)
        {
            _footer.Inlines.Add(new System.Windows.Documents.Run("\u2193 ") { Foreground = InFg });
            _footer.Inlines.Add("receiving   ");
            _footer.Inlines.Add(new System.Windows.Documents.Run("\u2191 ") { Foreground = OutFg });
            _footer.Inlines.Add("sending   ");
            _footer.Inlines.Add(new System.Windows.Documents.Run("white") { Foreground = Active });
            _footer.Inlines.Add(" both");
        }
        else
        {
            _footer.Inlines.Add(new System.Windows.Documents.Run(trafficError) { Foreground = Warn });
        }
    }

    /// <summary>
    /// Applies live traffic with colour only, on the level chosen by <see cref="Target"/>: that level's reserved
    /// ↓ / ↑ slots light up (and, for ports, the number turns green / pink / white). Each arrow then stays lit
    /// for <see cref="HoldMs"/> after its traffic stops. No text changes, so no layout shift.
    /// </summary>
    public void UpdateTraffic(TrafficSnapshot snapshot)
    {
        long now = _clockMs();
        foreach (var v in _views)
        {
            if (Target == ArrowTarget.Process)
            {
                var (inLit, outLit) = v.Latch.Observe(snapshot.ForProcess(v.Data.Pids), now, HoldMs);
                Light(v.In, v.Out, inLit, outLit);
                continue;
            }

            foreach (var t in v.Tokens)
            {
                var (inLit, outLit) = t.Latch.Observe(snapshot.ForPorts(t.Protocol, v.Data.Pids, t.Segment.Ports), now, HoldMs);
                Light(t.In, t.Out, inLit, outLit);
                t.Port.Foreground = inLit && outLit ? Active : inLit ? InFg : outLit ? OutFg : PortFg;
            }
        }
    }

    /// <summary>Every character of text in the list, in order (tests use it to prove traffic never changes the text).</summary>
    public string AllText() => string.Concat(_views.SelectMany(v =>
        ((TextBlock)v.Container.Child).Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text)));

    /// <summary>"in", "out", "both" or "idle" for a whole process; "off" when arrows are on ports (diagnostics / tests).</summary>
    public string ProcessActivity(string processName)
    {
        if (Target != ArrowTarget.Process) return "off";
        var v = _views.FirstOrDefault(x => x.Data.Name.Equals(processName, StringComparison.OrdinalIgnoreCase));
        return v is null ? "missing" : ReadState(v.In, v.Out);
    }

    /// <summary>"in", "out", "both" or "idle" for one port of a process; "off" when arrows are on processes (diagnostics / tests).</summary>
    public string PortActivity(string processName, string protocol, int port)
    {
        if (Target != ArrowTarget.Port) return "off";
        var v = _views.FirstOrDefault(x => x.Data.Name.Equals(processName, StringComparison.OrdinalIgnoreCase));
        var t = v?.Tokens.FirstOrDefault(x => x.Protocol == protocol && x.Segment.Ports.Contains(port));
        return t is null ? "missing" : ReadState(t.In, t.Out);
    }

    /// <summary>The colour a port number is currently drawn in (diagnostics / tests).</summary>
    public System.Windows.Media.Brush? PortBrush(string processName, string protocol, int port) =>
        _views.FirstOrDefault(x => x.Data.Name.Equals(processName, StringComparison.OrdinalIgnoreCase))
              ?.Tokens.FirstOrDefault(x => x.Protocol == protocol && x.Segment.Ports.Contains(port))?.Port.Foreground;

    public static System.Windows.Media.Brush InBrush   => InFg;
    public static System.Windows.Media.Brush OutBrush  => OutFg;
    public static System.Windows.Media.Brush BothBrush => Active;
    public static System.Windows.Media.Brush IdleBrush => PortFg;

    private static string ReadState(System.Windows.Documents.Run inArrow, System.Windows.Documents.Run outArrow) =>
        (inArrow.Foreground == InFg, outArrow.Foreground == OutFg) switch
        {
            (true, true)   => "both",
            (true, false)  => "in",
            (false, true)  => "out",
            _              => "idle",
        };

    /// <summary>Same code path as hovering a port; lets agents/tests render the highlighted state.</summary>
    public bool HighlightPort(string protocol, int port)
    {
        foreach (var v in _views)
        {
            var tok = v.Tokens.FirstOrDefault(t => t.Protocol == protocol && t.Segment.Ports.Contains(port));
            if (tok is null) continue;
            Highlight(protocol, tok.Segment.Ports, v);
            return true;
        }
        return false;
    }

    /// <summary>Shows above the taskbar, anchored to the cursor (shared placement: never under the bar).</summary>
    public void ShowNear(System.Drawing.Point cursorPx) => TrayPopupPlacement.ShowAbove(this, cursorPx);

    /// <summary>Live window rectangle in physical pixels (diagnostics / tests).</summary>
    public System.Drawing.Rectangle WindowRectPx() => TrayPopupPlacement.WindowRect(this);

    /// <summary>Renders the popup content to a PNG without showing the window (for agents/tests).</summary>
    public void RenderToPng(string path)
    {
        var root = (FrameworkElement)Content;
        root.Measure(new System.Windows.Size(double.PositiveInfinity, MaxHeight));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(root.DesiredSize.Width), (int)Math.Ceiling(root.DesiredSize.Height), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = System.IO.File.Create(path);
        enc.Save(fs);
    }

    /// <summary>Captures the live window via PrintWindow (works unfocused / occluded). For diagnostics.</summary>
    public void CaptureWindowPng(string path)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        GetWindowRect(hwnd, out var r);
        int w = r.R - r.L, h = r.B - r.T;
        using var bmp = new System.Drawing.Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            PrintWindow(hwnd, hdc, 2); // PW_RENDERFULLCONTENT
            g.ReleaseHdc(hdc);
        }
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    /// <summary>
    /// An arrow slot. It is always laid out and only its colour changes (transparent when idle), so
    /// traffic starting or stopping never changes any text width and nothing in the list moves.
    /// </summary>
    private static System.Windows.Documents.Run Slot(string glyph) => new(glyph) { Foreground = Clear, FontWeight = FontWeights.Bold };

    private static void Light(System.Windows.Documents.Run inArrow, System.Windows.Documents.Run outArrow, bool inLit, bool outLit)
    {
        inArrow.Foreground  = inLit  ? InFg  : Clear;
        outArrow.Foreground = outLit ? OutFg : Clear;
    }

    private static SolidColorBrush Frozen(string hex)
    {
        var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
}
