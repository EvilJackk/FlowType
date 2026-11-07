using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using FlowType.Session;
using FlowType.UI.Controls;
using CheckBox = System.Windows.Controls.CheckBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using KeyEventHandler = System.Windows.Input.KeyEventHandler;
using Orientation = System.Windows.Controls.Orientation;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;
using TextWrapping = System.Windows.TextWrapping;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace FlowType.UI.Pages;

/// <summary>
/// Vocabulary words and replacement rules — every dictionary entry that is
/// not a snippet (snippets have their own page). Entries can be switched off
/// without deleting them, edited in place, and filtered; ones FlowType learned
/// from a correction the user typed, or that the user approved from their past
/// writing ("From history"), are badged, so a wrong one is obvious and one
/// click from gone.
/// </summary>
public partial class DictionaryPage : IMainPage
{
    private Guid? _editing;
    private bool _shown;
    private bool _refreshQueued;

    /// <summary>
    /// The open inline editor, kept across list rebuilds. Any store change —
    /// a correction learned mid-dictation, another entry switched off —
    /// rebuilds the list; building the editor afresh from the stored entry
    /// would throw away what the user had typed and yank focus back to the
    /// phrase box. Re-adding this same element keeps the draft, caret, undo
    /// and focus exactly as they were.
    /// </summary>
    private FrameworkElement? _editor;
    private Guid? _editorFor;

    public DictionaryPage()
    {
        InitializeComponent();
        DictionaryStore.Instance.Changed += OnDataChanged;
    }

    public FrameworkElement SnapshotBody => Body;

