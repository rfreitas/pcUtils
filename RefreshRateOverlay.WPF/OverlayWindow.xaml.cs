using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using RefreshRateOverlay.WPF.Services;

namespace RefreshRateOverlay.WPF;

/// <summary>
/// WPF port of RefreshRateOverlay.NET's OverlayForm. Apply and the trash icon
/// take effect immediately via events rather than closing the window — only
/// the Close button (or Escape) dismisses it.
/// </summary>
public partial class OverlayWindow : Window
{
    private const string NotSyncedTip =
        "Not synced with the saved default yet (still settling from a recent change) — Apply will save what's shown here as the new default.";

    // Guards the touched-tracking below while a Refresh*LiveState call (or the
    // constructor's own initial selection) writes to a control programmatically
    // — only an edit the user actually made should ever mark a field touched.
    private bool _suppressTouchTracking;

    // One SyncedField per setting that has a sync dot (see SyncedField remarks)
    // — Rate/Hdr always exist; GsyncMode only when NVAPI made the row visible.
    private SyncedField<int>  _rateSync = null!;
    private SyncedField<bool> _hdrSync  = null!;
    private SyncedField<GsyncGlobalMode>? _gsyncSync;
    private SyncedField<VrrAppState>? _appVrrSync;
    private SyncedField<uint>? _frameCapSync;
    private SyncedField<string>? _dsxSync;

    // Test-facing only (RefreshRateOverlay.WPF.Tests, via InternalsVisibleTo) —
    // production code never reads these, it goes through Refresh*LiveState.
    internal SyncedField<int>  RateSync => _rateSync;
    internal SyncedField<bool> HdrSync  => _hdrSync;
    internal SyncedField<GsyncGlobalMode>? GsyncSync => _gsyncSync;
    internal SyncedField<VrrAppState>? AppVrrSync => _appVrrSync;
    internal SyncedField<uint>? FrameCapSync => _frameCapSync;
    internal SyncedField<string>? DsxSync => _dsxSync;

    public string ActiveApp { get; }

    public int SelectedRate =>
        RateDropDown.SelectedItem is string s && int.TryParse(s.Replace(" Hz", ""), out int r) ? r : 60;

    public bool SaveProfile => SaveCheckBox.IsChecked == true;
    public bool HdrEnabled  => HdrCheckBox.IsChecked == true;

    /// <summary>Null means "— Not Managed —" (index 0): leave DSX alone. Only
    /// meaningful when DsxProfileEditable is true.</summary>
    public string? SelectedDsxProfile =>
        DsxProfileDropDown.SelectedIndex > 0 ? DsxProfileDropDown.SelectedItem as string : null;

    /// <summary>
    /// True if the controller dropdown reflects a live profile list from DSX this
    /// session. False means DSX wasn't reachable and the row (if shown at all) is
    /// just a grayed-out echo of whatever's already saved — callers must leave
    /// the saved DSX profile untouched in that case rather than reading "nothing
    /// selected" as "user cleared it".
    /// </summary>
    public bool DsxProfileEditable { get; private set; }

    /// <summary>Null when NVAPI wasn't available this session — row is hidden and
    /// callers must not write anything. Otherwise always a real value: like
    /// Rate/HDR, the tick decides whether this becomes the new default or this
    /// app's own override — there's no "unmanaged" sentinel to pick from.</summary>
    public GsyncGlobalMode? SelectedGsyncMode =>
        GsyncModeRow.Visibility == Visibility.Visible
            ? (GsyncGlobalMode)GsyncModeDropDown.SelectedIndex
            : null;

    /// <summary>Null when NVAPI wasn't available or the read failed this
    /// session — row is hidden. Otherwise reflects the dropdown regardless of
    /// whether it's currently enabled; callers must gate the actual write on
    /// SaveProfile themselves (disabled just means "don't let the user touch
    /// this," not "this value isn't meaningful").</summary>
    public VrrAppState? SelectedAppVrrState =>
        AppVrrRow.Visibility == Visibility.Visible
            ? IndexToVrrState(AppVrrDropDown.SelectedIndex)
            : null;

    // Not Set (0xFFFFFFFE) doesn't sit next to the real driver values
    // (0-4) in NVIDIA's own enum, so dropdown position can't be a raw cast —
    // Not Set occupies index 0, the four real states follow at 1-4... 5.
    internal static int VrrStateToIndex(VrrAppState state) =>
        state == VrrAppState.NotSet ? 0 : (int)state + 1;

