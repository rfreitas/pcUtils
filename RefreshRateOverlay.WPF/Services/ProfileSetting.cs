using System;

namespace RefreshRateOverlay.WPF.Services;

/// <summary>
/// One persisted setting: an app-agnostic default plus per-app overrides, both
/// backed by the same INI file. Bundling default + per-app together (instead of
/// each setting type hand-rolling its own read/write/delete trio) makes it
/// structurally impossible to add a setting that only supports per-app saves
/// and silently has no default — that gap is exactly what let saving a DSX
/// profile as "general" get applied once and then never persist.
///
/// Uses a TryParse-style delegate rather than a nullable-returning parser:
/// unconstrained T can't be split into "value types get Nullable&lt;T&gt;,
/// reference types get a nullable reference" without a struct/class constraint,
/// which would defeat sharing this one class across int, bool, and string.
/// </summary>
internal sealed class ProfileSetting<T>
{
    public delegate bool ParseFunc(string raw, out T value);

    private readonly IniStore _ini;
    private readonly string _defaultSection;
    private readonly string _defaultKey;
    private readonly string _profileSection;
    private readonly ParseFunc _parse;
    private readonly Func<T, string> _format;
    private readonly T _fallback;

    public ProfileSetting(
        IniStore ini,
        string defaultSection, string defaultKey,
        string profileSection,
        ParseFunc parse, Func<T, string> format,
        T fallback)
    {
        _ini = ini;
        _defaultSection = defaultSection;
        _defaultKey = defaultKey;
        _profileSection = profileSection;
        _parse = parse;
        _format = format;
        _fallback = fallback;
    }

    public T ReadDefault()
    {
        string raw = _ini.ReadString(_defaultSection, _defaultKey);
        return raw.Length > 0 && _parse(raw, out T v) ? v : _fallback;
    }

    public void WriteDefault(T value) =>
        _ini.WriteString(_defaultSection, _defaultKey, _format(value));

    public bool HasProfile(string app) =>
        _ini.ReadString(_profileSection, app).Length > 0;

    /// <summary>True (with the saved value) if app has a profile for this setting.</summary>
    public bool TryReadProfile(string app, out T value)
    {
        string raw = _ini.ReadString(_profileSection, app);
        if (raw.Length == 0) { value = _fallback; return false; }
        return _parse(raw, out value);
    }

    public void WriteProfile(string app, T value) =>
        _ini.WriteString(_profileSection, app, _format(value));

    public void DeleteProfile(string app) =>
        _ini.DeleteKey(_profileSection, app);
}
