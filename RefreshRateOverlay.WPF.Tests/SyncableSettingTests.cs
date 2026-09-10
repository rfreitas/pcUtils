using System.IO;
using RefreshRateOverlay.WPF.Services;

namespace RefreshRateOverlay.WPF.Tests;

/// <summary>
/// Covers SyncableSetting&lt;T&gt; — the type TrayApp's ReconcileAll walks
/// instead of hand-writing a separate ReconcileXxx method per setting (see its
/// remarks). Backed by a real temp-file IniStore rather than a mock: this
/// project has no mocking framework, and IniStore's kernel32 calls are fast,
/// local, and side-effect-free once the file is deleted, so a real instance is
/// simpler than inventing a fake ProfileSetting&lt;T&gt; storage layer.
/// </summary>
public class SyncableSettingTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rro_test_{Guid.NewGuid():N}.ini");
    private readonly IniStore _ini;
    private readonly ProfileSetting<int> _storage;

    public SyncableSettingTests()
    {
        _ini = new IniStore(_path);
        _storage = new ProfileSetting<int>(
            _ini, "Settings", "DefaultRate", "Profiles",
            parse: (string s, out int v) => int.TryParse(s, out v),
            format: v => v.ToString(),
            fallback: 60);
    }

    public void Dispose()
    {
        _ini.Dispose();
        try { File.Delete(_path); } catch { /* best effort */ }
    }

    private SyncableSetting<int> MakeSetting(bool available, Func<int> readLive, int initialDefault = 60) =>
        new("Test", _storage,
            isAvailable: () => available,
            tryReadLive: (out int v) => { v = readLive(); return true; },
            describe: v => v.ToString(),
            initialDefault: initialDefault);

    [Fact]
    public void ResolveTarget_NoProfile_FallsBackToDefault()
    {
        var setting = MakeSetting(available: true, readLive: () => 60, initialDefault: 120);
        Assert.Equal(120, setting.ResolveTarget("acs.exe"));
    }

    [Fact]
    public void ResolveTarget_HasProfile_PrefersProfileOverDefault()
    {
        var setting = MakeSetting(available: true, readLive: () => 60, initialDefault: 120);
        setting.SaveOrClear("acs.exe", saveProfile: true, value: 144);

        Assert.Equal(144, setting.ResolveTarget("acs.exe"));
        Assert.Equal(120, setting.Default); // untouched — saved as a profile, not the default
    }

    [Fact]
    public void Reconcile_LiveMatchesTarget_ReturnsNone_WritesNothing()
    {
        var setting = MakeSetting(available: true, readLive: () => 120, initialDefault: 120);
        var result = setting.Reconcile("acs.exe");

        Assert.Equal(ReconcileAction.None, result!.Value.Action);
    }

    [Fact]
    public void Reconcile_DriftWithNoProfile_WritesNewDefault()
    {
        var setting = MakeSetting(available: true, readLive: () => 144, initialDefault: 60);
        var result = setting.Reconcile("acs.exe");

        Assert.Equal(ReconcileAction.WriteDefault, result!.Value.Action);
        Assert.Equal(144, setting.Default);
        Assert.Equal(144, _storage.ReadDefault());
    }

    [Fact]
    public void Reconcile_DriftWithExistingProfile_WritesProfile_LeavesDefaultAlone()
    {
        var setting = MakeSetting(available: true, readLive: () => 60, initialDefault: 60);
        setting.SaveOrClear("acs.exe", saveProfile: true, value: 120); // seed a profile

        // Live now disagrees with the app's own profile (120) — simulate external drift.
        var drifted = MakeSetting(available: true, readLive: () => 144, initialDefault: setting.Default);
        var result = drifted.Reconcile("acs.exe");

        Assert.Equal(ReconcileAction.WriteProfile, result!.Value.Action);
        Assert.True(_storage.TryReadProfile("acs.exe", out int saved));
        Assert.Equal(144, saved);
        Assert.Equal(60, drifted.Default); // default untouched — this app has its own profile
    }

    [Fact]
    public void Reconcile_Unavailable_ReturnsNull_WritesNothing()
    {
        var setting = MakeSetting(available: false, readLive: () => 144, initialDefault: 60);
        Assert.Null(setting.Reconcile("acs.exe"));
        Assert.Equal(60, _storage.ReadDefault());
    }

    [Fact]
    public void IsDrifted_LiveMatchesTarget_ReturnsFalse_WritesNothing()
    {
        var setting = MakeSetting(available: true, readLive: () => 120, initialDefault: 120);
        Assert.False(setting.IsDrifted("acs.exe"));
        Assert.Equal(120, setting.Default);
    }

    [Fact]
    public void IsDrifted_LiveDisagreesWithTarget_ReturnsTrue_WritesNothing()
    {
        var setting = MakeSetting(available: true, readLive: () => 144, initialDefault: 60);
        Assert.True(setting.IsDrifted("acs.exe"));

        // Unlike Reconcile, IsDrifted never persists the observed drift —
        // that's the whole point of using it when reconciliation is off.
        Assert.Equal(60, setting.Default);
        Assert.Equal(60, _storage.ReadDefault());
    }

    [Fact]
    public void IsDrifted_Unavailable_ReturnsFalse()
    {
        var setting = MakeSetting(available: false, readLive: () => 144, initialDefault: 60);
        Assert.False(setting.IsDrifted("acs.exe"));
    }

    // ---- HasDefault ------------------------------------------------------
    // Backs TrayApp's startup seeding decision (ReconcilePlanner.PlanSeed) —
    // it needs to tell "never configured" apart from "stored value happens to
    // equal the fallback", which ReadDefault alone can't do.

    [Fact]
    public void HasDefault_NeverWritten_ReturnsFalse()
    {
        Assert.False(_storage.HasDefault());
        Assert.Equal(60, _storage.ReadDefault()); // falls back silently — not a signal of "configured"
    }

    [Fact]
    public void HasDefault_AfterWriteDefault_ReturnsTrue_EvenIfValueEqualsFallback()
    {
        _storage.WriteDefault(60); // deliberately equals the fallback used above
        Assert.True(_storage.HasDefault());
    }

    [Fact]
    public void SaveOrClear_NotSaved_WritesDefaultAndDeletesProfile()
    {
        var setting = MakeSetting(available: true, readLive: () => 60, initialDefault: 60);
        setting.SaveOrClear("acs.exe", saveProfile: true, value: 120); // seed a profile first

        setting.SaveOrClear("acs.exe", saveProfile: false, value: 100);

        Assert.Equal(100, setting.Default);
        Assert.False(_storage.TryReadProfile("acs.exe", out _));
        Assert.Equal(100, _storage.ReadDefault());
    }
}
