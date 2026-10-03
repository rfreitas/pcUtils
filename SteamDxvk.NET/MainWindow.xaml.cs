using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SteamDxvk;

/// <summary>
/// Game list on top, per-game DXVK controls below, log at the bottom. Palette and font follow
/// Shared.NET/TRAY_APP_UX.md. The list is plain Borders in a ScrollViewer (like PortWatch's flyout) rather than a
/// templated ListView, so it stays trivially dark and renderable.
/// </summary>
internal partial class MainWindow : Window
{
    private enum Col { Game, Api, Bits, Engine, Dxvk, Notes }

    private static readonly (Col Id, string Title, GridLength Width)[] Columns =
    [
        // Every column but the last is fixed and the last one flexes, so column x-positions are identical in the header
        // and in the rows whatever width the scrollbar takes (a star column in the middle shifted everything after it).
        (Col.Game, "Game", new GridLength(300)),
        (Col.Api, "Graphics API", new GridLength(250)),
        (Col.Bits, "Bits", new GridLength(44)),
        (Col.Engine, "Engine", new GridLength(70)),
        (Col.Dxvk, "DXVK", new GridLength(190)),
        (Col.Notes, "Notes", new GridLength(1, GridUnitType.Star)),
    ];

    private static readonly Brush Fg = Frozen("#e6e6e6");
    private static readonly Brush Dim = Frozen("#888888");
    private static readonly Brush Accent = Frozen("#7fc8ff");
    private static readonly Brush Warn = Frozen("#ffb454");
    private static readonly Brush Good = Frozen("#6fcf97");
    private static readonly Brush HoverBg = Frozen("#3b4a5e");
    private static readonly Brush SelectedBg = Frozen("#4f6680");
    private static readonly Brush Clear = Brushes.Transparent;

    /// <summary>Remembered per-game choices (Run as). Replaceable so tests never write the real settings file.</summary>
    internal GameSettings Settings { get; set; } = new();

    private readonly List<GameScan> _scans = [];
    private readonly Dictionary<int, Border> _rowBorders = [];
    private readonly Dictionary<Col, TextBlock> _headers = [];
    private readonly DispatcherTimer _refreshTimer;
    private GameScan? _selected;
    private Col _sortCol = Col.Game;
    private bool _sortAscending = true;
    private bool _scanning, _busy, _populating, _dirty;

    public MainWindow()
    {
        InitializeComponent();
        // Rows sit inside a 1 px border with 8 px padding; mirror the left offset so header text lines up with row text.
        HeaderGrid.Margin = new Thickness(19, 0, 19, 2);
        BuildHeader();

        FilterBox.ItemsSource = GameFilter.Options;
        FilterBox.SelectedIndex = 0;
        GplBox.ItemsSource = new[] { "Auto", "True", "False" };
        GplBox.SelectedIndex = 0;

        FilterBox.SelectionChanged += (_, _) => RefreshRows();
        SearchBox.TextChanged += (_, _) => RefreshRows();
        ExeBox.SelectionChanged += (_, _) => { if (!_populating) { SyncDxvkOptions(); UpdateActions(); HealImpossibleInstall(); } };
        RescanBtn.Click += (_, _) => StartScan();
        FetchBtn.Click += OnFetch;
        OpenBtn.Click += OnOpen;
        LaunchBtn.Click += async (_, _) => await LaunchAsync();
        CheckBtn.Click += (_, _) => CheckLastRun();
        RunBox.SelectionChanged += async (_, _) => await OnRunAsChangedAsync();
        // Click (not Checked) so only the user's own clicks apply changes; programmatic updates never loop back here.
        DxvkBox.Click += async (_, _) => await OnDxvkTickedAsync();
        AsyncBox.Click += async (_, _) => await ReapplyAsync();
        HudBox.Click += async (_, _) => await ReapplyAsync();
        GplBox.SelectionChanged += async (_, _) => { if (!_populating) await ReapplyAsync(); };
        Loaded += (_, _) =>
        {
            if (_scans.Count == 0) StartScan();
            StartMonitoring();
        };

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _refreshTimer.Tick += (_, _) => { if (_dirty) { _dirty = false; RefreshRows(); } };
        UpdateActions();
    }

