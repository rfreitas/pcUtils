using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace SteamDxvk;

/// <summary>
/// Detects which graphics APIs a Windows executable/DLL uses. Two kinds of evidence:
/// <see cref="Imports"/> (import and delay-import tables: the loader needs these DLLs) and
/// <see cref="ScanStrings"/> (DLL names anywhere in the file: LoadLibrary calls, or a library merely mentioning them).
/// All logic works on a <see cref="Stream"/> so it is testable with synthetic data.
/// </summary>
internal static class PeInspector
{
    private const int Overlap = 64;
    private static readonly (byte[] Pattern, GfxApi Api)[] Patterns = BuildPatterns();

    private static (byte[], GfxApi)[] BuildPatterns()
    {
        var list = new List<(byte[], GfxApi)>();
        foreach (var (dll, api) in GfxApis.Dlls)
        {
            list.Add((Encoding.ASCII.GetBytes(dll), api));
            list.Add((Encoding.Unicode.GetBytes(dll), api));   // UTF-16LE: wide-string LoadLibraryW
        }
        return [.. list];
    }

    // ---------------------------------------------------------------- headers

    /// <summary>32 or 64 for x86 / x64 PE files; null for anything else (including ARM64).</summary>
    public static int? Bits(Stream s)
    {
        var head = ReadAt(s, 0, 64);
        if (head is null || head[0] != 'M' || head[1] != 'Z') return null;
        long pe = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(60));
        if (pe < 0) return null;
        var h = ReadAt(s, pe, 6);
        if (h is null || !IsPeSignature(h)) return null;
        return BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(4)) switch { 0x14C => 32, 0x8664 => 64, _ => null };
    }

    /// <summary>Graphics APIs linked through the import or delay-import table.</summary>
    public static GfxApi Imports(Stream s)
    {
        var apis = GfxApi.None;
        try
        {
            var head = ReadAt(s, 0, 64);
            if (head is null || head[0] != 'M' || head[1] != 'Z') return apis;
            long pe = BinaryPrimitives.ReadInt32LittleEndian(head.AsSpan(60));
            var hdr = pe < 0 ? null : ReadAt(s, pe, 24);
            if (hdr is null || !IsPeSignature(hdr)) return apis;

            int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(6));
            int optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(20));
            var opt = ReadAt(s, pe + 24, optionalSize);
            if (opt is null || opt.Length < 2) return apis;
            int dirsOffset = BinaryPrimitives.ReadUInt16LittleEndian(opt) == 0x20B ? 112 : 96;   // PE32+ vs PE32

            var raw = ReadAt(s, pe + 24 + optionalSize, 40 * sectionCount);
            if (raw is null) return apis;
            var sections = new (uint VSize, uint VAddr, uint RSize, uint RPtr)[sectionCount];
            for (int i = 0; i < sectionCount; i++)
                sections[i] = (U32(raw, i * 40 + 8), U32(raw, i * 40 + 12), U32(raw, i * 40 + 16), U32(raw, i * 40 + 20));

            long? ToOffset(uint rva)
            {
                foreach (var (vsize, vaddr, rsize, rptr) in sections)
                    if (rva >= vaddr && rva < vaddr + Math.Max(vsize, rsize)) return (long)rva - vaddr + rptr;
                return null;
            }

            string DllName(uint rva)
            {
                if (ToOffset(rva) is not long off) return "";
                var bytes = ReadUpTo(s, off, 64);
                int end = Array.IndexOf(bytes, (byte)0);
                return Encoding.ASCII.GetString(bytes, 0, end < 0 ? bytes.Length : end);
            }

            void Walk(int dirIndex, int entrySize, int nameField, bool needsRvaFlag)
            {
                if (dirsOffset + dirIndex * 8 + 8 > opt.Length) return;
                uint rva = U32(opt, dirsOffset + dirIndex * 8);
                if (rva == 0 || ToOffset(rva) is not long table) return;
                for (int i = 0; i < 1024; i++)
                {
                    var d = ReadAt(s, table + (long)i * entrySize, entrySize);
                    if (d is null || d.All(b => b == 0)) break;
                    if (needsRvaFlag && (U32(d, 0) & 1) == 0) continue;   // pre-VC7 delay imports hold VAs, not RVAs
                    apis |= GfxApis.FromDll(DllName(U32(d, nameField)));
                }
            }

            Walk(dirIndex: 1, entrySize: 20, nameField: 12, needsRvaFlag: false);   // IMAGE_DIRECTORY_ENTRY_IMPORT: Name at +12
            Walk(dirIndex: 13, entrySize: 32, nameField: 4, needsRvaFlag: true);    // DELAY_IMPORT: DllNameRVA at +4
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { }
        return apis;
    }

    // ---------------------------------------------------------------- strings

    /// <summary>Graphics DLL names found anywhere in the stream (ASCII or UTF-16, any case).</summary>
    public static GfxApi ScanStrings(Stream s, long cap = 1L << 30, int chunk = 1 << 24)
    {
        var found = GfxApi.None;
        var buf = new byte[chunk + Overlap];
        int tail = 0;
        long read = 0;
        try
        {
            while (read < cap)
            {
                int n = ReadFill(s, buf, tail, chunk);
                if (n == 0) break;
                read += n;
                var span = buf.AsSpan(0, tail + n);
                LowerAscii(span);
                foreach (var (pattern, api) in Patterns)
                    if ((found & api) == 0 && span.IndexOf(pattern) >= 0) found |= api;
                if ((found & GfxApis.Any) == GfxApis.Any) break;
                tail = Math.Min(Overlap, span.Length);
                span[^tail..].CopyTo(buf);   // overlapping copy is handled by Span.CopyTo
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return found;
    }

    /// <summary>True if <paramref name="text"/> (ASCII, case-insensitive) appears anywhere in the stream.</summary>
    public static bool ContainsAscii(Stream s, string text, int chunk = 1 << 22)
    {
        var pattern = Encoding.ASCII.GetBytes(text.ToLowerInvariant());
        var buf = new byte[chunk + pattern.Length];
        int tail = 0;
        try
        {
            while (true)
            {
                int n = ReadFill(s, buf, tail, chunk);
                if (n == 0) return false;
                var span = buf.AsSpan(0, tail + n);
                LowerAscii(span);
                if (span.IndexOf(pattern) >= 0) return true;
                tail = Math.Min(pattern.Length - 1, span.Length);
                span[^tail..].CopyTo(buf);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    // ---------------------------------------------------------- file overloads

    public static int? Bits(string path) => WithFile(path, Bits, null);

    public static GfxApi Imports(string path) => WithFile(path, Imports, GfxApi.None);

    public static GfxApi ScanStrings(string path) => WithFile(path, s => ScanStrings(s), GfxApi.None);

    public static bool ContainsAscii(string path, string text) => WithFile(path, s => ContainsAscii(s, text), false);

    // Games may be running, so open with the most permissive sharing.
    private static T WithFile<T>(string path, Func<Stream, T> action, T fallback)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return action(fs);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return fallback; }
    }

    // ---------------------------------------------------------------- helpers

    private static bool IsPeSignature(byte[] h) => h[0] == 'P' && h[1] == 'E' && h[2] == 0 && h[3] == 0;

    private static uint U32(byte[] b, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset));

    private static void LowerAscii(Span<byte> span)
    {
        for (int i = 0; i < span.Length; i++)
            if (span[i] is >= (byte)'A' and <= (byte)'Z') span[i] |= 0x20;
    }

    /// <summary>Exactly <paramref name="length"/> bytes at <paramref name="position"/>, or null if the stream is shorter.</summary>
    private static byte[]? ReadAt(Stream s, long position, int length)
    {
        var bytes = ReadUpTo(s, position, length);
        return bytes.Length == length ? bytes : null;
    }

    private static byte[] ReadUpTo(Stream s, long position, int max)
    {
        if (position < 0 || position >= s.Length) return [];
        s.Seek(position, SeekOrigin.Begin);
        var buf = new byte[max];
        int n = ReadFill(s, buf, 0, max);
        return n == max ? buf : buf[..n];
    }

    private static int ReadFill(Stream s, byte[] buf, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int n = s.Read(buf, offset + total, count - total);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}
