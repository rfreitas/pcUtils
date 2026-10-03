using System.IO;
using SteamDxvk;
using Xunit;

namespace SteamDxvk.Tests;

public class GameScannerTests
{
    private const int Big = 1_200_000;   // above the scanner's 1 MB "real exe" floor

    private static GameScan Scan(TempDir dir, ScanCache? cache = null) =>
        GameScanner.Scan(new Game(1, "Test", dir.Path), cache ?? new ScanCache(dir.Combine("cache.json")));

    [Fact]
    public void Unreal_game_picks_the_shipping_exe_and_reads_its_imports()
    {
        using var dir = new TempDir();
        dir.Write(@"Game.exe", TestPe.Build(minSize: 200_000));   // tiny launcher stub
        dir.Write(@"Game\Binaries\Win64\Game-Win64-Shipping.exe",
            TestPe.Build(imports: ["d3d11.dll"], delayImports: ["d3d12.dll"], minSize: Big, trailer: TestPe.Ascii("vulkan-1.dll")));

        var scan = Scan(dir);

        Assert.Equal("Unreal", scan.Engine);
        var exe = Assert.Single(scan.Exes);
        Assert.EndsWith("Game-Win64-Shipping.exe", exe.Path);
        Assert.Equal(GfxApi.D3D11 | GfxApi.D3D12, exe.Imports);
        Assert.Equal(GfxApi.D3D11 | GfxApi.D3D12 | GfxApi.Vulkan, exe.Apis);   // Vulkan only mentioned in a string
        Assert.Equal(64, exe.Bits);
    }

    [Fact]
    public void Unity_stub_exe_is_included_and_UnityPlayer_dll_supplies_the_apis()
    {
        using var dir = new TempDir();
        dir.Write("Game.exe", TestPe.Build(minSize: 650_000));   // under 1 MB, as real Unity stubs are
        dir.Write("UnityPlayer.dll", TestPe.Build(trailer: TestPe.Ascii("d3d11.dll")));

        var scan = Scan(dir);

        Assert.Equal("Unity", scan.Engine);
        Assert.Equal(GfxApi.D3D11, Assert.Single(scan.Exes).Apis);
    }

    [Fact]
    public void Small_exes_without_a_Unity_player_are_ignored()
    {
        using var dir = new TempDir();
        dir.Write("tool.exe", TestPe.Build(minSize: 650_000, imports: ["d3d11.dll"]));
        Assert.Empty(Scan(dir).Exes);
    }

    [Fact]
    public void The_biggest_exe_wins_over_a_launcher_that_merely_mentions_D3D()
    {
        using var dir = new TempDir();
        dir.Write("idTechLauncher.exe", TestPe.Build(imports: ["d3d11.dll"], minSize: Big));
        dir.Write("Game.exe", TestPe.Build(imports: ["vulkan-1.dll"], minSize: Big * 3));

        var scan = Scan(dir);

        Assert.EndsWith("Game.exe", scan.Primary!.Path);
        Assert.Equal(GfxApi.Vulkan, scan.Primary.Imports);
    }

    [Fact]
    public void Engine_exes_outrank_bigger_helpers()
    {
        using var dir = new TempDir();
        dir.Write("Game.exe", TestPe.Build(minSize: 650_000, imports: ["opengl32.dll"]));
        dir.Write("UnityPlayer.dll", TestPe.Build());
        dir.Write(@"Tools\encoder.exe", TestPe.Build(minSize: Big * 4, imports: ["d3d9.dll"]));

        Assert.EndsWith("Game.exe", Scan(dir).Primary!.Path);
    }

    [Fact]
    public void Excluded_names_and_dirs_never_become_the_game_exe()
    {
        using var dir = new TempDir();
        dir.Write("Game.exe", TestPe.Build(imports: ["d3d12.dll"], minSize: Big));
        dir.Write("start_protected_game.exe", TestPe.Build(imports: ["d3d11.dll"], minSize: Big * 2));
        dir.Write("UnityCrashHandler64.exe", TestPe.Build(minSize: Big * 2));
        dir.Write(@"Game_Data\StreamingAssets\FFmpeg\ffmpeg.exe", TestPe.Build(imports: ["d3d9.dll"], minSize: Big * 5));
        dir.Write(@"_CommonRedist\vcredist.exe", TestPe.Build(minSize: Big * 5));

        var scan = Scan(dir);

        Assert.EndsWith("Game.exe", Assert.Single(scan.Exes).Path);
    }

    [Fact]
    public void A_launcher_exe_borrows_the_apis_of_a_sibling_dll()
    {
        using var dir = new TempDir();
        dir.Write("hl2.exe", TestPe.Build(minSize: Big));
        dir.Write("engine.dll", TestPe.Build(minSize: Big, trailer: TestPe.Ascii("d3d9.dll")));
        dir.Write("steam_api64.dll", TestPe.Build(minSize: Big, trailer: TestPe.Ascii("d3d12.dll")));   // excluded name: must not count

        Assert.Equal(GfxApi.D3D9, Scan(dir).Primary!.Apis);
    }

