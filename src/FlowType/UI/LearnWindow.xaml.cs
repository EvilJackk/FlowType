using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FlowType.Core;
using FlowType.Engine;
using FlowType.Learning;
using FlowType.Session;
using FlowType.UI.Controls;
using FlowType.UI.Pages;
using Application = System.Windows.Application;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Orientation = System.Windows.Controls.Orientation;
using TextWrapping = System.Windows.TextWrapping;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace FlowType.UI;

/// <summary>
/// "Learn from your writing": pick what to learn from, watch it run, review
/// what it found, approve. Four states in one window — Ready, Running,
/// Results, Applied — because they are one task, and a wizard of separate
/// windows would lose the user's ticks every time they went back.
///
/// The window decides nothing. Mining, merging, dismissal and applying all
/// live in <see cref="HistoryLearner"/>; this class only shows the report and
/// passes back which suggestions were ticked and which were deliberately left
/// unticked. That split is what lets the quiet daily run reuse the same
/// review: a pending background report is just a report this window opens on.
///
/// Privacy: suggestions (and their example excerpts) are shown, never
/// written. The only thing saved from here is the user's own choice of
/// sources (<see cref="AppSettings.LearnFromWispr"/>) and, through
/// <see cref="HistoryLearner.Apply"/>, what they approved.
/// </summary>
public partial class LearnWindow : Window
{
    private enum Stage { Ready, Running, Results, Applied }

    /// <summary>Which card a suggestion lands in; decides the small tag on its row.</summary>
    private enum Section { Words, Fixes, Wispr, Style }

    /// <summary>One suggestion on screen and the checkbox that carries its tick.</summary>
    private sealed record Row(Suggestion Suggestion, CheckBox Box)
    {
        /// <summary>
        /// The row has actually been on screen: inside the results' scroll
        /// viewport, not merely outside "Show more" (see <see cref="MarkSeen"/>).
        /// Only rows the user could have read are dismissed when left
        /// unticked. A fix 2,000 px below the fold, under 40 pre-ticked words,
        /// was never turned down — it was never looked at — and a dismissal
        /// is for good.
        /// </summary>
        public bool Seen { get; set; }
    }

    /// <summary>
    /// A long section shows its ticked rows and this many in total before a
    /// "Show N more" link, so 80 word candidates don't bury the fixes and the
    /// style below them. Ticked rows are never hidden: nothing is applied that
    /// the user hasn't seen.
    /// </summary>
    private const int FirstRows = 12;

    /// <summary>The personal benchmark decodes at most this many recordings (newest first).</summary>
    private const int BenchMaxRecordings = 40;

    private static LearnWindow? _open;

    private readonly bool _snapshot;
    private readonly DispatcherTimer _clock;
    private readonly List<Row> _rows = new();
    private readonly List<Action> _sectionCounters = new();

    private Stage _stage;
    private IReadOnlyList<SourceSummary>? _sources;
    private string? _sourcesProblem;
    private bool? _useFlowType;
    private bool? _useWispr;
    private CheckBox? _flowTypeCheck;
    private CheckBox? _wisprCheck;
    private string? _readyNote;
    private CancellationTokenSource? _run;
    private DateTime _runStarted;
    private double _fraction = -1;
    private double _slideWidth = -1;
    private bool _closed;

    // Snapshot-only stand-ins for live state (see SnapshotAll).
    private bool _snapshotModelReady = true;

    private LearnWindow(bool snapshot)
    {
        _snapshot = snapshot;
        InitializeComponent();
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateElapsed();
        // The footer button reads "Don't suggest these" or "Done" depending on this box.
        DismissCheck.Checked += (_, _) => UpdateCounts();
        DismissCheck.Unchecked += (_, _) => UpdateCounts();
        if (snapshot) return;

        HistoryLearner.Instance.Changed += OnLearnerChanged;
        Transcriber.Instance.StateChanged += OnTranscriberChanged;

        // A quiet daily run left suggestions waiting: that is what the user
        // came to see, so open on them rather than on "Start". Both halves
        // are checked: the count alone says nothing about whether a report
        // is still held (after a restart it is not), and with no report the
        // plain Start screen is the honest thing to show.
        var learner = HistoryLearner.Instance;
        if (!learner.IsRunning && learner.PendingCount > 0 && learner.LastReport is { } pending)
        {
            ShowResults(pending, fromEarlierRun: true);
        }
        else
        {
            ShowReady();
        }
        LoadSources();
    }

