using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using LgtvBrightness.Forms;
using LgtvBrightness.Rendering;
using LgtvBrightness.Services;
using Shared;

namespace LgtvBrightness.Tray;

/// <summary>
/// Owns the tray icon and all services. WPF has no native tray-icon control,
/// so this uses System.Windows.Forms.NotifyIcon/ContextMenuStrip, same as
/// RefreshRateOverlay.WPF and AggressiveScreensaver.NET.
/// </summary>
internal sealed class TrayApp : IDisposable
{
    private readonly IniStore              _ini;
    private readonly Settings              _settings;
    private readonly BrightnessService     _brightness;
    private readonly AutoBrightnessService _autoBrightness;
    private readonly SystemMessageSink     _msgSink;
    private readonly HotkeyService         _hotkeyUp;
    private readonly HotkeyService         _hotkeyDown;

    private readonly NotifyIcon _tray;
    private Icon? _currentIcon;
    private ToolStripMenuItem? _valueItem;

    private BacklightSliderWindow? _sliderWindow;
    private BrightnessOsdWindow? _osd;

    private long _lastHotkeyTick;
    private const int HotkeyMinIntervalMs = 90; // throttle for key-repeat

    private long _lastHoverSyncTick;
    private const int HoverMinIntervalMs = 5000;

    private const string TaskName        = "LGTV_brightness";
    private static readonly string ExePath = Path.Combine(AppContext.BaseDirectory, "LGTV_brightness.NET.exe");
    private const string TaskDescription = "Launches LGTV Backlight Control at logon.";

    public TrayApp()
    {
        string iniPath = Path.Combine(AppContext.BaseDirectory, "LGTV_brightness.ini");
        _ini      = new IniStore(iniPath);
        _settings = new Settings(_ini);

        _brightness = new BrightnessService();
        _brightness.Changed += OnBrightnessChanged;
        _brightness.HotkeyApplied += OnHotkeyApplied;

        _autoBrightness = new AutoBrightnessService(_brightness) { Enabled = _settings.ReadAutoBrightness() };
        _autoBrightness.Initialize();

        _tray = new NotifyIcon
        {
            Icon    = TrayIconRenderer.CreateBarIcon(_brightness.Current),
            Text    = "LGTV Backlight: starting...",
            Visible = true,
        };
        _currentIcon = _tray.Icon;
        _tray.ContextMenuStrip = BuildContextMenu();
        _tray.MouseClick += TrayMouseClick;
        _tray.MouseMove  += TrayMouseMove;

        _msgSink = new SystemMessageSink(() => _autoBrightness.OnDisplayChange());

        (uint upMods, uint upVk)     = _settings.ReadHotkeyUp();
        (uint downMods, uint downVk) = _settings.ReadHotkeyDown();
        _hotkeyUp   = new HotkeyService(() => OnHotkey(+1), upMods, upVk, Logger.Log);
        _hotkeyDown = new HotkeyService(() => OnHotkey(-1), downMods, downVk, Logger.Log);

        _ = _brightness.SyncAsync(force: true);

        Logger.Log("TrayApp started.");
    }

    // -------------------------------------------------------------------------
    // Hotkeys
    // -------------------------------------------------------------------------
    private void OnHotkey(int direction)
    {
        long now = Environment.TickCount64;
        if (now - _lastHotkeyTick < HotkeyMinIntervalMs) return;
        _lastHotkeyTick = now;

        _ = _brightness.AdjustAsync(direction);
    }

    // -------------------------------------------------------------------------
    // Brightness state -> UI
    // -------------------------------------------------------------------------
    private void OnBrightnessChanged(int value)
    {
        var old = _currentIcon;
        _currentIcon = TrayIconRenderer.CreateBarIcon(value);
        _tray.Icon = _currentIcon;
        old?.Dispose();

        _tray.Text = $"LGTV Backlight: {value}";
        if (_valueItem is not null)
            _valueItem.Text = "   Backlight: " + value;

        if (_sliderWindow is { IsVisible: true })
            _sliderWindow.SetValue(value);
    }

    /// <summary>Only raised for hotkey-driven changes (not slider drags or tray sync) —
    /// shows the custom brightness OSD, since Windows has no real one for this TV.</summary>
    private void OnHotkeyApplied(int value)
    {
        _osd ??= new BrightnessOsdWindow();
        _osd.ShowValue(value);
    }

