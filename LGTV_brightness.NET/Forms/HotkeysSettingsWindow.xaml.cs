using System;
using System.Windows;
using System.Windows.Input;
using Shared;

namespace LgtvBrightness.Forms;

/// <summary>
/// Lets the user record both backlight hotkeys (increase/decrease) in one
/// dialog — a direct port of the AHK script's ShowHotkeySettings(), which
/// used two native Hotkey controls in a single window. WPF has no equivalent
/// control, so recording reuses Shared.HotkeyCapture's low-level hook the
/// same way Shared.HotkeySettingsWindow does, just with two independent slots.
/// </summary>
internal partial class HotkeysSettingsWindow : Window
{
    private readonly HotkeyCapture _capture = new();

    private uint _upMods, _upVk;
    private uint _downMods, _downVk;
    private bool _recordingUp;
    private bool _recordingDown;

    /// <summary>Return true if both combinations were accepted (registered successfully).</summary>
    public Func<uint, uint, uint, uint, bool>? SaveRequested { get; set; }

    public HotkeysSettingsWindow(HotkeyService hotkeyUp, HotkeyService hotkeyDown)
    {
        InitializeComponent();

        _upMods = hotkeyUp.Modifiers;
        _upVk   = hotkeyUp.Vk;
        _downMods = hotkeyDown.Modifiers;
        _downVk   = hotkeyDown.Vk;

        UpHotkeyText.Text   = HotkeyService.Describe(_upMods, _upVk);
        DownHotkeyText.Text = HotkeyService.Describe(_downMods, _downVk);

        _capture.Captured += OnCaptured;

        RecordUpButton.Click   += (_, _) => StartRecording(isUp: true);
        RecordDownButton.Click += (_, _) => StartRecording(isUp: false);
        SaveButton.Click       += (_, _) => Save();
        CloseButton.Click      += (_, _) => Close();

        PreviewKeyDown += OnPreviewKeyDown;
        Closed += (_, _) => _capture.Dispose();
    }

    // Ambiguous with System.Windows.Forms.KeyEventArgs whenever this is linked into
    // a UseWindowsForms=true host (this app is), so stay qualified.
    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if ((_recordingUp || _recordingDown) && e.Key == Key.Escape)
        {
            CancelRecording();
            e.Handled = true;
        }
    }

    private void StartRecording(bool isUp)
    {
        _recordingUp   = isUp;
        _recordingDown = !isUp;
        StatusText.Visibility = Visibility.Collapsed;

        RecordUpButton.IsEnabled   = false;
        RecordDownButton.IsEnabled = false;
        (isUp ? RecordUpButton : RecordDownButton).Content = "Press a key... (Esc to cancel)";

        _capture.Start();
    }

    private void CancelRecording()
    {
        _capture.Stop();
        ResetRecordButtons();
    }

    private void OnCaptured(uint mods, uint vk)
    {
        bool wasUp = _recordingUp;
        _capture.Stop();
        Dispatcher.Invoke(() =>
        {
            if (wasUp)
            {
                _upMods = mods; _upVk = vk;
                UpHotkeyText.Text = HotkeyService.Describe(mods, vk);
            }
            else
            {
                _downMods = mods; _downVk = vk;
                DownHotkeyText.Text = HotkeyService.Describe(mods, vk);
            }

            StatusText.Visibility = Visibility.Collapsed;
            ResetRecordButtons();
        });
    }

    private void ResetRecordButtons()
    {
        RecordUpButton.Content   = "Record";
        RecordDownButton.Content = "Record";
        RecordUpButton.IsEnabled   = true;
        RecordDownButton.IsEnabled = true;
        _recordingUp = _recordingDown = false;
    }

    private void Save()
    {
        if (_upMods == _downMods && _upVk == _downVk)
        {
            StatusText.Text = "Increase and decrease hotkeys must be different.";
            StatusText.Visibility = Visibility.Visible;
            return;
        }

        bool ok = SaveRequested?.Invoke(_upMods, _upVk, _downMods, _downVk) ?? false;
        if (ok)
        {
            Close();
        }
        else
        {
            StatusText.Text = "One of those combinations is already used by another app.";
            StatusText.Visibility = Visibility.Visible;
        }
    }
}
