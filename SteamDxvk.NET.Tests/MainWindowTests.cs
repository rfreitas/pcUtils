using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using AggressiveScreensaver.NET.Tests;
using SteamDxvk;
using Xunit;

namespace SteamDxvk.Tests;

public static class WpfCollection
{
    public const string Name = "WPF windows (not thread-safe to build in parallel)";
}

[CollectionDefinition(WpfCollection.Name)]
public sealed class WpfCollectionDefinition;

internal static class Sta
{
    /// <summary>Runs on an STA thread (WPF requirement) and rethrows whatever it threw.</summary>
    public static void Run(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception e) { error = e; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}

// WPF's resource loader (PackagePart.GetStream) isn't thread-safe: building windows from several test threads at once
// occasionally throws inside LoadComponent. Every class that builds a MainWindow shares this collection, so they run one at a time.
[Collection(WpfCollection.Name)]
public class MainWindowTests
{
    private static List<GameScan> MockLibrary() =>
    [
        TestData.Scan("Alpha Racing", GfxApi.D3D11, GfxApi.D3D11),
        TestData.Scan("Beta Souls", GfxApi.D3D12, GfxApi.D3D12, anticheat: ["EasyAntiCheat", "BattlEye"]),
        TestData.Scan("Gamma Unreal", GfxApi.D3D9 | GfxApi.D3D11 | GfxApi.D3D12 | GfxApi.Vulkan, GfxApi.D3D11 | GfxApi.D3D12, engine: "Unreal"),
        TestData.Scan("Delta Online", GfxApi.D3D11, GfxApi.D3D11, anticheat: ["BattlEye"]),
        TestData.Scan("Epsilon Vulkan", GfxApi.Vulkan, GfxApi.Vulkan),
        TestData.Scan("Zeta Mystery", GfxApi.None, bits: 32),
        TestData.Scan("Eta Manual", GfxApi.D3D9, status: new DxvkStatus(false, "DXVK (manual)", null)),
    ];

    /// <summary>A window whose remembered settings live in a throwaway file, never the user's real one.</summary>
    private static MainWindow Window(string? settingsPath = null, string? historyPath = null) => new()
    {
        Settings = new GameSettings(settingsPath ?? Path.Combine(Path.GetTempPath(), $"SteamDxvkSettings_{Guid.NewGuid():N}.json")),
        History = new RunHistory(historyPath ?? Path.Combine(Path.GetTempPath(), $"SteamDxvkHistory_{Guid.NewGuid():N}.json")),
    };

    private static string TempPng() => Path.Combine(Path.GetTempPath(), $"SteamDxvkTest_{Guid.NewGuid():N}.png");

    /// <summary>The text a TextBlock actually shows, including text built from inlines.</summary>
    private static string Shown(TextBlock block) => new System.Windows.Documents.TextRange(block.ContentStart, block.ContentEnd).Text;

    private static double X(UIElement element, UIElement relativeTo) => element.TranslatePoint(new System.Windows.Point(0, 0), relativeTo).X;

