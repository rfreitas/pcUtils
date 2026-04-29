using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using AggressiveScreensaver.Services;

namespace AggressiveScreensaver.Services;

/// <summary>
/// INI persistence using kernel32 WritePrivateProfileString / GetPrivateProfileString.
/// This produces UTF-16 LE files identical to those written by AHK's IniRead/IniWrite,
/// so the existing AggressiveScreensaver.ini is byte-compatible across both versions.
/// </summary>
internal sealed class IniStore : IDisposable
{
    // -------------------------------------------------------------------------
    // P/Invoke
    // -------------------------------------------------------------------------
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

    // -------------------------------------------------------------------------
    // Fields
    // -------------------------------------------------------------------------
    private readonly string _path;
    private System.Threading.Timer? _saveTimer;
    private volatile bool _pendingSave;

    public IniStore(string path)
    {
        _path = Path.GetFullPath(path);
    }

    // -------------------------------------------------------------------------
    // Read helpers
    // -------------------------------------------------------------------------

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

    /// <summary>
    /// Returns all key=value pairs in a section as a dictionary.
    /// </summary>
    public Dictionary<string, string> ReadSection(string section)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        char[] buf = new char[32768];
        uint len = GetPrivateProfileSection(section, buf, (uint)buf.Length, _path);
        if (len == 0)
            return result;

        // buf contains null-terminated strings, double-null at end
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

    // -------------------------------------------------------------------------
    // Write helpers
    // -------------------------------------------------------------------------

    public void WriteString(string section, string key, string value)
    {
        WritePrivateProfileString(section, key, value, _path);
    }

    public void WriteInt(string section, string key, int value)
    {
        WritePrivateProfileString(section, key, value.ToString(), _path);
    }

    /// <summary>
    /// Deletes a key. Passing null to WritePrivateProfileString deletes the key.
    /// </summary>
    public void DeleteKey(string section, string key)
    {
        WritePrivateProfileString(section, key, null, _path);
    }

    // -------------------------------------------------------------------------
    // Debounced save
    // -------------------------------------------------------------------------

    /// <summary>
    /// Schedules a settings save action to fire after 500 ms (debounced).
    /// </summary>
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