    internal static VrrAppState IndexToVrrState(int index) =>
        index == 0 ? VrrAppState.NotSet : (VrrAppState)(index - 1);

    /// <summary>Same no-default/global-scope shape as SelectedAppVrrState above
    /// (null = row hidden/NVAPI unavailable; a real value — including the
    /// NvidiaGsyncService.FrameCapNotSet sentinel for an explicit "Not Set"
    /// pick — otherwise) — callers gate the actual write on SaveProfile
    /// themselves, same as AppVrr.</summary>
    public uint? SelectedFrameCapFps =>
        FrameCapRow.Visibility == Visibility.Visible
            ? ParseFrameCapText(FrameCapTextBox.Text)
            : null;

    /// <summary>The app's own writable range for FRL_FPS — narrower than the
    /// driver's real 0-1023 (see NvidiaGsyncService.FrlFpsId remarks); nothing
    /// above 1000 is ever offered or accepted through this UI.</summary>
    internal const uint FrameCapMaxFps = 1000;

    internal static string DescribeFrameCap(uint capFps) =>
        capFps == NvidiaGsyncService.FrameCapNotSet ? "Not Set" : capFps.ToString();

    /// <summary>Inverse of DescribeFrameCap — plain numeric text only (blank
    /// or "Not Set" both mean the sentinel); anything that doesn't parse as a
    /// number within 0-FrameCapMaxFps falls back to Not Set rather than
    /// silently clamping to a value the user didn't type.</summary>
    internal static uint ParseFrameCapText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return NvidiaGsyncService.FrameCapNotSet;
        string t = text.Trim();
        if (t.Equals("Not Set", StringComparison.OrdinalIgnoreCase)) return NvidiaGsyncService.FrameCapNotSet;

