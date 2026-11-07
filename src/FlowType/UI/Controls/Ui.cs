using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using FlowType.Session;
using Application = System.Windows.Application;
using Binding = System.Windows.Data.Binding;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using Path = System.Windows.Shapes.Path;
using TextBox = System.Windows.Controls.TextBox;
using TextWrapping = System.Windows.TextWrapping;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace FlowType.UI.Controls;

/// <summary>How loud a status chip is.</summary>
internal enum ChipTone
{
    /// <summary>Hairline outline, muted text: "Recommended", "Word".</summary>
    Neutral,

    /// <summary>Amber wash: "Active", "Learned".</summary>
    Accent,

    /// <summary>Red wash: "Paused", failures.</summary>
    Danger,
}

/// <summary>
/// Small factories for the pieces the pages build in code (rows, chips, icon
/// buttons). Every colour and style comes from Theme.xaml by key, so a page
/// never hard-codes a hex value and a palette change is one file.
///
/// Resources are resolved through <see cref="Application.Current"/> rather
/// than an element, because rows are built before they are in a tree.
/// </summary>
internal static class Ui
{
    public static Brush BrushOf(string key) => (Brush)Application.Current.FindResource(key);

    public static Style StyleOf(string key) => (Style)Application.Current.FindResource(key);

    public static Geometry GeometryOf(string key) => (Geometry)Application.Current.FindResource(key);

