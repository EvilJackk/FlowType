using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Size = System.Windows.Size;

namespace FlowType.UI.Bar;

/// <summary>
/// The "working on it" mark: eight short spokes around a 16 DIP circle with a
/// bright head sweeping clockwise and a fading tail. Drawn in
/// <see cref="OnRender"/> and advanced by the bar's own frame clock, so it
/// costs nothing while hidden and renders deterministically for
/// <c>--snapshot</c> (pass any time and you get that exact frame).
/// </summary>
internal sealed class BarSpinner : FrameworkElement
{
    private const int Spokes = 8;
    private const double Box = 16;
    private const double SpokeWidth = 2;
    private const double SpokeLength = 5;
    private const double InnerRadius = 3;
    private const double PeriodSeconds = 0.95;

    private static readonly RotateTransform[] Rotations = BuildRotations();

    private Brush _brush = Brushes.White;
    private double _seconds;

    public BarSpinner() => IsHitTestVisible = false;

    public Brush SpinnerBrush
    {
        get => _brush;
        set
        {
            _brush = value;
            InvalidateVisual();
        }
    }

    /// <summary>Show the frame for this point on the clock.</summary>
    public void Update(double seconds)
    {
        _seconds = seconds;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(Box, Box);

    protected override void OnRender(DrawingContext dc)
    {
        var head = (_seconds / PeriodSeconds % 1 + 1) % 1 * Spokes;
        var c = Box / 2;
        var spoke = new Rect(c - SpokeWidth / 2, c - InnerRadius - SpokeLength, SpokeWidth, SpokeLength);
        for (var i = 0; i < Spokes; i++)
        {
            // 0 = the head itself; just under 8 = the spoke the head reaches next.
            var behind = ((head - i) % Spokes + Spokes) % Spokes;
            var opacity = 0.2 + 0.8 * Math.Pow(1 - behind / Spokes, 2.2);
            dc.PushTransform(Rotations[i]);
            dc.PushOpacity(opacity);
            dc.DrawRoundedRectangle(_brush, null, spoke, SpokeWidth / 2, SpokeWidth / 2);
            dc.Pop();
            dc.Pop();
        }
    }

    private static RotateTransform[] BuildRotations()
    {
        var all = new RotateTransform[Spokes];
        for (var i = 0; i < Spokes; i++)
        {
            all[i] = new RotateTransform(360.0 / Spokes * i, Box / 2, Box / 2);
            all[i].Freeze();
        }
        return all;
    }
}