    /// <summary>
    /// Open the window over <paramref name="owner"/>, or bring the one that is
    /// already open forward — two copies could apply the same report twice.
    /// </summary>
    public static void Open(Window? owner)
    {
        if (_open is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var window = new LearnWindow(snapshot: false);
        if (owner is { IsLoaded: true })
        {
            window.Owner = owner;
        }
        else
        {
            // Not owned, so nothing keeps it in front: give it a taskbar button.
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.ShowInTaskbar = true;
        }
        _open = window;
        window.Show();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        if (ReferenceEquals(_open, this)) _open = null;
        _clock.Stop();
        // Closing the window is how a user says "stop": a benchmark keeps the
        // GPU busy for minutes and nobody would be left to review its result.
        _run?.Cancel();
        if (!_snapshot)
        {
            HistoryLearner.Instance.Changed -= OnLearnerChanged;
            Transcriber.Instance.StateChanged -= OnTranscriberChanged;
        }
        base.OnClosed(e);
    }

    private void OnLearnerChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (!_closed && _stage == Stage.Ready) RefreshReadyState();
    });

    private void OnTranscriberChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (!_closed && _stage == Stage.Ready) RefreshReadyState();
    });

    // ----- Buttons and keys -----

    private void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        switch (_stage)
        {
            case Stage.Ready: Start(); break;
            case Stage.Results when _rows.Count > 0: Apply(); break;
            case Stage.Results:
                // "Nothing new to learn" (everything waiting was added by hand
                // since, say): the review is done, so the waiting dot goes too.
                ClearPending();
                Close();
                break;
            default: Close(); break;
        }
    }

    private void SecondaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_stage == Stage.Running) CancelRun();
        else Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (_stage == Stage.Running) CancelRun();
        else Close();
        e.Handled = true;
    }

    private void ReadyBannerLink_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryLearner.Instance.LastReport is { } report) ShowResults(report, fromEarlierRun: true);
    }

    private void OpenDictionary_Click(object sender, RoutedEventArgs e) => OpenPage(MainPage.Dictionary);

    private void OpenStyle_Click(object sender, RoutedEventArgs e) => OpenPage(MainPage.Style);

    private void OpenSnippets_Click(object sender, RoutedEventArgs e) => OpenPage(MainPage.Snippets);

    private void OpenPage(MainPage page)
    {
        if (Owner is MainWindow main) main.NavigateTo(page);
        Close();
    }

    /// <summary>Show exactly one state panel.</summary>
    private void SetPanel(FrameworkElement visible)
    {
        foreach (var panel in new FrameworkElement[] { ReadyPanel, RunningPanel, ResultsPanel, AppliedPanel })
        {
            panel.Visibility = ReferenceEquals(panel, visible) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ===================================================================
    // Ready: what to learn from
    // ===================================================================

    private void ShowReady(string? note = null)
    {
        _stage = Stage.Ready;
        _readyNote = note;
        HeaderTitle.Text = "Look back over what you've written";
        HeaderText.Text = "FlowType reads your past dictations and suggests words to listen for, fixes for things "
            + "it mishears and the style you actually write in. You choose what to keep.";
        SetPanel(ReadyPanel);
        DismissCheck.Visibility = Visibility.Collapsed;
        SecondaryButton.Content = "Close";
        SecondaryButton.IsEnabled = true;
        SecondaryButton.Visibility = Visibility.Visible;
        PrimaryButton.Content = "Start";
        PrimaryButton.Visibility = Visibility.Visible;
        BuildSources();
        RefreshReadyState();
    }

    /// <summary>
    /// Count what each history holds, off the UI thread: Wispr Flow's
    /// database can be hundreds of megabytes and is opened read-only here, so
    /// the window opens first and fills the sources in when they arrive.
    /// </summary>
    private async void LoadSources()
    {
        IReadOnlyList<SourceSummary> sources;
        try
        {
            sources = await Task.Run(() => HistoryLearner.Instance.SummarizeSources());
            _sourcesProblem = null;
        }
        catch (Exception)
        {
            sources = Array.Empty<SourceSummary>();
            _sourcesProblem = "Couldn't check your history just now. Close this window and try again.";
        }
        if (_closed) return;
        _sources = sources;
        if (_stage == Stage.Ready)
        {
            BuildSources();
            RefreshReadyState();
        }
    }

    private void BuildSources()
    {
        SourceList.Children.Clear();
        _flowTypeCheck = null;
        _wisprCheck = null;

        if (_sources == null)
        {
            SourceList.Children.Add(new TextBlock
            {
                Text = "Checking what's there…",
                Style = Ui.StyleOf("Hint"),
                Margin = new Thickness(2, 0, 0, 10),
            });
            return;
        }
        if (_sourcesProblem != null)
        {
            SourceList.Children.Add(new TextBlock
            {
                Text = _sourcesProblem,
                Style = Ui.StyleOf("Hint"),
                Foreground = Ui.BrushOf("DangerBrush"),
                Margin = new Thickness(2, 0, 0, 10),
            });
            return;
        }

        var settings = SettingsStore.Instance.Settings;
        foreach (var source in _sources.OrderBy(s => s.Source))
        {
            var usable = LearnFormat.IsUsable(source);
            bool ticked;
            if (source.Source == HistorySource.WisprFlow)
            {
                // Opt-in: off until the user ticks it, then remembered.
                _useWispr ??= usable && settings.LearnFromWispr;
                ticked = _useWispr.Value;
            }
            else
            {
                _useFlowType ??= usable;
                ticked = _useFlowType.Value;
            }

            var box = SourceRow(source, usable, ticked && usable);
            if (source.Source == HistorySource.WisprFlow) _wisprCheck = box;
            else _flowTypeCheck = box;
        }
    }

    private CheckBox SourceRow(SourceSummary source, bool usable, bool ticked)
    {
        var text = new StackPanel();
        text.Children.Add(Ui.Text(LearnFormat.SourceName(source.Source), 15, "TextBrush", wrap: true));
        text.Children.Add(new TextBlock
        {
            Text = Describe(source),
            Style = Ui.StyleOf("Hint"),
            Margin = new Thickness(0, 3, 0, 0),
        });
        if (source.Available && source.Problem != null && usable)
        {
            // Opened, but only partly: say so rather than silently learning from less.
            text.Children.Add(new TextBlock
            {
                Text = source.Problem,
                Style = Ui.StyleOf("Hint"),
                FontSize = 12.5,
                Foreground = Ui.BrushOf("FaintBrush"),
                Margin = new Thickness(0, 3, 0, 0),
            });
        }
        if (source.Source == HistorySource.WisprFlow && usable)
        {
            text.Children.Add(new TextBlock
            {
                Text = "Only read — FlowType never changes or copies it. Off unless you tick it.",
                Style = Ui.StyleOf("Hint"),
                FontSize = 12.5,
                Foreground = Ui.BrushOf("FaintBrush"),
                Margin = new Thickness(0, 3, 0, 0),
            });
        }

        var box = new CheckBox
        {
            Content = text,
            IsChecked = ticked,
            IsEnabled = usable,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ToolTip = source.Location.Length > 0 ? source.Location : null,
        };
        System.Windows.Automation.AutomationProperties.SetName(box, LearnFormat.SourceName(source.Source));
        box.Checked += (_, _) => OnSourceToggled(source.Source, true);
        box.Unchecked += (_, _) => OnSourceToggled(source.Source, false);

        SourceList.Children.Add(new Border { Style = Ui.StyleOf("InsetRow"), Child = box });
        return box;
    }

    private void OnSourceToggled(HistorySource source, bool on)
    {
        if (source == HistorySource.WisprFlow) _useWispr = on;
        else _useFlowType = on;
        RefreshReadyState();
    }

    /// <summary>"Found: 863 dictations · 246 you edited afterwards · 32 min of audio", or why it can't be used.</summary>
    private static string Describe(SourceSummary s)
    {
        if (!s.Available)
        {
            return s.Problem ?? (s.Source == HistorySource.WisprFlow ? "Not found on this PC" : "Couldn't be read");
        }
        if (s.Dictations == 0)
        {
            return s.Problem ?? (s.Source == HistorySource.FlowType
                ? "Nothing here yet — your History is empty"
                : "No dictations in it yet");
        }
        var parts = new List<string> { $"{s.Dictations:N0} {Ui.Plural(s.Dictations, "dictation", "dictations")}" };
        if (s.Edited > 0) parts.Add($"{s.Edited:N0} you edited afterwards");
        if (s.WithAudio > 0 && s.AudioSeconds >= 1) parts.Add($"{LearnFormat.Duration(s.AudioSeconds)} of audio");
        return "Found: " + string.Join(" · ", parts);
    }

    /// <summary>Start button, benchmark option and banner, from the current ticks and engine state.</summary>
    private void RefreshReadyState()
    {
        var learner = _snapshot ? null : HistoryLearner.Instance;
        var busy = learner?.IsRunning == true && _run == null;
        var useFlowType = _flowTypeCheck?.IsChecked == true;
        var useWispr = _wisprCheck?.IsChecked == true;

        // What the benchmark would decode: Wispr Flow's kept recordings when
        // it is ticked (PersonalBench plays nothing else), capped the same way.
        var (recordings, seconds) = LearnFormat.BenchAudio(_sources, useWispr, BenchMaxRecordings);

        var modelReady = _snapshot ? _snapshotModelReady : Transcriber.Instance.IsReady;
        var benchPossible = modelReady && recordings > 0;
        BenchLabel.Text = recordings > 0
            ? $"Also test on my recordings — up to {LearnFormat.Duration(seconds)} of audio"
            : "Also test on my recordings";
        BenchCheck.IsEnabled = benchPossible;
        if (!benchPossible) BenchCheck.IsChecked = false;
        BenchHint.Text = LearnFormat.BenchHint(modelReady, _sources, useWispr, recordings);

        // Banner: a background run in progress, suggestions already waiting, or a note from the last attempt.
        var pending = learner?.PendingCount ?? 0;
        if (busy)
        {
            ShowBanner("A quiet daily check is running right now. Start will be ready when it finishes.", waiting: false);
        }
        else if (pending > 0 && learner?.LastReport != null)
        {
            ShowBanner($"{pending:N0} {Ui.Plural(pending, "suggestion", "suggestions")} from an earlier check "
                + "are waiting for you.", waiting: true);
        }
        else if (_readyNote != null)
        {
            ShowBanner(_readyNote, waiting: false);
        }
        else
        {
            ReadyBanner.Visibility = Visibility.Collapsed;
        }

        PrimaryButton.IsEnabled = _sources != null && _sourcesProblem == null && !busy && (useFlowType || useWispr);
    }

    /// <summary>
    /// A line above the sources. Amber only for "suggestions are waiting" —
    /// the accent means "ready for you" everywhere else in FlowType, so a
    /// plain note ("Stopped. Nothing was changed.") stays neutral.
    /// </summary>
    private void ShowBanner(string text, bool waiting)
    {
        ReadyBannerText.Text = text;
        ReadyBannerLink.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        ReadyBanner.Background = Ui.BrushOf(waiting ? "AccentSoftBrush" : "InsetBrush");
        ReadyBanner.BorderBrush = Ui.BrushOf(waiting ? "AccentLineBrush" : "StrokeBrush");
        ReadyBanner.Visibility = Visibility.Visible;
    }

    // ===================================================================
    // Running
    // ===================================================================

    private async void Start()
    {
        var useFlowType = _flowTypeCheck?.IsChecked == true;
        var useWispr = _wisprCheck?.IsChecked == true;
        var bench = BenchCheck.IsEnabled && BenchCheck.IsChecked == true;
        if (!useFlowType && !useWispr) return;
        if (HistoryLearner.Instance.IsRunning)
        {
            ShowReady("A quiet daily check is running right now. Try again when it finishes.");
            return;
        }
        RememberWisprChoice();

        using var run = new CancellationTokenSource();
        _run = run;
        ShowRunning(new LearningProgress("Reading your history", -1));
        // Created here, on the UI thread, so reports arrive on it.
        var progress = new Progress<LearningProgress>(p =>
        {
            if (!_closed && _stage == Stage.Running && !run.IsCancellationRequested) SetProgress(p);
        });

        try
        {
            var report = await HistoryLearner.Instance.RunAsync(
                new LearningRequest(useFlowType, useWispr, bench), progress, run.Token);
            if (_closed) return;
            ShowResults(report, fromEarlierRun: false);
        }
        catch (OperationCanceledException)
        {
            if (!_closed) ShowReady("Stopped. Nothing was changed.");
        }
        catch (Exception ex)
        {
            if (!_closed) ShowReady($"That didn't work: {ex.Message}");
        }
        finally
        {
            _run = null;
            _clock.Stop();
            StopSlide();
        }
    }

    /// <summary>
    /// The background run reads Wispr Flow only once the user has chosen it
    /// here, and stops when they untick it — the latest choice wins. A source
    /// that wasn't found doesn't count as a choice either way.
    /// </summary>
    private void RememberWisprChoice()
    {
        if (_wisprCheck is not { IsEnabled: true } wispr) return;
        var settings = SettingsStore.Instance.Settings;
        var on = wispr.IsChecked == true;
        if (settings.LearnFromWispr == on) return;
        settings.LearnFromWispr = on;
        SettingsStore.Instance.Save();
    }

    private void CancelRun()
    {
        if (_run == null) return;
        _run.Cancel();
        SecondaryButton.IsEnabled = false;
        StageText.Text = "Stopping…";
        DetailText.Text = "";
    }

    private void ShowRunning(LearningProgress first)
    {
        _stage = Stage.Running;
        HeaderTitle.Text = "Reading your history…";
        HeaderText.Text = "You can keep using FlowType while this runs. Closing this window stops it, and nothing is changed.";
        SetPanel(RunningPanel);
        DismissCheck.Visibility = Visibility.Collapsed;
        PrimaryButton.Visibility = Visibility.Collapsed;
        SecondaryButton.Content = "Cancel";
        SecondaryButton.IsEnabled = true;
        SecondaryButton.Visibility = Visibility.Visible;
        _runStarted = DateTime.UtcNow;
        if (!_snapshot) _clock.Start();
        SetProgress(first);
    }

    private void SetProgress(LearningProgress p)
    {
        StageText.Text = p.Stage;
        DetailText.Text = p.Detail ?? "";
        _fraction = p.Fraction;
        LayoutProgress();
        UpdateElapsed();
    }

    private void UpdateElapsed()
    {
        var t = DateTime.UtcNow - _runStarted;
        var clock = $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        ElapsedText.Text = _fraction >= 0 ? $"{Math.Clamp(_fraction, 0, 1) * 100:0}%  ·  {clock}" : clock;
    }

    private void ProgressTrack_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutProgress();

    /// <summary>
    /// A determinate fill, or — when the stage can't say how far along it is —
    /// a short segment sliding across. The theme's ProgressBar can't do the
    /// latter: its template has no animation part, so "indeterminate" would
    /// draw a full bar that reads as "done".
    /// </summary>
    private void LayoutProgress()
    {
        var width = ProgressTrack.ActualWidth;
        if (_fraction >= 0)
        {
            StopSlide();
            ProgressFill.Width = Math.Max(0, width * Math.Clamp(_fraction, 0, 1));
            return;
        }
        if (width <= 0) return;
        var segment = Math.Max(48, width * 0.28);
        ProgressFill.Width = segment;
        if (Math.Abs(_slideWidth - width) < 0.5) return;
        _slideWidth = width;
        if (_snapshot)
        {
            ProgressSlide.X = width * 0.3;
            return;
        }
        ProgressSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
        {
            From = -segment,
            To = width,
            Duration = TimeSpan.FromSeconds(1.6),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    private void StopSlide()
    {
        _slideWidth = -1;
        ProgressSlide.BeginAnimation(TranslateTransform.XProperty, null);
        ProgressSlide.X = 0;
    }

    // ===================================================================
    // Results
    // ===================================================================

    private void ShowResults(LearningReport report, bool fromEarlierRun)
    {
        _stage = Stage.Results;
        SetPanel(ResultsPanel);
        ResultsPanel.ScrollToTop();
        ResultsList.Children.Clear();
        _rows.Clear();
        _sectionCounters.Clear();

        var settings = SettingsStore.Instance.Settings;
        var dictionary = _snapshot ? Array.Empty<DictionaryEntry>() : DictionaryStore.Instance.Entries;
        // Suggestions only: the benchmark's fixes are already merged into
        // them, past the "don't suggest again" and "already known" filters
        // (see LearnFormat.ReviewList). Anything in the dictionary or settings
        // by now (applied since this report was made, or added by hand) is
        // dropped rather than offered twice.
        var all = LearnFormat.ReviewList(report, s => !_snapshot && AlreadyKnown(s, dictionary, settings));

        var wispr = all.Where(IsFromWisprDictionary).ToList();
        var words = all.Where(s => s.Kind == SuggestionKind.Vocabulary && !wispr.Contains(s)).ToList();
        var fixes = all.Where(s => s.Kind == SuggestionKind.Correction && !wispr.Contains(s)).ToList();
        var styles = all.Where(s => s.Kind == SuggestionKind.Style && !wispr.Contains(s)).ToList();

        var when = fromEarlierRun ? $"From the check {LearnFormat.When(report.RunUtc)}. " : "";
        if (all.Count == 0)
        {
            HeaderTitle.Text = "Nothing new to learn — you're all set";
            HeaderText.Text = when + $"Read {report.SamplesRead:N0} {Ui.Plural(report.SamplesRead, "dictation", "dictations")} "
                + "— nothing new to add to your dictionary or settings.";
        }
        else
        {
            HeaderTitle.Text = "Here's what it found";
            HeaderText.Text = when + $"{all.Count:N0} {Ui.Plural(all.Count, "suggestion", "suggestions")} from "
                + $"{report.SamplesRead:N0} {Ui.Plural(report.SamplesRead, "dictation", "dictations")}. "
                + "The ticked ones are the confident ones — untick anything that looks wrong.";
        }

        AddToolbar(report);
        AddSection(Section.Words, "Words to recognise",
            "Names and terms you use that the speech model may not know. Added words are listened for while you speak.",
            words);
        AddSection(Section.Fixes, "Fixes for words it mishears",
            "When the left side comes out, the right side is written instead.", fixes);
        AddSection(Section.Wispr, "From your Wispr dictionary",
            "Words, fixes and snippets you set up in Wispr Flow.", wispr);
        AddSection(Section.Style, "Your style",
            "How you actually write in each kind of app. Only shown where it differs from your current setting.",
            styles);
        if (report.Bench is { } bench) AddBench(bench);
        if (all.Count == 0) AddEmptyCard(report);

        DismissCheck.Visibility = all.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SecondaryButton.Content = "Close";
        SecondaryButton.IsEnabled = true;
        SecondaryButton.Visibility = all.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PrimaryButton.Visibility = Visibility.Visible;
        if (all.Count == 0)
        {
            PrimaryButton.Content = "Done"; // nothing to apply: clears the waiting dot and closes
            PrimaryButton.IsEnabled = true;
        }
        else
        {
            UpdateCounts();
            // The first screenful counts as seen even if the user never scrolls.
            Dispatcher.BeginInvoke(MarkSeen, DispatcherPriority.Loaded);
        }
    }

    private void ResultsPanel_ScrollChanged(object sender, ScrollChangedEventArgs e) => MarkSeen();

    /// <summary>
    /// Mark every row whose first line is inside the results' viewport as
    /// seen. Runs on every scroll, and on every extent or viewport change —
    /// which covers "Show more" opening and the window being resized. Rows
    /// still behind "Show more" are not visible and stay unseen.
    /// </summary>
    private void MarkSeen()
    {
        if (_closed || _stage != Stage.Results || !ResultsPanel.IsVisible) return;
        var viewport = ResultsPanel.ViewportHeight;
        var changed = false;
        foreach (var r in _rows)
        {
            if (r.Seen || !r.Box.IsVisible) continue;
            Rect bounds;
            try
            {
                bounds = r.Box.TransformToAncestor(ResultsPanel).TransformBounds(new Rect(r.Box.RenderSize));
            }
            catch (InvalidOperationException)
            {
                continue; // between layouts the row may not be connected yet; the next pass gets it
            }
            if (!LearnFormat.WasSeen(bounds.Top, bounds.Height, viewport)) continue;
            r.Seen = true;
            changed = true;
        }
        if (changed) UpdateCounts();
    }

    /// <summary>
    /// The rows "Don't suggest the ones I left unticked" would turn down for
    /// good: unticked and actually seen, and only while that box is ticked.
    /// </summary>
    private List<Suggestion> DismissedRows() => DismissCheck.IsChecked == true
        ? _rows.Where(r => r.Box.IsChecked != true && r.Seen).Select(r => r.Suggestion).ToList()
        : new List<Suggestion>();

    /// <summary>
    /// Wispr's own dictionary gets its own card whatever kind each entry is:
    /// the user recognises those as "the things I typed into Wispr", not as
    /// words and fixes FlowType inferred.
    /// </summary>
    private static bool IsFromWisprDictionary(Suggestion s) =>
        s.Kind == SuggestionKind.Snippet
        || s.Origin.Contains("Wispr Flow dictionary", StringComparison.OrdinalIgnoreCase);

    /// <summary>Already applied (or added by hand) since the report was made.</summary>
    private static bool AlreadyKnown(Suggestion s, IReadOnlyList<DictionaryEntry> dictionary, AppSettings settings)
    {
        switch (s.Kind)
        {
            case SuggestionKind.Style:
                return string.Equals(LearnFormat.CurrentStyle(s.Phrase, settings), s.Replacement.Trim(),
                    StringComparison.OrdinalIgnoreCase);
            case SuggestionKind.Snippet:
                return dictionary.Any(e => e.IsSnippet
                    && e.Phrase.Trim().Equals(s.Phrase.Trim(), StringComparison.OrdinalIgnoreCase));
            case SuggestionKind.Vocabulary:
                return dictionary.Any(e => !e.IsSnippet && e.IsVocabularyOnly
                    && e.Phrase.Trim().Equals(s.Phrase.Trim(), StringComparison.OrdinalIgnoreCase));
            default:
                return dictionary.Any(e => !e.IsSnippet && !e.IsVocabularyOnly
                    && e.Phrase.Trim().Equals(s.Phrase.Trim(), StringComparison.OrdinalIgnoreCase)
                    && e.Replacement.Trim().Equals(s.Replacement.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Notes from the run (left) and "Check again" (right).</summary>
    private void AddToolbar(LearningReport report)
    {
        var grid = Ui.Columns(Ui.Star, Ui.Auto);
        grid.Margin = new Thickness(0, 0, 0, 12);

        var notes = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var note in report.Notes.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            var line = Ui.Columns(Ui.Auto, Ui.Star);
            line.Margin = new Thickness(2, 2, 0, 2);
            var dot = Ui.Dot("FaintBrush", 5);
            dot.VerticalAlignment = VerticalAlignment.Top;
            dot.Margin = new Thickness(0, 7, 10, 0);
            line.Children.Add(dot.At(0));
            line.Children.Add(new TextBlock { Text = note, Style = Ui.StyleOf("Hint"), FontSize = 12.5 }.At(1));
            notes.Children.Add(line);
        }
        grid.Children.Add(notes.At(0));

        var again = Ui.IconButton("IconRetry", "Check again", "Go back and run a fresh check");
        again.Margin = new Thickness(12, 0, 0, 0);
        again.VerticalAlignment = VerticalAlignment.Top;
        again.Click += (_, _) => ShowReady();
        grid.Children.Add(again.At(1));
        ResultsList.Children.Add(grid);
    }

    private void AddSection(Section section, string title, string hint, IReadOnlyList<Suggestion> items)
    {
        if (items.Count == 0) return;

        var body = new StackPanel();
        var header = Ui.Columns(Ui.Star, Ui.Auto);
        header.Margin = new Thickness(0, 0, 0, 10);
        var heading = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        heading.Children.Add(new TextBlock { Text = title, Style = Ui.StyleOf("SectionTitle"), TextWrapping = TextWrapping.Wrap });
        heading.Children.Add(new TextBlock { Text = hint, Style = Ui.StyleOf("Hint"), Margin = new Thickness(0, 4, 0, 0) });
        header.Children.Add(heading.At(0));

        var count = new TextBlock
        {
            FontSize = 12.5,
            Foreground = Ui.BrushOf("FaintBrush"),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 2, 6, 2),
        };
        var all = Ui.TextButton("All", "ChipButton", 0);
        var none = Ui.TextButton("None", "ChipButton", 2);
        all.ToolTip = $"Tick everything in “{title}”";
        none.ToolTip = $"Untick everything in “{title}”";
        var tools = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        tools.Children.Add(count);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(all);
        buttons.Children.Add(none);
        tools.Children.Add(buttons);
        header.Children.Add(tools.At(1));
        body.Children.Add(header);

        // Ticked rows and the first few are shown; the rest wait behind a link.
        // None is "seen" yet: that is decided by what actually scrolls into view.
        var shown = new StackPanel();
        var more = new StackPanel { Visibility = Visibility.Collapsed };
        var sectionRows = new List<Row>();
        var hidden = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var s = items[i];
            var visible = s.Recommended || i < FirstRows;
            var row = BuildRow(s, section);
            sectionRows.Add(row);
            _rows.Add(row);
            if (!visible) hidden++;
            var target = visible ? shown : more;
            if (target.Children.Count > 0) target.Children.Add(Separator());
            target.Children.Add(DictionaryPage.HoverRow(row.Box));
        }
        body.Children.Add(shown);

        Button? showMore = null;
        if (more.Children.Count > 0)
        {
            body.Children.Add(more);
            showMore = Ui.TextButton($"Show {hidden:N0} more", "LinkButton", 0);
            showMore.FontSize = 13.5;
            showMore.HorizontalAlignment = HorizontalAlignment.Left;
            showMore.Margin = new Thickness(14, 8, 0, 4);
            body.Children.Add(showMore);
        }

        void Expand()
        {
            if (showMore == null || more.Visibility == Visibility.Visible) return;
            more.Visibility = Visibility.Visible;
            showMore.Visibility = Visibility.Collapsed;
            // The first hidden row continues the list, so it needs a rule above it.
            shown.Children.Add(Separator());
            // Not marked seen here: the rows it reveals count once they scroll
            // into view (the extent change raises ScrollChanged → MarkSeen).
        }
        if (showMore != null) showMore.Click += (_, _) => Expand();

        all.Click += (_, _) =>
        {
            // Ticking what the user can't see would apply it unseen.
            Expand();
            foreach (var r in sectionRows) r.Box.IsChecked = true;
        };
        none.Click += (_, _) =>
        {
            foreach (var r in sectionRows) r.Box.IsChecked = false;
        };

        void UpdateSectionCount()
        {
            var ticked = sectionRows.Count(r => r.Box.IsChecked == true);
            count.Text = $"{ticked:N0} of {sectionRows.Count:N0} ticked";
        }
        _sectionCounters.Add(UpdateSectionCount);

        ResultsList.Children.Add(new Border
        {
            Style = Ui.StyleOf("Card"),
            Padding = new Thickness(20, 18, 16, 10),
            Child = body,
        });
    }

    private static Border Separator() => new()
    {
        Height = 1,
        Background = Ui.BrushOf("StrokeBrush"),
        Margin = new Thickness(14, 0, 14, 0),
    };

    /// <summary>One suggestion: a checkbox whose label is the term, the reason, and a tag where the source varies.</summary>
    private Row BuildRow(Suggestion s, Section section)
    {
        var grid = Ui.Columns(Ui.Star, Ui.Auto);
        var text = new StackPanel { Margin = new Thickness(0, 0, 4, 0) };
        text.Children.Add(TermLine(s));
        var why = string.IsNullOrWhiteSpace(s.Why) ? LearnFormat.Capitalise(s.Origin) : s.Why;
        if (why.Length > 0)
        {
            text.Children.Add(new TextBlock
            {
                Text = why,
                FontSize = 12.5,
                Foreground = Ui.BrushOf("FaintBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }
        grid.Children.Add(text.At(0));

        var tag = section switch
        {
            Section.Wispr => s.Kind switch
            {
                SuggestionKind.Vocabulary => "Word",
                SuggestionKind.Snippet => "Snippet",
                SuggestionKind.Style => "Style",
                _ => "Fix",
            },
            Section.Fixes => OriginTag(s.Origin),
            _ => null,
        };
        if (tag != null)
        {
            var chip = Ui.Chip(tag);
            chip.VerticalAlignment = VerticalAlignment.Top;
            chip.Margin = new Thickness(8, 1, 0, 0);
            grid.Children.Add(chip.At(1));
        }

        var box = new CheckBox
        {
            Content = grid,
            IsChecked = s.Recommended,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ToolTip = TooltipFor(s),
        };
        System.Windows.Automation.AutomationProperties.SetName(box, s.Kind switch
        {
            SuggestionKind.Vocabulary => s.Phrase,
            SuggestionKind.Style => $"{LearnFormat.BucketLabel(s.Phrase)}: {LearnFormat.StyleLabel(s.Replacement)}",
            _ => $"{s.Phrase} becomes {s.Replacement}",
        });
        box.Checked += (_, _) => UpdateCounts();
        box.Unchecked += (_, _) => UpdateCounts();
        return new Row(s, box);
    }

    /// <summary>"Your edits" / "Wispr's recogniser" / "Your recordings" — where a fix came from matters when judging it.</summary>
    private static string? OriginTag(string origin)
    {
        var o = origin.Trim();
        return o.Length == 0 ? null : LearnFormat.Capitalise(o);
    }

    /// <summary>The term itself: a word, "heard → written", or "bucket: now → suggested".</summary>
    private TextBlock TermLine(Suggestion s)
    {
        var line = new TextBlock { FontSize = 15, TextWrapping = TextWrapping.Wrap };
        switch (s.Kind)
        {
            case SuggestionKind.Vocabulary:
                line.Inlines.Add(new Run(s.Phrase) { Foreground = Ui.BrushOf("TextBrush") });
                break;
            case SuggestionKind.Style:
                var current = CurrentStyleFor(s.Phrase);
                line.Inlines.Add(new Run(LearnFormat.BucketLabel(s.Phrase)) { Foreground = Ui.BrushOf("TextBrush") });
                line.Inlines.Add(new Run("   ") { Foreground = Ui.BrushOf("FaintBrush") });
                if (current.Length > 0)
                {
                    line.Inlines.Add(new Run(LearnFormat.StyleLabel(current)) { Foreground = Ui.BrushOf("MutedBrush") });
                    line.Inlines.Add(new Run("   →   ") { Foreground = Ui.BrushOf("FaintBrush") });
                }
                line.Inlines.Add(new Run(LearnFormat.StyleLabel(s.Replacement)) { Foreground = Ui.BrushOf("TextBrush") });
                break;
            default:
                line.Inlines.Add(new Run(s.Phrase) { Foreground = Ui.BrushOf("MutedBrush") });
                line.Inlines.Add(new Run("   →   ") { Foreground = Ui.BrushOf("FaintBrush") });
                line.Inlines.Add(new Run(Shorten(s.Replacement)) { Foreground = Ui.BrushOf("TextBrush") });
                break;
        }
        return line;
    }

    private string CurrentStyleFor(string bucketId) =>
        _snapshot ? "formal" : LearnFormat.CurrentStyle(bucketId, SettingsStore.Instance.Settings);

    /// <summary>A snippet can be a whole signature; the row shows its start on one logical line.</summary>
    private static string Shorten(string text)
    {
        var flat = string.Join(" ", text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (flat.Length == 0) return $"“{text}” (space)";
        return flat.Length <= 90 ? flat : flat[..87].TrimEnd() + "…";
    }

    /// <summary>The example excerpt (and a long snippet in full), in a wrapping tooltip.</summary>
    private static object? TooltipFor(Suggestion s)
    {
        var parts = new List<string>();
        if (s.Kind == SuggestionKind.Snippet && Shorten(s.Replacement) != s.Replacement.Trim()) parts.Add(s.Replacement.Trim());
        if (!string.IsNullOrWhiteSpace(s.Example)) parts.Add($"“{s.Example.Trim()}”");
        if (parts.Count == 0) return null;
        return new TextBlock { Text = string.Join("\n\n", parts), TextWrapping = TextWrapping.Wrap, MaxWidth = 380 };
    }

    /// <summary>
    /// "How well FlowType hears you": the before/after numbers, and what they
    /// were measured against. The agreement figure is against Wispr Flow's
    /// raw recogniser transcript, so the card names that reference and says
    /// a learned spelling can lower it (see <see cref="LearnFormat.BenchIntro"/>);
    /// the term tile counts the user's own final text and is the fairer
    /// before/after.
    /// </summary>
    private void AddBench(BenchResult b)
    {
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = "How well FlowType hears you", Style = Ui.StyleOf("SectionTitle") });
        body.Children.Add(new TextBlock
        {
            Text = LearnFormat.BenchIntro(b.Recordings, b.AudioSeconds),
            Style = Ui.StyleOf("Hint"),
            Margin = new Thickness(0, 4, 0, 14),
        });

        var same = Math.Abs(b.LearnedAccuracy - b.BaselineAccuracy) < 0.0005;
        var tiles = Ui.Columns(Ui.Star, new GridLength(12), Ui.Star);
        tiles.Children.Add(Tile(LearnFormat.AgreementTitle,
            Percent(b.BaselineAccuracy), same ? null : Percent(b.LearnedAccuracy),
            LearnFormat.AgreementCaption(changed: !same)).At(0));
        tiles.Children.Add(b.TermOccurrences > 0
            ? Tile(LearnFormat.TermsTitle,
                $"{b.TermHitsBaseline:N0} of {b.TermOccurrences:N0}",
                $"{b.TermHitsLearned:N0} of {b.TermOccurrences:N0}",
                LearnFormat.TermsCaption).At(2)
            : Tile(LearnFormat.TermsTitle, "—", null,
                "None of the suggested words came up in these recordings.").At(2));
        body.Children.Add(tiles);

        var details = new List<string>();
        if (b.LiveChunks > 0) details.Add(LearnFormat.LiveChunksLine(b.LiveChunks, b.ForcedCuts));
        var model = ModelCatalog.ById(b.ModelId)?.DisplayName ?? b.ModelId;
        var speed = b.RealTimeFactor switch
        {
            <= 0 => "",
            < 1 => $", {1 / b.RealTimeFactor:0}× faster than real time",
            _ => $", {b.RealTimeFactor:0.0}× slower than real time",
        };
        if (model.Length > 0) details.Add($"Speech model: {model}{speed}.");
        if (details.Count > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = string.Join(" ", details),
                Style = Ui.StyleOf("Hint"),
                FontSize = 12.5,
                Foreground = Ui.BrushOf("FaintBrush"),
                Margin = new Thickness(0, 4, 0, 0),
            });
        }

        ResultsList.Children.Add(new Border { Style = Ui.StyleOf("Card"), Padding = new Thickness(20, 18, 20, 16), Child = body });
    }

    private static string Percent(double fraction) => $"{Math.Clamp(fraction, 0, 1) * 100:0.0}%";

    /// <summary>A small stat: eyebrow, "before → after" (or one value), caption.</summary>
    private static Border Tile(string eyebrow, string before, string? after, string caption)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = eyebrow, Style = Ui.StyleOf("Eyebrow"), FontSize = 11, TextWrapping = TextWrapping.Wrap });
        var value = new TextBlock
        {
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("DisplayFont"),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        if (after == null)
        {
            value.Inlines.Add(new Run(before) { FontSize = 26, Foreground = Ui.BrushOf("TextBrush") });
        }
        else
        {
            value.Inlines.Add(new Run(before) { FontSize = 20, Foreground = Ui.BrushOf("MutedBrush") });
            value.Inlines.Add(new Run("  →  ") { FontSize = 18, Foreground = Ui.BrushOf("FaintBrush") });
            value.Inlines.Add(new Run(after) { FontSize = 26, Foreground = Ui.BrushOf("TextBrush") });
        }
        stack.Children.Add(value);
        stack.Children.Add(new TextBlock
        {
            Text = caption,
            Style = Ui.StyleOf("Hint"),
            FontSize = 12.5,
            LineHeight = 18,
            Margin = new Thickness(0, 6, 0, 0),
        });
        return new Border { Style = Ui.StyleOf("InsetRow"), Margin = new Thickness(0), Padding = new Thickness(16, 14, 16, 14), Child = stack };
    }

    private void AddEmptyCard(LearningReport report)
    {
        // The header already says "nothing new"; the card says why, so it isn't read as broken.
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = report.SamplesRead == 0
                ? "There were no past dictations to read. Try again after you've used FlowType for a while."
                : "A word, fix or style is only suggested once it shows up often enough to be sure — usually "
                    + "across several dictations. Try again after you've dictated more.",
            Style = Ui.StyleOf("Hint"),
            FontSize = 14,
            LineHeight = 21,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 480,
        });
        ResultsList.Children.Add(new Border { Style = Ui.StyleOf("Card"), Padding = new Thickness(28, 30, 28, 30), Child = stack });
    }

    /// <summary>
    /// Section counts and the footer button, after any tick, "seen" or
    /// "don't suggest" change. The button stays enabled with nothing ticked
    /// (see <see cref="LearnFormat.ReviewButton"/>): it is the only way to
    /// turn everything down, and to clear the waiting dot without applying
    /// something unwanted.
    /// </summary>
    private void UpdateCounts()
    {
        foreach (var update in _sectionCounters) update();
        if (_stage != Stage.Results || _rows.Count == 0) return;
        var ticked = _rows.Count(r => r.Box.IsChecked == true);
        PrimaryButton.Content = LearnFormat.ReviewButton(ticked, ticked > 0 ? 0 : DismissedRows().Count);
        PrimaryButton.IsEnabled = true;
    }

    /// <summary>
    /// The user has been through the report without adding anything: let
    /// <see cref="HistoryLearner.Apply"/> with nothing chosen and nothing
    /// dismissed reset the pending count, so the Insights dot and "Review N
    /// suggestions" stop pointing at a review that is over. Best effort — the
    /// window is closing either way, and the next run recounts.
    /// </summary>
    private void ClearPending()
    {
        if (_snapshot) return;
        try
        {
            var learner = HistoryLearner.Instance;
            if (learner.PendingCount > 0) learner.Apply(Array.Empty<Suggestion>(), Array.Empty<Suggestion>());
        }
        catch (Exception)
        {
            // learning.json couldn't be saved; the dot stays until the next run.
        }
    }

    private void Apply()
    {
        // Whatever is on screen right now has been seen, even if no scroll event said so.
        MarkSeen();
        var chosen = _rows.Where(r => r.Box.IsChecked == true).Select(r => r.Suggestion).ToList();
        var dismissed = DismissedRows();
        if (_snapshot) return;
        if (chosen.Count == 0 && dismissed.Count == 0)
        {
            // "Done": nothing to add and nothing to turn down — no point in a
            // second screen that says so. The review is over, though.
            ClearPending();
            Close();
            return;
        }

        // With nothing chosen this only records the dismissals and resets the
        // pending count; ShowApplied then says "Nothing was added. The N you
        // left unticked won't be suggested again."
        ApplyResult result;
        try
        {
            result = HistoryLearner.Instance.Apply(chosen, dismissed);
        }
        catch (Exception ex)
        {
            Ui.Message(this, $"Couldn't apply the suggestions:\n{ex.Message}", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ShowApplied(result, dismissed.Count);
    }

    // ===================================================================
    // Applied
    // ===================================================================

    private void ShowApplied(ApplyResult result, int dismissed)
    {
        _stage = Stage.Applied;
        SetPanel(AppliedPanel);
        DismissCheck.Visibility = Visibility.Collapsed;
        SecondaryButton.Visibility = Visibility.Collapsed;
        PrimaryButton.Content = "Done";
        PrimaryButton.IsEnabled = true;
        PrimaryButton.Visibility = Visibility.Visible;

        HeaderTitle.Text = result.Total > 0 ? "Done — FlowType has learned from your writing" : "Done";
        HeaderText.Text = result.Total > 0 ? "It uses these from your next dictation." : "";

        AppliedText.Text = AppliedSentence(result, dismissed);
        var where = new List<string>();
        if (result.Words + result.Fixes > 0) where.Add("They're in Dictionary, marked “From history”.");
        if (result.Snippets > 0) where.Add($"{(result.Words + result.Fixes > 0 ? "Snippets" : "They")} are on the Snippets page.");
        if (result.Styles > 0) where.Add("Styles are on the Style page.");
        if (dismissed > 0 && result.Total > 0)
        {
            where.Add($"The {dismissed:N0} you left unticked won't be suggested again.");
        }
        AppliedWhere.Text = string.Join(" ", where);
        AppliedWhere.Visibility = where.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var canNavigate = _snapshot || Owner is MainWindow;
        OpenDictionaryLink.Visibility = canNavigate && result.Words + result.Fixes > 0 ? Visibility.Visible : Visibility.Collapsed;
        OpenSnippetsLink.Visibility = canNavigate && result.Snippets > 0 ? Visibility.Visible : Visibility.Collapsed;
        OpenStyleLink.Visibility = canNavigate && result.Styles > 0 ? Visibility.Visible : Visibility.Collapsed;
        AppliedLinks.Visibility = canNavigate && result.Total > 0 ? Visibility.Visible : Visibility.Collapsed;
        AppliedUndoHint.Visibility = result.Words + result.Fixes + result.Snippets > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>"Added 18 words, 3 fixes and 2 snippets, and changed 1 style."</summary>
    private static string AppliedSentence(ApplyResult r, int dismissed)
    {
        var added = new List<string>();
        if (r.Words > 0) added.Add($"{r.Words:N0} {Ui.Plural(r.Words, "word", "words")}");
        if (r.Fixes > 0) added.Add($"{r.Fixes:N0} {Ui.Plural(r.Fixes, "fix", "fixes")}");
        if (r.Snippets > 0) added.Add($"{r.Snippets:N0} {Ui.Plural(r.Snippets, "snippet", "snippets")}");
        var styles = r.Styles > 0 ? $"changed {r.Styles:N0} {Ui.Plural(r.Styles, "style", "styles")}" : null;

        if (added.Count == 0 && styles == null)
        {
            return dismissed > 0
                ? $"Nothing was added. The {dismissed:N0} you left unticked won't be suggested again."
                : "Nothing was added.";
        }
        var list = added.Count switch
        {
            0 => "",
            1 => added[0],
            _ => string.Join(", ", added.Take(added.Count - 1)) + " and " + added[^1],
        };
        if (list.Length == 0) return LearnFormat.Capitalise(styles!) + ".";
        return styles == null ? $"Added {list}." : $"Added {list}, and {styles}.";
    }

    // ===================================================================
    // Snapshot (hidden diagnostic)
    // ===================================================================

    /// <summary>
    /// Render every state to learn-*.png from made-up data
    /// (<see cref="LearnSamples"/>), off-screen, plus the Insights card in
    /// each of its states (insights-learn-*.png). Never reads or runs the
    /// learner, so it works — and shows nothing private — on any machine.
    /// </summary>
    public static void SnapshotAll(string dir)
    {
        Directory.CreateDirectory(dir);
        var window = new LearnWindow(snapshot: true)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.Show();
        try
        {
            window.SnapshotStates(dir);
        }
        finally
        {
            window.Close();
        }
        InsightsPage.SnapshotLearnCard(dir);
    }

    private void SnapshotStates(string dir)
    {
        var root = (FrameworkElement)Content;
        var bg = (Brush)FindResource("BgBrush");
        void Save(string name)
        {
            UpdateLayout();
            Snapshot.Save(root, System.IO.Path.Combine(dir, $"learn-{name}.png"), bg);
        }

        _sources = LearnSamples.Sources();
        _useWispr = true;
        _snapshotModelReady = true;
        ShowReady();
        Save("ready");

        // Wispr found but not ticked: the benchmark is off and says why.
        _useWispr = false;
        ShowReady();
        Save("ready-wispr-off");

        _sources = LearnSamples.Sources(wisprFound: false);
        _useWispr = null;
        _snapshotModelReady = false;
        ShowReady("Stopped. Nothing was changed.");
        Save("ready-nowispr");

        ShowRunning(new LearningProgress("Testing on your recordings", 0.42, "Recording 9 of 21 · first pass"));
        _runStarted = DateTime.UtcNow.AddSeconds(-72);
        UpdateElapsed();
        Save("running");
        _runStarted = DateTime.UtcNow.AddSeconds(-3);
        SetProgress(new LearningProgress("Reading Wispr Flow's history", -1));
        Save("running-indeterminate");
        StopSlide();

        ShowResults(LearnSamples.Report(), fromEarlierRun: false);
        Save("results");
        UpdateLayout();
        Snapshot.Save(ResultsList, System.IO.Path.Combine(dir, "learn-results-full.png"), bg);

        ShowResults(LearnSamples.Report(), fromEarlierRun: true);
        foreach (var r in _rows.Where(r => r.Suggestion.Kind == SuggestionKind.Vocabulary).Skip(2).Take(3))
        {
            r.Box.IsChecked = false;
        }
        Save("results-pending");

        // Everything unticked: the footer offers to turn them down rather than greying out.
        ShowResults(LearnSamples.Report(), fromEarlierRun: true);
        UpdateLayout();
        MarkSeen();
        foreach (var r in _rows) r.Box.IsChecked = false;
        Save("results-none-ticked");

        ShowResults(LearnSamples.EmptyReport(), fromEarlierRun: false);
        Save("results-empty");

        ShowApplied(new ApplyResult(18, 3, 2, 1), 4);
        Save("applied");
        ShowApplied(new ApplyResult(0, 0, 0, 0), 5);
        Save("applied-nothing");

        // The smallest the window may be: rows must still wrap, not clip.
        Width = MinWidth;
        Height = MinHeight;
        ShowResults(LearnSamples.Report(), fromEarlierRun: false);
        Save("results-narrow");
        _sources = LearnSamples.Sources();
        _useWispr = true;
        _snapshotModelReady = true;
        ShowReady();
        Save("ready-narrow");
    }

    // ===================================================================
    // Self-test (--selftest)
    // ===================================================================

    private delegate void Checker(string name, bool ok, string detail = "");

    /// <summary>
    /// The --selftest vectors for the review window: which suggestions it
    /// shows, what the footer button does with nothing ticked, which rows
    /// count as seen (and so can be turned down for good), what the benchmark
    /// option is sized from, and how the benchmark card names what it compares
    /// with. Pure checks on <see cref="LearnFormat"/> first, then the same
    /// behaviour on a real window in snapshot mode — laid out off-screen,
    /// never activated, and in that mode it never reads or runs the learner,
    /// so nothing the user has is touched. Synthetic data only; logged lines
    /// carry counts, never text from anyone's history. Every "must fire"
    /// check has a "must not fire" neighbour.
    /// </summary>
    internal static bool SelfTest(StringBuilder log)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess()) return dispatcher.Invoke(() => SelfTest(log));

        var pass = 0;
        var total = 0;
        void Check(string name, bool ok, string detail = "")
        {
            total++;
            if (ok) pass++;
            log.AppendLine($"learn window {(ok ? "PASS" : "FAIL")}: {name}" + (detail.Length > 0 ? $" [{detail}]" : ""));
        }

        ReviewChecks(Check);
        BenchChecks(Check);
        if (dispatcher != null) WindowChecks(Check);
        else Check("window: needs a WPF application", false);

        log.AppendLine($"learn window: {pass}/{total} passed");
        return pass == total;
    }

    private static Suggestion TestFix(string heard, string write) =>
        new(SuggestionKind.Correction, Suggestion.KeyFor(SuggestionKind.Correction, heard, write),
            heard, write, 2, 2, true, "", "your recordings");

    /// <summary>
    /// A benchmark run as HistoryLearner leaves it: "and fusion" was merged
    /// into the suggestions (listed twice, to check each key shows once); the
    /// user had dismissed "clawed", so the learner dropped it — but the raw
    /// benchmark list still carries both.
    /// </summary>
    private static LearningReport TestBenchReport()
    {
        var merged = TestFix("and fusion", "Enfusion");
        var dropped = TestFix("clawed", "Claude");
        var word = new Suggestion(SuggestionKind.Vocabulary, Suggestion.KeyFor(SuggestionKind.Vocabulary, "Reforger"),
            "Reforger", "", 5, 3, true, "", "your writing");
        var bench = new BenchResult(3, 120, 400, 0.90, 0.91, 4, 1, 3, 6, 1, 0.05, "test", DateTime.UtcNow,
            new[] { merged, dropped });
        return new LearningReport(DateTime.UtcNow, Array.Empty<SourceSummary>(), 10, new[] { word, merged, merged },
            bench, TimeSpan.Zero, Array.Empty<string>());
    }

    private static void ReviewChecks(Checker check)
    {
        // What the review lists (review #20).
        var list = LearnFormat.ReviewList(TestBenchReport(), _ => false);
        check("review: a bench fix merged into the suggestions shows once",
            list.Count(s => s.Phrase == "and fusion") == 1, $"{list.Count} rows");
        check("review: a bench fix the learner dropped (dismissed) stays out",
            !list.Any(s => s.Phrase == "clawed"), $"{list.Count} rows");
        var known = LearnFormat.ReviewList(TestBenchReport(), s => s.Kind == SuggestionKind.Vocabulary);
        check("review: already-known rows dropped, the rest kept",
            known.Count == 1 && known[0].Kind == SuggestionKind.Correction, $"{known.Count} rows");

        // The footer button with nothing ticked (review #22).
        check("button: something ticked applies it", LearnFormat.ReviewButton(2, 0) == "Apply 2 selected");
        check("button: nothing ticked, seen rows unticked → turn them down",
            LearnFormat.ReviewButton(0, 3) == "Don't suggest these");
        check("button: nothing ticked, nothing to turn down → Done", LearnFormat.ReviewButton(0, 0) == "Done");

        // What counts as seen (review #26). Viewport 450 px, rows 60 px.
        check("seen: row at the top of the viewport", LearnFormat.WasSeen(0, 60, 450));
        check("seen: first line just inside the bottom edge", LearnFormat.WasSeen(420, 60, 450));
        check("seen: a sliver at the bottom edge is not", !LearnFormat.WasSeen(440, 60, 450));
        check("seen: a row below the fold is not", !LearnFormat.WasSeen(2400, 60, 450));
        check("seen: a row wholly above the viewport is not (this pass)", !LearnFormat.WasSeen(-200, 60, 450));
        check("seen: a short row wholly inside", LearnFormat.WasSeen(100, 18, 450));
        check("seen: a row with no height (collapsed) never is", !LearnFormat.WasSeen(0, 0, 450));
    }

    private static void BenchChecks(Checker check)
    {
        // What the benchmark option is sized from (review #27). Samples:
        // FlowType keeps 9 recordings (290 s), Wispr 21 (1932 s).
        var sources = LearnSamples.Sources();
        var (wispr, wisprSeconds) = LearnFormat.BenchAudio(sources, useWispr: true, BenchMaxRecordings);
        check("bench audio: Wispr ticked counts Wispr's recordings only",
            wispr == 21 && Math.Abs(wisprSeconds - 1932) < 0.01, $"{wispr} rec, {wisprSeconds:0} s");
        var (flowTypeOnly, _) = LearnFormat.BenchAudio(sources, useWispr: false, BenchMaxRecordings);
        check("bench audio: FlowType's recordings alone offer no test", flowTypeOnly == 0, $"{flowTypeOnly} rec");
        var (missing, _) = LearnFormat.BenchAudio(LearnSamples.Sources(wisprFound: false), useWispr: true, BenchMaxRecordings);
        check("bench audio: no Wispr history, nothing to test", missing == 0, $"{missing} rec");
        var many = new[] { new SourceSummary(HistorySource.WisprFlow, true, 500, 80, 8000, 0, null, "") };
        var (capped, cappedSeconds) = LearnFormat.BenchAudio(many, useWispr: true, BenchMaxRecordings);
        check("bench audio: capped at 40, audio scaled with it",
            capped == 40 && Math.Abs(cappedSeconds - 4000) < 0.01, $"{capped} rec, {cappedSeconds:0} s");

        var off = LearnFormat.BenchHint(true, sources, useWispr: false, 0);
        check("bench hint: Wispr found but unticked says to tick it", off.StartsWith("Tick Wispr Flow history"));
        var none = LearnFormat.BenchHint(true, LearnSamples.Sources(wisprFound: false), useWispr: false, 0);
        check("bench hint: no Wispr history says it is needed", none.StartsWith("Needs Wispr Flow's history"));
        var on = LearnFormat.BenchHint(true, sources, useWispr: true, 21);
        check("bench hint: Wispr ticked names its recordings and raw transcript",
            on.Contains("21 of your Wispr Flow recordings") && on.Contains("Wispr Flow's raw transcript")
                && !on.Contains("transcript saved"));
        check("bench hint: speech model not loaded comes first",
            LearnFormat.BenchHint(false, sources, useWispr: true, 21).StartsWith("Needs the speech model"));

        // How the benchmark card words its reference (review #24).
        var intro = LearnFormat.BenchIntro(21, 1932);
        check("bench card: names Wispr Flow's raw recogniser transcript",
            intro.Contains("Wispr Flow's raw recogniser transcript") && intro.Contains("before Wispr's own fixes"));
        check("bench card: a comparison, not correctness",
            intro.Contains("not a measure of correctness")
                && LearnFormat.AgreementCaption(changed: true).Contains("Not a measure of correctness"));
        check("bench card: says learned spellings can lower it", intro.Contains("Learned spellings can lower it"));
        check("bench card: no \"what matters is the change\", no accuracy claim",
            !intro.Contains("what matters", StringComparison.OrdinalIgnoreCase)
                && !LearnFormat.AgreementTitle.Contains("ACCURA") && LearnFormat.AgreementTitle.Contains("WISPR"));
        check("bench card: term tile counts the times you said a word",
            LearnFormat.TermsCaption.Contains("times you said a suggested word") && LearnFormat.TermsCaption.Contains("written right")
                && !LearnFormat.TermsTitle.Contains("SPELLED"));
        var live = LearnFormat.LiveChunksLine(54, 3);
        check("bench card: live line names the Settings switch",
            live.Contains("Transcribe while you talk") && live.Contains("54 pieces") && !live.Contains("Live typing"));
    }

    private static void WindowChecks(Checker check)
    {
        var w = new LearnWindow(snapshot: true)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        w.Show();
        try
        {
            // Review #20, on the real window.
            w.ShowResults(TestBenchReport(), fromEarlierRun: false);
            check("window: a merged bench fix shows once", w._rows.Count(r => r.Suggestion.Phrase == "and fusion") == 1,
                $"{w._rows.Count} rows");
            check("window: never re-adds a bench fix the learner dropped", !w._rows.Any(r => r.Suggestion.Phrase == "clawed"));

            // Review #26: seen = actually scrolled into view.
            w.ShowResults(LearnSamples.Report(), fromEarlierRun: false);
            w.UpdateLayout();
            w.MarkSeen();
            var first = w._rows[0];
            var styles = w._rows.Where(r => r.Suggestion.Kind == SuggestionKind.Style).ToList();
            var behindMore = w._rows.Where(r => !r.Box.IsVisible).ToList();
            var seenNow = w._rows.Count(r => r.Seen);
            check("window seen: the first screenful is seen", first.Seen, $"{seenNow} of {w._rows.Count} seen");
            check("window seen: rows below the fold are not", styles.Count > 0 && styles.All(r => !r.Seen),
                $"{styles.Count(r => r.Seen)} of {styles.Count} style rows seen");

            first.Box.IsChecked = false;
            var below = styles[0];
            below.Box.IsChecked = false;
            var dismissed = w.DismissedRows();
            check("window dismiss: an unticked row the user saw is turned down", dismissed.Contains(first.Suggestion));
            check("window dismiss: an unticked row never scrolled to is not", !dismissed.Contains(below.Suggestion));
            check("window dismiss: rows behind Show more are not",
                behindMore.Count > 0 && !behindMore.Any(r => dismissed.Contains(r.Suggestion)), $"{behindMore.Count} hidden");

            below.Box.BringIntoView();
            w.UpdateLayout();
            w.MarkSeen();
            check("window seen: scrolling a row into view marks it", below.Seen);
            check("window seen: rows still behind Show more stay unseen", behindMore.All(r => !r.Seen));
            check("window dismiss: once seen, the unticked row is turned down", w.DismissedRows().Contains(below.Suggestion));
            w.DismissCheck.IsChecked = false;
            check("window dismiss: nothing is turned down with the box unticked", w.DismissedRows().Count == 0);
            w.DismissCheck.IsChecked = true;

            // Review #22: the footer with nothing ticked.
            foreach (var r in w._rows) r.Box.IsChecked = false;
            check("window button: nothing ticked is still enabled and turns them down",
                w.PrimaryButton.IsEnabled && Equals(w.PrimaryButton.Content, "Don't suggest these"));
            w.DismissCheck.IsChecked = false;
            check("window button: …or is just Done with the box unticked",
                w.PrimaryButton.IsEnabled && Equals(w.PrimaryButton.Content, "Done"));
            w.DismissCheck.IsChecked = true;
            first.Box.IsChecked = true;
            check("window button: one ticked applies it", Equals(w.PrimaryButton.Content, "Apply 1 selected"));

            // Review #27, on the real window.
            w._sources = LearnSamples.Sources();
            w._snapshotModelReady = true;
            w._useWispr = false;
            w.ShowReady();
            check("window bench: off while Wispr is unticked", !w.BenchCheck.IsEnabled && w.BenchHint.Text.StartsWith("Tick"));
            w._useWispr = true;
            w.ShowReady();
            check("window bench: on with Wispr ticked, sized from Wispr's audio only",
                w.BenchCheck.IsEnabled && w.BenchLabel.Text.EndsWith("32 min of audio"));
        }
        finally
        {
            w.Close();
        }
    }
}
