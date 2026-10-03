using System.Buffers.Binary;
using System.IO;
using System.Text;
using SteamDxvk;

namespace SteamDxvk.Tests;

/// <summary>A temp folder deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SteamDxvkTests_" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string Write(string relative, byte[] bytes)
    {
        string full = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        return full;
    }

    public string Write(string relative, string text) => Write(relative, Encoding.UTF8.GetBytes(text));

    /// <summary>relative path -> sha256 of every file under the folder (to compare before/after).</summary>
    public Dictionary<string, string> Snapshot() =>
        Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories).ToDictionary(
            f => System.IO.Path.GetRelativePath(Path, f),
            f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))));

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>Builds minimal but structurally valid PE files in memory (mock data: never touches real executables).</summary>
internal static class TestPe
{
    /// <param name="imports">DLL names for the import table.</param>
    /// <param name="delayImports">DLL names for the delay-import table.</param>
    /// <param name="minSize">Pad the file with zeros up to this size (the scanner ignores small exes).</param>
    /// <param name="trailer">Raw bytes appended at the very end (e.g. strings a LoadLibrary call would reference).</param>
    public static byte[] Build(bool is64 = true, string[]? imports = null, string[]? delayImports = null,
        int minSize = 0, params byte[][] trailer)
    {
        imports ??= [];
        delayImports ??= [];
        const int peOffset = 0x80, rawPointer = 0x200;
        const uint virtualAddress = 0x1000;
        int optionalSize = is64 ? 240 : 224, dirsOffset = is64 ? 112 : 96;

        // One section holding: import descriptors, delay-import descriptors, then the DLL name strings.
        int delayStart = (imports.Length + 1) * 20;
        int stringsStart = delayStart + (delayImports.Length + 1) * 32;
        var section = new List<byte>(new byte[stringsStart]);
        var nameRvas = new List<uint>();
        foreach (var name in imports.Concat(delayImports))
        {
            nameRvas.Add(virtualAddress + (uint)section.Count);
            section.AddRange(Encoding.ASCII.GetBytes(name));
            section.Add(0);
        }
        var sec = section.ToArray();
        for (int i = 0; i < imports.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(sec.AsSpan(i * 20 + 12), nameRvas[i]);
        for (int j = 0; j < delayImports.Length; j++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(sec.AsSpan(delayStart + j * 32), 1);   // attributes: RVAs
            BinaryPrimitives.WriteUInt32LittleEndian(sec.AsSpan(delayStart + j * 32 + 4), nameRvas[imports.Length + j]);
        }

        var file = new byte[Math.Max(minSize, rawPointer + sec.Length)];
        file[0] = (byte)'M'; file[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(60), peOffset);
        file[peOffset] = (byte)'P'; file[peOffset + 1] = (byte)'E';
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(peOffset + 4), (ushort)(is64 ? 0x8664 : 0x14C));   // Machine
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(peOffset + 6), 1);                                  // sections
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(peOffset + 20), (ushort)optionalSize);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(peOffset + 22), 0x22);

        int opt = peOffset + 24;
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(opt), (ushort)(is64 ? 0x20B : 0x10B));
        if (imports.Length > 0)
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(opt + dirsOffset + 1 * 8), virtualAddress);
        if (delayImports.Length > 0)
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(opt + dirsOffset + 13 * 8), virtualAddress + (uint)delayStart);

        int sectionHeader = opt + optionalSize;
        Encoding.ASCII.GetBytes(".idata").CopyTo(file, sectionHeader);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(sectionHeader + 8), (uint)sec.Length);    // VirtualSize
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(sectionHeader + 12), virtualAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(sectionHeader + 16), (uint)sec.Length);   // SizeOfRawData
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(sectionHeader + 20), rawPointer);
        sec.CopyTo(file, rawPointer);

        return [.. file, .. trailer.SelectMany(t => t)];
    }

    public static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    public static byte[] Utf16(string s) => Encoding.Unicode.GetBytes(s);
}

internal static class TestData
{
    private static int _id = 1000;

    /// <summary>What a captured launch looks like in assertions: "4242" or "4242 -dx11" (intent, not transport).</summary>
    public static string Describe(int appId, string? args) => args is null ? $"{appId}" : $"{appId} {args}";

    public static GameScan Scan(string name, GfxApi apis, GfxApi imports = GfxApi.None, string[]? anticheat = null,
        string engine = "", int bits = 64, string? root = null, DxvkStatus? status = null)
    {
        var game = new Game(_id++, name, root ?? @"C:\Games\" + name);
        return new GameScan(game)
        {
            Exes = [new ExeInfo(System.IO.Path.Combine(game.Root, name + ".exe"), bits, 50_000_000, apis, imports)],
            AntiCheat = [.. anticheat ?? []],
            Engine = engine,
            Status = status,
        };
    }
}
