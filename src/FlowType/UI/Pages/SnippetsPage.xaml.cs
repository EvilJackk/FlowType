using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FlowType.Session;
using FlowType.UI.Controls;
using CheckBox = System.Windows.Controls.CheckBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using TextBox = System.Windows.Controls.TextBox;
using TextWrapping = System.Windows.TextWrapping;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace FlowType.UI.Pages;

/// <summary>
/// Voice shortcuts: a trigger phrase and the text it expands to. Stored as
/// dictionary entries with <see cref="DictionaryEntry.IsSnippet"/> set, which
/// is what lets DictionaryStore treat a snippet spoken on its own as the whole
/// dictation (no stray full stop, no capitalisation fix-ups around it).
/// </summary>
public partial class SnippetsPage : IMainPage
{
    /// <summary>Upper bound on one expansion: enough for a signature or a template, not a document.</summary>
    public const int MaxExpansion = 4000;

    private Guid? _editing;
    private bool _shown;
    private bool _refreshQueued;

    /// <summary>
    /// The open inline editor, kept across list rebuilds. Any store change —
    /// switching another snippet off, a correction learned mid-dictation —
    /// rebuilds the list; building the editor afresh from the stored entry
    /// would throw away what the user had typed and yank focus back to the
    /// trigger box. Re-adding this same element keeps the draft, caret, undo
    /// and focus exactly as they were.
    /// </summary>
    private FrameworkElement? _editor;
    private Guid? _editorFor;

