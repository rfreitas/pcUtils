using System.Text;

namespace SteamDxvk;

/// <summary>Plain-text scan table, for the headless --scan flag and for diffing against expectations.</summary>
internal static class ScanReport
{
    public static string Format(IEnumerable<GameScan> scans)
    {
        var sb = new StringBuilder();
        sb.AppendLine("API column: linked APIs, then (+APIs only mentioned in strings: dynamically loaded or from libraries)");
        sb.AppendLine($"{"AppID",8}  {"Game",-42} {"API",-34} {"Bits",-4} DXVK");
        foreach (var s in scans.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            string name = s.Name.Length > 42 ? s.Name[..42] : s.Name;
            string note = s.AntiCheat.Count > 0 ? $"  [anti-cheat: {string.Join(", ", s.AntiCheat)}]" : "";
            sb.AppendLine($"{s.AppId,8}  {name,-42} {GameScanner.ApiSummary(s.Primary),-34} {s.Primary?.Bits.ToString() ?? "?",-4} {s.Status?.Label}{note}");
        }
        return sb.ToString();
    }
}
