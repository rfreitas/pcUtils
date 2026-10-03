using System.IO;
using SteamDxvk;
using Xunit;

namespace SteamDxvk.Tests;

public class SteamLibraryTests
{
    private const string NewLibraryFolders = """
        "libraryfolders"
        {
            "0"
            {
                "path"      "C:\\Program Files (x86)\\Steam"
                "label"     ""
                "apps"
                {
                    "228980"        "123"
                }
            }
            "1"
            {
                "path"      "D:\\SteamLibrary"
            }
        }
        """;

    private const string OldLibraryFolders = """
        "LibraryFolders"
        {
            "TimeNextStatsReport"   "1700000000"
            "ContentStatsID"        "-12345"
            "1"     "E:\\Games"
        }
        """;

    private static string Manifest(int id, string name, string dir) => $$"""
        "AppState"
        {
            "appid"     "{{id}}"
            "name"      "{{name}}"
            "installdir" "{{dir}}"
        }
        """;

    [Fact]
    public void Vdf_parses_nesting_escapes_and_is_case_insensitive()
    {
        var root = Vdf.Parse("\"Outer\" { \"a\" \"x\\\\y\" \"inner\" { \"b\" \"line\\nbreak\" } }");
        var outer = Vdf.Object(root["outer"])!;   // different case than the file
        Assert.Equal(@"x\y", Vdf.Str(outer, "a"));
        Assert.Equal("line\nbreak", Vdf.Str(Vdf.Object(outer["inner"])!, "b"));
    }

    [Fact]
    public void Vdf_tolerates_empty_values_and_unbalanced_braces()
    {
        Assert.Equal("", Vdf.Str(Vdf.Parse("\"k\" \"\""), "k"));
        Assert.NotNull(Vdf.Parse("\"a\" { \"b\" \"c\""));   // missing closing brace
        Assert.Empty(Vdf.Parse("}}}"));
    }

    [Fact]
    public void LibraryFolders_new_format_unescapes_paths() =>
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], SteamLibrary.ParseLibraryFolders(NewLibraryFolders));

    [Fact]
    public void LibraryFolders_old_format_keeps_only_numbered_paths() =>
        Assert.Equal([@"E:\Games"], SteamLibrary.ParseLibraryFolders(OldLibraryFolders));

    [Fact]
    public void LibraryFolders_garbage_gives_nothing() => Assert.Empty(SteamLibrary.ParseLibraryFolders("not vdf at all"));

    [Fact]
    public void AppManifest_parses()
    {
        var m = SteamLibrary.ParseAppManifest(Manifest(690790, "DiRT Rally 2.0", "DiRT Rally 2.0"));
        Assert.Equal((690790, "DiRT Rally 2.0", "DiRT Rally 2.0"), m);
    }

    [Fact]
    public void AppManifest_rejects_incomplete_files()
    {
        Assert.Null(SteamLibrary.ParseAppManifest("\"AppState\" { \"appid\" \"abc\" \"name\" \"x\" \"installdir\" \"y\" }"));
        Assert.Null(SteamLibrary.ParseAppManifest("\"AppState\" { \"appid\" \"1\" }"));
        Assert.Null(SteamLibrary.ParseAppManifest("\"Other\" { }"));
    }

    [Fact]
    public void ListGames_spans_libraries_skips_tools_missing_folders_and_duplicates_and_sorts()
    {
        using var steam = new TempDir();
        using var library = new TempDir();
        steam.Write(@"steamapps\libraryfolders.vdf",
            "\"libraryfolders\" { \"0\" { \"path\" \"" + steam.Path.Replace(@"\", @"\\") + "\" } \"1\" { \"path\" \"" + library.Path.Replace(@"\", @"\\") + "\" } }");

        steam.Write(@"steamapps\appmanifest_20.acf", Manifest(20, "zeta game", "Zeta"));
        Directory.CreateDirectory(steam.Combine(@"steamapps\common\Zeta"));
        steam.Write(@"steamapps\appmanifest_30.acf", Manifest(30, "Proton 9.0", "Proton 9.0"));          // tool
        Directory.CreateDirectory(steam.Combine(@"steamapps\common\Proton 9.0"));
        steam.Write(@"steamapps\appmanifest_40.acf", Manifest(40, "Uninstalled", "Gone"));               // folder missing

        library.Write(@"steamapps\appmanifest_10.acf", Manifest(10, "Alpha Game", "Alpha"));
        Directory.CreateDirectory(library.Combine(@"steamapps\common\Alpha"));
        library.Write(@"steamapps\appmanifest_20.acf", Manifest(20, "zeta game (dupe)", "Zeta"));        // same appid again
        Directory.CreateDirectory(library.Combine(@"steamapps\common\Zeta"));

        var games = SteamLibrary.ListGames(steam.Path);
        Assert.Equal(["Alpha Game", "zeta game"], games.Select(g => g.Name));
        Assert.Equal(steam.Combine(@"steamapps\common\Zeta"), games[1].Root);   // first library wins the duplicate
    }

    [Fact]
    public void ListGames_without_a_libraryfolders_file_uses_the_steam_folder_alone()
    {
        using var steam = new TempDir();
        steam.Write(@"steamapps\appmanifest_5.acf", Manifest(5, "Solo", "Solo"));
        Directory.CreateDirectory(steam.Combine(@"steamapps\common\Solo"));
        Assert.Equal(["Solo"], SteamLibrary.ListGames(steam.Path).Select(g => g.Name));
    }
}
