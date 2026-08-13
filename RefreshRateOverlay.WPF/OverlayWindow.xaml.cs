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
    public string ActiveApp { get; }

    public int SelectedRate =>
        RateDropDown.SelectedItem is string s && int.TryParse(s.Replace(" Hz", ""), out int r) ? r : 60;

    public bool SaveProfile => SaveCheckBox.IsChecked == true;
    public bool HdrEnabled  => HdrCheckBox.IsChecked == true;

    /// <summary>Null means "— Not Managed —" (index 0): leave DSX alone.</summary>
    public string? SelectedDsxProfile =>
        DsxProfileDropDown.SelectedIndex > 0 ? DsxProfileDropDown.SelectedItem as string : null;

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
        string?    preselectDsxProfile)
    {
        InitializeComponent();

        ActiveApp = activeApp;
        SubtitleText.Text = $"Active: {activeApp}";

        _ = LoadDsxProfilesAsync(preselectDsxProfile);

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
    }

    /// <summary>Reflects a profile save/delete that just happened via ApplyRequested.</summary>
    public void ReflectProfileState(bool hasProfile) =>
        DeleteProfileButton.Visibility = hasProfile ? Visibility.Visible : Visibility.Collapsed;

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
    /// DSX_Console.exe) and reveals the row — stays hidden if DSX isn't
    /// installed, isn't running, has no controller connected, or has no
    /// profiles defined, since there's nothing useful to offer in that case.
    /// </summary>
    private async Task LoadDsxProfilesAsync(string? preselect)
    {
        if (!DsxProfileService.IsAvailable) return;

        var devices = await DsxProfileService.ListDevicesAsync();
        if (devices.Count == 0) return;

        var profiles = await DsxProfileService.ListProfilesAsync();
        if (profiles.Count == 0) return;

        DsxProfileDropDown.Items.Add("— Not Managed —");
        int preIndex = 0;
        for (int i = 0; i < profiles.Count; i++)
        {
            DsxProfileDropDown.Items.Add(profiles[i]);
            if (profiles[i] == preselect) preIndex = i + 1;
        }
        DsxProfileDropDown.SelectedIndex = preIndex;
        DsxProfileRow.Visibility = Visibility.Visible;
    }
}
