using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace FlowType.UI.Controls;

/// <summary>
/// One line of a settings card: a title and an optional explanation on the
/// left, the control (usually a <c>Switch</c>-styled CheckBox) on the right,
/// and an optional <see cref="Extra"/> block under the explanation for things
/// like key hints or sub-options. The look lives in Theme.xaml.
///
/// Why a control rather than repeated Grid markup: the settings page has ~35
/// of these, and a single template means spacing, separators and type stay
/// identical everywhere — the thing that makes a long settings page feel
/// calm instead of hand-assembled.
/// </summary>
public sealed class SettingRow : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SettingRow), new PropertyMetadata(""));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingRow), new PropertyMetadata(null));

    public static readonly DependencyProperty ExtraProperty = DependencyProperty.Register(
        nameof(Extra), typeof(object), typeof(SettingRow), new PropertyMetadata(null));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>Optional content shown under the description (key caps, radios, a meter).</summary>
    public object? Extra
    {
        get => GetValue(ExtraProperty);
        set => SetValue(ExtraProperty, value);
    }

    /// <summary>
    /// Clicking the words flips the switch, the way a label does for a
    /// checkbox. Only when the row's control is a toggle, only when the click
    /// did not land on the toggle itself (which already handled it) and never
    /// when it landed inside the Extra block, which may hold its own controls.
    /// </summary>
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (e.Handled) return;
        if (Content is not ToggleButton toggle || !toggle.IsEnabled || toggle.IsMouseOver) return;
        if (e.OriginalSource is DependencyObject source && Extra is DependencyObject extra
            && IsInside(source, extra))
        {
            return;
        }
        toggle.IsChecked = toggle.IsChecked != true;
        e.Handled = true;
    }

    private static bool IsInside(DependencyObject node, DependencyObject ancestor)
    {
        // VisualTreeHelper throws on content elements (a Run), so walk the
        // logical tree for those and the visual tree for everything else.
        var current = node;
        while (current != null)
        {
            if (ReferenceEquals(current, ancestor)) return true;
            current = current is System.Windows.Media.Visual
                ? System.Windows.Media.VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }
}