        return uint.TryParse(t, out uint v) && v <= FrameCapMaxFps ? v : NvidiaGsyncService.FrameCapNotSet;
    }

    /// <summary>Raised immediately on Apply click; does not close the window.</summary>
    public event EventHandler? ApplyRequested;

    /// <summary>Raised immediately on trash-icon click; does not close the window.</summary>
    public event EventHandler? ProfileDeleteRequested;

    /// <summary>Raised when the app picker is changed to something other than
    /// ActiveApp — this window never retargets itself in place, the caller
    /// is expected to close it and open a fresh one for the new app (see
    /// TrayApp.ShowOverlay's forcedApp parameter).</summary>
    public event EventHandler<string>? AppSwitchRequested;

    // Guards AppPickerDropDown.SelectionChanged during the constructor's own
    // initial population — only a change the user actually made should raise
    // AppSwitchRequested, same pattern as _suppressTouchTracking.
    private bool _suppressAppPickerEvents;

    /// <summary>Wires one SyncedField's full lifecycle in one call: construct,
    /// initial Refresh, and hook the control's own change event to
    /// MarkTouched+Refresh (guarded by _suppressTouchTracking, so the
    /// constructor's own initial selection and a Refresh*LiveState push never
    /// count as a user edit). Every dot-bearing setting — Rate, HDR, GsyncMode,
    /// App VRR, Controller — wires through this one helper instead of
    /// hand-repeating these lines per setting; that repetition (and the easy
    /// chance to skip a step) is exactly what let App VRR and Controller each
    /// go without a dot for as long as they did.</summary>
    private SyncedField<T> BindSynced<T>(
        System.Windows.Shapes.Ellipse dot, FrameworkElement control, T stored, Func<T> getLive,
        Action<Action> subscribeChanged, Func<bool>? isActive = null)
    {
        var field = new SyncedField<T>(dot, control, stored, getLive, NotSyncedTip, isActive);
        field.Refresh();
        subscribeChanged(() =>
        {
            if (!_suppressTouchTracking) field.MarkTouched();
            field.Refresh();
        });
        return field;
    }

    public OverlayWindow(
        string     activeApp,
        List<int>  availableRates,
        int        preselectRate,
        bool       hdrSupported,
        bool       hdrEnabled,
        bool       hasProfile,
        WindowMode windowMode,
        string?    preselectDsxProfile,
        GsyncGlobalMode? gsyncMode,
        VrrAppState? appVrrState,
        List<string> runningApps,
        int        storedRate,
        bool       storedHdr,
        string?    applyWarning = null,
        uint?      frameCapFps = null)
    {
        InitializeComponent();

        ActiveApp = activeApp;

        SetApplyWarning(applyWarning);

        _suppressAppPickerEvents = true;
        var pickerItems = new List<string>(runningApps);
        if (!pickerItems.Contains(activeApp, StringComparer.OrdinalIgnoreCase))
            pickerItems.Add(activeApp);
        pickerItems.Sort(StringComparer.OrdinalIgnoreCase);
        foreach (string a in pickerItems) AppPickerDropDown.Items.Add(a);
        AppPickerDropDown.SelectedItem = activeApp;
        _suppressAppPickerEvents = false;

        AppPickerDropDown.SelectionChanged += (_, _) =>
        {
            if (_suppressAppPickerEvents) return;
            if (AppPickerDropDown.SelectedItem is string selected && selected != ActiveApp)
                AppSwitchRequested?.Invoke(this, selected);
        };

        _ = LoadDsxProfilesAsync(preselectDsxProfile);

        // Gated only on NVAPI availability, not on whether there's a "real"
        // foreground app — same as Rate/HDR, which apply to Desktop too. A
        // single dropdown whose persistence target (default vs. this app's own
        // override) is decided by the Save tick, exactly like Rate/HDR.
        if (gsyncMode is { } mode)
        {
            foreach (string label in new[] { "Disabled", "Fullscreen Only", "Fullscreen + Windowed" })
                GsyncModeDropDown.Items.Add(label);
            GsyncModeDropDown.SelectedIndex = (int)mode;
            GsyncModeRow.Visibility = Visibility.Visible;

            _gsyncSync = BindSynced(GsyncSyncDot, GsyncModeDropDown, mode,
                () => (GsyncGlobalMode)GsyncModeDropDown.SelectedIndex,
                changed => GsyncModeDropDown.SelectionChanged += (_, _) => changed());
        }

        // No default/global scope of its own (see VrrAppState remarks) — only
        // ever editable while this app is in per-app-profile mode (the Save
        // tick), so it starts enabled/disabled from hasProfile and is kept in
        // sync with the checkbox below rather than read once at open. Null
        // just means "couldn't read it" or NVAPI unavailable, so the row
        // stays hidden rather than showing a misleading value.
        if (appVrrState is { } vrr)
        {
            // Not Set is a real, distinct entry in NVIDIA's own data model —
            // the profile has no VRR_APPLICATION_OVERRIDE key at all, as
            // opposed to Allow, where the key exists with value 0. They
            // behave identically today (driver just follows global G-SYNC
            // either way), but exposing Not Set as its own slot lets Apply
            // actually delete the key (see SetAppVrrOverride) and put the app
            // back to a genuinely unmanaged state, instead of only ever being
            // able to write Allow explicitly.
            foreach (string label in new[] { "Not Set", "Allow (follow global)", "Force Off", "Disallow", "ULMB", "Fixed Refresh" })
                AppVrrDropDown.Items.Add(label);
            AppVrrDropDown.SelectedIndex = VrrStateToIndex(vrr);
            AppVrrDropDown.IsEnabled = hasProfile;
            AppVrrRow.Visibility = Visibility.Visible;

            // Baseline is whatever NVIDIA reported when this dialog opened —
            // there's no background reconciliation for this setting (see
            // TrayApp remarks: it's never auto-applied on focus change, only
            // ever written on an explicit Apply click), so the dot means "does
            // the dropdown still match what was true at open," not "matches
            // our own persisted truth" the way Rate/HDR/GsyncMode's dots do.
            _appVrrSync = BindSynced(AppVrrSyncDot, AppVrrDropDown, vrr,
                () => IndexToVrrState(AppVrrDropDown.SelectedIndex),
                changed => AppVrrDropDown.SelectionChanged += (_, _) => changed());
        }

        // Same no-default/global-scope shape as App VRR just above — see its
        // comment and SelectedFrameCapFps's remarks. Plain text box, not a
        // dropdown — FRL_FPS's 0-FrameCapMaxFps range is too wide for a
        // preset list to be much more than decoration.
        if (frameCapFps is { } capFps)
        {
            FrameCapTextBox.Text = DescribeFrameCap(capFps);
            FrameCapTextBox.IsEnabled = hasProfile;
            FrameCapRow.Visibility = Visibility.Visible;

            _frameCapSync = BindSynced(FrameCapSyncDot, FrameCapTextBox, capFps,
                () => ParseFrameCapText(FrameCapTextBox.Text),
                changed => FrameCapTextBox.TextChanged += (_, _) => changed());
        }

        bool canCheckPresentation = !string.IsNullOrEmpty(activeApp) && activeApp != "Desktop";
        if (canCheckPresentation)
        {
            PresentationText.Text = "Presentation: checking…";
            RecommendationText.Text = "";
            _ = RunPresentationCheckAsync(activeApp, windowMode);
        }
        else
        {
            PresentationText.Text = $"Mode: {WindowModeService.Describe(windowMode)}";
            RecommendationText.Text = "";
        }

        int preSelect = 0;
        for (int i = 0; i < availableRates.Count; i++)
        {
            RateDropDown.Items.Add($"{availableRates[i]} Hz");
            if (availableRates[i] == preselectRate) preSelect = i;
        }
        RateDropDown.SelectedIndex = preSelect;

        if (hdrSupported)
        {
            HdrCheckBox.IsChecked = hdrEnabled;
        }
        else
        {
            HdrRow.Visibility = Visibility.Collapsed;
        }

        // The sync dots compare against these baselines — see SyncedField
        // remarks. Constructed here (not earlier) since RateDropDown/HdrCheckBox
        // must already hold their initial values for the first Refresh() to be
        // meaningful.
        _rateSync = BindSynced(RateSyncDot, RateDropDown, storedRate, () => SelectedRate,
            changed => RateDropDown.SelectionChanged += (_, _) => changed());
        _hdrSync = BindSynced(HdrSyncDot, HdrCheckBox, storedHdr, () => HdrEnabled,
            changed =>
            {
                HdrCheckBox.Checked   += (_, _) => changed();
                HdrCheckBox.Unchecked += (_, _) => changed();
            },
            isActive: () => HdrRow.Visibility == Visibility.Visible);

        SaveCheckBox.Content   = new TextBlock { Text = $"Save for {activeApp}", TextWrapping = TextWrapping.Wrap };
        SaveCheckBox.IsChecked = hasProfile;

        // App VRR and Frame Cap have no default/global scope of their own to
        // write into (see VrrAppState/SelectedFrameCapFps remarks) — only ever
        // editable while Save is ticked, and reactively so, since the user can
        // flip that tick after opening.
        SaveCheckBox.Checked   += (_, _) => { AppVrrDropDown.IsEnabled = true;  FrameCapTextBox.IsEnabled = true; };
        SaveCheckBox.Unchecked += (_, _) => { AppVrrDropDown.IsEnabled = false; FrameCapTextBox.IsEnabled = false; };

        DeleteProfileButton.Visibility = hasProfile ? Visibility.Visible : Visibility.Collapsed;
        DeleteProfileButton.Click += (_, _) =>
        {
            ProfileDeleteRequested?.Invoke(this, EventArgs.Empty);
            SaveCheckBox.IsChecked = false;
            DeleteProfileButton.Visibility = Visibility.Collapsed;
        };

        ApplyButton.Click  += (_, _) => ApplyRequested?.Invoke(this, EventArgs.Empty);
        CancelButton.Click += (_, _) => Close();
    }

    /// <summary>Reflects a profile save/delete that just happened via ApplyRequested.</summary>
    public void ReflectProfileState(bool hasProfile) =>
        DeleteProfileButton.Visibility = hasProfile ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Shows or hides the reconciliation-off "couldn't apply" banner —
    /// null/empty hides it. Called from the constructor (reflecting whatever
    /// TrayApp's enforcement already gave up on before this dialog opened) and
    /// again by TrayApp whenever that state changes while the overlay stays
    /// open (a fresh give-up, a recovery, or the user clicking Apply).</summary>
    public void SetApplyWarning(string? failedSettings)
    {
        bool hasWarning = !string.IsNullOrEmpty(failedSettings);
        ApplyWarningText.Text = hasWarning ? $"⚠ Couldn't apply: {failedSettings} — click Apply to retry." : "";
        ApplyWarningText.Visibility = hasWarning ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Called by TrayApp.ReconcileRateAndHdr (a HardwareChange subscriber)
    /// whenever it updates the INI for the app this overlay is currently
    /// showing, so the dialog doesn't sit there silently stale for as long as
    /// it stays open. The displayed control value only follows the new baseline
    /// if the user hasn't touched that control since opening — a background
    /// sync landing mid-decision shouldn't overwrite an in-progress choice —
    /// but the dot still gets recomputed either way (SyncedField.UpdateStored),
    /// since "touched" only protects the selection, not whether it still
    /// matches the (possibly just-changed) baseline.
    /// </summary>
    public void RefreshLiveState(int currentRate, bool currHdr)
    {
        _suppressTouchTracking = true;
        try
        {
            if (!_rateSync.Touched)
            {
                string target = $"{currentRate} Hz";
                for (int i = 0; i < RateDropDown.Items.Count; i++)
                {
                    if (RateDropDown.Items[i] is string s && s == target)
                    {
                        RateDropDown.SelectedIndex = i;
                        break;
                    }
                }
            }
            _rateSync.UpdateStored(currentRate);

            if (!_hdrSync.Touched && HdrRow.Visibility == Visibility.Visible)
            {
                HdrCheckBox.IsChecked = currHdr;
            }
            _hdrSync.UpdateStored(currHdr);
        }
        finally { _suppressTouchTracking = false; }
    }

    /// <summary>Same role as RefreshLiveState, called by
    /// TrayApp.ReconcileGsyncMode instead — kept as its own method rather than
    /// folded into RefreshLiveState since it's driven by a completely separate
    /// HardwareChange subscriber that knows nothing about rate/HDR. No-ops if
    /// this app's overlay never showed a G-SYNC row to begin with.</summary>
    public void RefreshGsyncLiveState(GsyncGlobalMode liveMode)
    {
        if (_gsyncSync is not { } sync) return;

        _suppressTouchTracking = true;
        try
        {
            if (!sync.Touched) GsyncModeDropDown.SelectedIndex = (int)liveMode;
            sync.UpdateStored(liveMode);
        }
        finally { _suppressTouchTracking = false; }
    }

    // -------------------------------------------------------------------------
    // Apply-reflection — App VRR / Frame Cap / Controller. Unlike Rate/HDR/
    // GsyncMode (which get RefreshLiveState/RefreshGsyncLiveState pushed from
    // TrayApp's background HardwareChange reconciler), none of these three
    // ever get reconciled in the background — Apply is the ONLY moment their
    // dot can ever be told "what's shown is now the saved truth." Before
    // these existed, TrayApp's Apply handler wrote the new value to storage/
    // NVIDIA but never told the dot, so it stayed red until the dialog was
    // closed and reopened (which recomputes the baseline from scratch) — a
    // successful Apply looked like it hadn't taken effect. No touched-check
    // needed the way RefreshLiveState has one: unlike a background push that
    // might race an in-progress edit, this always fires with the exact value
    // Apply just read off the control, so there's nothing to protect against
    // overwriting — just move the baseline to match what's already shown.
    // -------------------------------------------------------------------------

    /// <summary>Called by TrayApp right after a successful SetAppVrrOverride
    /// write. No-op if this session never showed the row.</summary>
    public void ReflectAppVrrApplied(VrrAppState state) => _appVrrSync?.UpdateStored(state);

    /// <summary>Same role as ReflectAppVrrApplied, for Frame Cap (after a
    /// successful SetAppFrameCap write).</summary>
    public void ReflectFrameCapApplied(uint capFps) => _frameCapSync?.UpdateStored(capFps);

    /// <summary>Same role as ReflectAppVrrApplied, for the DSX controller
    /// profile (after a successful ApplyDsxProfileToDevicesAsync push).
    /// Matches DsxSync's own getLive convention (SelectedDsxProfile ?? "") —
    /// callers should pass that same "" for a cleared/not-managed
    /// selection.</summary>
    public void ReflectDsxApplied(string profile) => _dsxSync?.UpdateStored(profile);

    /// <summary>
    /// Runs automatically whenever the overlay opens for a real app — no button,
    /// no extra prompt: the whole process is already elevated (app.manifest), so
    /// this is just a normal ~3s background capture. Fills in both the raw
    /// presentation-mode line and the recommended-mode verdict line.
    /// </summary>
    private async Task RunPresentationCheckAsync(string app, WindowMode currentMode)
    {
        var result = await PresentModeService.CaptureAsync(app);

        if (result is null)
        {
            PresentationText.Text = $"Mode: {WindowModeService.Describe(currentMode)} — presentation data unavailable.";
            RecommendationText.Text = "";
            return;
        }

        PresentationText.Text = $"Presentation: {result.RawMode}";

        WindowMode? recommended = result.Verdict switch
        {
            PresentModeVerdict.Optimal or PresentModeVerdict.Good => currentMode,
            PresentModeVerdict.Inefficient                        => WindowMode.ExclusiveFullscreen,
            _                                                     => null,
        };

        RecommendationText.Text = recommended switch
        {
            null                                  => "Recommendation unavailable.",
            var r when r == currentMode            => $"✓ {WindowModeService.Describe(currentMode)} is correct.",
            _                                       => $"✗ Recommended: {WindowModeService.Describe(recommended.Value)} (currently {WindowModeService.Describe(currentMode)}).",
        };
    }

    /// <summary>
    /// Populates the controller-profile dropdown from DSX itself (via
    /// DSX_Console.exe) and reveals the row as editable. DSX_Console round-trips
    /// (ListDevicesAsync/ListProfilesAsync) can take a couple seconds each, so
    /// ShowDsxLoading puts up a grayed placeholder first — synchronously, before
    /// the first await — instead of leaving the row invisible and popping it in
    /// once data lands. If DSX isn't installed, isn't running yet, has no
    /// controller connected, or has no profiles defined, ShowDsxAsUnavailable
    /// replaces that placeholder rather than just hiding the row — otherwise a
    /// saved DSX profile silently gets wiped on Apply the moment DSX happens to
    /// not be up yet (e.g. opened after the overlay).
    /// </summary>
    private async Task LoadDsxProfilesAsync(string? preselect)
    {
        if (!DsxProfileService.IsAvailable) { ShowDsxAsUnavailable(preselect); return; }

        ShowDsxLoading(preselect);

        var devices = await DsxProfileService.ListDevicesAsync();
        if (devices.Count == 0) { ShowDsxAsUnavailable(preselect); return; }

        var profiles = await DsxProfileService.ListProfilesAsync();
        if (profiles.Count == 0) { ShowDsxAsUnavailable(preselect); return; }

        DsxProfileDropDown.Items.Clear();
        DsxProfileDropDown.Items.Add("— Not Managed —");
        int preIndex = 0;
        for (int i = 0; i < profiles.Count; i++)
        {
            DsxProfileDropDown.Items.Add(profiles[i]);
            if (profiles[i] == preselect) preIndex = i + 1;
        }
        DsxProfileDropDown.SelectedIndex = preIndex;
        DsxProfileDropDown.IsEnabled = true;
        DsxProfileRow.Visibility = Visibility.Visible;
        DsxProfileEditable = true;

        // Baseline is the first connected device's actual active profile —
        // there's no background reconciliation for DSX (no synchronous live
        // read exists; this async, multi-second DSX_Console round trip is the
        // only one), so like App VRR this dot means "does the dropdown still
        // match what DSX reported when this dialog opened," refreshed only by
        // the user's own edits within the session. Multiple controllers could
        // in theory carry different active profiles, but ApplyDsxProfileToDevicesAsync
        // already pushes one profile to every device uniformly, so "the" live
        // value is the same simplification the apply path already makes.
        _dsxSync = BindSynced(DsxSyncDot, DsxProfileDropDown, devices[0].ActiveProfile,
            () => SelectedDsxProfile ?? "",
            changed => DsxProfileDropDown.SelectionChanged += (_, _) => changed());
    }

    /// <summary>
    /// Immediate, synchronous placeholder shown the instant we know DSX is
    /// reachable, before the multi-second round trip to DSX_Console for the
    /// actual device/profile list — fills the row's space right away instead of
    /// a late pop-in. Grayed and disabled: not a real choice yet.
    /// </summary>
    private void ShowDsxLoading(string? preselect)
    {
        DsxProfileDropDown.Items.Clear();
        DsxProfileDropDown.Items.Add(preselect ?? "Loading…");
        DsxProfileDropDown.SelectedIndex = 0;
        DsxProfileDropDown.IsEnabled = false;
        DsxProfileRow.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// DSX isn't reachable (or turned out to have nothing to offer) this
    /// session. If a profile is already saved for this app, show it as a
    /// disabled, grayed-out entry — visible so the user can see it's still
    /// assigned, but not a real choice, since DsxProfileEditable stays false
    /// and callers must not touch DSX storage based on it. Otherwise collapses
    /// the row (also undoes ShowDsxLoading's placeholder if that ran first).
    /// </summary>
    private void ShowDsxAsUnavailable(string? preselect)
    {
        DsxProfileDropDown.Items.Clear();
        if (preselect is null)
        {
            DsxProfileRow.Visibility = Visibility.Collapsed;
            return;
        }

        DsxProfileDropDown.Items.Add(preselect);
        DsxProfileDropDown.SelectedIndex = 0;
        DsxProfileDropDown.IsEnabled = false;
        DsxProfileRow.Visibility = Visibility.Visible;
    }
}
