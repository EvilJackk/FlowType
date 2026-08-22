using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;

namespace FlowType.UI;

/// <summary>
/// Rasterises a laid-out element to PNG without involving the screen. Used by
/// the hidden <c>--snapshot</c> diagnostic so the UI can be checked on a
/// machine where a game or remote session owns the display. A VisualBrush is
/// used (rather than rendering the element directly) so the element's offset
/// inside its parent doesn't shift the image.
/// </summary>
internal static class Snapshot
{
    public static void Save(FrameworkElement element, string path, Brush? background = null)
    {
        var width = (int)Math.Ceiling(element.ActualWidth);
        var height = (int)Math.Ceiling(element.ActualHeight);
        if (width <= 0 || height <= 0) return;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (background != null)
            {
                dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
            }
            var brush = new VisualBrush(element)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            };
            dc.DrawRectangle(brush, null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
