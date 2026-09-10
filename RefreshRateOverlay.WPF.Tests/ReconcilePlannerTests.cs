using RefreshRateOverlay.WPF.Services;

namespace RefreshRateOverlay.WPF.Tests;

public class ReconcilePlannerTests
{
    // ---- Resolve ---------------------------------------------------------

    [Fact]
    public void Resolve_HasProfile_ReturnsProfileValue()
    {
        Assert.Equal(100, ReconcilePlanner.Resolve(hasProfile: true, profileValue: 100, defaultValue: 60));
    }

    [Fact]
    public void Resolve_NoProfile_ReturnsDefaultValue()
    {
        Assert.Equal(60, ReconcilePlanner.Resolve(hasProfile: false, profileValue: 100, defaultValue: 60));
    }

    [Fact]
    public void Resolve_WorksForBool()
    {
        Assert.True(ReconcilePlanner.Resolve(hasProfile: true, profileValue: true, defaultValue: false));
        Assert.False(ReconcilePlanner.Resolve(hasProfile: false, profileValue: true, defaultValue: false));
    }

    [Fact]
    public void Resolve_WorksForEnum()
    {
        Assert.Equal(GsyncGlobalMode.FullscreenAndWindowed, ReconcilePlanner.Resolve(
            hasProfile: true, profileValue: GsyncGlobalMode.FullscreenAndWindowed, defaultValue: GsyncGlobalMode.Disabled));
        Assert.Equal(GsyncGlobalMode.Disabled, ReconcilePlanner.Resolve(
            hasProfile: false, profileValue: GsyncGlobalMode.FullscreenAndWindowed, defaultValue: GsyncGlobalMode.Disabled));
    }

    // ---- Plan --------------------------------------------------------------
    // Covers every (matches-target?, hasProfile?) combination — the exact
    // branching where both of this app's reconciliation bugs lived.

    [Fact]
    public void Plan_LiveMatchesTarget_ReturnsNone_RegardlessOfProfile()
    {
        Assert.Equal(ReconcileAction.None, ReconcilePlanner.Plan(live: 100, target: 100, hasProfile: true));
        Assert.Equal(ReconcileAction.None, ReconcilePlanner.Plan(live: 100, target: 100, hasProfile: false));
    }

    [Fact]
    public void Plan_LiveDiffersFromTarget_HasProfile_ReturnsWriteProfile()
    {
        Assert.Equal(ReconcileAction.WriteProfile, ReconcilePlanner.Plan(live: 100, target: 60, hasProfile: true));
    }

    [Fact]
    public void Plan_LiveDiffersFromTarget_NoProfile_ReturnsWriteDefault()
    {
        Assert.Equal(ReconcileAction.WriteDefault, ReconcilePlanner.Plan(live: 100, target: 60, hasProfile: false));
    }

    [Fact]
    public void Plan_WorksForBool()
    {
        Assert.Equal(ReconcileAction.None, ReconcilePlanner.Plan(live: true, target: true, hasProfile: false));
        Assert.Equal(ReconcileAction.WriteDefault, ReconcilePlanner.Plan(live: true, target: false, hasProfile: false));
        Assert.Equal(ReconcileAction.WriteProfile, ReconcilePlanner.Plan(live: true, target: false, hasProfile: true));
    }

    [Fact]
    public void Plan_WorksForEnum()
    {
        Assert.Equal(
            ReconcileAction.WriteDefault,
            ReconcilePlanner.Plan(live: GsyncGlobalMode.FullscreenOnly, target: GsyncGlobalMode.Disabled, hasProfile: false));
        Assert.Equal(
            ReconcileAction.WriteProfile,
            ReconcilePlanner.Plan(live: GsyncGlobalMode.FullscreenOnly, target: GsyncGlobalMode.Disabled, hasProfile: true));
        Assert.Equal(
            ReconcileAction.None,
            ReconcilePlanner.Plan(live: GsyncGlobalMode.FullscreenOnly, target: GsyncGlobalMode.FullscreenOnly, hasProfile: true));
    }

    // ---- PlanSeed ------------------------------------------------------
    // Covers TrayApp's startup default-seeding decision — the exact gap that
    // let a live value merely left over from whatever was on screen at
    // restart (a game's own profiled rate/HDR/G-SYNC) permanently overwrite
    // the real default with ReconciliationEnabled=0, indistinguishable from a
    // deliberate profile change. See PlanSeed's own remarks for the full
    // production incident this is regression coverage for.

    [Fact]
    public void PlanSeed_NeverConfigured_AdoptsLive_RegardlessOfReconciliation()
    {
        Assert.Equal(SeedAction.AdoptLive,
            ReconcilePlanner.PlanSeed(hasStoredDefault: false, stored: 60, live: 100, reconciliationEnabled: false));
        Assert.Equal(SeedAction.AdoptLive,
            ReconcilePlanner.PlanSeed(hasStoredDefault: false, stored: 60, live: 100, reconciliationEnabled: true));
    }

    [Fact]
    public void PlanSeed_ConfiguredAndReconciliationDisabled_KeepsStored_EvenWhenLiveDiffers()
    {
        // The actual bug: a restart while a 100Hz-profiled game was still
        // driving the display must not silently rewrite the real 60Hz
        // default just because reconciliation is off.
        Assert.Equal(SeedAction.KeepStored,
            ReconcilePlanner.PlanSeed(hasStoredDefault: true, stored: 60, live: 100, reconciliationEnabled: false));
    }

    [Fact]
    public void PlanSeed_ConfiguredAndReconciliationEnabled_AdoptsLiveOnlyWhenItDiffers()
    {
        Assert.Equal(SeedAction.AdoptLive,
            ReconcilePlanner.PlanSeed(hasStoredDefault: true, stored: 60, live: 100, reconciliationEnabled: true));
        Assert.Equal(SeedAction.KeepStored,
            ReconcilePlanner.PlanSeed(hasStoredDefault: true, stored: 100, live: 100, reconciliationEnabled: true));
    }

    [Fact]
    public void PlanSeed_WorksForBoolAndEnum()
    {
        Assert.Equal(SeedAction.KeepStored,
            ReconcilePlanner.PlanSeed(hasStoredDefault: true, stored: false, live: true, reconciliationEnabled: false));
        Assert.Equal(SeedAction.AdoptLive,
            ReconcilePlanner.PlanSeed(hasStoredDefault: true, stored: GsyncGlobalMode.Disabled,
                live: GsyncGlobalMode.FullscreenAndWindowed, reconciliationEnabled: true));
    }
}