    // ------------------------------------------------------- public (tests, headless flags)

    /// <summary>
    /// For diagnostic flags (--render, --detect, --check, --assess): selecting a game must not tidy up its folder or settings.
    /// A look at the state must never change the state.
    /// </summary>
    internal bool ReadOnly { get; set; }

    public bool IsScanning => _scanning;
    public int ScanCount => _scans.Count;
    public bool DxvkEnabled => DxvkBox.IsEnabled;
    public GameScan? Selected => _selected;

    public void LoadScans(IEnumerable<GameScan> scans)
    {
        _scans.Clear();
        _scans.AddRange(scans);
        RefreshRows();
    }

    public bool SelectByName(string fragment)
    {
        var match = _scans.FirstOrDefault(s => s.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase));
        if (match is null) return false;
        Select(match);
        if (_rowBorders.TryGetValue(match.AppId, out var row))
            Dispatcher.InvokeAsync(row.BringIntoView, DispatcherPriority.Loaded);   // after layout, so the ScrollViewer can scroll to it
        return true;
    }

    /// <summary>Runs queued dispatcher work so layout/popups settle (headless checks).</summary>
    public static void Pump() => Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));

    /// <summary>Opens every dropdown once: fails fast if a combo template is broken.</summary>
    public void ExerciseControls()
    {
        foreach (var box in new[] { ExeBox, GplBox, FilterBox, RunBox })
        {
            box.IsDropDownOpen = true;
            Pump();
            box.IsDropDownOpen = false;
            Pump();
        }
    }

    /// <summary>Lays the window out at its normal size without showing it, so positions can be measured (tests, renders).</summary>
    internal void LayoutNow()
    {
        double w = double.IsNaN(Width) ? 1180 : Width, h = double.IsNaN(Height) ? 900 : Height;
        Root.Measure(new Size(w, h));
        Root.Arrange(new Rect(0, 0, w, h));
        Root.UpdateLayout();
    }

    /// <summary>Renders the whole window to a PNG without showing it (for agents/tests). Always view the result.</summary>
    public void RenderToPng(string path)
    {
        double w = double.IsNaN(Width) ? 1180 : Width, h = double.IsNaN(Height) ? 900 : Height;
        LayoutNow();
        if (_selected is not null && _rowBorders.TryGetValue(_selected.AppId, out var row))
        {
            row.BringIntoView();   // a selected game further down the list must be visible in the render
            Root.UpdateLayout();
        }
        var bmp = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(Root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    // ---------------------------------------------------------------------------- window

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        int on = 1;   // DWMWA_USE_IMMERSIVE_DARK_MODE: dark title bar to match the client area
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, sizeof(int));
    }

    // -------------------------------------------------------------------------- scanning

    private void StartScan()
    {
        if (_scanning) return;
        _scanning = true;
        _scans.Clear();
        _selected = null;
        RefreshRows();
        PopulateDetail();
        Log("Scanning Steam libraries ...");
        _refreshTimer.Start();
        UpdateActions();

        Task.Run(() =>
        {
            try
            {
                LibraryScanner.ScanAll(
                    onTotal: n => Dispatcher.InvokeAsync(() => { Progress.Maximum = Math.Max(n, 1); Progress.Value = 0; }),
                    onResult: s => Dispatcher.InvokeAsync(() => { _scans.Add(s); Progress.Value++; _dirty = true; }));
            }
            catch (Exception e)
            {
                Logger.Log($"Scan failed: {e}");
                Log($"ERROR: {e.Message}");
            }
            finally
            {
                // Queued after every per-game InvokeAsync above, so the list is complete when this runs.
                Dispatcher.InvokeAsync(() =>
                {
                    _scanning = false;
                    _refreshTimer.Stop();
                    Progress.Value = 0;
                    RefreshRows();
                    Log($"Scanned {_scans.Count} games.");
                    UpdateActions();
                });
            }
        });
    }

    // ------------------------------------------------------------------------------ list

    private void BuildHeader()
    {
        foreach (var (id, title, width) in Columns)
        {
            HeaderGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = width });
            var header = new TextBlock
            {
                Text = title, Foreground = Dim, FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand, Background = Clear,
            };
            var col = id;
            header.MouseLeftButtonUp += (_, _) => SortBy(col);
            Grid.SetColumn(header, HeaderGrid.ColumnDefinitions.Count - 1);
            HeaderGrid.Children.Add(header);
            _headers[id] = header;
        }
    }

    private void SortBy(Col col)
    {
        _sortAscending = _sortCol != col || !_sortAscending;
        _sortCol = col;
        RefreshRows();
    }

    private static string SortKey(Col col, GameScan s) => col switch
    {
        Col.Game => s.Name,
        Col.Api => GameScanner.ApiSummary(s.Primary),
        Col.Bits => (s.Primary?.Bits ?? 0).ToString("D3"),
        Col.Engine => s.Engine,
        Col.Dxvk => s.Status?.Label ?? "",
        _ => NoteText(s),
    };

    private void RefreshRows()
    {
        string filter = FilterBox.SelectedItem as string ?? "All", search = SearchBox.Text.Trim();
        var visible = _scans.Where(s => GameFilter.Matches(s, filter, search));
        var ordered = (_sortAscending
                ? visible.OrderBy(s => SortKey(_sortCol, s), StringComparer.OrdinalIgnoreCase)
                : visible.OrderByDescending(s => SortKey(_sortCol, s), StringComparer.OrdinalIgnoreCase))
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase);

        Rows.Children.Clear();
        _rowBorders.Clear();
        foreach (var scan in ordered) Rows.Children.Add(BuildRow(scan));

        foreach (var (id, header) in _headers)
        {
            string title = Columns.First(c => c.Id == id).Title;
            header.Text = id == _sortCol ? $"{title} {(_sortAscending ? "▲" : "▼")}" : title;
        }
    }

    private Border BuildRow(GameScan s)
    {
        var grid = new Grid();
        foreach (var column in Columns) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = column.Width });

        void Add(int col, TextBlock tb)
        {
            Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
        }

        var margin = new Thickness(0, 0, 8, 0);
        var nameBlock = new TextBlock { Foreground = Fg, TextTrimming = TextTrimming.CharacterEllipsis, Margin = margin };
        if (RunningSession(s.AppId) is { } running)
        {
            // Green dot while running; amber with a warning glyph when it isn't on the API that was asked for.
            bool mismatch = ApiDetector.Mismatch(running.Requested, running.Api) is not null;
            nameBlock.Inlines.Add(new System.Windows.Documents.Run(mismatch ? "⚠ " : "● ") { Foreground = mismatch ? Warn : Good });
        }
        nameBlock.Inlines.Add(new System.Windows.Documents.Run(s.Name));
        Add(0, nameBlock);
        Add(1, ApiBlock(s.Primary, margin));
        Add(2, new TextBlock { Text = s.Primary?.Bits.ToString() ?? "?", Foreground = Dim });
        Add(3, new TextBlock { Text = s.Engine, Foreground = Dim });
        Add(4, new TextBlock { Text = s.Status?.Label ?? "", Foreground = Good, TextTrimming = TextTrimming.CharacterEllipsis, Margin = margin });

        string note = NoteText(s);
        var runningNow = RunningSession(s.AppId);
        if (runningNow is not null && runningNow.Api.Api != GfxApi.None) note = $"running: {runningNow.Api.Label}" + (note.Length > 0 ? "; " + note : "");
        Add(5, new TextBlock
        {
            Text = note, Foreground = runningNow is not null ? Good : s.AntiCheat.Count > 0 ? Warn : Dim,
            TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = note.Length > 0 ? note : null,
        });

        bool selected = _selected?.AppId == s.AppId;
        var border = new Border { Padding = new Thickness(8, 3, 8, 3), Background = selected ? SelectedBg : Clear, Child = grid, Cursor = Cursors.Hand };
        border.MouseEnter += (_, _) => { if (_selected?.AppId != s.AppId) border.Background = HoverBg; };
        border.MouseLeave += (_, _) => { if (_selected?.AppId != s.AppId) border.Background = Clear; };
        border.MouseLeftButtonDown += (_, _) => Select(s);
        _rowBorders[s.AppId] = border;
        return border;
    }

    /// <summary>Linked APIs in the accent colour, string-only mentions dimmed after a plus (see GameScanner.ApiSummary).</summary>
    private static TextBlock ApiBlock(ExeInfo? exe, Thickness margin)
    {
        var block = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, Margin = margin };
        if (exe is null || exe.Apis == GfxApi.None)
        {
            block.Inlines.Add(new System.Windows.Documents.Run("?") { Foreground = Dim });
            return block;
        }
        string linked = GfxApis.Join(exe.Imports), mentioned = GfxApis.Join(exe.Apis & ~exe.Imports);
        if (linked.Length > 0) block.Inlines.Add(new System.Windows.Documents.Run(linked) { Foreground = Accent });
        if (mentioned.Length > 0)
            block.Inlines.Add(new System.Windows.Documents.Run(linked.Length > 0 ? $" (+{mentioned})" : $"({mentioned})") { Foreground = Dim });
        block.ToolTip = "Plain: linked at load time. (+...): only mentioned in strings (loaded dynamically, or just referenced by a library).";
        return block;
    }

    private static string NoteText(GameScan s)
    {
        var parts = new List<string>();
        if (s.Primary is { } p && p.Apis.HasFlag(GfxApi.D3D12) && p.Apis.Overlaps(GfxApis.Supported)) parts.Add("also has D3D12");
        if (s.AntiCheat.Count > 0) parts.Add("⚠ anti-cheat: " + string.Join(", ", s.AntiCheat));
        if (s.Error.Length > 0) parts.Add(s.Error);
        return string.Join("; ", parts);
    }

    // ----------------------------------------------------------------------------- detail

    private void Select(GameScan? scan)
    {
        _selected = scan;
        foreach (var (id, border) in _rowBorders) border.Background = scan?.AppId == id ? SelectedBg : Clear;
        AdoptExistingInstall();
        PopulateDetail();
        HealImpossibleInstall();
    }

    /// <summary>
    /// A DXVK this app installed before any choice was remembered counts as the user having chosen it: otherwise removing it later
    /// (because Run as D3D12 can't use it) would also forget that they wanted it.
    /// </summary>
    private void AdoptExistingInstall()
    {
        if (ReadOnly) return;
        if (_selected is { } scan && Settings.GetUseDxvk(scan.AppId) is null &&
            scan.Exes.Any(e => DxvkInstaller.ReadManifest(e.Dir) is not null))
            Settings.SetUseDxvk(scan.AppId, true);
    }

    /// <summary>Whether DXVK is wanted for the game: the remembered choice, or (never decided) whatever is installed.</summary>
    private bool WantsDxvk(GameScan scan, DxvkStatus? installed) => Settings.GetUseDxvk(scan.AppId) ?? installed is not null;

    /// <summary>
    /// A DXVK this app installed that can't be used with the game's current setup (Run as D3D12 or Vulkan saved earlier, or a game
    /// only found to use D3D12) would crash it or do nothing, and the tick is greyed so it can't be undone by hand. The files are
    /// removed (the remembered choice is kept, so DXVK returns when it can be used again). Hand-made installs are never touched.
    /// </summary>
    private void HealImpossibleInstall()
    {
        if (ReadOnly || _busy || _selected is not { } scan || CurrentExe() is not { } exe) return;
        if (DxvkInstaller.ReadManifest(exe.Dir) is null) return;
        var plan = InstallPlanner.Plan(scan, exe, SelectedRunMode());
        if (plan.CanInstall) return;

        Log($"DXVK is installed for {scan.Name} but can't be used here ({plan.Blocked}), so it is switched off for now. Your choice is remembered.");
        _ = UninstallAsync(scan, exe);
    }

    private void PopulateDetail()
    {
        _populating = true;
        try
        {
            var s = _selected;
            DetailTitle.Text = s?.Name ?? "Select a game";
            DetailPath.Text = s?.Root ?? "";
            ExeBox.ItemsSource = s?.Exes.Select(e => $"{Path.GetRelativePath(s.Root, e.Path)}   [{GameScanner.ApiSummary(e)}]  {e.Bits}-bit").ToList();
            ExeBox.SelectedIndex = s?.Exes.Count > 0 ? 0 : -1;
            // Only modes the app can apply for this engine are offered; a saved choice that no longer fits falls back to Default.
            RefreshRunChoices();
            SyncDxvkOptions();
        }
        finally { _populating = false; }
        UpdateActions();
    }

    /// <summary>
    /// Fills the DXVK option controls: what is installed if DXVK is in this folder, else what was last used for this game,
    /// else the defaults. Programmatic: never applies anything.
    /// </summary>
    private void SyncDxvkOptions()
    {
        bool wasPopulating = _populating;
        _populating = true;
        try
        {
            var options = CurrentExe() is { } exe ? DxvkInstaller.Status(exe.Dir)?.Options : null;
            options ??= _selected is null ? null : Settings.GetDxvkOptions(_selected.AppId);
            AsyncBox.IsChecked = options?.Async ?? false;
            HudBox.IsChecked = options?.Hud ?? false;
            GplBox.SelectedItem = options?.Gpl ?? "Auto";
        }
        finally { _populating = wasPopulating; }
    }

    private ExeInfo? CurrentExe() =>
        _selected is { } s && ExeBox.SelectedIndex >= 0 && ExeBox.SelectedIndex < s.Exes.Count ? s.Exes[ExeBox.SelectedIndex] : null;

    private void UpdateActions()
    {
        var exe = CurrentExe();
        var mode = SelectedRunMode();
        var plan = _selected is null ? null : InstallPlanner.Plan(_selected, exe, mode);
        var launch = _selected is null ? null : LaunchPlanner.Plan(_selected, mode, _selected.Status is not null);
        var dxvk = exe is null ? null : DxvkInstaller.Status(exe.Dir);
        bool managed = dxvk?.Managed == true, manual = dxvk is { Managed: false };
        bool possible = plan?.CanInstall == true;

        // The tick shows what the user wants, remembered per game, and stays that way even when DXVK can't be used right now
        // (Run as D3D12): it is just greyed. Hiding or unticking it then would make the layout jump every time Run as changes.
        bool wanted = manual || (_selected is not null && WantsDxvk(_selected, dxvk));
        if (!_busy) DxvkBox.IsChecked = wanted;
        DxvkBox.IsEnabled = !_busy && !manual && possible;
        DxvkBox.ToolTip = manual ? "DXVK was installed in this folder by hand, not by this app, so the app leaves it alone."
            : possible ? "Ticking installs DXVK for this game; unticking restores the original files. The right DLLs are chosen automatically."
            : plan?.Blocked;
        DxvkOptionsPanel.Visibility = wanted && !manual ? Visibility.Visible : Visibility.Hidden;   // Hidden keeps the row's space
        AsyncBox.IsEnabled = HudBox.IsEnabled = GplBox.IsEnabled = !_busy && possible;

        OpenBtn.IsEnabled = exe is not null;
        LaunchBtn.IsEnabled = launch?.CanLaunch == true && !_busy;
        var problem = SelectedProblem();
        LaunchBtn.Content = problem is null ? "Launch via Steam" : "Launch anyway";
        CheckBtn.IsEnabled = _selected is not null;
        UpdateRunStatus();

        // Run as only where the engine has launch flags; for a plain single-API game it would just be noise.
        // Hidden, not Collapsed: the Use DXVK tick beside it stays at the same x for every game instead of sliding left.
        var runAs = _selected is not null && PlanSummary.ShowRunAs(_selected) ? Visibility.Visible : Visibility.Hidden;
        RunLabel.Visibility = RunBox.Visibility = runAs;

        string? summary = _selected is null ? null : PlanSummary.Describe(_selected, exe, mode, dxvk is not null);
        PlanText.Text = summary ?? "";
        PlanText.Visibility = summary is null ? Visibility.Collapsed : Visibility.Visible;
        RescanBtn.IsEnabled = !_scanning && !_busy;
        FetchBtn.IsEnabled = !_busy;

        // Shown inline (not in a modal), so it is visible in a render. Install warnings are only news before DXVK is in.
        var lines = new List<(string Text, Brush Color)>();
        if (plan is not null)
        {
            if (plan.CanInstall) { if (dxvk is null) lines.AddRange(plan.Warnings.Select(w => ("⚠ " + w, Warn))); }
            else if (plan.Blocked is not null) lines.Add((plan.Blocked, Dim));
        }
        if (launch is not null)
        {
            lines.AddRange(launch.Warnings.Select(w => ("⚠ " + w, Warn)));
            if (launch.Blocked is not null) lines.Add(("⚠ " + launch.Blocked, Warn));
        }
        // Remembered from an earlier run of exactly this setup (Run as + DXVK state).
        if (problem is not null)
        {
            bool crashed = problem.Outcome == RunOutcome.Crashed;
            lines.Add(($"{(crashed ? "✖ Crashed" : "⚠ Exited early")} last time with {ConfigKey.Describe(problem.Config)} " +
                       $"({When(problem.WhenUtc)}): {problem.Detail}.", crashed ? Bad : Warn));
        }

        WarningText.Inlines.Clear();
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0) WarningText.Inlines.Add(new System.Windows.Documents.LineBreak());
            WarningText.Inlines.Add(new System.Windows.Documents.Run(lines[i].Text) { Foreground = lines[i].Color });
        }
        WarningText.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------------------------------------------------------------------- actions

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateActions();
    }

    /// <summary>How the newest DXVK build is obtained. Replaceable so tests never touch the network.</summary>
    internal Func<DxvkVariant, Task<(string Dir, string Version)>> LatestBuild { get; set; } = null!;

    /// <summary>Asked before DXVK goes into a game with anti-cheat. Replaceable so tests never show a dialog.</summary>
    internal Func<string, bool> ConfirmRisk { get; set; } = message =>
        MessageBox.Show(message, "Steam DXVK Manager", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private DxvkOptions CurrentOptions() =>
        new(AsyncBox.IsChecked == true, GplBox.SelectedItem as string ?? "Auto", HudBox.IsChecked == true);

    /// <summary>The Use DXVK tick was clicked: install (choosing the DLLs automatically) or restore the originals.</summary>
    private async Task OnDxvkTickedAsync()
    {
        if (_selected is not { } scan || CurrentExe() is not { } exe || _busy) { UpdateActions(); return; }
        if (DxvkBox.IsChecked != true)
        {
            Settings.SetUseDxvk(scan.AppId, false);   // a deliberate "no": remembered, and nothing brings DXVK back
            await ReconcileAsync(scan, exe);
            return;
        }

        var plan = InstallPlanner.Plan(scan, exe, SelectedRunMode());
        if (!plan.CanInstall)
        {
            Log($"DXVK can't be used here: {plan.Blocked}");
            UpdateActions();   // puts the tick back to what is really installed
            return;
        }
        if (scan.AntiCheat.Count > 0 &&
            !ConfirmRisk($"{plan.Warnings.FirstOrDefault(w => w.StartsWith("Anti-cheat", StringComparison.Ordinal))}\n\nUse DXVK anyway?"))
        {
            UpdateActions();
            return;
        }

        // DXVK serves D3D11, so a game that could start in D3D12 is told to start in D3D11 (where its engine has a flag for that).
        // Otherwise ticking DXVK on such a game would be a coin-flip on a startup crash.
        if (SelectedRunMode() == RunMode.Default && exe.Apis.HasFlag(GfxApi.D3D12) && EngineFlags.Flag(scan.Engine, RunMode.D3D11) is not null)
        {
            SetRunMode(RunMode.D3D11);
            Log("Run as set to D3D11: DXVK needs the game to start in D3D11.");
            plan = InstallPlanner.Plan(scan, exe, RunMode.D3D11);
        }
        Settings.SetUseDxvk(scan.AppId, true);
        if (!await InstallAsync(scan, exe, plan))
        {
            Settings.SetUseDxvk(scan.AppId, false);   // it didn't go in, so the tick goes back and can simply be tried again
            UpdateActions();
        }
    }

    /// <summary>An option (async, pipeline library, HUD) changed: remember it, and re-apply if DXVK is in.</summary>
    private async Task ReapplyAsync()
    {
        if (_populating || _busy || _selected is not { } scan || CurrentExe() is not { } exe) return;
        Settings.SetDxvkOptions(scan.AppId, CurrentOptions());
        await ReconcileAsync(scan, exe);
    }

    /// <summary>Run as changed: save it, then make the folder match the remembered DXVK choice for the new mode.</summary>
    private async Task OnRunAsChangedAsync()
    {
        if (_populating || _selected is not { } scan) return;
        Settings.SetRunMode(scan.AppId, SelectedRunMode());
        if (CurrentExe() is { } exe && !_busy) await ReconcileAsync(scan, exe);
        UpdateActions();
    }

    /// <summary>
    /// Makes the game folder match what the user wants: DXVK in when it is wanted and usable (installing, or re-applying when the
    /// options or the needed DLLs changed), out when it isn't wanted or can't be used right now. The wish itself is never changed
    /// here, so a DXVK removed for Run as D3D12 returns by itself when Run as goes back to D3D11. Hand-made installs are never touched.
    /// </summary>
    private async Task ReconcileAsync(GameScan scan, ExeInfo exe)
    {
        if (_busy) return;
        var status = DxvkInstaller.Status(exe.Dir);
        if (status is { Managed: false }) return;

        bool wanted = WantsDxvk(scan, status), installed = status is { Managed: true };
        var plan = InstallPlanner.Plan(scan, exe, SelectedRunMode());

        if (wanted && plan.CanInstall)
        {
            bool upToDate = installed && status!.Options == CurrentOptions() &&
                DxvkInstaller.ReadManifest(exe.Dir)!.Files.Keys.Order().SequenceEqual(DxvkInstaller.DllsFor(plan.Apis).Order());
            if (upToDate) { UpdateActions(); return; }
            if (!installed) Log($"DXVK is switched back on for {scan.Name}.");
            await InstallAsync(scan, exe, plan);
        }
        else if (installed)
        {
            if (wanted) Log($"DXVK is switched off for now for {scan.Name} ({plan.Blocked}). Your choice is remembered, so it comes back when it can be used.");
            await UninstallAsync(scan, exe);
        }
        else UpdateActions();
    }

    private async Task<bool> InstallAsync(GameScan scan, ExeInfo exe, InstallPlan plan)
    {
        var options = CurrentOptions();
        Settings.SetDxvkOptions(scan.AppId, options);
        bool installed = false;
        SetBusy(true);
        try
        {
            Log($"Installing DXVK for {scan.Name} ...");
            var (buildDir, version) = await (LatestBuild ?? (v => DxvkBuilds.EnsureBuildAsync(v, Log)))(options.Variant);
            // Copying a few DLLs is quick, so it runs inline: the download above is the only part that waits.
            Log($"Done: {DxvkInstaller.Install(exe.Dir, scan.Root, exe.Bits, plan.Apis, options, buildDir, version, Log)}");
            installed = true;
        }
        catch (Exception ex)
        {
            Logger.Log($"Install failed: {ex}");
            Log($"ERROR: {ex.Message}");
        }
        finally { AfterChange(); }
        return installed;
    }

    private Task UninstallAsync(GameScan scan, ExeInfo exe)
    {
        SetBusy(true);
        try { DxvkInstaller.Uninstall(exe.Dir, Log); }
        catch (Exception ex)
        {
            Logger.Log($"Uninstall failed: {ex}");
            Log($"ERROR: {ex.Message}");
        }
        finally { AfterChange(); }
        return Task.CompletedTask;
    }

    private async void OnFetch(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            foreach (var variant in new[] { DxvkVariant.Official, DxvkVariant.Async })
            {
                var (_, version) = await DxvkBuilds.EnsureBuildAsync(variant, Log);
                Log($"{DxvkInstaller.Label(variant)} {version} ready");
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Fetch failed: {ex}");
            Log($"ERROR: {ex.Message}");
        }
        finally { SetBusy(false); }
    }

    /// <summary>How a launch is started and how a missing build is found. Replaceable so tests never start Steam or hit the network.</summary>
    internal Action<int, string?> StartGame { get; set; } = SteamLaunch.Start;
    internal Func<DxvkVariant, string, Task<(string Dir, string Version)>> ResolveBuild { get; set; } =
        (variant, version) => DxvkBuilds.ResolveAsync(variant, version);

    /// <summary>
    /// Launches the selected game through Steam. Steam can't be given per-launch settings, so "respecting the config" means
    /// the files must be right at launch: any manager-installed DXVK is verified and repaired first, because Steam's
    /// Verify Integrity or an update may have reverted it since it was installed.
    /// </summary>
    internal async Task LaunchAsync()
    {
        if (_selected is not { } scan || _busy) return;
        // Launching does what was asked: DXVK in if it is wanted and usable (it may have been reverted, or Run as may have changed),
        // out if it can't be used. Then the checks below run on the result.
        if (CurrentExe() is { } preparedExe) await ReconcileAsync(scan, preparedExe);
        SetBusy(true);
        try
        {
            var launchPlan = LaunchPlanner.Plan(scan, SelectedRunMode(), scan.Status is not null);
            if (!launchPlan.CanLaunch)
            {
                Log($"Not launching {scan.Name}: {launchPlan.Blocked}");
                return;
            }
            foreach (var warning in launchPlan.Warnings) Log($"Warning: {warning}");

            foreach (var exe in scan.Exes.Where(x => DxvkInstaller.ReadManifest(x.Dir) is not null))
            {
                var problems = DxvkInstaller.Verify(exe.Dir);
                if (problems.Count == 0) continue;

                Log($"DXVK for {scan.Name} has drifted ({string.Join("; ", problems)}). Repairing before launch ...");
                var installed = DxvkInstaller.ReadManifest(exe.Dir)!;
                var variant = installed.Variant == "async" ? DxvkVariant.Async : DxvkVariant.Official;
                var (buildDir, version) = await ResolveBuild(variant, installed.Version);
                Log("Repaired: " + DxvkInstaller.Repair(exe.Dir, scan.Root, buildDir, version, Log));
            }

            var mode = SelectedRunMode();
            _launches[scan.AppId] = new LaunchRequest(DateTime.UtcNow, mode, ConfigKey.Of(mode, scan.Status));
            Log($"Launching {scan.Name} via Steam ({launchPlan.Args ?? "no extra arguments"}) ...");
            StartGame(scan.AppId, launchPlan.Args);
        }
        catch (Exception ex)
        {
            Logger.Log($"Launch failed: {ex}");
            Log($"ERROR: {ex.Message}");
        }
        finally { AfterChange(); }
    }

    /// <summary>Reports which API the game's last run really used, from DXVK's own log files.</summary>
    internal void CheckLastRun()
    {
        if (_selected is not { } scan) return;
        DateTime? since = _launches.TryGetValue(scan.AppId, out var launched) ? launched.Utc : null;
        var dirs = scan.Exes.Select(e => e.Dir).Distinct().Where(d => DxvkInstaller.Status(d) is not null).ToList();
        if (dirs.Count == 0)
        {
            Log($"{scan.Name}: DXVK isn't installed, so there are no DXVK logs to read.");
            return;
        }
        foreach (var dir in dirs) Log($"{scan.Name}: {RunDiagnosis.Check(dir, since).Verdict}");
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (CurrentExe() is { } exe) Process.Start(new ProcessStartInfo(exe.Dir) { UseShellExecute = true });
    }

    private void AfterChange()
    {
        if (_selected is { } s) s.Status = DxvkInstaller.StatusOf(s);
        _busy = false;
        RefreshRunChoices();   // the DXVK state is part of a setup, so crash flags may now apply to different dropdown entries
        RefreshRows();
        UpdateActions();
    }

    // ------------------------------------------------------------------------------- log

    /// <summary>Safe from any thread.</summary>
    public void Log(string message)
    {
        Logger.Log(message);
        Dispatcher.InvokeAsync(() =>
        {
            LogBox.AppendText(message + Environment.NewLine);
            LogBox.ScrollToEnd();
        });
    }

    private static SolidColorBrush Frozen(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
