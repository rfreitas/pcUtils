using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace RefreshRateOverlay.WPF.Tests;

public class SyncedFieldTests
{
    private const string Tip = "not synced";

    [Fact]
    public void Refresh_LiveMatchesStored_DotGreen_NoTooltip() => StaTestHelper.Run(() =>
    {
        var dot = new Ellipse();
        var control = new ComboBox();
        var field = new SyncedField<int>(dot, control, initialStored: 60, getLive: () => 60, Tip);

        field.Refresh();

        Assert.Equal(Brushes.LimeGreen.Color, ((SolidColorBrush)dot.Fill).Color);
        Assert.Null(control.ToolTip);
    });

    [Fact]
    public void Refresh_LiveDiffersFromStored_DotRed_WithTooltip() => StaTestHelper.Run(() =>
    {
        var dot = new Ellipse();
        var control = new ComboBox();
        var field = new SyncedField<int>(dot, control, initialStored: 60, getLive: () => 100, Tip);

        field.Refresh();

        Assert.Equal(Brushes.Red.Color, ((SolidColorBrush)dot.Fill).Color);
        Assert.Equal(Tip, control.ToolTip);
    });

    [Fact]
    public void Refresh_NotActive_DoesNothing() => StaTestHelper.Run(() =>
    {
        var dot = new Ellipse { Fill = Brushes.Blue };
        var control = new ComboBox();
        var field = new SyncedField<int>(dot, control, initialStored: 60, getLive: () => 100, Tip, isActive: () => false);

        field.Refresh();

        // Would be Red if active (100 != 60) — untouched instead.
        Assert.Equal(Brushes.Blue.Color, ((SolidColorBrush)dot.Fill).Color);
        Assert.Null(control.ToolTip);
    });

    [Fact]
    public void MarkTouched_SetsTouchedTrue() => StaTestHelper.Run(() =>
    {
        var field = new SyncedField<bool>(new Ellipse(), new CheckBox(), initialStored: false, () => false, Tip);

        Assert.False(field.Touched);
        field.MarkTouched();
        Assert.True(field.Touched);
    });

    [Fact]
    public void UpdateStored_ChangesBaselineAndRefreshesDot() => StaTestHelper.Run(() =>
    {
        var dot = new Ellipse();
        var control = new ComboBox();
        int live = 100;
        var field = new SyncedField<int>(dot, control, initialStored: 60, getLive: () => live, Tip);
        field.Refresh();
        Assert.Equal(Brushes.Red.Color, ((SolidColorBrush)dot.Fill).Color); // 100 != 60

        field.UpdateStored(100);

        Assert.Equal(100, field.Stored);
        Assert.Equal(Brushes.LimeGreen.Color, ((SolidColorBrush)dot.Fill).Color); // live now matches Stored
        Assert.Null(control.ToolTip);
    });

    [Fact]
    public void UpdateStored_LiveNoLongerMatchesAfterBaselineMoves_DotTurnsRed() => StaTestHelper.Run(() =>
    {
        var dot = new Ellipse();
        var control = new ComboBox();
        var field = new SyncedField<int>(dot, control, initialStored: 60, getLive: () => 60, Tip);
        field.Refresh();
        Assert.Equal(Brushes.LimeGreen.Color, ((SolidColorBrush)dot.Fill).Color);

        // Baseline moves to a value the still-unchanged live control no longer matches
        // (the "external drift changed while a control was untouched" case).
        field.UpdateStored(120);

        Assert.Equal(Brushes.Red.Color, ((SolidColorBrush)dot.Fill).Color);
        Assert.Equal(Tip, control.ToolTip);
    });

    [Fact]
    public void GenericOverBool_ComparesCorrectly() => StaTestHelper.Run(() =>
    {
        var dot = new Ellipse();
        var control = new CheckBox();
        var field = new SyncedField<bool>(dot, control, initialStored: true, getLive: () => false, Tip);

        field.Refresh();

        Assert.Equal(Brushes.Red.Color, ((SolidColorBrush)dot.Fill).Color);
    });
}
