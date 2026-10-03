using System.IO;
using SteamDxvk;
using Xunit;

namespace SteamDxvk.Tests;

public class DxvkInstallerTests
{
    private const string Version = "v9.9.9";

    /// <summary>A fake extracted DXVK build: distinct content per arch and DLL so copies can be told apart.</summary>
    private static TempDir FakeBuild()
    {
        var build = new TempDir();
        foreach (var arch in new[] { "x32", "x64" })
            foreach (var dll in DxvkInstaller.AllDlls)
                build.Write($@"{arch}\{dll}", $"DXVK-{arch}-{dll}");
        return build;
    }

    private static string Install(TempDir build, string exeDir, string root, GfxApi apis, DxvkOptions? opts = null, int bits = 64, Action<string>? log = null) =>
        DxvkInstaller.Install(exeDir, root, bits, apis, opts ?? new DxvkOptions(), build.Path, Version, log);

    /// <summary>A game laid out like Unreal's: exe deep in Binaries, game root above it.</summary>
    private static (TempDir Game, string ExeDir) Game()
    {
        var game = new TempDir();
        string exeDir = game.Combine(@"Binaries\Win64");
        Directory.CreateDirectory(exeDir);
        game.Write(@"Binaries\Win64\Game.exe", "exe");
        return (game, exeDir);
    }

    // ----------------------------------------------------------------- pure helpers

    [Theory]
    [InlineData(GfxApi.D3D9, "d3d9.dll")]
    [InlineData(GfxApi.D3D11, "d3d10core.dll,d3d11.dll,dxgi.dll")]
    [InlineData(GfxApi.D3D10, "d3d10core.dll,d3d11.dll,dxgi.dll")]
    [InlineData(GfxApi.D3D8, "d3d8.dll,d3d9.dll")]
    [InlineData(GfxApi.D3D9 | GfxApi.D3D11, "d3d10core.dll,d3d11.dll,d3d9.dll,dxgi.dll")]
    [InlineData(GfxApi.D3D12, "")]
    [InlineData(GfxApi.Vulkan | GfxApi.OpenGL, "")]
    public void DllsFor_picks_the_right_set(GfxApi apis, string expected) =>
        Assert.Equal(expected, string.Join(",", DxvkInstaller.DllsFor(apis)));

    [Fact] public void ConfLines_default_is_empty() => Assert.Empty(DxvkInstaller.ConfLines(new DxvkOptions()));

    [Fact]
    public void ConfLines_cover_every_option() =>
        Assert.Equal(
            ["dxvk.enableAsync = True", "dxvk.enableGraphicsPipelineLibrary = False", "dxvk.hud = compiler"],
            DxvkInstaller.ConfLines(new DxvkOptions(Async: true, Gpl: "False", Hud: true)));

    [Fact]
    public void Variant_follows_the_async_flag()
    {
        Assert.Equal(DxvkVariant.Official, new DxvkOptions().Variant);
        Assert.Equal(DxvkVariant.Async, new DxvkOptions(Async: true).Variant);
    }

    // --------------------------------------------------------------------- install

    [Fact]
    public void Install_copies_the_right_arch_and_only_the_needed_dlls()
    {
        using var build = FakeBuild();
        var (game, exeDir) = Game();
        using var _ = game;

        Install(build, exeDir, game.Path, GfxApi.D3D9, bits: 32);

        Assert.Equal("DXVK-x32-d3d9.dll", File.ReadAllText(Path.Combine(exeDir, "d3d9.dll")));
        Assert.False(File.Exists(Path.Combine(exeDir, "dxgi.dll")));
    }

    [Fact]
    public void Install_64_bit_d3d11_uses_x64_dlls()
    {
        using var build = FakeBuild();
        var (game, exeDir) = Game();
        using var _ = game;

        Install(build, exeDir, game.Path, GfxApi.D3D11);

        foreach (var dll in new[] { "d3d10core.dll", "d3d11.dll", "dxgi.dll" })
            Assert.Equal($"DXVK-x64-{dll}", File.ReadAllText(Path.Combine(exeDir, dll)));
    }

