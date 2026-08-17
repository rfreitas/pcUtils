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

    // Set true the first time the user actually interacts with the
    // corresponding control, so RefreshLiveState (a background sync arriving
    // while this window is still open) never clobbers a selection already
    // in progress. Guarded against by _suppressTouchTracking while
    // RefreshLiveState itself is writing to the controls programmatically.
    private bool _suppressTouchTracking;
    private bool _rateTouched;
    private bool _hdrTouched;

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
        bool       rateSynced = true,
        bool       hdrSynced  = true)
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
            HdrCheckBox.Visibility = Visibility.Collapsed;
        }

        // Not synced means this app has no profile of its own and hardware has
        // drifted from the stored default (most likely still mid-settling from a
        // recent apply) — what's shown here is live hardware, not what Apply
        // would currently be overwriting.
        SetSyncIndicator(RateSyncDot, RateDropDown, rateSynced);
        SetSyncIndicator(HdrSyncDot, HdrCheckBox, hdrSynced);

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

        // Wired last, after every initial value above is already set — only
        // marks a control "touched" (see RefreshLiveState) on a change the user
        // actually made, not the constructor's own initial selection.
        RateDropDown.SelectionChanged += (_, _) => { if (!_suppressTouchTracking) _rateTouched = true; };
        HdrCheckBox.Checked           += (_, _) => { if (!_suppressTouchTracking) _hdrTouched  = true; };
        HdrCheckBox.Unchecked         += (_, _) => { if (!_suppressTouchTracking) _hdrTouched  = true; };
    }

    /// <summary>Reflects a profile save/delete that just happened via ApplyRequested.</summary>
    public void ReflectProfileState(bool hasProfile) =>
        DeleteProfileButton.Visibility = hasProfile ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Called by TrayApp whenever a background reconciliation (OnDisplayChange)
    /// updates the INI for the app this overlay is currently showing, so the
    /// dialog doesn't sit there silently stale for as long as it stays open.
    /// Never touches a control the user has already interacted with since
    /// opening — a background sync landing mid-decision shouldn't overwrite an
    /// in-progress choice out from under them.
    /// </summary>
    public void RefreshLiveState(int currentRate, bool rateSynced, bool currHdr, bool hdrSynced)
    {
        _suppressTouchTracking = true;
        try
        {
            if (!_rateTouched)
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
                SetSyncIndicator(RateSyncDot, RateDropDown, rateSynced);
            }

            if (!_hdrTouched && HdrCheckBox.Visibility == Visibility.Visible)
            {
                HdrCheckBox.IsChecked = currHdr;
                SetSyncIndicator(HdrSyncDot, HdrCheckBox, hdrSynced);
            }
        }
        finally { _suppressTouchTracking = false; }
    }

    /// <summary>Green dot + no tooltip when synced; red dot + explanatory tooltip
    /// when not — one shared definition so the constructor's initial render and
    /// RefreshLiveState's later updates can never disagree on what "synced"
    /// looks like.</summary>
    private static void SetSyncIndicator(System.Windows.Shapes.Ellipse dot, FrameworkElement control, bool synced)
    {
        dot.Fill = synced ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.Red;
        control.ToolTip = synced ? null : NotSyncedTip;
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