    // -------------------------------------------------------------------------
    // Context menu
    // -------------------------------------------------------------------------
    private ContextMenuStrip BuildContextMenu()
    {
        const string space = "   ";
        const string check = "✓ ";

        var menu = new ContextMenuStrip
        {
            Renderer        = new DarkMenuRenderer(),
            ShowCheckMargin = false,
            ShowImageMargin = false,
        };

        menu.Items.Add(new ToolStripMenuItem(" LGTV Backlight") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        _valueItem = new ToolStripMenuItem(space + "Backlight: " + _brightness.Current, null, (_, _) => ShowSlider());
        menu.Items.Add(_valueItem);
        menu.Items.Add(new ToolStripSeparator());

        var autoBrightnessItem = new ToolStripMenuItem((_autoBrightness.Enabled ? check : space) + "Auto-Brightness (Refresh Rate)")
        {
            CheckOnClick = true,
            Checked      = _autoBrightness.Enabled,
        };
        autoBrightnessItem.CheckedChanged += (sender, _) =>
        {
            if (sender is not ToolStripMenuItem item) return;
            item.Text = (item.Checked ? check : space) + "Auto-Brightness (Refresh Rate)";
            _autoBrightness.Enabled = item.Checked;
            _settings.WriteAutoBrightness(item.Checked);
            if (item.Checked) _autoBrightness.Initialize();
        };
        menu.Items.Add(autoBrightnessItem);

        menu.Items.Add(space + "Sync from TV now", null, (_, _) => _ = _brightness.SyncAsync(force: true));
        menu.Items.Add(space + "Change Hotkeys…", null, (_, _) => ShowHotkeySettings());
        menu.Items.Add(new ToolStripSeparator());

        // Start at Login
        bool installed = StartupTaskService.IsInstalled(TaskName);
        var startupItem = new ToolStripMenuItem((installed ? check : space) + "Start at Login")
        {
            CheckOnClick = true,
            Checked      = installed,
        };
        bool reverting = false;
        startupItem.CheckedChanged += (sender, _) =>
        {
            if (reverting || sender is not ToolStripMenuItem item) return;
            item.Text = (item.Checked ? check : space) + "Start at Login";
            bool ok = item.Checked
                // This app runs asInvoker (no admin needed), so the startup task must not
                // request RunLevel=HighestAvailable — registering that requires the calling
                // process to already be elevated, which this one deliberately isn't.
                ? StartupTaskService.Install(TaskName, ExePath, TaskDescription, Logger.Log, requireElevation: false)
                : StartupTaskService.Uninstall(TaskName, Logger.Log);
            if (!ok)
            {
                reverting = true;
                item.Checked = !item.Checked;
                reverting = false;
                MessageBox.Show(
                    "Failed to update the startup task.\nCheck the log file for details.",
                    "LGTV Backlight",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        };
        menu.Items.Add(startupItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(space + "Exit", null, (_, _) => ExitApp());

        return menu;
    }

    // -------------------------------------------------------------------------
    // Tray interaction
    // -------------------------------------------------------------------------
    private void TrayMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            ShowSlider();
            _ = _brightness.SyncAsync(force: true);
        }
    }

    private void TrayMouseMove(object? sender, MouseEventArgs e)
    {
        long now = Environment.TickCount64;
        if (now - _lastHoverSyncTick < HoverMinIntervalMs) return;
        _lastHoverSyncTick = now;
        _ = _brightness.SyncAsync(force: true);
    }

    private void ShowSlider()
    {
        if (_sliderWindow is null)
            _sliderWindow = new BacklightSliderWindow(_brightness.Current, val => _brightness.QueueSliderValue(val));
        else
            _sliderWindow.SetValue(_brightness.Current);

        _sliderWindow.ShowAboveMouse();
    }

    // -------------------------------------------------------------------------
    // Hotkey settings
    // -------------------------------------------------------------------------
    private void ShowHotkeySettings()
    {
        var win = new HotkeysSettingsWindow(_hotkeyUp, _hotkeyDown)
        {
            SaveRequested = (upMods, upVk, downMods, downVk) =>
            {
                uint origUpMods = _hotkeyUp.Modifiers, origUpVk = _hotkeyUp.Vk;
                uint origDownMods = _hotkeyDown.Modifiers, origDownVk = _hotkeyDown.Vk;

                if (!_hotkeyUp.Rebind(upMods, upVk))
                {
                    _hotkeyUp.Rebind(origUpMods, origUpVk);
                    return false;
                }

                if (!_hotkeyDown.Rebind(downMods, downVk))
                {
                    _hotkeyDown.Rebind(origDownMods, origDownVk);
                    _hotkeyUp.Rebind(origUpMods, origUpVk);
                    return false;
                }

                _settings.WriteHotkeyUp(upMods, upVk);
                _settings.WriteHotkeyDown(downMods, downVk);
                return true;
            },
        };
        win.Show();
    }

    // -------------------------------------------------------------------------
    // Exit
    // -------------------------------------------------------------------------
    private void ExitApp()
    {
        Logger.Log("Exit requested.");
        System.Windows.Application.Current.Shutdown();
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------
    private bool _disposed;
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _msgSink.Dispose();
        _hotkeyUp.Dispose();
        _hotkeyDown.Dispose();
        _ini.Dispose();
        _osd?.Close();
        _tray.Visible = false;
        _currentIcon?.Dispose();
        _tray.Dispose();
    }
}
