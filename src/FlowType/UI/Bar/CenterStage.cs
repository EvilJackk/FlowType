using System.Windows;
using System.Windows.Media;
using Size = System.Windows.Size;

namespace FlowType.UI.Bar;

/// <summary>
/// Holds the pill's content "faces" while the pill itself morphs.
///
/// A Grid would re-measure a face against the pill's current, animating
/// width — so a toast's text would re-wrap into five lines and back again
/// during a 190 ms grow. This panel measures every child with unlimited space
/// (wrapping is controlled explicitly with MaxWidth) and always centres it,
/// even when the child is wider than the pill; <see cref="UIElement.ClipToBounds"/>
/// then trims the overflow evenly on both sides, which reads as the content
/// being revealed from the middle as the pill grows. Offsets are snapped to
/// device pixels so text never lands on a half pixel and blurs.
///
/// It reports a desired size of zero: the pill's explicit Width/Height (set by
/// the morph) is the only thing that decides how big the pill is.
/// </summary>
internal sealed class CenterStage : System.Windows.Controls.Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var unlimited = new Size(double.PositiveInfinity, double.PositiveInfinity);
        foreach (UIElement child in InternalChildren) child.Measure(unlimited);
        return new Size(0, 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double scale;
        try { scale = VisualTreeHelper.GetDpi(this).DpiScaleX; }
        catch { scale = 1; }
        if (scale <= 0) scale = 1;

        foreach (UIElement child in InternalChildren)
        {
            var d = child.DesiredSize;
            var x = Math.Round((finalSize.Width - d.Width) / 2 * scale) / scale;
            var y = Math.Round((finalSize.Height - d.Height) / 2 * scale) / scale;
            child.Arrange(new Rect(x, y, d.Width, d.Height));
        }
        return finalSize;
    }
}
