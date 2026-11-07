using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using FlowType.Core;
using FlowType.Engine;
using FlowType.Input;
using FlowType.Session;
using FlowType.UI.Controls;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;
using Orientation = System.Windows.Controls.Orientation;
using TextWrapping = System.Windows.TextWrapping;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace FlowType.UI.Pages;

/// <summary>
/// Home: greeting, this week's numbers, a practice box, and the dictation
/// history — grouped by day, searchable, with per-row actions (copy, play,
/// transcribe again, show the original, delete) revealed on hover.
///
/// History is built as plain elements, newest first, and capped at
/// <see cref="PageSize"/> rows per "Show more" step. NotesStore keeps up to
/// 1,000 notes; building all of them on every dictation would cost ~100 ms
/// of layout for rows nobody scrolls to, while 150 rows lay out in a few ms.
/// </summary>
public partial class HomePage : IMainPage
{
    private const int PageSize = 150;

    /// <summary>Longer than this (or more lines than <see cref="FoldLines"/>) starts folded.</summary>
    private const int FoldChars = 360;
    private const int FoldLines = 5;
    private const double LineHeight = 22;

    /// <summary>Copy, play, transcribe again, more, delete — plus the gutter before them.</summary>
    private const double ActionsColumnWidth = 172;

    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly HashSet<Guid> _busy = new();
    private readonly HashSet<Guid> _showOriginal = new();
    private readonly HashSet<Guid> _expanded = new();
    private readonly Dictionary<Guid, (string Text, bool Error)> _flash = new();
    private readonly Dictionary<Guid, Button> _playButtons = new();
    private Guid? _playingId;
    private int _limit = PageSize;
    private bool _shown;
    private bool _refreshQueued;
    private FrameworkElement? _firstRowActions;

    public HomePage()
    {
        InitializeComponent();
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            _limit = PageSize;
            RefreshNotes();
        };

        // A newcomer gets the practice box open; a regular gets it folded.
        SetPracticeOpen(StatsStore.Instance.TotalUtterances < 5);