    /// <summary>A 20×20 line icon. With no stroke given it follows the nearest Button's Foreground.</summary>
    public static Path Icon(string geometryKey, Brush? stroke = null, double scale = 1.0)
    {
        var path = new Path
        {
            Style = StyleOf("IconPath"),
            Data = GeometryOf(geometryKey),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (Math.Abs(scale - 1.0) > 0.001)
        {
            path.LayoutTransform = new ScaleTransform(scale, scale);
        }
        if (stroke != null)
        {
            path.Stroke = stroke;
        }
        else
        {
            // Bind rather than copy so the icon brightens with the button's
            // hover state, which the ChipButton template drives through Foreground.
            path.SetBinding(Shape.StrokeProperty, new Binding("Foreground")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1),
            });
        }
        return path;
    }

    public static Button TextButton(string text, string styleKey, double leftMargin = 8) => new()
    {
        Content = text,
        Style = StyleOf(styleKey),
        Margin = new Thickness(leftMargin, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A quiet icon button (row actions); label optional, tooltip always.</summary>
    public static Button IconButton(string geometryKey, string? label, string tooltip,
        string styleKey = "ChipButton")
    {
        var button = new Button
        {
            Style = StyleOf(styleKey),
            ToolTip = tooltip,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
        };
        SetIconContent(button, geometryKey, label);
        if (label == null) button.Padding = new Thickness(6);
        System.Windows.Automation.AutomationProperties.SetName(button, label ?? tooltip);
        return button;
    }

    /// <summary>Swap an icon button's glyph/label in place (copy → check feedback).</summary>
    public static void SetIconContent(Button button, string geometryKey, string? label)
    {
        var icon = Icon(geometryKey, scale: 0.85);
        if (label == null)
        {
            button.Content = icon;
            return;
        }
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        icon.Margin = new Thickness(0, 0, 4, 0);
        panel.Children.Add(icon);
        panel.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        button.Content = panel;
    }

    public static Border Chip(string text, ChipTone tone = ChipTone.Neutral) => new()
    {
        Child = new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            FontWeight = FontWeights.Bold,
            Foreground = tone switch
            {
                ChipTone.Accent => BrushOf("AccentBrush"),
                ChipTone.Danger => BrushOf("DangerBrush"),
                _ => BrushOf("MutedBrush"),
            },
        },
        Background = tone switch
        {
            ChipTone.Accent => BrushOf("AccentSoftBrush"),
            ChipTone.Danger => BrushOf("DangerSoftBrush"),
            _ => Brushes.Transparent,
        },
        BorderBrush = tone == ChipTone.Neutral ? BrushOf("StrokeStrongBrush") : Brushes.Transparent,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(7, 1, 7, 2),
        Margin = new Thickness(8, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>One key drawn as a key cap.</summary>
    public static Border Keycap(string text) => new()
    {
        Child = new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = BrushOf("SoftTextBrush"),
        },
        Background = BrushOf("InputBrush"),
        BorderBrush = BrushOf("StrokeStrongBrush"),
        BorderThickness = new Thickness(1, 1, 1, 2),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(7, 2, 7, 3),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A chord as key caps joined by "+", optionally followed by a caption.</summary>
    public static StackPanel Keys(string caption, params string[] keys)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 0) };
        for (var i = 0; i < keys.Length; i++)
        {
            if (i > 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "+",
                    Foreground = BrushOf("FaintBrush"),
                    FontSize = 12,
                    Margin = new Thickness(4, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            panel.Children.Add(Keycap(keys[i]));
        }
        if (caption.Length > 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = caption,
                Style = StyleOf("Hint"),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        return panel;
    }

    public static TextBlock Text(string text, double size, string brushKey,
        FontWeight? weight = null, bool wrap = false) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = BrushOf(brushKey),
        FontWeight = weight ?? FontWeights.Normal,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static Ellipse Dot(string brushKey, double size = 8) => new()
    {
        Width = size,
        Height = size,
        Fill = BrushOf(brushKey),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A Grid with a stretching first column and auto-sized rest.</summary>
    public static Grid Columns(params GridLength[] widths)
    {
        var grid = new Grid();
        foreach (var w in widths) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        return grid;
    }

    public static readonly GridLength Star = new(1, GridUnitType.Star);
    public static readonly GridLength Auto = GridLength.Auto;

    public static T At<T>(this T element, int column) where T : UIElement
    {
        Grid.SetColumn(element, column);
        return element;
    }

    /// <summary>
    /// Populate a combo from (value, label) pairs and select a value. A saved
    /// value that is not in the list (hand-edited settings, a future default)
    /// is added rather than silently replaced by the first item — otherwise
    /// the next unrelated settings change would overwrite it.
    /// </summary>
    public static void FillCombo(ComboBox combo, (string Value, string Label)[] items, string selected,
        Func<string, string>? labelForUnknown = null)
    {
        combo.Items.Clear();
        var index = -1;
        for (var i = 0; i < items.Length; i++)
        {
            combo.Items.Add(new ComboBoxItem { Content = items[i].Label, Tag = items[i].Value });
            if (items[i].Value == selected) index = i;
        }
        if (index < 0 && labelForUnknown != null && !string.IsNullOrEmpty(selected))
        {
            combo.Items.Add(new ComboBoxItem { Content = labelForUnknown(selected), Tag = selected });
            index = combo.Items.Count - 1;
        }
        combo.SelectedIndex = Math.Max(0, index);
    }

    public static string ComboValue(ComboBox combo, string fallback) =>
        combo.SelectedItem is ComboBoxItem { Tag: string value } ? value : fallback;

    public static string Plural(long n, string one, string many) => n == 1 ? one : many;

    /// <summary>Keep a TextBox's length readout ("120 / 4,000") in step with its text.</summary>
    public static void BindCounter(TextBox box, TextBlock counter, int max)
    {
        void Update()
        {
            counter.Text = $"{box.Text.Length:N0} / {max:N0}";
            counter.Foreground = BrushOf(box.Text.Length >= max ? "DangerBrush" : "FaintBrush");
        }
        box.TextChanged += (_, _) => Update();
        Update();
    }

    /// <summary>Find the window hosting an element, for dialog owners.</summary>
    public static Window? OwnerOf(DependencyObject element) => Window.GetWindow(element);

    /// <summary>
    /// A message box owned by the element's window when it has one. The
    /// owner overload throws on null, and a page can be asked to show one
    /// while it is between windows.
    /// </summary>
    public static MessageBoxResult Message(DependencyObject source, string text,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.Information)
    {
        var owner = OwnerOf(source);
        return owner != null
            ? System.Windows.MessageBox.Show(owner, text, "FlowType", buttons, icon)
            : System.Windows.MessageBox.Show(text, "FlowType", buttons, icon);
    }
}

/// <summary>
/// The numbers the Home strip and Insights share, computed one way so the two
/// pages can never disagree.
/// </summary>
internal static class Metrics
{
    /// <summary>A take slower than this is mostly silence — a mic left open — not speech.</summary>
    internal const double MinTakeWpm = 30;

    /// <summary>
    /// Speaking pace over recent dictations: total words over total speaking
    /// minutes for the latest 100 takes that had words and a believable pace
    /// of their own (a forgotten hands-free mic would otherwise drag the
    /// figure to zero). Until 2.2.0 that filter was "under 400 s", but takes
    /// have no length limit now, and a long dictation is exactly the one
    /// worth counting — so a take is skipped for being mostly silence (under
    /// <see cref="MinTakeWpm"/>), not for being long.
    /// A result outside 20–250 wpm means the sample is too odd to show, so
    /// the lifetime figure is used instead.
    /// </summary>
    public static int RecentWpm(IReadOnlyList<Note> newestFirst, int lifetimeFallback)
    {
        double words = 0, seconds = 0;
        var taken = 0;
        foreach (var note in newestFirst)
        {
            if (note.WordCount <= 0 || note.DurationSeconds <= 0) continue;
            if (note.WordCount / (note.DurationSeconds / 60.0) < MinTakeWpm) continue;
            words += note.WordCount;
            seconds += note.DurationSeconds;
            if (++taken >= 100) break;
        }
        if (seconds < 1) return lifetimeFallback;
        var wpm = (int)Math.Round(words / (seconds / 60.0));
        return wpm is > 20 and < 250 ? wpm : lifetimeFallback;
    }

    /// <summary>Words dictated over the last <paramref name="days"/> days, today included.</summary>
    public static long WordsInLastDays(int days)
    {
        var perDay = StatsStore.Instance.WordsPerDay;
        long total = 0;
        for (var i = 0; i < days; i++)
        {
            total += perDay.GetValueOrDefault(DateTime.Today.AddDays(-i).ToString("yyyy-MM-dd"), 0);
        }
        return total;
    }

    /// <summary>Heatmap level 0–4 for a day's word count: 0 / &lt;250 / &lt;500 / &lt;750 / ≥750.</summary>
    public static int HeatLevel(long words) => words switch
    {
        <= 0 => 0,
        < 250 => 1,
        < 500 => 2,
        < 750 => 3,
        _ => 4,
    };
}
