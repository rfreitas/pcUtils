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

    private sealed record Token(string Protocol, PortSegment Segment);
    private sealed record RowView(ProcessRow Data, Border Container, List<Token> Tokens);

    private readonly StackPanel _rows = new();
    private readonly List<RowView> _views = new();

    public PortPopup()
    {
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

        var footer = new TextBlock { Margin = new Thickness(10, 4, 10, 6), Foreground = Dim, FontSize = 11 };
        footer.Inlines.Add(new System.Windows.Documents.Run("■ ") { Foreground = SysFg });
        footer.Inlines.Add("system   ");
        footer.Inlines.Add(new System.Windows.Documents.Run("+N ") { Foreground = Warn });
        footer.Inlines.Add("TCP port shared   +N UDP shared   hover a port to highlight");

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(6, 6, 6, 4),
            Content = _rows,
        };
        Grid.SetRow(footer, 1);
        grid.Children.Add(scroll);
        grid.Children.Add(footer);

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
            string pids = r.Pids.Count == 1 ? $" ({r.Pids[0]})" : $" (×{r.Pids.Count})";
            line.Inlines.Add(new System.Windows.Documents.Run(pids) { Foreground = Dim });

            var view = new RowView(r, new Border { Padding = new Thickness(4, 2, 4, 2), Child = line, Background = Clear }, new List<Token>());
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
            view.Tokens.Add(new Token(proto, seg));
            run.MouseEnter += (_, _) => Highlight(proto, seg.Ports, view);
            run.MouseLeave += (_, _) => ClearHighlight();
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
