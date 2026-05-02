using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using AggressiveScreensaver.Services;

namespace AggressiveScreensaver.Forms;

/// <summary>
/// Checkbox list of all apps that have ever requested DISPLAY power.
/// Checked = ignored by overlay logic.
/// Ported from ShowBlacklistGui() in index.ahk.
/// </summary>
internal sealed class BlacklistForm : Form
{
    private readonly PowercfgService _powercfg;
    private readonly IniStore        _ini;

    private const int DarkBg   = 0x2D2D2D;
    private const int DarkFg   = 0xCCCCCC;

    public BlacklistForm(PowercfgService powercfg, IniStore ini)
    {
        _powercfg = powercfg;
        _ini      = ini;

        Text             = "Blacklist Apps (Check to Ignore)";
        Font             = new Font("Segoe UI", 9f);
        BackColor        = ColorTranslator.FromHtml("#2d2d2d");
        ForeColor        = Color.FromArgb(0xCC, 0xCC, 0xCC);
        FormBorderStyle  = FormBorderStyle.FixedDialog;
        MaximizeBox      = false;
        MinimizeBox      = false;
        StartPosition    = FormStartPosition.CenterScreen;
        AutoScroll       = true;
        ClientSize       = new Size(420, 400);

        var info = new Label
        {
            Text      = "Checked apps will be IGNORED by this script.\n(They won't stop the black screen overlay)",
            ForeColor = Color.FromArgb(0xCC, 0xCC, 0xCC),
            BackColor = Color.Transparent,
            AutoSize  = false,
            Bounds    = new Rectangle(10, 10, 400, 40),
        };
        Controls.Add(info);

        var panel = new FlowLayoutPanel
        {
            Bounds        = new Rectangle(10, 55, 400, 295),
            FlowDirection = FlowDirection.TopDown,
            AutoScroll    = true,
            BackColor     = ColorTranslator.FromHtml("#2d2d2d"),
            ForeColor     = Color.FromArgb(0xCC, 0xCC, 0xCC),
            WrapContents  = false,
        };

        var sorted = _powercfg.HistoryApps.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        if (sorted.Count == 0)
        {
            panel.Controls.Add(new Label
            {
                Text = "No blocking apps detected yet.",
                ForeColor = Color.FromArgb(0xCC, 0xCC, 0xCC),
                AutoSize = true,
            });
        }
        else
        {
            foreach (var appName in sorted)
            {
                bool isBlacklisted = _powercfg.BlacklistedApps.ContainsKey(appName);
                var cb = new CheckBox
                {
                    Text      = appName,
                    Checked   = isBlacklisted,
                    ForeColor = Color.FromArgb(0xCC, 0xCC, 0xCC),
                    BackColor = Color.Transparent,
                    UseVisualStyleBackColor = false,
                    AutoSize  = true,
                    Width     = 390,
                };
                cb.CheckedChanged += OnCheckChanged;
                panel.Controls.Add(cb);
            }
        }

        Controls.Add(panel);
    }

    private void OnCheckChanged(object? sender, EventArgs e)
    {
        if (sender is not CheckBox cb) return;
        string appName  = cb.Text;
        bool   isChecked = cb.Checked;

        if (isChecked)
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