    public SnippetsPage()
    {
        InitializeComponent();
        Ui.BindCounter(ExpansionBox, ExpansionCounter, MaxExpansion);
        TriggerBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                ExpansionBox.Focus();
                e.Handled = true;
            }
        };
        DictionaryStore.Instance.Changed += OnDataChanged;
    }

    public FrameworkElement SnapshotBody => Body;

    public void OnShown()
    {
        _shown = true;
        RefreshSnippets();
    }

    public void OnHidden()
    {
        _shown = false;
        _editing = null;
        _editor = null;
        _editorFor = null;
    }

    public void Detach() => DictionaryStore.Instance.Changed -= OnDataChanged;

    private void OnDataChanged()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _refreshQueued = false;
            if (_shown) RefreshSnippets();
        }, DispatcherPriority.Background);
    }

    private void SnippetSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_shown) RefreshSnippets();
    }

    /// <summary>The same normalisation DictionaryStore uses to match a spoken trigger.</summary>
    private static string TriggerKey(string s) =>
        new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private void RefreshSnippets()
    {
        var all = DictionaryStore.Instance.Entries.Where(e => e.IsSnippet).ToList();

        // Deleted elsewhere while being edited: there is nothing left to save to.
        if (_editing is { } editing && !all.Any(e => e.Id == editing)) _editing = null;
        if (_editorFor != _editing)
        {
            _editor = null;
            _editorFor = null;
        }

        var query = SnippetSearchBox.Text.Trim();
        var list = query.Length == 0
            ? all
            : all.Where(e => e.Phrase.Contains(query, StringComparison.OrdinalIgnoreCase)
                || e.Replacement.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        SnippetCountText.Text = all.Count == 0 ? "" : $"{all.Count:N0}";

        // WPF re-checks a detached focused element asynchronously, so re-adding
        // the editor in this same pass normally keeps its focus. If it was
        // dropped anyway, hand it straight back to the box the user was in.
        var refocus = _editor?.IsKeyboardFocusWithin == true ? Keyboard.FocusedElement as UIElement : null;
        SnippetList.Children.Clear();
        foreach (var entry in list)
        {
            SnippetList.Children.Add(entry.Id == _editing ? EditorFor(entry) : BuildRow(entry));
        }
        if (refocus is { IsKeyboardFocused: false, IsVisible: true }) refocus.Focus();

        SnippetsEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (all.Count == 0)
        {
            SnippetsEmptyTitle.Text = "No snippets yet";
            SnippetsEmptyText.Text = "Good first ones: your email address, a scheduling link, your home address, a signature.";
        }
        else
        {
            SnippetsEmptyTitle.Text = "No matches";
            SnippetsEmptyText.Text = $"No snippet mentions “{query}”.";
        }
    }

    private FrameworkElement BuildRow(DictionaryEntry entry)
    {
        var id = entry.Id;
        var grid = Ui.Columns(Ui.Auto, Ui.Star, Ui.Auto);

        var enabled = new CheckBox
        {
            Style = Ui.StyleOf("Switch"),
            IsChecked = entry.Enabled,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 18, 0),
            ToolTip = entry.Enabled ? "On — click to switch this snippet off" : "Off — click to switch it on",
        };
        enabled.Checked += (_, _) => DictionaryStore.Instance.SetEnabled(id, true);
        enabled.Unchecked += (_, _) => DictionaryStore.Instance.SetEnabled(id, false);
        System.Windows.Automation.AutomationProperties.SetName(enabled, $"Use snippet “{entry.Phrase}”");
        grid.Children.Add(enabled.At(0));

        var content = new StackPanel { Opacity = entry.Enabled ? 1.0 : 0.45 };
        content.Children.Add(new TextBlock
        {
            Text = $"“{entry.Phrase}”",
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = Ui.BrushOf("TextBrush"),
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new Border
        {
            Background = Ui.BrushOf("InputBrush"),
            BorderBrush = Ui.BrushOf("StrokeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 10, 0, 0),
            Child = new TextBlock
            {
                Text = entry.Replacement,
                FontSize = 14,
                LineHeight = 21,
                Foreground = Ui.BrushOf("SoftTextBrush"),
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 21 * 4,
            },
        });
        var lines = entry.Replacement.Split('\n').Length;
        content.Children.Add(new TextBlock
        {
            Text = $"{entry.Replacement.Length:N0} {Ui.Plural(entry.Replacement.Length, "character", "characters")}"
                + (lines > 1 ? $"  ·  {lines} lines" : ""),
            FontSize = 12.5,
            Foreground = Ui.BrushOf("FaintBrush"),
            Margin = new Thickness(0, 8, 0, 0),
        });
        grid.Children.Add(content.At(1));

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12, -4, 0, 0),
        };
        var edit = Ui.IconButton("IconEdit", null, "Edit");
        edit.Click += (_, _) =>
        {
            _editing = id;
            RefreshSnippets();
        };
        actions.Children.Add(edit);
        var delete = Ui.IconButton("IconTrash", null, "Delete", "DangerChipButton");
        delete.Click += (_, _) =>
        {
            // A learned snippet is remembered as "don't suggest again" first,
            // or the next run would offer it straight back.
            if (entry.LearnedFromHistory) FlowType.Learning.HistoryLearner.Instance.Forget(entry);
            DictionaryStore.Instance.Delete(id);
        };
        actions.Children.Add(delete);
        grid.Children.Add(actions.At(2));

        return new Border
        {
            Style = Ui.StyleOf("Card"),
            Padding = new Thickness(20, 18, 14, 16),
            Margin = new Thickness(0, 0, 0, 12),
            Child = grid,
        };
    }

    /// <summary>The live editor for <paramref name="entry"/>: reused if already open, built (from the stored entry) if not.</summary>
    private FrameworkElement EditorFor(DictionaryEntry entry)
    {
        if (_editor == null || _editorFor != entry.Id)
        {
            _editor = BuildEditor(entry);
            _editorFor = entry.Id;
        }
        else if (_editor.Parent is System.Windows.Controls.Panel oldParent)
        {
            // Children.Clear() already detached it; this is belt and braces.
            oldParent.Children.Remove(_editor);
        }
        return _editor;
    }

    private FrameworkElement BuildEditor(DictionaryEntry entry)
    {
        var id = entry.Id;
        var panel = new StackPanel();
        var trigger = new TextBox { Text = entry.Phrase, Tag = "Trigger phrase" };
        var expansion = new TextBox
        {
            Text = entry.Replacement,
            Tag = "Text to insert",
            Height = 130,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxLength = MaxExpansion,
            Margin = new Thickness(0, 10, 0, 0),
        };
        var counter = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        Ui.BindCounter(expansion, counter, MaxExpansion);
        var error = new TextBlock
        {
            Style = Ui.StyleOf("Hint"),
            Foreground = Ui.BrushOf("DangerBrush"),
            Margin = new Thickness(14, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };

        void Save()
        {
            var t = trigger.Text.Trim();
            var problem = Validate(t, expansion.Text, except: id);
            if (problem != null)
            {
                error.Text = problem;
                error.Visibility = Visibility.Visible;
                return;
            }
            _editing = null;
            DictionaryStore.Instance.Update(id, t, expansion.Text.TrimEnd());
            RefreshSnippets();
        }

        void Cancel()
        {
            _editing = null;
            RefreshSnippets();
        }

        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = Ui.TextButton("Cancel", "GhostButton", 0);
        cancel.Click += (_, _) => Cancel();
        var save = Ui.TextButton("Save", "AccentButton");
        save.Click += (_, _) => Save();
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        DockPanel.SetDock(buttons, Dock.Right);
        footer.Children.Add(buttons);
        footer.Children.Add(counter);
        footer.Children.Add(error);

        trigger.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { expansion.Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
        };
        expansion.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { Save(); e.Handled = true; }
        };
        // Focus once, when the editor first opens. The element is re-added on
        // every list rebuild, and a second Loaded must not pull focus back to
        // the trigger box from wherever the user is typing.
        RoutedEventHandler? focusOnce = null;
        focusOnce = (_, _) =>
        {
            trigger.Loaded -= focusOnce;
            trigger.Focus();
        };
        trigger.Loaded += focusOnce;

        panel.Children.Add(trigger);
        panel.Children.Add(expansion);
        panel.Children.Add(footer);
        return new Border
        {
            Style = Ui.StyleOf("Card"),
            BorderBrush = Ui.BrushOf("AccentLineBrush"),
            Margin = new Thickness(0, 0, 0, 12),
            Child = panel,
        };
    }

    /// <summary>Null when the snippet can be saved, otherwise what to fix.</summary>
    private static string? Validate(string trigger, string expansion, Guid? except)
    {
        if (TriggerKey(trigger).Length == 0) return "Give it a trigger phrase to say.";
        if (expansion.Trim().Length == 0)
        {
            return "A snippet needs text to insert. To just teach a word, use Dictionary.";
        }
        if (expansion.Length > MaxExpansion) return $"Keep it under {MaxExpansion:N0} characters.";
        var clash = DictionaryStore.Instance.Entries.FirstOrDefault(e => e.IsSnippet && e.Id != except
            && TriggerKey(e.Phrase) == TriggerKey(trigger));
        return clash != null ? $"You already have a snippet for “{clash.Phrase}”." : null;
    }

    private void AddSnippet_Click(object sender, RoutedEventArgs e)
    {
        var trigger = TriggerBox.Text.Trim();
        var problem = Validate(trigger, ExpansionBox.Text, except: null);
        if (problem != null)
        {
            AddErrorText.Text = problem;
            AddErrorText.Visibility = Visibility.Visible;
            (trigger.Length == 0 ? TriggerBox : ExpansionBox).Focus();
            return;
        }
        AddErrorText.Visibility = Visibility.Collapsed;
        DictionaryStore.Instance.Add(trigger, ExpansionBox.Text.TrimEnd(), true, isSnippet: true);
        TriggerBox.Clear();
        ExpansionBox.Clear();
        TriggerBox.Focus();
    }
}