    [Fact]
    public void Anti_cheat_is_detected_by_file_and_folder_names()
    {
        using var dir = new TempDir();
        dir.Write("Game.exe", TestPe.Build(minSize: Big));
        dir.Write(@"EasyAntiCheat\EasyAntiCheat_Setup.exe", TestPe.Build());
        dir.Write(@"BattlEye\BEClient_x64.dll", [1, 2, 3]);

        Assert.Equal(["BattlEye", "EasyAntiCheat"], Scan(dir).AntiCheat);
    }

    [Fact]
    public void No_anti_cheat_means_an_empty_list()
    {
        using var dir = new TempDir();
        dir.Write("Game.exe", TestPe.Build(minSize: Big));
        Assert.Empty(Scan(dir).AntiCheat);
    }

    [Fact]
    public void Folders_deeper_than_the_limit_are_not_searched()
    {
        using var dir = new TempDir();
        dir.Write(@"a\b\c\d\e\Deep.exe", TestPe.Build(minSize: Big));
        Assert.Empty(Scan(dir).Exes);
    }

    [Fact]
    public void An_empty_or_missing_game_folder_gives_an_empty_scan()
    {
        using var dir = new TempDir();
        Assert.Empty(Scan(dir).Exes);
        Assert.Empty(GameScanner.Scan(new Game(2, "Gone", dir.Combine("not-there")), new ScanCache(dir.Combine("c.json"))).Exes);
    }

    [Theory]
    [InlineData(GfxApi.D3D11, GfxApi.D3D11 | GfxApi.D3D12 | GfxApi.Vulkan, "D3D11 (+D3D12, Vulkan)")]
    [InlineData(GfxApi.D3D11 | GfxApi.D3D12, GfxApi.D3D11 | GfxApi.D3D12 | GfxApi.Vulkan, "D3D11, D3D12 (+Vulkan)")]
    [InlineData(GfxApi.D3D11 | GfxApi.D3D12, GfxApi.D3D11 | GfxApi.D3D12, "D3D11, D3D12")]
    [InlineData(GfxApi.None, GfxApi.D3D12 | GfxApi.Vulkan, "(D3D12, Vulkan)")]
    [InlineData(GfxApi.None, GfxApi.None, "?")]
    public void ApiSummary_marks_string_only_mentions(GfxApi imports, GfxApi all, string expected) =>
        Assert.Equal(expected, GameScanner.ApiSummary(new ExeInfo("x.exe", 64, 1, all, imports)));

    [Fact]
    public void ApiSummary_of_nothing_is_a_question_mark() => Assert.Equal("?", GameScanner.ApiSummary(null));

    [Fact]
    public void Cache_returns_the_same_answer_persists_and_prunes_untouched_entries()
    {
        using var dir = new TempDir();
        string a = dir.Write("a.exe", TestPe.Build(imports: ["d3d11.dll"]));
        string b = dir.Write("b.exe", TestPe.Build(imports: ["d3d9.dll"]));
        string cachePath = dir.Combine("cache.json");

        var first = new ScanCache(cachePath);
        var fresh = first.Info(a);
        first.Info(b);
        first.Save();

        var second = new ScanCache(cachePath);
        Assert.Equal(fresh, second.Info(a));   // served from the file, same value
        second.Save();                          // only 'a' was touched this session: 'b' is pruned

        string json = File.ReadAllText(cachePath);
        Assert.Contains(a.Replace(@"\", @"\\"), json);   // JSON escapes the backslashes in the key
        Assert.DoesNotContain(b.Replace(@"\", @"\\"), json);
    }

    [Fact]
    public void Cache_notices_a_changed_file()
    {
        using var dir = new TempDir();
        string exe = dir.Write("a.exe", TestPe.Build(imports: ["d3d11.dll"]));
        var cache = new ScanCache(dir.Combine("cache.json"));
        Assert.Equal(GfxApi.D3D11, cache.Info(exe).Apis);

        File.WriteAllBytes(exe, TestPe.Build(imports: ["d3d12.dll"], minSize: 5000));   // size changed -> new key
        Assert.Equal(GfxApi.D3D12, cache.Info(exe).Apis);
    }

    [Fact]
    public void Cache_survives_a_corrupt_file_and_a_missing_binary()
    {
        using var dir = new TempDir();
        dir.Write("cache.json", "{ this is not json");
        var cache = new ScanCache(dir.Combine("cache.json"));
        Assert.Equal(default, cache.Info(dir.Combine("missing.exe")));
    }
}
