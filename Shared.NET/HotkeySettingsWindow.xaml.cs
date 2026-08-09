using System;
using System.Windows;

namespace Shared;

/// <summary>
/// Lets the user record a new global shortcut. Recording uses a low-level
/// keyboard hook (HotkeyCapture) rather than normal WPF key events, since
/// the Windows key doesn't route reliably through routed input.
/// Reused across apps (RefreshRateOverlay.WPF, AggressiveScreensaver.NET)
/// via file-linking from Shared.NET.
/// </summary>
public partial class HotkeySettingsWindow : Window
{
    private readonly HotkeyCapture _capture = new();
    private readonly uint _originalMods;
    private readonly uint _originalVk;
    private uint? _pendingMods;
    private uint? _pendingVk;
    private bool _recording;

    /// <summary>Return true if the combination was accepted (registered successfully).</summary>
    public Func<uint, uint, bool>? SaveRequested { get; set; }

    public HotkeySettingsWindow(string title, uint modifiers, uint vk)
    {
        InitializeComponent();

        TitleText.Text = title;
        _originalMods  = modifiers;
        _originalVk    = vk;
        CurrentHotkeyText.Text = HotkeyService.Describe(modifiers, vk);

        _capture.Captured += OnCaptured;

        RecordButton.Click += (_, _) => StartRecording();
        SaveButton.Click   += (_, _) => Save();
        CloseButton.Click  += (_, _) => Close();

        PreviewKeyDown += OnPreviewKeyDown;
        Closed += (_, _) => _capture.Dispose();
    }

    // Ambiguous with System.Windows.Forms.KeyEventArgs whenever this is linked into
    // a UseWindowsForms=true host (both current consumers are), so stay qualified.
    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_recording && e.Key == System.Windows.Input.Key.Escape)
        {
            CancelRecording();
            e.Handled = true;
        }
    }

    private void StartRecording()
    {
        _recording = true;
        StatusText.Visibility = Visibility.Collapsed;
        RecordButton.Content = "Press a key or combination... (Esc to cancel)";
        RecordButton.IsEnabled = false;
        _capture.Start();
    }

    private void CancelRecording()
    {
        _recording = false;
        _capture.Stop();
        RecordButton.Content = "Record New Shortcut";
        RecordButton.IsEnabled = true;
    }

    private void OnCaptured(uint mods, uint vk)
    {
        _recording = false;
        _capture.Stop();
        Dispatcher.Invoke(() =>
        {
            _pendingMods = mods;
            _pendingVk   = vk;
            RecordButton.Content = "Record New Shortcut";
            RecordButton.IsEnabled = true;
            CurrentHotkeyText.Text = HotkeyService.Describe(mods, vk);
            StatusText.Visibility = Visibility.Collapsed;
        });
    }

    private void Save()
    {
        if (_pendingMods is null || _pendingVk is null)
        {
            Close(); // nothing changed
            return;
        }

        bool ok = SaveRequested?.Invoke(_pendingMods.Value, _pendingVk.Value) ?? false;
        if (ok)
        {
            Close();
        }
        else
        {
            StatusText.Text = "That combination is already used by another app.";
            StatusText.Visibility = Visibility.Visible;
            CurrentHotkeyText.Text = HotkeyService.Describe(_originalMods, _originalVk);
            _pendingMods = null;
            _pendingVk = null;
        }
    }
}