    [Fact]
    public void Install_and_uninstall_restore_the_folder_byte_for_byte()
    {
        using var build = FakeBuild();
        var (game, exeDir) = Game();
        using var _ = game;
        game.Write(@"Binaries\Win64\dxgi.dll", "ORIGINAL-DXGI");      // must be backed up and restored
        game.Write("dxvk.conf", "dxvk.hud = fps\n# my own line\n");     // must survive our block
        var before = game.Snapshot();

        Install(build, exeDir, game.Path, GfxApi.D3D11);
        Assert.Equal("ORIGINAL-DXGI", File.ReadAllText(Path.Combine(exeDir, DxvkInstaller.BackupDir, "dxgi.dll")));
        Assert.NotEqual("ORIGINAL-DXGI", File.ReadAllText(Path.Combine(exeDir, "dxgi.dll")));

        Install(build, exeDir, game.Path, GfxApi.D3D11, new DxvkOptions(Async: true, Hud: true));   // reconfigure in place
        DxvkInstaller.Uninstall(exeDir);

        Assert.Equal(before, game.Snapshot());
    }

    [Fact]
    public void Config_block_is_replaced_not_duplicated_and_user_lines_are_kept()
    {
        using var build = FakeBuild();
        var (game, exeDir) = Game();
        using var _ = game;
        game.Write("dxvk.conf", "dxvk.hud = fps\n");

        Install(build, exeDir, game.Path, GfxApi.D3D11, new DxvkOptions(Async: true));
        Install(build, exeDir, game.Path, GfxApi.D3D11, new DxvkOptions(Async: true, Gpl: "True"));

        string conf = File.ReadAllText(game.Combine("dxvk.conf"));
        Assert.StartsWith("dxvk.hud = fps", conf);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(conf, "steam-dxvk-manager \\(managed"));
        Assert.Contains("dxvk.enableGraphicsPipelineLibrary = True", conf);
        Assert.Contains("dxvk.enableAsync = True", conf);
    }

    [Fact]
    public void Config_is_written_to_the_exe_folder_and_the_game_root()
    {
        using var build = FakeBuild();
        var (game, exeDir) = Game();
        using var _ = game;

        Install(build, exeDir, game.Path, GfxApi.D3D11, new DxvkOptions(Async: true));

        Assert.Contains("dxvk.enableAsync = True", File.ReadAllText(Path.Combine(exeDir, "dxvk.conf")));
        Assert.Contains("dxvk.enableAsync = True", File.ReadAllText(game.Combine("dxvk.conf")));
    }

    [Fact]
    public void Plain_official_install_writes_no_config_and_uninstall_leaves_no_trace()
    {
        using var build = FakeBuild();
        var (game, exeDir) = Game();
        using var _ = game;
        var before = game.Snapshot();

        Install(build, exeDir, game.Path, GfxApi.D3D11);
        Assert.False(File.Exists(game.Combine("dxvk.conf")));

        Install(build, exeDir, game.Path, GfxApi.D3D11, new DxvkOptions(Async: true));   // now a config exists...
        Assert.True(File.Exists(game.Combine("dxvk.conf")));

        DxvkInstaller.Uninstall(exeDir);                                                   // ...and is removed again
        Assert.Equal(before, game.Snapshot());
    }

    [Fact]
    public void Config_with_windows_line_endings_round_trips()
    {
        using var build = FakeBuild();
        var (game, exeDir) = Game();
        using var _ = game;
        game.Write("dxvk.conf", "dxvk.hud = fps\r\n# crlf file\r\n");
        var before = game.Snapshot();

        Install(build, exeDir, game.Path, GfxApi.D3D11, new DxvkOptions(Hud: true));
        Assert.DoesNotContain("\n\n", File.ReadAllText(game.Combine("dxvk.conf")).Replace("\r\n", "\r"));   // stayed CRLF throughout
        DxvkInstaller.Uninstall(exeDir);

        Assert.Equal(before, game.Snapshot());
    }

    [Fact]
    public void Reconfiguring_to_fewer_apis_removes_dlls_no_longer_needed()
    {
        using var build = FakeBuild();
        var (game, exeDir) = Game();
        using var _ = game;

        Install(build, exeDir, game.Path, GfxApi.D3D11);
        Install(build, exeDir, game.Path, GfxApi.D3D9);

        Assert.True(File.Exists(Path.Combine(exeDir, "d3d9.dll")));
        Assert.False(File.Exists(Path.Combine(exeDir, "d3d11.dll")));
        Assert.False(File.Exists(Path.Combine(exeDir, "dxgi.dll")));
    }

    [Fact]
    public void A_dll_changed_after_install_is_left_alone_on_uninstall()
    {
        using var build = FakeBuild();
        var (game, exeDir) = Game();
        using var _ = game;
        game.Write(@"Binaries\Win64\dxgi.dll", "ORIGINAL-DXGI");
        Install(build, exeDir, game.Path, GfxApi.D3D11);
        File.WriteAllText(Path.Combine(exeDir, "dxgi.dll"), "CHANGED-BY-STEAM-VERIFY");
        var messages = new List<string>();

        DxvkInstaller.Uninstall(exeDir, messages.Add);

        Assert.Equal("CHANGED-BY-STEAM-VERIFY", File.ReadAllText(Path.Combine(exeDir, "dxgi.dll")));
        Assert.Contains(messages, m => m.Contains("changed since install"));
        Assert.False(File.Exists(Path.Combine(exeDir, "d3d11.dll")));      // the untouched ones still go
        Assert.Null(DxvkInstaller.ReadManifest(exeDir));
    }

    [Fact]
    public void Install_for_d3d12_only_is_rejected()
    {
        using var build = FakeBuild();
        var (game, exeDir) = Game();
        using var _ = game;
        Assert.Throws<ArgumentException>(() => Install(build, exeDir, game.Path, GfxApi.D3D12));
        Assert.Empty(Directory.GetFiles(exeDir, "*.dll"));
    }

    [Fact]
    public void Install_into_a_locked_dll_explains_the_game_is_probably_running()
    {
        using var build = FakeBuild();
        var (game, exeDir) = Game();
        using var _ = game;
        string dest = game.Write(@"Binaries\Win64\dxgi.dll", "IN-USE");
        using var lockHandle = new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.None);

        var ex = Assert.Throws<InvalidOperationException>(() => Install(build, exeDir, game.Path, GfxApi.D3D11));
        Assert.Contains("running", ex.Message);
    }

    [Fact]
    public void A_failed_install_leaves_the_folder_exactly_as_it_was_and_is_not_mistaken_for_a_manual_install()
    {
        using var build = FakeBuild();
        var (game, exeDir) = Game();
        using var _ = game;
        game.Write(@"Binaries\Win64\dxgi.dll", "ORIGINAL-DXGI");
        game.Write(@"Binaries\Win64\d3d11.dll", "ORIGINAL-D3D11");   // backed up, then put back by the rollback
        var before = game.Snapshot();

        using (var running = new FileStream(Path.Combine(exeDir, "dxgi.dll"), FileMode.Open, FileAccess.Read, FileShare.None))   // the game holds it
            Assert.Throws<InvalidOperationException>(() => Install(build, exeDir, game.Path, GfxApi.D3D11));

        // d3d10core.dll and d3d11.dll were already copied when dxgi.dll failed: they must be taken back out.
        Assert.Equal(before, game.Snapshot());
        Assert.Null(DxvkInstaller.ReadManifest(exeDir));
        Assert.Null(DxvkInstaller.Status(exeDir));
    }

    [Fact]
    public void A_failed_install_can_simply_be_retried()
    {
        using var build = FakeBuild();
        var (game, exeDir) = Game();
        using var _ = game;
        game.Write(@"Binaries\Win64\dxgi.dll", "ORIGINAL-DXGI");
        var before = game.Snapshot();
        using (var running = new FileStream(Path.Combine(exeDir, "dxgi.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<InvalidOperationException>(() => Install(build, exeDir, game.Path, GfxApi.D3D11));

        Install(build, exeDir, game.Path, GfxApi.D3D11);   // the game was closed

        Assert.Empty(DxvkInstaller.Verify(exeDir));
        DxvkInstaller.Uninstall(exeDir);
        Assert.Equal(before, game.Snapshot());
    }

    [Fact]
    public void Uninstall_without_an_install_is_an_error()
    {
        var (game, exeDir) = Game();
        using var _ = game;
        Assert.Throws<InvalidOperationException>(() => DxvkInstaller.Uninstall(exeDir));
    }

    [Fact]
    public void Install_logs_each_step()
    {
        using var build = FakeBuild();
        var (game, exeDir) = Game();
        using var _ = game;
        game.Write(@"Binaries\Win64\dxgi.dll", "ORIGINAL");
        var messages = new List<string>();

        Install(build, exeDir, game.Path, GfxApi.D3D11, log: messages.Add);

        Assert.Contains("  backed up existing dxgi.dll", messages);
        Assert.Contains("  installed x64/d3d11.dll", messages);
    }

    // ------------------------------------------------------------- verify / repair

    [Fact]
    public void Verify_of_a_fresh_install_finds_nothing_wrong()
    {
        using var build = FakeBuild();
        var (game, exeDir) = Game();
        using var _ = game;
        Install(build, exeDir, game.Path, GfxApi.D3D11, new DxvkOptions(Async: true, Hud: true));
        Assert.Empty(DxvkInstaller.Verify(exeDir));
    }

    [Fact]
    public void Verify_reports_a_missing_dll_a_replaced_dll_and_a_lost_config()
    {
        using var build = FakeBuild();
        var (game, exeDir) = Game();
        using var _ = game;
        Install(build, exeDir, game.Path, GfxApi.D3D11, new DxvkOptions(Async: true));

        File.Delete(Path.Combine(exeDir, "d3d11.dll"));
        File.WriteAllText(Path.Combine(exeDir, "dxgi.dll"), "STEAM RESTORED THE ORIGINAL");
        File.Delete(game.Combine("dxvk.conf"));

        var problems = DxvkInstaller.Verify(exeDir);

        Assert.Contains(problems, p => p.Contains("d3d11.dll is missing"));
        Assert.Contains(problems, p => p.Contains("dxgi.dll was replaced"));
        Assert.Contains(problems, p => p.Contains("dxvk.conf") && p.Contains(game.Path));
        Assert.DoesNotContain(problems, p => p.Contains("d3d10core.dll"));   // untouched files are fine
    }

    [Fact]
    public void Verify_catches_a_config_edited_so_the_managed_lines_are_gone()
    {
        using var build = FakeBuild();
        var (game, exeDir) = Game();
        using var _ = game;
        Install(build, exeDir, game.Path, GfxApi.D3D11, new DxvkOptions(Async: true));
        File.WriteAllText(Path.Combine(exeDir, "dxvk.conf"), "dxvk.hud = fps\n");   // the user replaced the whole file

        Assert.Single(DxvkInstaller.Verify(exeDir));
    }

    [Fact]
    public void Verify_does_not_require_a_config_when_the_options_need_none()
    {
        using var build = FakeBuild();
        var (game, exeDir) = Game();
        using var _ = game;
        Install(build, exeDir, game.Path, GfxApi.D3D11);   // official, no options: no dxvk.conf is written
        Assert.Empty(DxvkInstaller.Verify(exeDir));
    }

    [Fact]
    public void Verify_without_an_install_is_an_error()
    {
        var (game, exeDir) = Game();
        using var _ = game;
        Assert.Throws<InvalidOperationException>(() => DxvkInstaller.Verify(exeDir));
    }

    [Fact]
    public void Repair_restores_files_and_config_exactly_as_installed_including_arch_and_options()
    {
        using var build = FakeBuild();
        var (game, exeDir) = Game();
        using var _ = game;
        Install(build, exeDir, game.Path, GfxApi.D3D9, new DxvkOptions(Async: true, Gpl: "False"), bits: 32);

        File.Delete(Path.Combine(exeDir, "d3d9.dll"));
        File.Delete(game.Combine("dxvk.conf"));
        File.Delete(Path.Combine(exeDir, "dxvk.conf"));
        DxvkInstaller.Repair(exeDir, game.Path, build.Path, Version);

        Assert.Empty(DxvkInstaller.Verify(exeDir));
        Assert.Equal("DXVK-x32-d3d9.dll", File.ReadAllText(Path.Combine(exeDir, "d3d9.dll")));   // still the 32-bit one
        string conf = File.ReadAllText(game.Combine("dxvk.conf"));
        Assert.Contains("dxvk.enableAsync = True", conf);
        Assert.Contains("dxvk.enableGraphicsPipelineLibrary = False", conf);
        Assert.False(File.Exists(Path.Combine(exeDir, "d3d11.dll")));   // no extra DLLs appear
    }

    [Fact]
    public void Repair_after_the_original_dll_came_back_still_uninstalls_cleanly()
    {
        using var build = FakeBuild();
        var (game, exeDir) = Game();
        using var _ = game;
        game.Write(@"Binaries\Win64\dxgi.dll", "ORIGINAL-DXGI");
        var before = game.Snapshot();
        Install(build, exeDir, game.Path, GfxApi.D3D11);
        File.WriteAllText(Path.Combine(exeDir, "dxgi.dll"), "ORIGINAL-DXGI");   // Steam verify put the original back

        DxvkInstaller.Repair(exeDir, game.Path, build.Path, Version);
        DxvkInstaller.Uninstall(exeDir);

        Assert.Equal(before, game.Snapshot());
    }

    [Theory]
    [InlineData("d3d9.dll", GfxApi.D3D9)]
    [InlineData("d3d8.dll,d3d9.dll", GfxApi.D3D8 | GfxApi.D3D9)]
    [InlineData("d3d10core.dll,d3d11.dll,dxgi.dll", GfxApi.D3D11)]
    [InlineData("unrelated.dll", GfxApi.None)]
    public void ApisFromFiles_recovers_what_an_install_was_for(string files, GfxApi expected) =>
        Assert.Equal(expected, DxvkInstaller.ApisFromFiles(files.Split(',')));

    // ---------------------------------------------------------------------- status

    [Fact]
    public void Status_reports_a_managed_install_with_its_options()
    {
        using var build = FakeBuild();
        var (game, exeDir) = Game();
        using var _ = game;
        Install(build, exeDir, game.Path, GfxApi.D3D11, new DxvkOptions(Async: true, Gpl: "False", Hud: true));

        var status = DxvkInstaller.Status(exeDir)!;

        Assert.True(status.Managed);
        Assert.Equal($"DXVK-gplasync {Version}", status.Label);
        Assert.Equal(new DxvkOptions(true, "False", true), status.Options);
    }

    [Fact]
    public void Status_labels_the_official_build()
    {
        using var build = FakeBuild();
        var (game, exeDir) = Game();
        using var _ = game;
        Install(build, exeDir, game.Path, GfxApi.D3D11);
        Assert.Equal($"DXVK {Version}", DxvkInstaller.Status(exeDir)!.Label);
    }

    [Fact]
    public void Status_recognises_a_manual_dxvk_install_by_its_dll_contents()
    {
        var (game, exeDir) = Game();
        using var _ = game;
        game.Write(@"Binaries\Win64\d3d11.dll", "MZ...this build is DXVK 2.3...");

        var status = DxvkInstaller.Status(exeDir)!;

        Assert.False(status.Managed);
        Assert.Equal("DXVK (manual)", status.Label);
        Assert.Null(status.Options);
    }

    [Fact]
    public void Status_ignores_non_dxvk_dlls_such_as_reshade()
    {
        var (game, exeDir) = Game();
        using var _ = game;
        game.Write(@"Binaries\Win64\dxgi.dll", "MZ...ReShade proxy...");
        Assert.Null(DxvkInstaller.Status(exeDir));
    }

    [Fact]
    public void Status_of_a_scan_uses_the_first_exe_folder_that_has_dxvk()
    {
        using var build = FakeBuild();
        var (game, exeDir) = Game();
        using var _ = game;
        Install(build, exeDir, game.Path, GfxApi.D3D11);
        var scan = new GameScan(new SteamDxvk.Game(1, "G", game.Path))
        {
            Exes = [new ExeInfo(game.Combine("Other.exe"), 64, 1, GfxApi.D3D11, GfxApi.D3D11),
                    new ExeInfo(Path.Combine(exeDir, "Game.exe"), 64, 1, GfxApi.D3D11, GfxApi.D3D11)],
        };
        Assert.Equal($"DXVK {Version}", DxvkInstaller.StatusOf(scan)!.Label);
    }

    [Fact]
    public void A_corrupt_manifest_is_treated_as_no_manifest()
    {
        var (game, exeDir) = Game();
        using var _ = game;
        game.Write(@"Binaries\Win64\" + DxvkInstaller.ManifestName, "{ not json");
        Assert.Null(DxvkInstaller.ReadManifest(exeDir));
        Assert.Null(DxvkInstaller.Status(exeDir));
    }
}