    [Fact]
    public void Render_produces_a_png_with_the_repo_dark_background()
    {
        string path = TempPng();
        Sta.Run(() =>
        {
            var w = Window();
            w.LoadScans(MockLibrary());
            w.SelectByName("Alpha");
            w.RenderToPng(path);
        });

        var frame = new PngBitmapDecoder(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var pixel = new byte[4];
        frame.CopyPixels(new Int32Rect(2, 2, 1, 1), pixel, 4, 0);   // inside the margin: pure background
        Assert.Equal((0x2D, 0x2D, 0x2D), (pixel[2], pixel[1], pixel[0]));   // #2d2d2d, not transparent or white
        Assert.True(frame.PixelWidth >= 900 && frame.PixelHeight >= 600);
        File.Delete(path);
    }

    [Fact]
    public void Header_and_row_columns_line_up()
    {
        Sta.Run(() =>
        {
            var w = Window();
            w.LoadScans(MockLibrary());
            w.RenderToPng(TempPng());   // forces layout

            var row = (Grid)((Border)w.Rows.Children[0]).Child;
            Assert.Equal(w.HeaderGrid.Children.Count, row.Children.Count);
            for (int i = 0; i < row.Children.Count; i++)
                Assert.True(Math.Abs(X(w.HeaderGrid.Children[i], w.Root) - X(row.Children[i], w.Root)) < 1.0,
                    $"column {i}: header at {X(w.HeaderGrid.Children[i], w.Root)}, row at {X(row.Children[i], w.Root)}");
        });
    }

    [Fact]
    public void Every_game_gets_a_row_and_clicking_a_header_sorts()
    {
        Sta.Run(() =>
        {
            var w = Window();
            w.LoadScans(MockLibrary());
            Assert.Equal(7, w.Rows.Children.Count);

            // The name cell is built from inline runs (for the running marker), so read what is displayed, not TextBlock.Text.
            string First() => Shown((TextBlock)((Grid)((Border)w.Rows.Children[0]).Child).Children[0]);
            Assert.Equal("Alpha Racing", First());

            var header = (TextBlock)w.HeaderGrid.Children[0];
            header.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            Assert.Equal("Zeta Mystery", First());   // second click on the same column reverses the order
            Assert.Contains("▼", header.Text);
        });
    }

    [Fact]
    public void Filter_and_search_narrow_the_list()
    {
        Sta.Run(() =>
        {
            var w = Window();
            w.LoadScans(MockLibrary());

            w.FilterBox.SelectedItem = "D3D12";
            Assert.Equal(2, w.Rows.Children.Count);   // Beta + Gamma

            w.FilterBox.SelectedItem = "No API detected";
            Assert.Single(w.Rows.Children);

            w.FilterBox.SelectedItem = "All";
            w.SearchBox.Text = "racing";
            Assert.Single(w.Rows.Children);
        });
    }

    [Fact]
    public void The_dxvk_tick_is_enabled_only_where_dxvk_can_be_used_and_the_options_stay_hidden_until_ticked()
    {
        Sta.Run(() =>
        {
            var w = Window();
            w.LoadScans(MockLibrary());

            w.SelectByName("Alpha");   // plain D3D11
            Assert.True(w.DxvkBox.IsEnabled);
            Assert.False(w.DxvkBox.IsChecked);
            Assert.Equal(Visibility.Hidden, w.DxvkOptionsPanel.Visibility);

            w.SelectByName("Beta");    // D3D12 only: greyed. The greyed control is the message; the reason is its tooltip.
            Assert.False(w.DxvkBox.IsEnabled);
            Assert.Contains("only uses D3D12", (string)w.DxvkBox.ToolTip);

            w.SelectByName("Gamma");   // Unreal with D3D11+D3D12: fine, ticking will set Run as D3D11 itself
            Assert.True(w.DxvkBox.IsEnabled);

            w.SelectByName("Delta");   // anti-cheat: allowed; the list flags it and ticking asks to confirm
            Assert.True(w.DxvkBox.IsEnabled);

            w.SelectByName("Epsilon"); // Vulkan only
            Assert.False(w.DxvkBox.IsEnabled);

            w.SelectByName("Zeta");    // nothing detected: allowed; the tooltip says both DLL sets go in
            Assert.True(w.DxvkBox.IsEnabled);
            Assert.Contains("both the D3D9 and D3D11 files", (string)w.DxvkBox.ToolTip);
        });
    }

    [Fact]
    public void Constraints_the_ui_already_enforces_are_not_repeated_as_text()
    {
        // Greyed tick, hidden options, flagged list row: each says it by itself. A line saying the same thing again is clutter.
        Sta.Run(() =>
        {
            var w = Window();
            w.LoadScans(MockLibrary());
            foreach (var name in new[] { "Alpha", "Beta", "Gamma", "Delta", "Epsilon", "Zeta", "Eta" })
            {
                w.SelectByName(name);
                Assert.True(w.WarningText.Visibility == Visibility.Collapsed, $"{name}: unexpected message '{Text(w.WarningText)}'");
                Assert.Equal(Visibility.Collapsed, w.RunStatusText.Visibility);
            }

            w.SelectByName("Gamma");
            w.SetRunMode(RunMode.D3D12);   // DXVK can't be used: the tick is greyed, and that is all that needs to be said
            Assert.False(w.DxvkBox.IsEnabled);
            Assert.Equal(Visibility.Collapsed, w.WarningText.Visibility);
        });
    }

    [Fact]
    public void Anti_cheat_is_flagged_once_in_the_list_and_asked_about_when_ticking_not_repeated_in_the_panel()
    {
        Sta.Run(() =>
        {
            var w = Window();
            w.LoadScans(MockLibrary());
            w.SelectByName("Delta");
            Assert.Equal(Visibility.Collapsed, w.WarningText.Visibility);
            var row = w.Rows.Children.Cast<Border>().First(b => Shown((System.Windows.Controls.TextBlock)((Grid)b.Child).Children[0]).Contains("Delta"));
            var notes = (System.Windows.Controls.TextBlock)((Grid)row.Child).Children[5];
            Assert.Contains("anti-cheat: BattlEye", notes.Text);
        });
    }

    [Fact]
    public void The_notes_column_does_not_repeat_what_the_api_column_lists()
    {
        Sta.Run(() =>
        {
            var w = Window();
            w.LoadScans(MockLibrary());
            foreach (Border row in w.Rows.Children)
                Assert.DoesNotContain("also has", ((System.Windows.Controls.TextBlock)((Grid)row.Child).Children[5]).Text);
        });
    }

    private static string Text(System.Windows.Controls.TextBlock block) =>
        new System.Windows.Documents.TextRange(block.ContentStart, block.ContentEnd).Text;

    [Fact]
    public void Nothing_is_enabled_before_a_game_is_selected()
    {
        Sta.Run(() =>
        {
            var w = Window();
            w.LoadScans(MockLibrary());
            Assert.False(w.DxvkBox.IsEnabled);
            Assert.False(w.LaunchBtn.IsEnabled);
            Assert.False(w.OpenBtn.IsEnabled);
            Assert.Equal("Select a game", w.DetailTitle.Text);
        });
    }

    [Fact]
    public void A_dxvk_installed_by_hand_is_shown_ticked_but_left_alone()
    {
        using var game = new TempDir();
        game.Write(@"bin\Game.exe", "exe");
        game.Write(@"bin\d3d9.dll", "MZ this is DXVK, installed by hand");
        var scan = new GameScan(new Game(77, "Manual Game", game.Path))
        {
            Exes = [new ExeInfo(game.Combine(@"bin\Game.exe"), 64, 1, GfxApi.D3D9, GfxApi.D3D9)],
        };
        scan.Status = DxvkInstaller.StatusOf(scan);

        Sta.Run(() =>
        {
            var w = Window();
            w.LoadScans([scan]);
            w.SelectByName("Manual");

            Assert.True(w.DxvkBox.IsChecked);
            Assert.False(w.DxvkBox.IsEnabled);
            Assert.Contains("by hand", (string)w.DxvkBox.ToolTip);
            Assert.Equal(Visibility.Hidden, w.DxvkOptionsPanel.Visibility);
        });
    }

    [Fact]
    public void Selecting_a_game_with_dxvk_installed_ticks_it_and_shows_its_options()
    {
        using var game = new TempDir();
        string exeDir = game.Combine("bin");
        Directory.CreateDirectory(exeDir);
        game.Write(@"bin\Game.exe", "exe");
        using var build = new TempDir();
        foreach (var arch in new[] { "x32", "x64" })
            foreach (var dll in DxvkInstaller.AllDlls) build.Write($@"{arch}\{dll}", dll);
        DxvkInstaller.Install(exeDir, game.Path, 64, GfxApi.D3D11, new DxvkOptions(Async: true, Gpl: "False", Hud: true), build.Path, "v1");

        var scan = new GameScan(new Game(7, "Installed Game", game.Path))
        {
            Exes = [new ExeInfo(Path.Combine(exeDir, "Game.exe"), 64, 1, GfxApi.D3D11, GfxApi.D3D11)],
        };
        scan.Status = DxvkInstaller.StatusOf(scan);

        Sta.Run(() =>
        {
            var w = Window();
            w.LoadScans([scan]);
            w.SelectByName("Installed");

            Assert.True(w.DxvkBox.IsChecked);
            Assert.True(w.DxvkBox.IsEnabled);
            Assert.Equal(Visibility.Visible, w.DxvkOptionsPanel.Visibility);
            Assert.True(w.AsyncBox.IsChecked);
            Assert.True(w.HudBox.IsChecked);
            Assert.Equal("False", w.GplBox.SelectedItem);
        });
    }

    // ------------------------------------------------------------- the tick does the work

    /// <summary>A game in a temp folder (exe in bin\, a stand-in for its own dxgi.dll) and a fake DXVK build, so no network is used.</summary>
    private sealed class TickGame : IDisposable
    {
        public TempDir Game { get; } = new();
        public TempDir Build { get; } = new();
        public string ExeDir { get; }
        public GameScan Scan { get; }

        public TickGame(string engine = "", GfxApi apis = GfxApi.D3D11, string[]? anticheat = null)
        {
            ExeDir = Game.Combine("bin");
            Directory.CreateDirectory(ExeDir);
            Game.Write(@"bin\Game.exe", "exe");
            Game.Write(@"bin\dxgi.dll", "ORIGINAL-DXGI");
            foreach (var arch in new[] { "x32", "x64" })
                foreach (var dll in DxvkInstaller.AllDlls) Build.Write($@"{arch}\{dll}", $"DXVK-{arch}-{dll}");
            Scan = new GameScan(new Game(9100, "Tick Game", Game.Path))
            {
                Exes = [new ExeInfo(Path.Combine(ExeDir, "Game.exe"), 64, 1, apis, apis)],
                Engine = engine,
                AntiCheat = [.. anticheat ?? []],
            };
        }

        public void Dispose() { Game.Dispose(); Build.Dispose(); }
    }

    private static MainWindow Ready(TickGame game, MainWindow? existing = null)
    {
        var w = existing ?? Window();
        w.LoadScans([game.Scan]);
        w.SelectByName("Tick Game");
        w.LatestBuild = variant => Task.FromResult((game.Build.Path, variant == DxvkVariant.Async ? "async-v1" : "v1"));
        w.ConfirmRisk = _ => true;
        return w;
    }

    /// <summary>What a user click does: the box changes state, then Click fires (programmatic changes don't fire Click).</summary>
    private static void UserSets(System.Windows.Controls.Primitives.ToggleButton box, bool on)
    {
        box.IsChecked = on;
        box.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }

    [Fact]
    public void Ticking_dxvk_installs_it_with_the_right_dlls_chosen_automatically()
    {
        using var game = new TickGame();
        Sta.Run(() =>
        {
            var w = Ready(game);

            UserSets(w.DxvkBox, true);

            Assert.Equal("DXVK-x64-d3d11.dll", File.ReadAllText(Path.Combine(game.ExeDir, "d3d11.dll")));
            Assert.Equal("DXVK-x64-d3d10core.dll", File.ReadAllText(Path.Combine(game.ExeDir, "d3d10core.dll")));
            Assert.NotNull(DxvkInstaller.ReadManifest(game.ExeDir));
            Assert.True(w.DxvkBox.IsChecked);
            Assert.Equal(Visibility.Visible, w.DxvkOptionsPanel.Visibility);
            MainWindow.Pump();
            Assert.Contains("Installing DXVK for Tick Game", w.LogBox.Text);
        });
    }

    [Fact]
    public void Unticking_dxvk_restores_the_games_original_files_exactly()
    {
        using var game = new TickGame();
        var before = game.Game.Snapshot();
        Sta.Run(() =>
        {
            var w = Ready(game);
            UserSets(w.DxvkBox, true);
            Assert.NotEqual(before, game.Game.Snapshot());

            UserSets(w.DxvkBox, false);

            Assert.Equal(before, game.Game.Snapshot());   // original dxgi.dll back, manifest and backup folder gone
            Assert.False(w.DxvkBox.IsChecked);
            Assert.Equal(Visibility.Hidden, w.DxvkOptionsPanel.Visibility);
        });
    }

    [Fact]
    public void Changing_an_option_while_dxvk_is_ticked_re_applies_it()
    {
        using var game = new TickGame();
        Sta.Run(() =>
        {
            var w = Ready(game);
            UserSets(w.DxvkBox, true);
            Assert.False(DxvkInstaller.Status(game.ExeDir)!.Options!.Async);

            UserSets(w.AsyncBox, true);

            var installed = DxvkInstaller.Status(game.ExeDir)!;
            Assert.True(installed.Options!.Async);
            Assert.StartsWith("DXVK-gplasync", installed.Label);   // the async build, not just a config line
            Assert.Contains("dxvk.enableAsync = True", File.ReadAllText(game.Game.Combine("dxvk.conf")));
        });
    }

    [Fact]
    public void Changing_an_option_while_unticked_only_remembers_it_for_next_time()
    {
        using var game = new TickGame();
        var before = game.Game.Snapshot();
        string settings = Path.Combine(Path.GetTempPath(), $"SteamDxvkSettings_{Guid.NewGuid():N}.json");
        Sta.Run(() =>
        {
            var w = Ready(game, Window(settings));

            UserSets(w.AsyncBox, true);
            Assert.Equal(before, game.Game.Snapshot());                          // nothing installed
            Assert.True(new GameSettings(settings).GetDxvkOptions(9100)!.Async);   // but remembered

            UserSets(w.DxvkBox, true);                                            // ticking later uses what was chosen
            Assert.True(DxvkInstaller.Status(game.ExeDir)!.Options!.Async);
        });
        File.Delete(settings);
    }

    [Fact]
    public void The_options_come_back_when_the_game_is_selected_again()
    {
        using var game = new TickGame();
        Sta.Run(() =>
        {
            var w = Ready(game);
            w.LoadScans([game.Scan, TestData.Scan("Other", GfxApi.D3D11)]);
            UserSets(w.AsyncBox, true);
            UserSets(w.HudBox, true);

            w.SelectByName("Other");
            Assert.False(w.AsyncBox.IsChecked);   // the other game has its own

            w.SelectByName("Tick Game");
            Assert.True(w.AsyncBox.IsChecked);
            Assert.True(w.HudBox.IsChecked);
        });
    }

    [Fact]
    public void Ticking_dxvk_on_a_game_that_could_start_in_d3d12_sets_run_as_d3d11_for_it()
    {
        using var game = new TickGame(engine: "Unreal", apis: GfxApi.D3D11 | GfxApi.D3D12);
        Sta.Run(() =>
        {
            var w = Ready(game);
            Assert.Equal("Default", SelectedMode(w));

            UserSets(w.DxvkBox, true);

            Assert.Equal("D3D11", SelectedMode(w));
            Assert.NotNull(DxvkInstaller.ReadManifest(game.ExeDir));
            MainWindow.Pump();
            Assert.Contains("Run as set to D3D11", w.LogBox.Text);
        });
    }

    [Fact]
    public void Switching_run_as_to_d3d12_takes_dxvk_out_but_the_tick_stays_ticked_greyed_and_remembered()
    {
        using var game = new TickGame(engine: "Unreal", apis: GfxApi.D3D11 | GfxApi.D3D12);
        var before = game.Game.Snapshot();
        Sta.Run(() =>
        {
            var w = Ready(game);
            UserSets(w.DxvkBox, true);
            UserSets(w.AsyncBox, true);
            Assert.NotNull(DxvkInstaller.ReadManifest(game.ExeDir));

            w.SetRunMode(RunMode.D3D12);

            Assert.Equal(before, game.Game.Snapshot());       // DXVK is out of the folder: originals back, byte for byte
            Assert.True(w.DxvkBox.IsChecked);                  // but the choice is remembered, not lost
            Assert.False(w.DxvkBox.IsEnabled);                 // and greyed: it can't be used in D3D12
            Assert.Contains("D3D12", (string)w.DxvkBox.ToolTip);
            Assert.Equal(Visibility.Visible, w.DxvkOptionsPanel.Visibility);   // the layout doesn't jump
            Assert.False(w.AsyncBox.IsEnabled);                // options greyed as well
            Assert.True(w.AsyncBox.IsChecked);                 // and they keep their values
            MainWindow.Pump();
            Assert.Contains("switched off for now", w.LogBox.Text);
        });
    }

    [Fact]
    public void The_greyed_but_ticked_state_renders_for_a_visual_check()
    {
        using var game = new TickGame(engine: "Unreal", apis: GfxApi.D3D11 | GfxApi.D3D12);
        string png = Path.Combine(AppContext.BaseDirectory, "SteamDxvk_greyed_tick.png");
        string withMessage = Path.Combine(AppContext.BaseDirectory, "SteamDxvk_with_message.png");
        Sta.Run(() =>
        {
            var w = Ready(game);
            UserSets(w.DxvkBox, true);
            UserSets(w.AsyncBox, true);
            w.SetRunMode(RunMode.D3D12);
            w.RenderToPng(png);

            // the same state with a remembered crash, to see the status area with something in it
            w.History.Add(new RunRecord(9100, "D3D12|no-dxvk", RunOutcome.Crashed,
                "Unreal crash report: hr failed at D3D12RHI/Private/Windows/WindowsD3D12Viewport.cpp:224 with error DXGI_ERROR_UNSUPPORTED", DateTime.UtcNow));
            w.SetRunMode(RunMode.Default);
            w.SetRunMode(RunMode.D3D12);
            w.RenderToPng(withMessage);
        });
        Assert.True(new FileInfo(png).Length > 5_000);
        Assert.True(new FileInfo(withMessage).Length > 5_000);
        Console.WriteLine($"[render] {png} {withMessage}");
    }

    [Fact]
    public void Dxvk_comes_back_by_itself_with_the_same_options_when_run_as_returns_to_d3d11()
    {
        using var game = new TickGame(engine: "Unreal", apis: GfxApi.D3D11 | GfxApi.D3D12);
        Sta.Run(() =>
        {
            var w = Ready(game);
            UserSets(w.DxvkBox, true);
            UserSets(w.AsyncBox, true);
            UserSets(w.HudBox, true);
            w.SetRunMode(RunMode.D3D12);
            Assert.Null(DxvkInstaller.ReadManifest(game.ExeDir));

            w.SetRunMode(RunMode.D3D11);

            var installed = DxvkInstaller.Status(game.ExeDir)!;
            Assert.True(installed.Managed);
            Assert.Equal(new DxvkOptions(Async: true, Gpl: "Auto", Hud: true), installed.Options);   // as it was left
            Assert.True(w.DxvkBox.IsEnabled);
            Assert.True(w.DxvkBox.IsChecked);
            MainWindow.Pump();
            Assert.Contains("switched back on", w.LogBox.Text);
        });
    }

    /// <summary>Where every control of the detail panel sits and how big it is, so two states can be compared exactly.</summary>
    private static Dictionary<string, (double X, double Y, double W, double H)> Layout(MainWindow w)
    {
        w.LayoutNow();
        var controls = new (string Name, FrameworkElement Element)[]
        {
            ("title", w.DetailTitle), ("path", w.DetailPath), ("messages", w.MessageArea), ("exe", w.ExeBox),
            ("runLabel", w.RunLabel), ("runBox", w.RunBox), ("dxvk", w.DxvkBox), ("options", w.DxvkOptionsPanel),
            ("async", w.AsyncBox), ("gpl", w.GplBox), ("hud", w.HudBox),
            ("launch", w.LaunchBtn), ("open", w.OpenBtn), ("check", w.CheckBtn), ("log", w.LogBox),
        };
        return controls.ToDictionary(c => c.Name, c =>
        {
            var p = c.Element.TranslatePoint(new System.Windows.Point(0, 0), w.Root);
            return (Math.Round(p.X, 1), Math.Round(p.Y, 1), Math.Round(c.Element.ActualWidth, 1), Math.Round(c.Element.ActualHeight, 1));
        });
    }

    [Fact]
    public void Nothing_moves_whatever_game_option_or_message_is_shown()
    {
        // Every kind of message and toggle in one window: blocked reasons, anti-cheat and "couldn't tell" warnings, a remembered crash
        // (long warning, and the Launch button's label changes), running status, Run as in every mode, DXVK ticked and unticked
        // and greyed, options showing and hidden. The reserved space means none of it may shift a single control, or resize one.
        using var game = new TickGame(engine: "Unreal", apis: GfxApi.D3D11 | GfxApi.D3D12);
        Sta.Run(() =>
        {
            var w = Window();
            var library = MockLibrary();
            w.LoadScans([.. library, game.Scan]);
            w.LatestBuild = v => Task.FromResult((game.Build.Path, v == DxvkVariant.Async ? "async-v1" : "v1"));
            w.ConfirmRisk = _ => true;
            w.AttachMonitor(new GameMonitor(new FakeProbe(), windowsDir: @"C:\Windows"));
            int gammaId = library.First(s => s.Name.StartsWith("Gamma")).AppId;

            w.SelectByName("Alpha");
            var baseline = Layout(w);
            var states = new List<(string State, Dictionary<string, (double, double, double, double)> Layout)>();
            void Snap(string state) => states.Add((state, Layout(w)!));

            foreach (var name in new[] { "Beta", "Gamma", "Delta", "Epsilon", "Zeta", "Eta", "Alpha" })
            {
                w.SelectByName(name);
                Snap($"game {name}");
            }

            w.SelectByName("Gamma");
            foreach (var mode in new[] { RunMode.D3D12, RunMode.Vulkan, RunMode.D3D11, RunMode.Default })
            {
                w.SetRunMode(mode);
                Snap($"gamma run as {mode}");
            }

            w.History.Add(new RunRecord(gammaId, "Default|no-dxvk", RunOutcome.Crashed, "a long crash message that wraps " + new string('x', 200), DateTime.UtcNow));
            w.SelectByName("Alpha");
            w.SelectByName("Gamma");   // Launch now reads "Launch anyway", with a long crash warning
            Assert.Equal("Launch anyway", w.LaunchBtn.Content);
            Snap("gamma with a remembered crash");

            w.SelectByName("Tick Game");
            Snap("tick game");
            UserSets(w.DxvkBox, true);
            Snap("dxvk ticked");
            UserSets(w.AsyncBox, true);
            Snap("async ticked");
            w.SetRunMode(RunMode.D3D12);
            Snap("ticked then run as D3D12 (greyed)");
            w.SetRunMode(RunMode.D3D11);
            Snap("back to D3D11");
            UserSets(w.DxvkBox, false);
            Snap("unticked");

            foreach (var (state, layout) in states)
                foreach (var (name, expected) in baseline)
                    Assert.True(expected == layout[name], $"'{name}' moved or resized in state '{state}': baseline {expected}, now {layout[name]}");
        });
    }

    [Fact]
    public void The_message_area_is_a_fixed_height_and_scrolls_instead_of_growing()
    {
        Sta.Run(() =>
        {
            var w = Window();
            w.LoadScans(MockLibrary());
            w.SelectByName("Alpha");
            w.LayoutNow();
            double quiet = w.MessageArea.ActualHeight;

            w.History.Add(new RunRecord(w.Selected!.AppId, "Default|no-dxvk", RunOutcome.Crashed, new string('x', 2000), DateTime.UtcNow));
            w.SelectByName("Beta");
            w.SelectByName("Alpha");   // a huge message
            w.LayoutNow();

            Assert.Equal(quiet, w.MessageArea.ActualHeight);   // still the same height
            Assert.True(w.MessageArea.ScrollableHeight > 0);   // it scrolls instead
        });
    }

    [Fact]
    public void The_layout_stays_put_while_run_as_changes_so_nothing_jumps()
    {
        using var game = new TickGame(engine: "Unreal", apis: GfxApi.D3D11 | GfxApi.D3D12);
        Sta.Run(() =>
        {
            var w = Ready(game);
            UserSets(w.DxvkBox, true);

            var seen = new List<(Visibility Options, bool? Ticked)>();
            foreach (var mode in new[] { RunMode.D3D11, RunMode.D3D12, RunMode.Vulkan, RunMode.Default, RunMode.D3D11 })
            {
                w.SetRunMode(mode);
                seen.Add((w.DxvkOptionsPanel.Visibility, w.DxvkBox.IsChecked));
            }

            Assert.All(seen, s => Assert.Equal((Visibility.Visible, (bool?)true), s));   // same on screen in every mode
        });
    }

    [Fact]
    public void A_deliberate_untick_is_remembered_and_dxvk_never_comes_back_on_its_own()
    {
        using var game = new TickGame(engine: "Unreal", apis: GfxApi.D3D11 | GfxApi.D3D12);
        Sta.Run(() =>
        {
            var w = Ready(game);
            UserSets(w.DxvkBox, true);
            UserSets(w.DxvkBox, false);

            w.SetRunMode(RunMode.D3D12);
            w.SetRunMode(RunMode.D3D11);
            w.SetRunMode(RunMode.Default);

            Assert.Null(DxvkInstaller.ReadManifest(game.ExeDir));
            Assert.False(w.DxvkBox.IsChecked);
            Assert.Equal(Visibility.Hidden, w.DxvkOptionsPanel.Visibility);
        });
    }

    [Fact]
    public void The_remembered_choice_survives_closing_and_reopening_the_app()
    {
        using var game = new TickGame(engine: "Unreal", apis: GfxApi.D3D11 | GfxApi.D3D12);
        string settings = Path.Combine(Path.GetTempPath(), $"SteamDxvkSettings_{Guid.NewGuid():N}.json");
        Sta.Run(() =>
        {
            var w = Ready(game, Window(settings));
            UserSets(w.DxvkBox, true);
            w.SetRunMode(RunMode.D3D12);   // leaves it ticked but greyed, and out of the folder
        });

        game.Scan.Status = DxvkInstaller.StatusOf(game.Scan);
        Sta.Run(() =>
        {
            var w = Window(settings);   // a fresh window on the same settings file
            w.LoadScans([game.Scan]);
            w.SelectByName("Tick Game");

            Assert.True(w.DxvkBox.IsChecked);
            Assert.False(w.DxvkBox.IsEnabled);
            Assert.Equal(Visibility.Visible, w.DxvkOptionsPanel.Visibility);
        });
        File.Delete(settings);
    }

    [Fact]
    public void Launching_puts_back_a_wanted_dxvk_that_went_missing()
    {
        using var game = new TickGame(engine: "Unreal", apis: GfxApi.D3D11);
        Sta.Run(() =>
        {
            var w = Ready(game);
            UserSets(w.DxvkBox, true);
            DxvkInstaller.Uninstall(game.ExeDir);   // e.g. Steam's Verify Integrity wiped it
            var launched = new List<string>();
            w.StartGame = (id, args) => launched.Add(TestData.Describe(id, args));

            w.LaunchAsync().GetAwaiter().GetResult();

            Assert.True(DxvkInstaller.Status(game.ExeDir)!.Managed);   // installed again first
            Assert.Equal(["9100"], launched);                           // then launched
        });
    }

    [Fact]
    public void Launching_never_installs_dxvk_the_user_did_not_ask_for()
    {
        using var game = new TickGame(engine: "Unreal", apis: GfxApi.D3D11);
        var before = game.Game.Snapshot();
        Sta.Run(() =>
        {
            var w = Ready(game);
            w.StartGame = (_, _) => { };

            w.LaunchAsync().GetAwaiter().GetResult();

            Assert.Equal(before, game.Game.Snapshot());
        });
    }

    [Fact]
    public void Launching_takes_out_a_dxvk_that_the_chosen_run_as_cannot_use()
    {
        using var game = new TickGame(engine: "Unreal", apis: GfxApi.D3D11 | GfxApi.D3D12);
        Sta.Run(() =>
        {
            var w = Ready(game);
            w.SetRunMode(RunMode.D3D12);
            w.Settings.SetUseDxvk(9100, true);
            DxvkInstaller.Install(game.ExeDir, game.Game.Path, 64, GfxApi.D3D11, new DxvkOptions(), game.Build.Path, "v1");   // appears behind the app's back
            var launched = new List<string>();
            w.StartGame = (id, args) => launched.Add(TestData.Describe(id, args));

            w.LaunchAsync().GetAwaiter().GetResult();

            Assert.Null(DxvkInstaller.ReadManifest(game.ExeDir));   // D3D12 would have crashed with it
            Assert.Equal(["9100 -dx12"], launched);
        });
    }

    /// <summary>A game that already has the app's DXVK in it (installed before the window opens), with a saved Run as.</summary>
    private static (TickGame Game, string Settings, Dictionary<string, string> Before) InstalledBeforeOpening(
        RunMode savedMode, string engine = "Unreal", GfxApi apis = GfxApi.D3D11 | GfxApi.D3D12)
    {
        var game = new TickGame(engine, apis);
        var before = game.Game.Snapshot();
        DxvkInstaller.Install(game.ExeDir, game.Game.Path, 64, GfxApi.D3D11, new DxvkOptions(Async: true), game.Build.Path, "v1");
        game.Scan.Status = DxvkInstaller.StatusOf(game.Scan);
        string settings = Path.Combine(Path.GetTempPath(), $"SteamDxvkSettings_{Guid.NewGuid():N}.json");
        new GameSettings(settings).SetRunMode(9100, savedMode);
        return (game, settings, before);
    }

    [Fact]
    public void An_install_that_the_saved_run_as_cannot_use_is_removed_on_selection_and_the_tick_is_greyed()
    {
        // Session's real situation: DXVK installed, Run as D3D12 saved earlier. The tick was enabled and ticked there (the reported bug).
        var (game, settings, before) = InstalledBeforeOpening(RunMode.D3D12);
        using var _ = game;
        Sta.Run(() =>
        {
            var w = Window(settings);
            w.LoadScans([game.Scan]);

            w.SelectByName("Tick Game");

            Assert.False(w.DxvkBox.IsEnabled);                 // greyed because D3D12 can't use DXVK
            Assert.True(w.DxvkBox.IsChecked);                  // still ticked: that install counts as the user having chosen DXVK
            Assert.Null(DxvkInstaller.ReadManifest(game.ExeDir));   // but nothing is left in the game folder
            Assert.Equal(before, game.Game.Snapshot());        // the game's own files are back, byte for byte
            Assert.Equal(Visibility.Visible, w.DxvkOptionsPanel.Visibility);
            Assert.Contains("D3D12", (string)w.DxvkBox.ToolTip);
            Assert.True(new GameSettings(settings).GetUseDxvk(9100));   // remembered on disk
            MainWindow.Pump();
            Assert.Contains("switched off for now", w.LogBox.Text);
        });
        File.Delete(settings);
    }

    [Fact]
    public void A_read_only_window_looks_without_touching_the_game_folder_or_the_settings()
    {
        // The diagnostic flags (--render, --detect, ...) use this: Session's real state is exactly an unusable install.
        var (game, settings, _) = InstalledBeforeOpening(RunMode.D3D12);
        using var _ = game;
        var folderBefore = game.Game.Snapshot();
        string settingsBefore = File.ReadAllText(settings);
        Sta.Run(() =>
        {
            var w = Window(settings);
            w.ReadOnly = true;
            w.LoadScans([game.Scan]);

            w.SelectByName("Tick Game");

            Assert.Equal(folderBefore, game.Game.Snapshot());          // DXVK still installed
            Assert.Equal(settingsBefore, File.ReadAllText(settings));   // and no choice recorded on its behalf
            Assert.NotNull(DxvkInstaller.ReadManifest(game.ExeDir));
            Assert.False(w.DxvkBox.IsEnabled);                          // it is still shown greyed
            Assert.True(w.DxvkBox.IsChecked);
        });
        File.Delete(settings);
    }

    [Fact]
    public void A_hand_made_or_never_decided_game_has_no_remembered_choice_until_it_is_made()
    {
        using var game = new TickGame();
        string settings = Path.Combine(Path.GetTempPath(), $"SteamDxvkSettings_{Guid.NewGuid():N}.json");
        Sta.Run(() =>
        {
            var w = Ready(game, Window(settings));
            Assert.Null(new GameSettings(settings).GetUseDxvk(9100));   // selecting a game alone decides nothing
        });
        File.Delete(settings);
    }

    [Fact]
    public void An_install_from_before_choices_were_remembered_is_adopted_as_a_choice()
    {
        var (game, settings, _) = InstalledBeforeOpening(RunMode.D3D11);
        using var _ = game;
        Sta.Run(() =>
        {
            var w = Window(settings);
            w.LoadScans([game.Scan]);
            w.SelectByName("Tick Game");

            Assert.True(new GameSettings(settings).GetUseDxvk(9100));
        });
        File.Delete(settings);
    }

    [Fact]
    public void The_tick_is_greyed_for_vulkan_mode_too_and_a_leftover_install_is_removed()
    {
        var (game, settings, before) = InstalledBeforeOpening(RunMode.Vulkan);
        using var _ = game;
        Sta.Run(() =>
        {
            var w = Window(settings);
            w.LoadScans([game.Scan]);
            w.SelectByName("Tick Game");

            Assert.False(w.DxvkBox.IsEnabled);
            Assert.Equal(before, game.Game.Snapshot());
        });
        File.Delete(settings);
    }

    [Fact]
    public void A_game_found_to_use_only_d3d12_loses_a_leftover_install_too()
    {
        var (game, settings, before) = InstalledBeforeOpening(RunMode.Default, engine: "", apis: GfxApi.D3D12);
        using var _ = game;
        Sta.Run(() =>
        {
            var w = Window(settings);
            w.LoadScans([game.Scan]);
            w.SelectByName("Tick Game");

            Assert.False(w.DxvkBox.IsEnabled);
            Assert.Equal(before, game.Game.Snapshot());
        });
        File.Delete(settings);
    }

    [Fact]
    public void A_usable_install_is_left_alone_when_the_game_is_selected()
    {
        var (game, settings, _) = InstalledBeforeOpening(RunMode.D3D11);
        using var _ = game;
        Sta.Run(() =>
        {
            var w = Window(settings);
            w.LoadScans([game.Scan]);
            w.SelectByName("Tick Game");

            Assert.NotNull(DxvkInstaller.ReadManifest(game.ExeDir));
            Assert.True(w.DxvkBox.IsEnabled);
            Assert.True(w.DxvkBox.IsChecked);
            Assert.Equal(Visibility.Visible, w.DxvkOptionsPanel.Visibility);
        });
        File.Delete(settings);
    }

    [Fact]
    public void A_dxvk_the_app_did_not_install_is_never_removed_even_when_it_cannot_be_used()
    {
        using var game = new TempDir();
        game.Write(@"bin\Game-Win64-Shipping.exe", "exe");
        game.Write(@"bin\d3d11.dll", "MZ this is DXVK installed by hand");
        var scan = new GameScan(new Game(9200, "Hand Made", game.Path))
        {
            Exes = [new ExeInfo(game.Combine(@"bin\Game-Win64-Shipping.exe"), 64, 1, GfxApi.D3D11 | GfxApi.D3D12, GfxApi.D3D11 | GfxApi.D3D12)],
            Engine = "Unreal",
        };
        scan.Status = DxvkInstaller.StatusOf(scan);
        string settings = Path.Combine(Path.GetTempPath(), $"SteamDxvkSettings_{Guid.NewGuid():N}.json");
        new GameSettings(settings).SetRunMode(9200, RunMode.D3D12);
        Sta.Run(() =>
        {
            var w = Window(settings);
            w.LoadScans([scan]);
            w.SelectByName("Hand Made");

            Assert.True(File.Exists(game.Combine(@"bin\d3d11.dll")));
            Assert.False(w.DxvkBox.IsEnabled);
        });
        File.Delete(settings);
    }

    [Fact]
    public void Switching_run_as_between_dxvk_compatible_modes_leaves_dxvk_alone()
    {
        using var game = new TickGame(engine: "Unreal", apis: GfxApi.D3D11 | GfxApi.D3D12);
        Sta.Run(() =>
        {
            var w = Ready(game);
            UserSets(w.DxvkBox, true);

            w.SetRunMode(RunMode.Default);
            w.SetRunMode(RunMode.D3D11);

            Assert.NotNull(DxvkInstaller.ReadManifest(game.ExeDir));
            Assert.True(w.DxvkBox.IsEnabled);
        });
    }

    [Fact]
    public void Anti_cheat_games_ask_first_and_a_no_installs_nothing()
    {
        using var game = new TickGame(anticheat: ["EasyAntiCheat"]);
        var before = game.Game.Snapshot();
        var asked = new List<string>();
        Sta.Run(() =>
        {
            var w = Ready(game);
            w.ConfirmRisk = message => { asked.Add(message); return false; };

            UserSets(w.DxvkBox, true);

            Assert.Single(asked);
            Assert.Contains("EasyAntiCheat", asked[0]);
            Assert.Equal(before, game.Game.Snapshot());
            Assert.False(w.DxvkBox.IsChecked);   // the tick goes back: it only mirrors what is installed
        });
    }

    [Fact]
    public void Anti_cheat_games_install_after_a_yes()
    {
        using var game = new TickGame(anticheat: ["BattlEye"]);
        Sta.Run(() =>
        {
            var w = Ready(game);
            w.ConfirmRisk = _ => true;

            UserSets(w.DxvkBox, true);

            Assert.NotNull(DxvkInstaller.ReadManifest(game.ExeDir));
        });
    }

    [Fact]
    public void A_game_without_anti_cheat_is_never_asked()
    {
        using var game = new TickGame();
        bool asked = false;
        Sta.Run(() =>
        {
            var w = Ready(game);
            w.ConfirmRisk = _ => { asked = true; return false; };
            UserSets(w.DxvkBox, true);
            Assert.False(asked);
            Assert.NotNull(DxvkInstaller.ReadManifest(game.ExeDir));
        });
    }

    [Fact]
    public void A_tick_on_a_game_dxvk_cannot_serve_is_refused_and_the_tick_goes_back()
    {
        using var game = new TickGame(apis: GfxApi.D3D12);
        var before = game.Game.Snapshot();
        Sta.Run(() =>
        {
            var w = Ready(game);

            UserSets(w.DxvkBox, true);   // the box is greyed in the real UI; this proves the handler is safe regardless

            Assert.Equal(before, game.Game.Snapshot());
            Assert.False(w.DxvkBox.IsChecked);
            MainWindow.Pump();
            Assert.Contains("DXVK can't be used here", w.LogBox.Text);
        });
    }

    [Fact]
    public void A_failed_install_is_reported_and_the_tick_goes_back_to_the_truth()
    {
        using var game = new TickGame();
        Sta.Run(() =>
        {
            var w = Ready(game);
            w.LatestBuild = _ => throw new InvalidOperationException("offline and nothing cached");

            UserSets(w.DxvkBox, true);

            Assert.False(w.DxvkBox.IsChecked);
            Assert.True(w.DxvkBox.IsEnabled);   // and it can be tried again
            MainWindow.Pump();
            Assert.Contains("ERROR: offline and nothing cached", w.LogBox.Text);
        });
    }

    [Fact]
    public void Installing_into_a_running_game_reports_it_and_leaves_the_tick_off()
    {
        using var game = new TickGame();
        Sta.Run(() =>
        {
            var w = Ready(game);
            using var locked = new FileStream(Path.Combine(game.ExeDir, "dxgi.dll"), FileMode.Open, FileAccess.Read, FileShare.None);   // a running game holds its DLLs

            UserSets(w.DxvkBox, true);

            Assert.False(w.DxvkBox.IsChecked);
            MainWindow.Pump();
            Assert.Contains("is the game running?", w.LogBox.Text);
        });
    }

    // --------------------------------------------------------------------- launch

    /// <summary>A real DXVK install in temp folders plus a scan pointing at it (nothing of the user's is touched).</summary>
    private sealed class InstalledGame : IDisposable
    {
        public TempDir Game { get; } = new();
        public TempDir Build { get; } = new();
        public string ExeDir { get; }
        public GameScan Scan { get; }

        public InstalledGame(DxvkOptions? options = null, string engine = "", GfxApi apis = GfxApi.D3D11)
        {
            ExeDir = Game.Combine("bin");
            Directory.CreateDirectory(ExeDir);
            Game.Write(@"bin\Game.exe", "exe");
            foreach (var arch in new[] { "x32", "x64" })
                foreach (var dll in DxvkInstaller.AllDlls) Build.Write($@"{arch}\{dll}", $"{arch}-{dll}");
            DxvkInstaller.Install(ExeDir, Game.Path, 64, GfxApi.D3D11, options ?? new DxvkOptions(Async: true), Build.Path, "v1");
            Scan = new GameScan(new Game(4242, "Launch Test", Game.Path))
            {
                Exes = [new ExeInfo(Path.Combine(ExeDir, "Game.exe"), 64, 1, apis, apis)],
                Engine = engine,
            };
            Scan.Status = DxvkInstaller.StatusOf(Scan);
        }

        public void Dispose() { Game.Dispose(); Build.Dispose(); }
    }

    private static (List<string> Launched, List<(DxvkVariant, string)> Resolved) Wire(MainWindow w, InstalledGame? game = null)
    {
        var launched = new List<string>();
        var resolved = new List<(DxvkVariant, string)>();
        w.StartGame = (id, args) => launched.Add(TestData.Describe(id, args));
        w.ResolveBuild = (variant, version) =>
        {
            resolved.Add((variant, version));
            return Task.FromResult((game!.Build.Path, version));
        };
        return (launched, resolved);
    }

    [Fact]
    public void Launch_goes_through_steam_with_the_games_appid()
    {
        using var game = new InstalledGame();
        Sta.Run(() =>
        {
            var w = Window();
            w.LoadScans([game.Scan]);
            w.SelectByName("Launch Test");
            var (launched, resolved) = Wire(w, game);

            w.LaunchAsync().GetAwaiter().GetResult();

            Assert.Equal(["4242"], launched);
            Assert.Empty(resolved);   // intact install: nothing to repair, no build lookup
        });
    }

    [Fact]
    public void Launch_repairs_a_reverted_install_before_starting_the_game()
    {
        using var game = new InstalledGame();
        File.Delete(Path.Combine(game.ExeDir, "d3d11.dll"));   // what Steam verify might do
        File.Delete(game.Game.Combine("dxvk.conf"));

        Sta.Run(() =>
        {
            var w = Window();
            w.LoadScans([game.Scan]);
            w.SelectByName("Launch Test");
            var (launched, resolved) = Wire(w, game);
            var order = new List<string>();
            w.StartGame = (id, args) => { order.Add(DxvkInstaller.Verify(game.ExeDir).Count == 0 ? "launch-after-repair" : "launch-while-broken"); launched.Add(TestData.Describe(id, args)); };

            w.LaunchAsync().GetAwaiter().GetResult();

            Assert.Equal(["launch-after-repair"], order);   // the files were fixed first
            Assert.Equal([(DxvkVariant.Async, "v1")], resolved);   // repaired from the same build it was installed from
            Assert.True(File.Exists(Path.Combine(game.ExeDir, "d3d11.dll")));
            Assert.Contains("dxvk.enableAsync = True", File.ReadAllText(game.Game.Combine("dxvk.conf")));
        });
    }

    [Fact]
    public void Launch_works_for_a_game_without_dxvk_and_touches_nothing()
    {
        using var folder = new TempDir();
        var scan = TestData.Scan("Plain Game", GfxApi.D3D11, GfxApi.D3D11, root: folder.Path);
        Sta.Run(() =>
        {
            var w = Window();
            w.LoadScans([scan]);
            w.SelectByName("Plain");
            var (launched, resolved) = Wire(w);

            w.LaunchAsync().GetAwaiter().GetResult();

            Assert.Equal([$"{scan.AppId}"], launched);
            Assert.Empty(resolved);
        });
    }

    [Fact]
    public void A_failed_repair_blocks_the_launch_and_the_error_is_shown_in_the_log()
    {
        using var game = new InstalledGame();
        File.Delete(Path.Combine(game.ExeDir, "d3d11.dll"));
        Sta.Run(() =>
        {
            var w = Window();
            w.LoadScans([game.Scan]);
            w.SelectByName("Launch Test");
            var launched = new List<string>();
            w.StartGame = (id, args) => launched.Add(TestData.Describe(id, args));
            w.ResolveBuild = (_, _) => throw new InvalidOperationException("offline and nothing cached");

            w.LaunchAsync().GetAwaiter().GetResult();

            MainWindow.Pump();   // log lines are posted to the dispatcher
            Assert.Empty(launched);   // a broken install is not launched silently
            Assert.Contains("offline and nothing cached", w.LogBox.Text);
        });
    }

    [Fact]
    public void Launch_button_needs_a_selection_and_is_off_while_busy()
    {
        Sta.Run(() =>
        {
            var w = Window();
            w.LoadScans(MockLibrary());
            Assert.False(w.LaunchBtn.IsEnabled);
            w.SelectByName("Beta");   // even a D3D12-only game can be launched; it just has nothing to install
            Assert.True(w.LaunchBtn.IsEnabled);
        });
    }

    [Fact]
    public void Launch_does_nothing_without_a_selection()
    {
        Sta.Run(() =>
        {
            var w = Window();
            w.LoadScans(MockLibrary());
            var (launched, _) = Wire(w);
            w.LaunchAsync().GetAwaiter().GetResult();
            Assert.Empty(launched);
        });
    }

    // ----------------------------------------------------------------------- run as

    private static List<string> RunChoices(MainWindow w) => w.RunBox.Items.Cast<RunChoice>().Select(c => c.Mode.ToString()).ToList();

    private static string SelectedMode(MainWindow w) => ((RunChoice)w.RunBox.SelectedItem).Mode.ToString();

    [Fact]
    public void Run_as_offers_only_what_the_engine_can_apply()
    {
        Sta.Run(() =>
        {
            var w = Window();
            w.LoadScans(MockLibrary());

            w.SelectByName("Gamma");   // Unreal
            Assert.Equal(["Default", "D3D11", "D3D12", "Vulkan"], RunChoices(w));

            w.SelectByName("Alpha");   // engine unknown: nothing to pass, so only Default
            Assert.Equal(["Default"], RunChoices(w));
        });
    }

    [Fact]
    public void Run_as_is_only_shown_for_engines_with_launch_flags()
    {
        Sta.Run(() =>
        {
            var w = Window();
            w.LoadScans(MockLibrary());

            w.SelectByName("Alpha");   // plain D3D11, unknown engine: nothing to choose
            Assert.Equal(Visibility.Hidden, w.RunBox.Visibility);
            Assert.Equal(Visibility.Hidden, w.RunLabel.Visibility);

            w.SelectByName("Gamma");   // Unreal with several renderers: Run as matters
            Assert.Equal(Visibility.Visible, w.RunBox.Visibility);
            Assert.Equal(Visibility.Visible, w.RunLabel.Visibility);
        });
    }

    [Fact]
    public void Run_as_is_saved_per_game_and_restored_when_the_game_is_selected_again()
    {
        string settings = Path.Combine(Path.GetTempPath(), $"SteamDxvkSettings_{Guid.NewGuid():N}.json");
        var library = MockLibrary();
        int gammaId = library.First(s => s.Name.StartsWith("Gamma")).AppId;

        Sta.Run(() =>
        {
            var w = Window(settings);
            w.LoadScans(library);
            w.SelectByName("Gamma");
            w.SetRunMode(RunMode.D3D11);

            Assert.Equal(RunMode.D3D11, new GameSettings(settings).GetRunMode(gammaId));   // written straight away

            w.SelectByName("Alpha");
            Assert.Equal("Default", SelectedMode(w));
            w.SelectByName("Gamma");
            Assert.Equal("D3D11", SelectedMode(w));
        });

        // and a fresh window (next app start) still has it
        Sta.Run(() =>
        {
            var w = Window(settings);
            w.LoadScans(library);
            w.SelectByName("Gamma");
            Assert.Equal("D3D11", SelectedMode(w));
        });
        File.Delete(settings);
    }

    [Fact]
    public void A_saved_mode_the_engine_cannot_apply_falls_back_to_default()
    {
        string settings = Path.Combine(Path.GetTempPath(), $"SteamDxvkSettings_{Guid.NewGuid():N}.json");
        var library = MockLibrary();
        new GameSettings(settings).SetRunMode(library.First(s => s.Name.StartsWith("Alpha")).AppId, RunMode.D3D11);   // Alpha has no known engine

        Sta.Run(() =>
        {
            var w = Window(settings);
            w.LoadScans(library);
            w.SelectByName("Alpha");
            Assert.Equal("Default", SelectedMode(w));
        });
        File.Delete(settings);
    }

    [Fact]
    public void Launch_passes_the_run_as_flag_to_steam()
    {
        using var game = new InstalledGame(engine: "Unreal", apis: GfxApi.D3D11 | GfxApi.D3D12);
        Sta.Run(() =>
        {
            var w = Window();
            w.LoadScans([game.Scan]);
            w.SelectByName("Launch Test");
            w.SetRunMode(RunMode.D3D11);
            var (launched, _) = Wire(w, game);

            w.LaunchAsync().GetAwaiter().GetResult();

            Assert.Equal([$"{game.Scan.AppId} -dx11"], launched);
        });
    }

    [Fact]
    public void Launch_uses_the_plain_url_when_run_as_is_default()
    {
        using var game = new InstalledGame(engine: "Unreal", apis: GfxApi.D3D11);
        Sta.Run(() =>
        {
            var w = Window();
            w.LoadScans([game.Scan]);
            w.SelectByName("Launch Test");
            var (launched, _) = Wire(w, game);

            w.LaunchAsync().GetAwaiter().GetResult();

            Assert.Equal([$"{game.Scan.AppId}"], launched);
        });
    }

    [Fact]
    public void Run_as_d3d12_removes_a_dxvk_the_app_installed_so_the_launch_can_go_ahead()
    {
        using var game = new InstalledGame(engine: "Unreal", apis: GfxApi.D3D11 | GfxApi.D3D12);
        Sta.Run(() =>
        {
            var w = Window();
            w.LoadScans([game.Scan]);
            w.SelectByName("Launch Test");
            var (launched, _) = Wire(w, game);

            w.SetRunMode(RunMode.D3D12);   // DXVK would crash a D3D12 game, so the app takes it out

            Assert.Null(DxvkInstaller.ReadManifest(game.ExeDir));
            Assert.True(w.LaunchBtn.IsEnabled);
            w.LaunchAsync().GetAwaiter().GetResult();
            Assert.Equal([$"{game.Scan.AppId} -dx12"], launched);
        });
    }

    [Fact]
    public void Run_as_d3d12_still_cannot_launch_over_a_dxvk_the_app_cannot_remove_and_says_why()
    {
        using var game = new TempDir();
        game.Write(@"bin\Game-Win64-Shipping.exe", "exe");
        game.Write(@"bin\d3d11.dll", "MZ this is DXVK installed by hand");   // not ours: no manifest, so the app must not touch it
        var scan = new GameScan(new Game(321, "Hand Made", game.Path))
        {
            Exes = [new ExeInfo(game.Combine(@"bin\Game-Win64-Shipping.exe"), 64, 1, GfxApi.D3D11 | GfxApi.D3D12, GfxApi.D3D11 | GfxApi.D3D12)],
            Engine = "Unreal",
        };
        scan.Status = DxvkInstaller.StatusOf(scan);
        Sta.Run(() =>
        {
            var w = Window();
            w.LoadScans([scan]);
            w.SelectByName("Hand Made");
            var launched = new List<string>();
            w.StartGame = (id, args) => launched.Add(TestData.Describe(id, args));

            w.SetRunMode(RunMode.D3D12);

            Assert.True(File.Exists(game.Combine(@"bin\d3d11.dll")));   // left alone
            Assert.False(w.LaunchBtn.IsEnabled);                       // the button is off, with the reason on screen
            Assert.Contains("dxgi.dll", new System.Windows.Documents.TextRange(w.WarningText.ContentStart, w.WarningText.ContentEnd).Text);

            w.LaunchAsync().GetAwaiter().GetResult();   // and calling it anyway still refuses

            MainWindow.Pump();
            Assert.Empty(launched);
            Assert.Contains("Not launching", w.LogBox.Text);
        });
    }

    [Fact]
    public void Default_mode_with_dxvk_on_a_d3d12_capable_game_warns_on_screen_but_can_still_launch()
    {
        using var game = new InstalledGame(engine: "Unreal", apis: GfxApi.D3D11 | GfxApi.D3D12);
        Sta.Run(() =>
        {
            var w = Window();
            w.LoadScans([game.Scan]);
            w.SelectByName("Launch Test");

            Assert.True(w.LaunchBtn.IsEnabled);
            Assert.Contains("Run as to D3D11", new System.Windows.Documents.TextRange(w.WarningText.ContentStart, w.WarningText.ContentEnd).Text);
        });
    }

    [Fact]
    public void Run_as_d3d11_makes_dxvk_available_for_a_game_that_only_showed_d3d12()
    {
        var scan = TestData.Scan("Unreal12", GfxApi.D3D12, GfxApi.D3D12, engine: "Unreal");
        Sta.Run(() =>
        {
            var w = Window();
            w.LoadScans([scan]);
            w.SelectByName("Unreal12");
            Assert.False(w.DxvkBox.IsEnabled);   // detection says D3D12 only: the tick is greyed

            w.SetRunMode(RunMode.D3D11);
            Assert.True(w.DxvkBox.IsEnabled);    // the user has said it will run as D3D11
        });
    }

    [Fact]
    public void Check_last_run_reports_what_dxvks_log_says()
    {
        using var game = new InstalledGame(engine: "Unreal");
        game.Game.Write(@"bin\Game-Win64-Shipping_d3d11.log", "info:  Game: Game.exe\ninfo:  Found device: Test GPU\n");
        Sta.Run(() =>
        {
            var w = Window();
            w.LoadScans([game.Scan]);
            w.SelectByName("Launch Test");

            w.CheckLastRun();

            MainWindow.Pump();
            Assert.Contains("DXVK is rendering D3D11 on Test GPU", w.LogBox.Text);
        });
    }

    [Fact]
    public void Check_last_run_without_dxvk_says_there_is_nothing_to_read()
    {
        Sta.Run(() =>
        {
            var w = Window();
            w.LoadScans(MockLibrary());
            w.SelectByName("Alpha");
            Assert.True(w.CheckBtn.IsEnabled);

            w.CheckLastRun();

            MainWindow.Pump();
            Assert.Contains("DXVK isn't installed", w.LogBox.Text);
        });
    }

    [Fact]
    public void Check_last_run_only_considers_logs_written_after_the_launch_made_from_the_app()
    {
        using var game = new InstalledGame(engine: "Unreal");
        string stale = game.Game.Write(@"bin\Game-Win64-Shipping_d3d11.log", "info:  Game: Game.exe\ninfo:  Found device: Old GPU\n");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));
        Sta.Run(() =>
        {
            var w = Window();
            w.LoadScans([game.Scan]);
            w.SelectByName("Launch Test");
            var (launched, _) = Wire(w, game);
            w.LaunchAsync().GetAwaiter().GetResult();   // launching stamps the time

            w.CheckLastRun();

            MainWindow.Pump();
            Assert.Contains("No DXVK log was written since", w.LogBox.Text);   // the 2-hour-old log is from an earlier run
        });
    }