    public void OnShown()
    {
        _shown = true;
        RefreshDictionary();
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
            if (_shown) RefreshDictionary();
        }, DispatcherPriority.Background);
    }

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (_shown) RefreshDictionary();
    }

    private void DictSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_shown) RefreshDictionary();
    }

    // ----- List -----

    private void RefreshDictionary()
    {
        var all = DictionaryStore.Instance.Entries.Where(e => !e.IsSnippet).ToList();

        // Deleted elsewhere while being edited: there is nothing left to save to.
        if (_editing is { } editing && !all.Any(e => e.Id == editing)) _editing = null;
        if (_editorFor != _editing)
        {
            _editor = null;
            _editorFor = null;
        }

        SetTab(FilterAll, "All", all.Count);
        SetTab(FilterWords, "Words", all.Count(e => e.IsVocabularyOnly));
        SetTab(FilterRules, "Replacements", all.Count(e => !e.IsVocabularyOnly));
        SetTab(FilterLearned, "Learned", all.Count(IsLearned));

        IEnumerable<DictionaryEntry> shown = all;
        if (FilterWords.IsChecked == true) shown = shown.Where(e => e.IsVocabularyOnly);
        else if (FilterRules.IsChecked == true) shown = shown.Where(e => !e.IsVocabularyOnly);
        else if (FilterLearned.IsChecked == true) shown = shown.Where(IsLearned);

        var query = DictSearchBox.Text.Trim();
        if (query.Length > 0)
        {
            shown = shown.Where(e => e.Phrase.Contains(query, StringComparison.OrdinalIgnoreCase)
                || e.Replacement.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
        var list = shown.ToList();

        // WPF re-checks a detached focused element asynchronously, so re-adding
        // the editor in this same pass normally keeps its focus. If it was
        // dropped anyway, hand it straight back to the box the user was in.
        var refocus = _editor?.IsKeyboardFocusWithin == true ? Keyboard.FocusedElement as UIElement : null;
        DictList.Children.Clear();
        for (var i = 0; i < list.Count; i++)
        {
            if (i > 0)
            {
                DictList.Children.Add(new Border
                {
                    Height = 1,
                    Background = Ui.BrushOf("StrokeBrush"),
                    Margin = new Thickness(14, 0, 14, 0),
                });
            }
            DictList.Children.Add(list[i].Id == _editing ? EditorFor(list[i]) : BuildRow(list[i]));
        }
        if (refocus is { IsKeyboardFocused: false, IsVisible: true }) refocus.Focus();

        DictCard.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DictEmpty.Visibility = list.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (all.Count == 0)
        {
            DictEmptyTitle.Text = "No words yet";
            DictEmptyText.Text = "Add the names, products and jargon you say often. FlowType listens for them, "
                + "and fixes near-misses by sound as well as spelling.";
        }
        else if (query.Length > 0)
        {
            DictEmptyTitle.Text = "No matches";
            DictEmptyText.Text = $"Nothing in this list mentions “{query}”.";
        }
        else
        {
            DictEmptyTitle.Text = "Nothing in this view";
            DictEmptyText.Text = FilterLearned.IsChecked == true
                ? "When you backspace over a word FlowType got wrong and type the right one, it lands here — "
                    + "and so does anything you approve in Insights → Learn from your writing."
                : "Switch to All to see every entry.";
        }
    }

    /// <summary>
    /// Added by FlowType rather than typed in here: from a correction made
    /// right after a dictation, or approved from a look back over past
    /// writing. Both are what the "Learned" tab is for — the place to go when
    /// a rule you never wrote is changing your text.
    /// </summary>
    private static bool IsLearned(DictionaryEntry e) => e.LearnedFromCorrection || e.LearnedFromHistory;

    /// <summary>"Words  12" — the count in a quieter colour than the label.</summary>
    private static void SetTab(RadioButton tab, string label, int count)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(new TextBlock
        {
            Text = count.ToString("N0"),
            Foreground = Ui.BrushOf("FaintBrush"),
            Margin = new Thickness(7, 0, 0, 0),
        });
        tab.Content = panel;
    }

    private FrameworkElement BuildRow(DictionaryEntry entry)
    {
        var id = entry.Id;
        var grid = Ui.Columns(Ui.Auto, Ui.Star, Ui.Auto);

        var enabled = new CheckBox
        {
            Style = Ui.StyleOf("Switch"),
            IsChecked = entry.Enabled,
            ToolTip = entry.Enabled ? "On — click to switch this entry off" : "Off — click to switch it on",
            Margin = new Thickness(0, 0, 16, 0),
        };
        enabled.Checked += (_, _) => DictionaryStore.Instance.SetEnabled(id, true);
        enabled.Unchecked += (_, _) => DictionaryStore.Instance.SetEnabled(id, false);
        System.Windows.Automation.AutomationProperties.SetName(enabled, $"Use “{entry.Phrase}”");
        grid.Children.Add(enabled.At(0));

        var content = new StackPanel { Opacity = entry.Enabled ? 1.0 : 0.45, VerticalAlignment = VerticalAlignment.Center };
        var line = new TextBlock { FontSize = 15, TextWrapping = TextWrapping.Wrap };
        if (entry.IsVocabularyOnly)
        {
            line.Inlines.Add(new Run(entry.Phrase) { Foreground = Ui.BrushOf("TextBrush") });
        }
        else
        {
            line.Inlines.Add(new Run(entry.Phrase) { Foreground = Ui.BrushOf("MutedBrush") });
            line.Inlines.Add(new Run("   →   ") { Foreground = Ui.BrushOf("FaintBrush") });
            // A replacement of only spaces is a real rule ("-" → " "), so show it.
            var replacement = entry.Replacement.Trim().Length == 0
                ? $"“{entry.Replacement}” (space)"
                : entry.Replacement;
            line.Inlines.Add(new Run(replacement) { Foreground = Ui.BrushOf("TextBrush") });
        }
        content.Children.Add(line);

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        if (entry.LearnedFromCorrection)
        {
            var learned = Ui.Chip("Learned", ChipTone.Accent);
            learned.Margin = new Thickness(0, 0, 8, 0);
            learned.ToolTip = "Added automatically from a correction you typed";
            meta.Children.Add(learned);
        }
        else if (entry.LearnedFromHistory)
        {
            // Same accent as "Learned", different words: the user approved this
            // one in the review window, so it should read as "from my history",
            // not as something FlowType decided on its own.
            var history = Ui.Chip("From history", ChipTone.Accent);
            history.Margin = new Thickness(0, 0, 8, 0);
            history.ToolTip = "Added from your past writing (Insights → Learn from your writing). "
                + "“Undo everything learned” there removes it.";
            meta.Children.Add(history);
        }
        meta.Children.Add(new TextBlock
        {
            Text = entry.IsVocabularyOnly
                ? "Listened for while you speak"
                : entry.MatchWholeWord ? "Replaced after transcription" : "Replaced anywhere it appears, even inside words",
            FontSize = 12.5,
            Foreground = Ui.BrushOf("FaintBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        content.Children.Add(meta);
        grid.Children.Add(content.At(1));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var edit = Ui.IconButton("IconEdit", null, "Edit");
        edit.Click += (_, _) =>
        {
            _editing = id;
            RefreshDictionary();
        };
        actions.Children.Add(edit);
        var delete = Ui.IconButton("IconTrash", null, "Delete", "DangerChipButton");
        delete.Click += (_, _) =>
        {
            // A learned entry is remembered as "don't suggest again" first:
            // the history it came from is still there, and the next run would
            // otherwise offer it — or, with words added automatically, add it —
            // straight back.
            if (entry.LearnedFromHistory) FlowType.Learning.HistoryLearner.Instance.Forget(entry);
            DictionaryStore.Instance.Delete(id);
        };
        actions.Children.Add(delete);
        grid.Children.Add(actions.At(2));

        return HoverRow(grid);
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

    /// <summary>The row swapped for two boxes; Enter saves, Esc cancels.</summary>
    private FrameworkElement BuildEditor(DictionaryEntry entry)
    {
        var id = entry.Id;
        var grid = Ui.Columns(Ui.Star, Ui.Auto, Ui.Star, Ui.Auto, Ui.Auto);
        var phrase = new TextBox { Text = entry.Phrase, Tag = "Word or phrase" };
        var replacement = new TextBox { Text = entry.Replacement, Tag = "Becomes (optional)" };
        grid.Children.Add(phrase.At(0));
        grid.Children.Add(new TextBlock
        {
            Text = "→",
            FontSize = 18,
            Foreground = Ui.BrushOf("FaintBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0),
        }.At(1));
        grid.Children.Add(replacement.At(2));

        void Save()
        {
            var p = phrase.Text.Trim();
            if (p.Length == 0)
            {
                phrase.Focus();
                return;
            }
            _editing = null;
            DictionaryStore.Instance.Update(id, p, replacement.Text);
            RefreshDictionary();
        }

        void Cancel()
        {
            _editing = null;
            RefreshDictionary();
        }

        var save = Ui.TextButton("Save", "AccentButton", 12);
        save.Click += (_, _) => Save();
        var cancel = Ui.TextButton("Cancel", "GhostButton");
        cancel.Click += (_, _) => Cancel();
        grid.Children.Add(save.At(3));
        grid.Children.Add(cancel.At(4));

        KeyEventHandler keys = (_, e) =>
        {
            if (e.Key == Key.Enter) { Save(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
        };
        phrase.KeyDown += keys;
        replacement.KeyDown += keys;
        // Focus once, when the editor first opens. The element is re-added on
        // every list rebuild, and a second Loaded must not pull focus (and the
        // caret) back to the phrase box from wherever the user is typing.
        RoutedEventHandler? focusOnce = null;
        focusOnce = (_, _) =>
        {
            phrase.Loaded -= focusOnce;
            phrase.Focus();
            phrase.CaretIndex = phrase.Text.Length;
        };
        phrase.Loaded += focusOnce;

        return new Border
        {
            Background = Ui.BrushOf("CardHoverBrush"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 10, 12, 10),
            Child = grid,
        };
    }

    /// <summary>A rounded row that lights up under the pointer.</summary>
    internal static Border HoverRow(UIElement child)
    {
        var row = new Border
        {
            Background = System.Windows.Media.Brushes.Transparent,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 8, 12),
            Child = child,
        };
        row.MouseEnter += (_, _) => row.Background = Ui.BrushOf("CardHoverBrush");
        row.MouseLeave += (_, _) => row.Background = System.Windows.Media.Brushes.Transparent;
        return row;
    }

    // ----- Add -----

    private void AddBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AddDictEntry_Click(sender, e);
        e.Handled = true;
    }

    private void AddDictEntry_Click(object sender, RoutedEventArgs e)
    {
        var phrase = PhraseBox.Text.Trim();
        if (phrase.Length == 0)
        {
            PhraseBox.Focus();
            return;
        }

        // Replacement deliberately untrimmed: a single space is a meaningful rule.
        var replacement = ReplacementBox.Text;
        var duplicate = DictionaryStore.Instance.Entries.Any(x => !x.IsSnippet
            && x.Phrase.Equals(phrase, StringComparison.OrdinalIgnoreCase)
            && x.Replacement == replacement);
        if (duplicate)
        {
            AddErrorText.Text = $"“{phrase}” is already in your dictionary.";
            AddErrorText.Visibility = Visibility.Visible;
            return;
        }
        AddErrorText.Visibility = Visibility.Collapsed;

        DictionaryStore.Instance.Add(phrase, replacement, WholeWordCheck.IsChecked == true);
        PhraseBox.Clear();
        ReplacementBox.Clear();
        PhraseBox.Focus();
    }
}
