using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace LgtvBrightness.Services;

/// <summary>
/// INI persistence using kernel32 WritePrivateProfileString / GetPrivateProfileString.
/// </summary>
internal sealed class IniStore : IDisposable
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WritePrivateProfileString(
        string lpAppName, string lpKeyName, string? lpString, string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetPrivateProfileString(
        string lpAppName, string lpKeyName, string lpDefault,
        StringBuilder lpReturnedString, uint nSize, string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetPrivateProfileSection(
        string lpAppName, char[] lpReturnedString, uint nSize, string lpFileName);

    private readonly string _path;
    private System.Threading.Timer? _saveTimer;
    private volatile bool _pendingSave;

    public IniStore(string path) => _path = Path.GetFullPath(path);

    public string ReadString(string section, string key, string defaultValue = "")
    {
        var sb = new StringBuilder(1024);
        GetPrivateProfileString(section, key, defaultValue, sb, (uint)sb.Capacity, _path);
        return sb.ToString();
    }

    public int ReadInt(string section, string key, int defaultValue = 0)
    {
        string s = ReadString(section, key, defaultValue.ToString());
        return int.TryParse(s, out int v) ? v : defaultValue;
    }

    public bool ReadBool(string section, string key, bool defaultValue = false)
    {
        string s = ReadString(section, key, defaultValue ? "1" : "0");
        return s == "1";
    }

    public Dictionary<string, string> ReadSection(string section)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        char[] buf = new char[32768];
        uint len = GetPrivateProfileSection(section, buf, (uint)buf.Length, _path);
        if (len == 0) return result;

        int start = 0;
        for (int i = 0; i < (int)len; i++)
        {
            if (buf[i] == '\0')
            {
                string entry = new string(buf, start, i - start);
                int eq = entry.IndexOf('=');
                if (eq >= 0)
                    result[entry[..eq]] = entry[(eq + 1)..];
                start = i + 1;
            }
        }
        return result;
    }

    public void WriteString(string section, string key, string value) =>
        WritePrivateProfileString(section, key, value, _path);

    public void WriteInt(string section, string key, int value) =>
        WritePrivateProfileString(section, key, value.ToString(), _path);

    public void WriteBool(string section, string key, bool value) =>
        WritePrivateProfileString(section, key, value ? "1" : "0", _path);

    public void DeleteKey(string section, string key) =>
        WritePrivateProfileString(section, key, null, _path);

    public void DebouncedSave(Action saveAction)
    {
        _pendingSave = true;
        _saveTimer?.Dispose();
        _saveTimer = new System.Threading.Timer(_ =>
        {
            if (_pendingSave)
            {
                _pendingSave = false;
                try { saveAction(); }
                catch (Exception ex) { Logger.LogException(ex); }
            }
        }, null, 500, System.Threading.Timeout.Infinite);
    }

    public void Dispose() => _saveTimer?.Dispose();
}