    [Fact]
    public void Steam_launch_url_is_the_rungameid_protocol() => Assert.Equal("steam://rungameid/1849250", SteamLaunch.Url(1849250));

    [Fact]
    public void Dropdowns_open_without_error_in_a_shown_window()
    {
        Sta.Run(() =>
        {
            var w = Window();
            w.LoadScans(MockLibrary());
            w.Show();   // popups need a real window
            w.SelectByName("Gamma");
            w.ExerciseControls();
            w.Close();
        });
    }

    [Fact]
    public void Real_window_capture_via_the_shared_render_helper()
    {
        string path = RenderHelper.CaptureWpfWindow(() =>
        {
            var w = Window();
            w.LoadScans(MockLibrary());
            w.SelectByName("Delta");
            return w;
        }, "SteamDxvk_window");

        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 5_000, "capture looks blank");
    }

    [Fact]
    public void Scan_report_lists_games_sorted_with_anti_cheat_and_dxvk_notes()
    {
        string report = ScanReport.Format(MockLibrary());
        var lines = report.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.StartsWith("API column", lines[0]);
        Assert.Contains("Alpha Racing", lines[2]);
        Assert.Contains(lines, l => l.Contains("Beta Souls") && l.Contains("[anti-cheat: EasyAntiCheat, BattlEye]"));
        Assert.Contains(lines, l => l.Contains("Eta Manual") && l.Contains("DXVK (manual)"));
        Assert.Contains(lines, l => l.Contains("Zeta Mystery") && l.Contains("?"));
    }
}
