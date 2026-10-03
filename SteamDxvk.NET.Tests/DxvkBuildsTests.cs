using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using SteamDxvk;
using Xunit;

namespace SteamDxvk.Tests;

public class DxvkBuildsTests
{
    private static byte[] TarGz(params (string Name, string Content)[] entries)
    {
        var tar = new MemoryStream();
        using (var writer = new TarWriter(tar, leaveOpen: true))
            foreach (var (name, content) in entries)
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)) });
        tar.Position = 0;
        var gz = new MemoryStream();
        using (var z = new GZipStream(gz, CompressionLevel.Fastest, leaveOpen: true)) tar.CopyTo(z);
        return gz.ToArray();
    }

    private static (string, string)[] FullBuild(string top = "dxvk-9.9.9") =>
        [.. from arch in new[] { "x32", "x64" } from dll in DxvkInstaller.AllDlls select ($"{top}/{arch}/{dll}", $"{arch}:{dll}")];

    [Fact]
    public void Extract_writes_every_dll_under_its_arch_folder_and_marks_the_build_complete()
    {
        using var dir = new TempDir();
        string dest = dir.Combine("official-v9.9.9");

        DxvkBuilds.Extract(TarGz(FullBuild()), dest);

        Assert.Equal("x64:d3d11.dll", File.ReadAllText(Path.Combine(dest, "x64", "d3d11.dll")));
        Assert.Equal("x32:dxgi.dll", File.ReadAllText(Path.Combine(dest, "x32", "dxgi.dll")));
        Assert.True(File.Exists(Path.Combine(dest, ".ok")));
        Assert.False(Directory.Exists(dest + ".tmp"));
    }

    [Fact]
    public void Extract_ignores_unrelated_files_and_does_not_care_about_the_top_folder_name()
    {
        using var dir = new TempDir();
        string dest = dir.Combine("b");

        DxvkBuilds.Extract(TarGz([.. FullBuild("dxvk-gplasync-v3.1.1-1"), ("dxvk-gplasync-v3.1.1-1/README.md", "hi"), ("x64/notes.txt", "hi")]), dest);

        Assert.Equal(["d3d10core.dll", "d3d11.dll", "d3d8.dll", "d3d9.dll", "dxgi.dll"],
            Directory.GetFiles(Path.Combine(dest, "x64")).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void Extract_never_writes_outside_the_destination()
    {
        using var dir = new TempDir();
        string dest = dir.Combine(@"build\b");

        DxvkBuilds.Extract(TarGz([.. FullBuild(), ("../../evil/x64/d3d11.dll", "EVIL"), ("../evil.dll", "EVIL")]), dest);

        Assert.False(File.Exists(dir.Combine("evil.dll")));
        Assert.False(Directory.Exists(dir.Combine("evil")));
        Assert.Equal("x64:d3d11.dll", File.ReadAllText(Path.Combine(dest, "x64", "d3d11.dll")));   // the legitimate one won, in place
    }

    [Fact]
    public void Extract_keeps_the_first_copy_of_a_duplicated_member()
    {
        using var dir = new TempDir();
        string dest = dir.Combine("b");

        DxvkBuilds.Extract(TarGz([.. FullBuild(), ("dxvk-9.9.9/x64/d3d11.dll", "SECOND")]), dest);

        Assert.Equal("x64:d3d11.dll", File.ReadAllText(Path.Combine(dest, "x64", "d3d11.dll")));
    }

    [Fact]
    public void Extract_of_an_incomplete_archive_leaves_nothing_behind()
    {
        using var dir = new TempDir();
        string dest = dir.Combine("b");

        Assert.Throws<InvalidDataException>(() => DxvkBuilds.Extract(TarGz(FullBuild().Take(5).ToArray()), dest));

        Assert.False(Directory.Exists(dest));
        Assert.False(Directory.Exists(dest + ".tmp"));
    }

    [Fact]
    public void Extract_replaces_an_existing_build_directory()
    {
        using var dir = new TempDir();
        string dest = dir.Combine("b");
        dir.Write(@"b\stale.txt", "old");

        DxvkBuilds.Extract(TarGz(FullBuild()), dest);

        Assert.False(File.Exists(Path.Combine(dest, "stale.txt")));
        Assert.True(File.Exists(Path.Combine(dest, ".ok")));
    }

    [Fact]
    public void Extract_rejects_data_that_is_not_a_gzip_tar()
    {
        using var dir = new TempDir();
        Assert.ThrowsAny<Exception>(() => DxvkBuilds.Extract([1, 2, 3, 4], dir.Combine("b")));
        Assert.False(Directory.Exists(dir.Combine("b")));
    }
}
