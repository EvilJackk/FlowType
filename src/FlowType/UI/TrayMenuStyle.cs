using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FlowType.UI;

/// <summary>Window-level polish for the tray menu that the renderer can't do.</summary>
internal static class TrayMenuStyle
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmcpRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Ask DWM to round the drop-down's corners, matching Windows 11 menus.
    /// Done at the window level so the rounding is composited smoothly rather
    /// than clipped with a jagged region. Silently no-ops on Windows 10.
    /// </summary>
    public static void ApplyRoundedCorners(ToolStripDropDown menu)
    {
        try
        {
            if (!menu.IsHandleCreated) return;
            var preference = DwmcpRound;
            DwmSetWindowAttribute(menu.Handle, DwmwaWindowCornerPreference,
                ref preference, sizeof(int));
        }
        catch
        {
            // Pre-Windows-11 or dwmapi unavailable — square corners are fine.
        }
    }
}

/// <summary>
/// Dark, rounded styling for the system-tray menu. WinForms' stock
/// ToolStripProfessionalRenderer paints a light-gray Office-2003 menu with an
/// image gutter, which looks nothing like the rest of FlowType — this renderer
/// replaces the chrome (background, border, hover, separators, checkmarks)
/// with the app's palette.
/// </summary>
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    // Mirrors Theme.xaml's monochrome palette (MenuBrush, StrokeStrongBrush…).
    public static readonly Color Background = Color.FromArgb(20, 20, 20);
    public static readonly Color Border = Color.FromArgb(58, 58, 58);
    public static readonly Color Hover = Color.FromArgb(38, 38, 38);
    public static readonly Color Text = Color.FromArgb(244, 244, 244);
    public static readonly Color Muted = Color.FromArgb(155, 155, 155);
    public static readonly Color Accent = Color.FromArgb(255, 255, 255);

    public DarkMenuRenderer() : base(new DarkColorTable()) { }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        e.Graphics.Clear(Background);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var bounds = new Rectangle(Point.Empty, e.AffectedBounds.Size);
        bounds.Width -= 1;
        bounds.Height -= 1;
        using var pen = new Pen(Border);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRect(bounds, 8);
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;

        var bounds = new Rectangle(3, 0, e.Item.Width - 7, e.Item.Height - 1);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Hover);
        using var path = RoundedRect(bounds, 6);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        // Header rows are disabled by design; render them as quiet captions
        // rather than "greyed out and broken".
        e.TextColor = e.Item.Enabled ? Text : Muted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(Border);
        var y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, 12, y, e.Item.Width - 12, y);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        // A small accent dot reads better than the stock boxed checkmark.
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Accent);
        var size = 7;
        var rect = new Rectangle(
            e.ImageRectangle.Left + (e.ImageRectangle.Width - size) / 2,
            e.ImageRectangle.Top + (e.ImageRectangle.Height - size) / 2,
            size, size);
        e.Graphics.FillEllipse(brush, rect);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        // Suppress the lighter gutter strip entirely.
        using var brush = new SolidBrush(Background);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public DarkColorTable() => UseSystemColors = false;

        public override Color ToolStripDropDownBackground => Background;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
        public override Color CheckBackground => Background;
        public override Color CheckSelectedBackground => Hover;
    }
}
