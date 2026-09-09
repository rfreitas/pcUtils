using System;
using System.Collections.Generic;

namespace RefreshRateOverlay.WPF.Services;

/// <summary>
/// Non-generic handle for TrayApp's single HardwareChange reconciliation loop —
/// every default+profile setting that has a live readback (Rate, HDR, G-SYNC
/// Mode, and any future one of the same shape) registers one
/// SyncableSetting&lt;T&gt; and TrayApp walks a plain List&lt;ISyncableSetting&gt;
/// instead of hand-writing a separate ReconcileXxx method — and a separate
/// HardwareChange subscription — per setting. That per-setting hand-writing is
/// exactly the gap that let App VRR's own reconciliation never get wired up in
/// the first place: nothing forced it to happen, so it silently didn't.
/// </summary>
internal interface ISyncableSetting
{
    string Name { get; }

    /// <summary>Re-reads live state, compares it to what this app's
    /// profile-or-default says it should be, and — if they differ — persists
    /// the observed drift as the new truth (WriteProfile if this app has its
    /// own profile, WriteDefault otherwise). Returns the action taken plus
    /// display strings for the live and target values, or null if unavailable
    /// this tick (NVAPI down, a transient DRS read failure, etc).</summary>
    (ReconcileAction Action, string Live, string Target)? Reconcile(string app);

    /// <summary>Read-only drift check for when reconciliation is turned off —
    /// answers "does live state disagree with what the INI says it should be"
    /// without ever persisting anything, so the INI stays the sole source of
    /// truth and TrayApp can instead reassert its target back onto hardware.
    /// False (not drifted) whenever unavailable or unreadable this tick, same
    /// as Reconcile's null-and-skip treatment.</summary>
    bool IsDrifted(string app);
}

/// <summary>
/// One default+profile setting, fully wired: persistence (via
/// ProfileSetting&lt;T&gt;), live readback, and the resolve/plan/persist
/// reconciliation ReconcilePlanner already made generic. Rate, HDR, and G-SYNC
/// Mode each get exactly one instance instead of three hand-duplicated copies
/// of the same Resolve/Plan/Write branching TrayApp used to carry per setting.
///
/// Deliberately doesn't own "push to hardware" — Rate and HDR must be pushed
/// together in a specific order for hardware-sequencing reasons
/// (TrayApp.ApplyDisplayState), so there's no single per-setting Apply that
/// would be correct for all three; each call site still decides how to push,
/// same as before. This type only unifies the half that genuinely is uniform
/// across every setting of this shape: what's true, and what to do when
/// reality disagrees with it.
/// </summary>
internal sealed class SyncableSetting<T> : ISyncableSetting
{
    public delegate bool TryReadFunc(out T value);

    private readonly ProfileSetting<T> _storage;
    private readonly Func<bool> _isAvailable;
    private readonly TryReadFunc _tryReadLive;
    private readonly Func<T, string> _describe;

    public string Name { get; }

    /// <summary>The current app-agnostic default — seeded by the caller at
    /// construction (same "reseed from live state at startup" treatment
    /// _defaultRate/_defaultHdr/_defaultGsyncMode used to each get by hand),
    /// then only ever updated here, mirrored into storage every time.</summary>
    public T Default { get; private set; }

    public SyncableSetting(
        string name,
        ProfileSetting<T> storage,
        Func<bool> isAvailable,
        TryReadFunc tryReadLive,
        Func<T, string> describe,
        T initialDefault)
    {
        Name = name;
        _storage = storage;
        _isAvailable = isAvailable;
        _tryReadLive = tryReadLive;
        _describe = describe;
        Default = initialDefault;
    }

    public bool HasProfile(string app) => _storage.HasProfile(app);

    /// <summary>What this setting SHOULD be right now for the given app: its
    /// own profile if it has one, otherwise the shared default.</summary>
    public T ResolveTarget(string app) =>
        ReconcilePlanner.Resolve(_storage.TryReadProfile(app, out T v), v, Default);

    public (ReconcileAction Action, string Live, string Target)? Reconcile(string app)
    {
        if (!_isAvailable()) return null;
        if (!_tryReadLive(out T live)) return null;

        T target = ResolveTarget(app);
        var plan = ReconcilePlanner.Plan(live, target, HasProfile(app));
        switch (plan)
        {
            case ReconcileAction.WriteProfile:
                _storage.WriteProfile(app, live);
                break;
            case ReconcileAction.WriteDefault:
                Default = live;
                _storage.WriteDefault(Default);
                break;
        }
        return (plan, _describe(live), _describe(target));
    }

    public bool IsDrifted(string app)
    {
        if (!_isAvailable()) return false;
        if (!_tryReadLive(out T live)) return false;
        return !EqualityComparer<T>.Default.Equals(live, ResolveTarget(app));
    }

    /// <summary>Shared save path for the overlay's Apply click: per-app when
    /// saveProfile is set, otherwise persisted as the new app-agnostic
    /// default. Same branch every setting of this shape routes through — see
    /// ProfileSetting's remarks for why that consistency matters.</summary>
    public void SaveOrClear(string app, bool saveProfile, T value)
    {
        if (saveProfile)
        {
            _storage.WriteProfile(app, value);
            return;
        }

        _storage.DeleteProfile(app);
        Default = value;
        _storage.WriteDefault(Default);
    }
}
