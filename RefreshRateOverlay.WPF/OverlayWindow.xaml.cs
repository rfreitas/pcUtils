using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace RefreshRateOverlay.WPF;

/// <summary>
/// WPF port of RefreshRateOverlay.NET's OverlayForm. Same public surface
/// (ActiveApp/SelectedRate/SaveProfile/HdrEnabled/Applied) so it can drop
/// into TrayApp in place of the WinForms version later.
/// </summary>
public partial class OverlayWindow : Window
{
    public string ActiveApp { get; }

    public int SelectedRate =>
        RateDropDown.SelectedItem is string s && int.TryParse(s.Replace(" Hz", ""), out int r) ? r : 60;

    public bool SaveProfile => SaveCheckBox.IsChecked == true;
    public bool HdrEnabled  => HdrCheckBox.IsChecked == true;
    public bool Applied     { get; private set; }

    public OverlayWindow(
        string    activeApp,
        List<int> availableRates,
        int       currentRate,
        bool      hdrSupported,
        bool      hdrEnabled,
        bool      hasProfile)
    {
        InitializeComponent();

        ActiveApp = activeApp;
        SubtitleText.Text = $"Active: {activeApp}";

        int preSelect = 0;
        for (int i = 0; i < availableRates.Count; i++)
        {
            RateDropDown.Items.Add($"{availableRates[i]} Hz");
            if (availableRates[i] == currentRate) preSelect = i;
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

        ApplyButton.Click  += (_, _) => { Applied = true;  Close(); };
        CancelButton.Click += (_, _) => { Applied = false; Close(); };
    }
}
