using System.IO;
using SteamDxvk;
using Xunit;

namespace SteamDxvk.Tests;

public class PeInspectorTests
{
    private static MemoryStream S(byte[] bytes) => new(bytes);

    [Fact] public void Bits_64() => Assert.Equal(64, PeInspector.Bits(S(TestPe.Build(is64: true))));
    [Fact] public void Bits_32() => Assert.Equal(32, PeInspector.Bits(S(TestPe.Build(is64: false))));
    [Fact] public void Bits_not_a_pe_is_null() => Assert.Null(PeInspector.Bits(S(TestPe.Ascii("hello, definitely not a pe file at all, but long enough to read a header from"))));
    [Fact] public void Bits_empty_is_null() => Assert.Null(PeInspector.Bits(S([])));

    [Fact]
    public void Bits_arm64_is_unsupported()
    {
        var pe = TestPe.Build();
        pe[0x80 + 4] = 0x64; pe[0x80 + 5] = 0xAA;   // Machine = 0xAA64
        Assert.Null(PeInspector.Bits(S(pe)));
    }

    [Fact]
    public void Imports_finds_d3d11_through_import_table()
    {
        var apis = PeInspector.Imports(S(TestPe.Build(imports: ["KERNEL32.dll", "d3d11.dll", "dxgi.dll"])));
        Assert.Equal(GfxApi.D3D11, apis);   // dxgi is not an API by itself
    }

    [Fact]
    public void Imports_is_case_insensitive_and_handles_several()
    {
        var apis = PeInspector.Imports(S(TestPe.Build(imports: ["D3D9.DLL", "VULKAN-1.dll", "opengl32.dll"])));
        Assert.Equal(GfxApi.D3D9 | GfxApi.Vulkan | GfxApi.OpenGL, apis);
    }

    [Fact]
    public void Imports_reads_delay_import_table()
    {
        var apis = PeInspector.Imports(S(TestPe.Build(imports: ["KERNEL32.dll"], delayImports: ["d3d12.dll"])));
        Assert.Equal(GfxApi.D3D12, apis);
    }

    [Fact]
    public void Imports_ignores_non_graphics_dlls() =>
        Assert.Equal(GfxApi.None, PeInspector.Imports(S(TestPe.Build(imports: ["KERNEL32.dll", "USER32.dll"]))));

    [Fact]
    public void Imports_works_for_32_bit_pe32()
    {
        var apis = PeInspector.Imports(S(TestPe.Build(is64: false, imports: ["d3d9.dll"])));
        Assert.Equal(GfxApi.D3D9, apis);
    }

    [Fact]
    public void Imports_survives_truncated_and_garbage_files()
    {
        var pe = TestPe.Build(imports: ["d3d11.dll"]);
        Assert.Equal(GfxApi.None, PeInspector.Imports(S(pe[..100])));
        Assert.Equal(GfxApi.None, PeInspector.Imports(S(pe[..0x1B0])));   // headers present, section data missing
        Assert.Equal(GfxApi.None, PeInspector.Imports(S(new byte[4096])));
    }

    [Fact]
    public void ScanStrings_finds_ascii_name() =>
        Assert.Equal(GfxApi.D3D12, PeInspector.ScanStrings(S([.. new byte[100], .. TestPe.Ascii("d3d12.dll")])));

    [Fact]
    public void ScanStrings_finds_utf16_name() =>
        Assert.Equal(GfxApi.D3D11, PeInspector.ScanStrings(S([.. new byte[10], .. TestPe.Utf16("d3d11.dll")])));

    [Fact]
    public void ScanStrings_is_case_insensitive() =>
        Assert.Equal(GfxApi.D3D11, PeInspector.ScanStrings(S(TestPe.Ascii("..D3D11.DLL.."))));

    [Fact]
    public void ScanStrings_maps_every_d3d10_variant()
    {
        foreach (var name in new[] { "d3d10.dll", "d3d10_1.dll", "d3d10core.dll" })
            Assert.Equal(GfxApi.D3D10, PeInspector.ScanStrings(S(TestPe.Ascii(name))));
    }

    [Fact]
    public void ScanStrings_finds_a_name_straddling_a_chunk_boundary()
    {
        const int chunk = 4096;
        var data = new byte[chunk * 2];
        TestPe.Ascii("vulkan-1.dll").CopyTo(data, chunk - 5);   // 5 bytes in chunk one, the rest in chunk two
        Assert.Equal(GfxApi.Vulkan, PeInspector.ScanStrings(S(data), chunk: chunk));
    }

    [Fact]
    public void ScanStrings_returns_none_when_absent() =>
        Assert.Equal(GfxApi.None, PeInspector.ScanStrings(S(TestPe.Ascii("nothing graphical in here"))));

    [Fact]
    public void ScanStrings_respects_the_read_cap()
    {
        var data = new byte[8192];
        TestPe.Ascii("d3d11.dll").CopyTo(data, 7000);
        Assert.Equal(GfxApi.None, PeInspector.ScanStrings(S(data), cap: 4096, chunk: 4096));
    }

    [Fact] public void ContainsAscii_is_case_insensitive() => Assert.True(PeInspector.ContainsAscii(S(TestPe.Ascii("xx DXVK 3.1 xx")), "dxvk"));
    [Fact] public void ContainsAscii_false_when_absent() => Assert.False(PeInspector.ContainsAscii(S(TestPe.Ascii("a real d3d11 dll")), "dxvk"));

    [Fact]
    public void ContainsAscii_finds_text_across_a_chunk_boundary()
    {
        const int chunk = 4096;
        var data = new byte[chunk * 2];
        TestPe.Ascii("dxvk").CopyTo(data, chunk - 2);
        Assert.True(PeInspector.ContainsAscii(S(data), "dxvk", chunk));
    }

    [Fact]
    public void File_overloads_work_and_missing_file_is_a_quiet_default()
    {
        using var dir = new TempDir();
        string exe = dir.Write("g.exe", TestPe.Build(imports: ["d3d11.dll"], trailer: TestPe.Ascii("d3d12.dll")));
        Assert.Equal(64, PeInspector.Bits(exe));
        Assert.Equal(GfxApi.D3D11, PeInspector.Imports(exe));
        Assert.Equal(GfxApi.D3D11 | GfxApi.D3D12, PeInspector.ScanStrings(exe));

        string missing = dir.Combine("nope.exe");
        Assert.Null(PeInspector.Bits(missing));
        Assert.Equal(GfxApi.None, PeInspector.Imports(missing));
        Assert.Equal(GfxApi.None, PeInspector.ScanStrings(missing));
    }

    [Fact]
    public void File_overloads_can_read_a_file_another_process_has_open()
    {
        using var dir = new TempDir();
        string exe = dir.Write("running.exe", TestPe.Build(imports: ["d3d11.dll"]));
        using var holder = new FileStream(exe, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);   // like a running game
        Assert.Equal(GfxApi.D3D11, PeInspector.Imports(exe));
    }
}
