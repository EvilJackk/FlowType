using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Size = System.Windows.Size;

namespace FlowType.UI.Bar;

/// <summary>
/// The flow bar's live waveform: ten slim, rounded bars that swell with the
/// voice. Drawn directly in <see cref="OnRender"/> rather than as ten
/// Rectangles because it repaints ~30 times a second while you talk, and a
/// single draw call per bar costs no layout pass at all.
///
/// Shape of a frame (our own tuning, inspired by the reference app's
/// "centre-weighted" bar):
/// <list type="bullet">
/// <item><b>Bulge</b> — bar i gets 1 − d²/48, d = distance from the centre in
///   bar units, so the middle bars reach full height and the outermost ones
///   about 58 % of it. The pill reads as a voice, not an equaliser.</item>
/// <item><b>Wiggle</b> — two slow sines with a per-bar phase (1.15 s and
///   0.41 s periods, ±16 % and ±6 %), so a steady tone still moves.</item>
/// <item><b>Ripple</b> — up to 0.9 DIP of travelling ripple at rest, fading
///   out as the level rises, so a live but silent mic does not look frozen.</item>
/// <item><b>Brightness</b> — the owner keeps the bars at ~40 % until the first
///   buffer with real signal arrives (see <see cref="MicLevelMeter.MicActive"/>).</item>
/// </list>
/// Heights rest at 4 DIP inside an 18 DIP box so the idle bars sit on whole
/// pixels at 100 % scaling (an odd height would centre on a half pixel and blur).
/// </summary>
internal sealed class BarWaveform : FrameworkElement
{
    public const int BarCount = 10;
    private const double BarWidth = 3;
    private const double BarGap = 2;
    private const double BoxHeight = 18;
    private const double RestHeight = 4;
    private const double PeakHeight = 18;
    private const double Gain = 1.15;

    private readonly double[] _heights = new double[BarCount];
    private Brush _brush = Brushes.White;
    private double _brightness = 1;

    public BarWaveform()
    {
        for (var i = 0; i < BarCount; i++) _heights[i] = RestHeight;
        IsHitTestVisible = false;
    }

    /// <summary>Total width the bars occupy, in DIPs.</summary>
    public static double NaturalWidth => BarCount * BarWidth + (BarCount - 1) * BarGap;

    public Brush BarBrush
    {
        get => _brush;
        set
        {
            _brush = value;
            InvalidateVisual();
        }
    }

    /// <summary>Centre weighting for bar <paramref name="i"/>: 1 − d²/48.</summary>
    public static double Bulge(int i)
    {
        var d = i - (BarCount - 1) / 2.0;
        return 1 - d * d / 48;
    }

    /// <summary>
    /// Compute one frame. <paramref name="level"/> is the smoothed 0..1 voice
    /// level, <paramref name="seconds"/> a monotonic clock for the wiggle, and
    /// <paramref name="brightness"/> the bar opacity (0.4 until the mic is live).
    /// </summary>
    public void Update(double level, double seconds, double brightness)
    {
        level = Math.Clamp(level, 0, 1);
        for (var i = 0; i < BarCount; i++)
        {
            var phase = i * 0.55;
            var wiggle = 1
                + 0.16 * Math.Sin(2 * Math.PI * seconds / 1.15 + phase)
                + 0.06 * Math.Sin(2 * Math.PI * seconds / 0.41 + 2.3 * phase);
            var amplitude = Math.Clamp(level * Gain * Bulge(i) * wiggle, 0, 1);
            var ripple = 0.9 * (0.5 + 0.5 * Math.Sin(2 * Math.PI * seconds / 1.6 - i * 0.6));
            _heights[i] = RestHeight + ripple * (1 - amplitude) + (PeakHeight - RestHeight) * amplitude;
        }
        _brightness = Math.Clamp(brightness, 0, 1);
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(NaturalWidth, BoxHeight);

    protected override void OnRender(DrawingContext dc)
    {
        dc.PushOpacity(_brightness);
        for (var i = 0; i < BarCount; i++)
        {
            var h = Math.Clamp(_heights[i], RestHeight, PeakHeight);
            var x = i * (BarWidth + BarGap);
            var y = (BoxHeight - h) / 2;
            dc.DrawRoundedRectangle(_brush, null, new Rect(x, y, BarWidth, h), BarWidth / 2, BarWidth / 2);
        }
        dc.Pop();
    }
}
