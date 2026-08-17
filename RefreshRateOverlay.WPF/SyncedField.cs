using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Shapes;

namespace RefreshRateOverlay.WPF;

/// <summary>
/// Tracks one control's live value against a stored (saved) baseline and keeps
/// a sync-status dot in step with both — the user's own edits and a
/// background push (OverlayWindow.Refresh*LiveState) alike. One shared
/// implementation instead of hand-duplicating the touched/stored/dot pattern
/// per setting; Rate, HDR, and GsyncMode in OverlayWindow each own one
/// instance. Top-level (not nested in OverlayWindow) and internal — rather
/// than private — specifically so it's unit-testable on its own without
/// constructing a whole OverlayWindow.
/// </summary>
internal sealed class SyncedField<T>
{
    private readonly Ellipse _dot;
    private readonly FrameworkElement _control;
    private readonly Func<T> _getLive;
    private readonly Func<bool> _isActive;
    private readonly string _notSyncedTooltip;

    /// <summary>True once the user has actually changed this control since
    /// the overlay opened — callers (Refresh*LiveState) use this to avoid
    /// overwriting a selection already in progress.</summary>
    public bool Touched { get; private set; }

    /// <summary>The actual currently-saved value (profile-or-default) this
    /// field's live value is compared against.</summary>
    public T Stored { get; private set; }

    /// <param name="isActive">False means nothing meaningful to compare right
    /// now (e.g. HDR's row hidden because it isn't supported) — Refresh
    /// becomes a no-op rather than coloring a dot nobody sees.</param>
    public SyncedField(Ellipse dot, FrameworkElement control, T initialStored,
        Func<T> getLive, string notSyncedTooltip, Func<bool>? isActive = null)
    {
        _dot = dot;
        _control = control;
        _getLive = getLive;
        _notSyncedTooltip = notSyncedTooltip;
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
        _control.ToolTip = synced ? null : _notSyncedTooltip;
    }

    /// <summary>A background reconciler observed a new stored value — updates
    /// the baseline and refreshes the dot. Does not touch the displayed value
    /// itself: "what to set the control to" differs per control type
    /// (SelectedIndex vs. IsChecked), so callers still do that bit themselves,
    /// gated on Touched, before calling this.</summary>
    public void UpdateStored(T newStored)
    {
        Stored = newStored;
        Refresh();
    }
}
