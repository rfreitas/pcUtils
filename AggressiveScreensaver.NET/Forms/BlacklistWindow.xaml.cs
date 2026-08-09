using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AggressiveScreensaver.Services;

namespace AggressiveScreensaver.Forms;

/// <summary>
/// Checkbox list of all apps that have ever requested DISPLAY power.
/// Checked = ignored by overlay logic.
/// </summary>
internal partial class BlacklistWindow : Window
{
    private readonly PowercfgService _powercfg;
    private readonly IniStore        _ini;

    public BlacklistWindow(PowercfgService powercfg, IniStore ini)
    {
        InitializeComponent();
        _powercfg = powercfg;
        _ini      = ini;

        HeaderText.Text = "Checked apps will be IGNORED by this script.\n(They won't stop the black screen overlay)";

        var lightGray = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xCC, 0xCC));

        var sorted = _powercfg.HistoryApps.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        if (sorted.Count == 0)
        {
            AppsPanel.Children.Add(new TextBlock
            {
                Text       = "No blocking apps detected yet.",
                Foreground = lightGray,
            });
            return;
        }

        foreach (var appName in sorted)
        {
            var cb = new System.Windows.Controls.CheckBox
            {
                Content    = appName,
                Foreground = lightGray,
                IsChecked  = _powercfg.BlacklistedApps.ContainsKey(appName),
                Margin     = new Thickness(0, 0, 0, 4),
            };
            cb.Checked   += (_, _) => SetBlacklisted(appName, true);
            cb.Unchecked += (_, _) => SetBlacklisted(appName, false);
            AppsPanel.Children.Add(cb);
        }
    }

    private void SetBlacklisted(string appName, bool blacklisted)
    {
        if (blacklisted)
        {
            _powercfg.BlacklistedApps[appName] = true;
            _ini.WriteString("Blacklist", appName, "1");
        }
        else
        {
            _powercfg.BlacklistedApps.Remove(appName);
            _ini.DeleteKey("Blacklist", appName);
        }

        // Force immediate refresh
        _powercfg.Refresh();
    }
}
