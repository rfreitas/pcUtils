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

    /// <summary>Raised immediately on Apply click; does not close the window.</summary>
    public event EventHandler? ApplyRequested;

    /// <summary>Raised immediately on trash-icon click; does not close the window.</summary>
    public event EventHandler? ProfileDeleteRequested;

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
        int        storedRate,
        bool       storedHdr)
    {
        InitializeComponent();

        ActiveApp = activeApp;
        SubtitleText.Text = $"Active: {activeApp}";

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

            var gsyncSync = new SyncedField<GsyncGlobalMode>(
                GsyncSyncDot, GsyncModeDropDown, mode, () => (GsyncGlobalMode)GsyncModeDropDown.SelectedIndex);
            _gsyncSync = gsyncSync;
            gsyncSync.Refresh();

            GsyncModeDropDown.SelectionChanged += (_, _) =>
            {
                if (!_suppressTouchTracking) gsyncSync.MarkTouched();
                gsyncSync.Refresh();
            };
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
        _rateSync = new SyncedField<int>(RateSyncDot, RateDropDown, storedRate, () => SelectedRate);
        _rateSync.Refresh();
        _hdrSync = new SyncedField<bool>(HdrSyncDot, HdrCheckBox, storedHdr, () => HdrEnabled,
            isActive: () => HdrRow.Visibility == Visibility.Visible);
        _hdrSync.Refresh();

        SaveCheckBox.Content   = new TextBlock { Text = $"Save for {activeApp}", TextWrapping = TextWrapping.Wrap };
        SaveCheckBox.IsChecked = hasProfile;

        DeleteProfileButton.Visibility = hasProfile ? Visibility.Visible : Visibility.Collapsed;
        DeleteProfileButton.Click += (_, _) =>
        {
            ProfileDeleteRequested?.Invoke(this, EventArgs.Empty);
            SaveCheckBox.IsChecked = false;
            DeleteProfileButton.Visibility = Visibility.Collapsed;
        };

        ApplyButton.Click  += (_, _) => ApplyRequested?.Invoke(this, EventArgs.Empty);
        CancelButton.Click += (_, _) => Close();

        // Wired last, after every initial value above is already set. Marks a
        // field "touched" on a change the user actually made (not the
        // constructor's own initial selection, nor a Refresh*LiveState call
        // writing programmatically — both guarded by _suppressTouchTracking),
        // and always recomputes that field's dot — this is what makes the dot
        // react to the user's own edits, not just to background reconciliation.
        RateDropDown.SelectionChanged += (_, _) =>
        {
            if (!_suppressTouchTracking) _rateSync.MarkTouched();
            _rateSync.Refresh();
        };
        HdrCheckBox.Checked   += (_, _) => { if (!_suppressTouchTracking) _hdrSync.MarkTouched(); _hdrSync.Refresh(); };
        HdrCheckBox.Unchecked += (_, _) => { if (!_suppressTouchTracking) _hdrSync.MarkTouched(); _hdrSync.Refresh(); };
    }

    /// <summary>Reflects a profile save/delete that just happened via ApplyRequested.</summary>
    public void ReflectProfileState(bool hasProfile) =>
        DeleteProfileButton.Visibility = hasProfile ? Visibility.Visible : Visibility.Collapsed;

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

    /// <summary>
    /// Tracks one control's live value against a stored (saved) baseline and
    /// keeps a sync-status dot in step with both — the user's own edits and a
    /// background push (Refresh*LiveState) alike. One shared implementation
    /// instead of hand-duplicating the touched/stored/dot pattern per setting;
    /// Rate, HDR, and GsyncMode each own one instance.
    /// </summary>
    private sealed class SyncedField<T>
    {
        private readonly System.Windows.Shapes.Ellipse _dot;
        private readonly FrameworkElement _control;
        private readonly Func<T> _getLive;
        private readonly Func<bool> _isActive;

        /// <summary>True once the user has actually changed this control since
        /// the overlay opened — callers (Refresh*LiveState) use this to avoid
        /// overwriting a selection already in progress.</summary>
        public bool Touched { get; private set; }

        /// <summary>The actual currently-saved value (profile-or-default) this
        /// field's live value is compared against.</summary>
        public T Stored { get; private set; }

        /// <param name="isActive">False means nothing meaningful to compare
        /// right now (e.g. HDR's row hidden because it isn't supported) —
        /// Refresh becomes a no-op rather than coloring a dot nobody sees.</param>
        public SyncedField(System.Windows.Shapes.Ellipse dot, FrameworkElement control, T initialStored,
            Func<T> getLive, Func<bool>? isActive = null)
        {
            _dot = dot;
            _control = control;
            _getLive = getLive;
            _isActive = isActive ?? (() => true);
            Stored = initialStored;
        }

        public void MarkTouched() => Touched = true;

        /// <summary>Recomputes the dot from the current live value vs Stored —
        /// call after any change to either side (a user edit, or UpdateStored
        /// below).</summary>
        public void Refresh()
        {
            if (!_isActive()) return;
            bool synced = EqualityComparer<T>.Default.Equals(_getLive(), Stored);
            _dot.Fill = synced ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.Red;
            _control.ToolTip = synced ? null : NotSyncedTip;
        }

        /// <summary>A background reconciler observed a new stored value —
        /// updates the baseline and refreshes the dot. Does not touch the
        /// displayed value itself: "what to set the control to" differs per
        /// control type (SelectedIndex vs. IsChecked), so callers still do that
        /// bit themselves, gated on Touched, before calling this.</summary>
        public void UpdateStored(T newStored)
        {
            Stored = newStored;
            Refresh();
        }
    }

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