        NotesStore.Instance.Changed += OnDataChanged;
        StatsStore.Instance.Changed += OnDataChanged;
        SettingsStore.Instance.Changed += OnDataChanged;
        Transcriber.Instance.StateChanged += OnDataChanged;
        RecordingArchive.Instance.Changed += OnDataChanged;
        RecordingArchive.Instance.PlaybackStateChanged += OnPlaybackStateChanged;
    }

    public FrameworkElement SnapshotBody => Body;

    public void OnShown()
    {
        _shown = true;
        RefreshAll();
    }

    public void OnHidden()
    {
        _shown = false;
        _searchTimer.Stop();
    }

    public void Detach()
    {
        _searchTimer.Stop();
        NotesStore.Instance.Changed -= OnDataChanged;
        StatsStore.Instance.Changed -= OnDataChanged;
        SettingsStore.Instance.Changed -= OnDataChanged;
        Transcriber.Instance.StateChanged -= OnDataChanged;
        RecordingArchive.Instance.Changed -= OnDataChanged;
        RecordingArchive.Instance.PlaybackStateChanged -= OnPlaybackStateChanged;
        try { UserActions.StopPlayback(); } catch { /* nothing playing */ }
    }

    /// <summary>Ctrl+F from anywhere in the window lands here.</summary>
    public void FocusSearch()
    {
        NotesSearchBox.Focus();
        NotesSearchBox.SelectAll();
    }

    /// <summary>For --snapshot: show the first row's hover actions without a mouse.</summary>
    internal void RevealFirstRowActions(bool reveal)
    {
        if (_firstRowActions != null) _firstRowActions.Opacity = reveal ? 1 : 0;
    }

    // ----- Refresh plumbing -----

    /// <summary>
    /// Services raise events off the UI thread and often several at once (a
    /// dictation changes notes, stats and the archive together), so refreshes
    /// are coalesced into one pass at background priority. Hidden pages skip
    /// the work and catch up in <see cref="OnShown"/>.
    /// </summary>
    private void OnDataChanged()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _refreshQueued = false;
            if (_shown) RefreshAll();
        }, DispatcherPriority.Background);
    }

    private void RefreshAll()
    {
        RefreshHeader();
        RefreshNotes();
    }

    // ----- Header, stats, practice box -----

    private void RefreshHeader()
    {
        var s = SettingsStore.Instance.Settings;
        GreetingText.Text = DateTime.Now.Hour switch
        {
            < 5 => "Up late?",
            < 12 => "Good morning",
            < 18 => "Good afternoon",
            _ => "Good evening",
        };

        var hotkey = HotkeyDefs.CurrentDisplay(s);
        HomeHintText.Text = s.DictationPaused
            ? "Dictation is paused — the hotkey is ignored until you resume it from the tray or Settings → Privacy."
            : s.ActivationMode switch
            {
                "hold" => $"Hold {hotkey} anywhere and talk — let go to insert.",
                "toggle" => $"Press {hotkey} anywhere to start talking, and again to insert.",
                _ => $"Hold {hotkey} anywhere and talk — let go to insert. Quick-tap it for hands-free.",
            };
        TryHintText.Text = $"Click into the box, hold {hotkey} and talk.";

        var stats = StatsStore.Instance;
        StatWords.Text = Metrics.WordsInLastDays(7).ToString("N0");
        var wpm = Metrics.RecentWpm(NotesStore.Instance.All, stats.AverageWpm);
        StatWpm.Text = wpm > 0 ? wpm.ToString("N0") : "—";
        StatStreak.Text = stats.StreakDays.ToString("N0");
        StatFixes.Text = stats.TotalFixes.ToString("N0");

        var t = Transcriber.Instance;
        // Only when there is genuinely nothing to load — not while the saved
        // model is still being loaded at start-up.
        var chosenOnDisk = ModelCatalog.ById(SettingsStore.Instance.Settings.SelectedModel)?.IsDownloaded == true;
        SetupBanner.Visibility = !t.IsReady && !t.IsLoading && !chosenOnDisk ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TryToggle_Click(object sender, RoutedEventArgs e)
    {
        var open = PracticeBox.Visibility != Visibility.Visible;
        SetPracticeOpen(open);
        if (open) PracticeBox.Focus();
    }

    private void SetPracticeOpen(bool open)
    {
        PracticeBox.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        TryToggle.Content = open ? "Close" : "Open";
    }

    private void GoToSettings_Click(object sender, RoutedEventArgs e) =>
        (Ui.OwnerOf(this) as MainWindow)?.NavigateToSettings("model");

    // ----- History -----

    private void NotesSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void ShowMore_Click(object sender, RoutedEventArgs e)
    {
        _limit += PageSize;
        RefreshNotes();
    }

    private void RefreshNotes()
    {
        NotesList.Children.Clear();
        _playButtons.Clear();
        _firstRowActions = null;
        try { _playingId = RecordingArchive.Instance.NowPlaying; }
        catch { _playingId = null; }

        var query = NotesSearchBox.Text.Trim();
        var groups = NotesStore.Instance.GroupedByDay(query.Length == 0 ? null : query);
        var matched = groups.Sum(g => g.Notes.Count);
        var total = query.Length == 0 ? matched : NotesStore.Instance.All.Count;

        HistoryCountText.Text = query.Length == 0
            ? total == 0 ? "" : $"{total:N0} {Ui.Plural(total, "dictation", "dictations")}"
            : $"{matched:N0} of {total:N0} match";

        ShowEmptyState(matched, query);

        var rendered = 0;
        foreach (var (day, notes) in groups)
        {
            if (rendered >= _limit) break;
            var take = notes.Take(_limit - rendered).ToList();
            NotesList.Children.Add(BuildDay(day, notes.Count, take));
            rendered += take.Count;
        }

        var remaining = matched - rendered;
        ShowMoreButton.Visibility = remaining > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowMoreButton.Content = $"Show {Math.Min(PageSize, remaining):N0} more";
    }

    private void ShowEmptyState(int matched, string query)
    {
        if (matched > 0)
        {
            NotesEmpty.Visibility = Visibility.Collapsed;
            return;
        }
        NotesEmpty.Visibility = Visibility.Visible;
        if (query.Length > 0)
        {
            NotesEmptyTitle.Text = "No matches";
            NotesEmptyText.Text = $"Nothing in your history mentions “{query}”.";
        }
        else if (!SettingsStore.Instance.Settings.SaveHistory)
        {
            NotesEmptyTitle.Text = "History is off";
            NotesEmptyText.Text = "Nothing you dictate is written to disk. Turn on “Save transcripts” in Settings → Privacy to keep them here.";
        }
        else
        {
            NotesEmptyTitle.Text = "Nothing here yet";
            NotesEmptyText.Text = "Everything you dictate lands here, grouped by day — searchable, and kept only on this PC.";
        }
    }

    /// <summary>A day header and one card holding that day's rows.</summary>
    private FrameworkElement BuildDay(DateTime day, int dayCount, IReadOnlyList<Note> notes)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 8) };
        header.Children.Add(new TextBlock
        {
            Text = NotesStore.DayLabel(day).ToUpperInvariant(),
            Style = Ui.StyleOf("Eyebrow"),
        });
        header.Children.Add(new TextBlock
        {
            Text = $"{dayCount:N0} {Ui.Plural(dayCount, "dictation", "dictations")}",
            Style = Ui.StyleOf("Eyebrow"),
            FontWeight = FontWeights.Normal,
            Margin = new Thickness(10, 0, 0, 0),
        });
        panel.Children.Add(header);

        var rows = new StackPanel();
        for (var i = 0; i < notes.Count; i++)
        {
            if (i > 0)
            {
                rows.Children.Add(new Border
                {
                    Height = 1,
                    Background = Ui.BrushOf("StrokeBrush"),
                    Margin = new Thickness(14, 0, 14, 0),
                });
            }
            rows.Children.Add(BuildRow(notes[i]));
        }

        panel.Children.Add(new Border
        {
            Style = Ui.StyleOf("Card"),
            Padding = new Thickness(6),
            Margin = new Thickness(0),
            Child = rows,
        });
        return panel;
    }

    /// <summary>
    /// One dictation: time | text + details | actions. Actions sit in a
    /// reserved column at zero opacity and fade in on hover, so the text never
    /// reflows as the pointer moves down the list.
    /// </summary>
    private FrameworkElement BuildRow(Note note)
    {
        var id = note.Id;
        var hasOriginal = !string.IsNullOrEmpty(note.RawText)
            && !string.Equals(note.RawText.Trim(), note.Text.Trim(), StringComparison.Ordinal);
        var showingOriginal = hasOriginal && _showOriginal.Contains(id);
        var shown = showingOriginal ? note.RawText : note.Text;

        // When recordings are kept most rows carry four actions; reserving that
        // width on every row keeps the right edge of the text in one line.
        var actionsWidth = SettingsStore.Instance.Settings.KeepRecentRecordings > 0
            ? new GridLength(ActionsColumnWidth) : Ui.Auto;
        var grid = Ui.Columns(new GridLength(84), Ui.Star, actionsWidth);

        var time = Ui.Text(note.Timestamp.ToString("h:mm tt"), 13, "FaintBrush");
        time.VerticalAlignment = VerticalAlignment.Top;
        time.Margin = new Thickness(0, 2, 0, 0);
        grid.Children.Add(time.At(0));

        var content = new StackPanel();
        var text = new TextBlock
        {
            Text = shown,
            Style = Ui.StyleOf("BodyText"),
            Foreground = Ui.BrushOf(showingOriginal ? "SoftTextBrush" : "TextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var foldable = shown.Length > FoldChars || shown.Count(c => c == '\n') >= FoldLines;
        if (foldable && !_expanded.Contains(id)) text.MaxHeight = LineHeight * FoldLines;
        content.Children.Add(text);

        if (foldable)
        {
            var more = new Button
            {
                Style = Ui.StyleOf("LinkButton"),
                Content = _expanded.Contains(id) ? "Show less" : "Show all",
                FontSize = 13,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 0),
            };
            more.Click += (_, _) =>
            {
                if (!_expanded.Remove(id)) _expanded.Add(id);
                RefreshNotes();
            };
            content.Children.Add(more);
        }

        var meta = new TextBlock
        {
            Text = MetaLine(note, showingOriginal),
            FontSize = 12.5,
            Foreground = Ui.BrushOf("FaintBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            ToolTip = Diagnostics(note),
        };
        if (_flash.TryGetValue(id, out var flash))
        {
            meta.Text = flash.Text;
            meta.Foreground = Ui.BrushOf(flash.Error ? "DangerBrush" : "AccentBrush");
        }
        content.Children.Add(meta);
        grid.Children.Add(content.At(1));

        var actions = BuildActions(note, text, hasOriginal);
        var busy = _busy.Contains(id) || _playingId == id;
        actions.Opacity = busy ? 1 : 0;
        grid.Children.Add(actions.At(2));
        _firstRowActions ??= actions;

        var row = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 8, 12),
            Child = grid,
        };
        row.MouseEnter += (_, _) =>
        {
            row.Background = Ui.BrushOf("CardHoverBrush");
            actions.Opacity = 1;
        };
        row.MouseLeave += (_, _) =>
        {
            row.Background = Brushes.Transparent;
            if (!_busy.Contains(id) && _playingId != id) actions.Opacity = 0;
        };
        // Keyboard users tabbing onto an action see the whole set.
        actions.IsKeyboardFocusWithinChanged += (_, e) =>
        {
            if ((bool)e.NewValue) actions.Opacity = 1;
            else if (!row.IsMouseOver && !_busy.Contains(id) && _playingId != id) actions.Opacity = 0;
        };
        return row;
    }

    /// <summary>"Discord · 42 words · 12.3 s · AI polished · Casual · ready in 380 ms".</summary>
    private static string MetaLine(Note note, bool showingOriginal)
    {
        var parts = new List<string>();
        if (showingOriginal) parts.Add("What was heard, before formatting");
        if (!string.IsNullOrEmpty(note.AppName)) parts.Add(note.AppName);
        parts.Add($"{note.WordCount:N0} {Ui.Plural(note.WordCount, "word", "words")}");
        if (note.DurationSeconds > 0) parts.Add($"{note.DurationSeconds:0.0} s");
        if (note.AiPolished) parts.Add("AI polished");
        else if (note.CanRedoAiEdit) parts.Add("AI edit undone");
        if (note.Style == "command")
        {
            parts.Add("Command Mode");
        }
        else if (!string.IsNullOrEmpty(note.Style))
        {
            var style = WritingStyles.All.FirstOrDefault(a => a.Id == note.Style).Label;
            if (!string.IsNullOrEmpty(style)) parts.Add(style);
        }
        if (!string.IsNullOrEmpty(note.Language) && note.Language != "en") parts.Add(note.Language.ToUpperInvariant());
        if (note.Retranscribed) parts.Add("transcribed again");
        if (note.LatencyMs > 0) parts.Add($"ready in {note.LatencyMs:N0} ms");
        return string.Join("  ·  ", parts);
    }

    /// <summary>Hover detail for the meta line: which model, and where the time went.</summary>
    private static string Diagnostics(Note note)
    {
        var lines = new List<string> { $"{note.Timestamp:dddd, MMMM d, yyyy  h:mm:ss tt}" };
        if (!string.IsNullOrEmpty(note.ModelId)) lines.Add($"Speech model: {ModelCatalog.ById(note.ModelId)?.DisplayName ?? note.ModelId}");
        if (note.DecodeMs > 0) lines.Add($"Transcribed in {note.DecodeMs:N0} ms");
        if (note.LatencyMs > 0) lines.Add($"Text ready {note.LatencyMs:N0} ms after you stopped talking");
        return string.Join(Environment.NewLine, lines);
    }

    private StackPanel BuildActions(Note note, TextBlock text, bool hasOriginal)
    {
        var id = note.Id;
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(12, -4, 0, 0),
        };

        if (_busy.Contains(id))
        {
            actions.Children.Add(new TextBlock
            {
                Text = "Transcribing again…",
                FontSize = 13,
                Foreground = Ui.BrushOf("AccentBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 6, 8, 0),
            });
            return actions;
        }

        var copy = Ui.IconButton("IconCopy", null, "Copy");
        copy.Click += async (_, _) =>
        {
            TextInjector.CopyOnly(text.Text);
            Ui.SetIconContent(copy, "IconCheck", null);
            copy.ToolTip = "Copied";
            await Task.Delay(1400);
            Ui.SetIconContent(copy, "IconCopy", null);
            copy.ToolTip = "Copy";
        };
        actions.Children.Add(copy);

        bool canReplay;
        try { canReplay = UserActions.CanRetranscribe(note); }
        catch { canReplay = false; }

        if (canReplay)
        {
            var playing = _playingId == id;
            var play = Ui.IconButton(playing ? "IconStop" : "IconPlay", null,
                playing ? "Stop" : "Play the recording");
            play.Click += (_, _) => TogglePlayback(id);
            _playButtons[id] = play;
            actions.Children.Add(play);

            var retry = Ui.IconButton("IconRetry", null, "Transcribe again with the current model");
            retry.Click += (_, _) => Retranscribe(note);
            actions.Children.Add(retry);
        }

        var menu = BuildMoreMenu(note, hasOriginal, canReplay);
        if (menu != null)
        {
            var more = Ui.IconButton("IconMore", null, "More");
            more.Click += (_, _) =>
            {
                menu.PlacementTarget = more;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
            };
            actions.Children.Add(more);
        }

        // NotesStore.Delete removes the note's kept recording with it.
        var delete = Ui.IconButton("IconTrash", null, "Delete", "DangerChipButton");
        delete.Click += (_, _) => NotesStore.Instance.Delete(id);
        actions.Children.Add(delete);
        return actions;
    }

    /// <summary>
    /// The less frequent row actions, behind "⋯" so the row keeps four quiet
    /// icons: undo/redo the AI edit, see what was heard, save the recording.
    /// Null when the note offers none of them.
    /// </summary>
    private ContextMenu? BuildMoreMenu(Note note, bool hasOriginal, bool canReplay)
    {
        var id = note.Id;
        var menu = new ContextMenu();

        if (note.CanUndoAiEdit || note.CanRedoAiEdit)
        {
            var item = new MenuItem
            {
                Header = note.CanUndoAiEdit ? "Undo AI edit — keep the rules-only text" : "Redo AI edit",
            };
            item.Click += (_, _) =>
            {
                if (!NotesStore.Instance.ToggleAiEdit(id)) Flash(id, "This dictation has only one version now.", error: true);
            };
            menu.Items.Add(item);
        }

        if (hasOriginal)
        {
            var item = new MenuItem
            {
                Header = _showOriginal.Contains(id) ? "Show the final text" : "Show what was heard, before formatting",
            };
            item.Click += (_, _) =>
            {
                if (!_showOriginal.Remove(id)) _showOriginal.Add(id);
                RefreshNotes();
            };
            menu.Items.Add(item);
        }

        if (canReplay)
        {
            var item = new MenuItem { Header = "Save recording…" };
            item.Click += (_, _) => SaveRecording(note);
            menu.Items.Add(item);
        }

        return menu.Items.Count > 0 ? menu : null;
    }

    private void TogglePlayback(Guid id)
    {
        try
        {
            if (RecordingArchive.Instance.NowPlaying == id)
            {
                RecordingArchive.Instance.StopPlayback();
            }
            else if (!RecordingArchive.Instance.Play(id))
            {
                Flash(id, "Couldn't play it — the recording is gone, or there is no audio output device.", error: true);
            }
        }
        catch (Exception ex)
        {
            Flash(id, $"Couldn't play the recording: {ex.Message}", error: true);
        }
    }

    /// <summary>Play ↔ Stop on the row whose recording started or ended.</summary>
    private void OnPlaybackStateChanged(Guid id, bool playing) => Dispatcher.BeginInvoke(() =>
    {
        if (playing) _playingId = id;
        else if (_playingId == id) _playingId = null;
        if (!_playButtons.TryGetValue(id, out var button)) return;
        Ui.SetIconContent(button, playing ? "IconStop" : "IconPlay", null);
        button.ToolTip = playing ? "Stop" : "Play the recording";
        // Keep the row's actions up while it plays, so Stop stays reachable.
        if (button.Parent is FrameworkElement actions)
        {
            actions.Opacity = playing ? 1 : (actions.IsMouseOver ? 1 : 0);
        }
    });

    private void SaveRecording(Note note)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"flowtype-{note.Timestamp:yyyy-MM-dd-HHmmss}.wav",
            DefaultExt = ".wav",
            Filter = "WAV audio (*.wav)|*.wav",
        };
        if (ShowDialog(dialog) != true) return;
        try
        {
            if (!RecordingArchive.Instance.ExportTo(note.Id, dialog.FileName))
            {
                Flash(note.Id, "The recording is no longer kept.", error: true);
            }
        }
        catch (Exception ex)
        {
            Ui.Message(this, $"Couldn't save the recording:\n{ex.Message}", MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    /// <summary>A file dialog owned by this window when there is one.</summary>
    private bool? ShowDialog(Microsoft.Win32.CommonDialog dialog)
    {
        var owner = Ui.OwnerOf(this);
        return owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
    }

    private async void Retranscribe(Note note)
    {
        var id = note.Id;
        if (!_busy.Add(id)) return;
        RefreshNotes();
        try
        {
            var result = await UserActions.RetranscribeAsync(note);
            if (result == null)
            {
                Flash(id, "Nothing new — the recording is gone or had no recognisable speech.", error: true);
            }
            else
            {
                Flash(id, "Transcribed again with the current model.", error: false);
            }
        }
        catch (Exception ex)
        {
            Flash(id, $"Couldn't transcribe again: {ex.Message}", error: true);
        }
        finally
        {
            _busy.Remove(id);
            RefreshNotes();
        }
    }

    /// <summary>Show a short message in a row's detail line for a few seconds.</summary>
    private async void Flash(Guid id, string message, bool error)
    {
        _flash[id] = (message, error);
        RefreshNotes();
        await Task.Delay(TimeSpan.FromSeconds(5));
        if (_flash.Remove(id) && _shown) RefreshNotes();
    }

    private void ExportNotes_Click(object sender, RoutedEventArgs e)
    {
        if (NotesStore.Instance.All.Count == 0)
        {
            Ui.Message(this, "There's nothing to export yet.");
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"flowtype-history-{DateTime.Now:yyyy-MM-dd}.txt",
            DefaultExt = ".txt",
            Filter = "Text file (*.txt)|*.txt",
        };
        if (ShowDialog(dialog) != true) return;

        try
        {
            NotesStore.Instance.ExportTo(dialog.FileName);
        }
        catch (Exception ex)
        {
            Ui.Message(this, $"Couldn't write the file:\n{ex.Message}", MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ClearNotes_Click(object sender, RoutedEventArgs e)
    {
        if (NotesStore.Instance.All.Count == 0) return;
        var result = Ui.Message(this,
            "Delete your whole history? Saved recordings go with it. This can't be undone.",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;
        NotesStore.Instance.Clear();   // also deletes every kept recording
    }
}
