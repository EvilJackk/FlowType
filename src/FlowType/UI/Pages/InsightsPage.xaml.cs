using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FlowType.Core;
using FlowType.Learning;
using FlowType.Session;
using FlowType.UI.Controls;
using Brush = System.Windows.Media.Brush;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace FlowType.UI.Pages;

/// <summary>
/// The usage dashboard. Every number here is measured on this machine —
/// there is deliberately no "top N% of users" figure, because FlowType
/// cannot see any other users and inventing a percentile would be a lie.
/// The comparison shown instead is against typing speed, which is a real
/// number the user can check.
/// </summary>
public partial class InsightsPage : IMainPage
{
    private const double CellSize = 14;
    private const double CellGap = 4;
    private const double DayLabelWidth = 36;

    private bool _shown;
    private bool _refreshQueued;
    private int _heatWeeks;

    public InsightsPage()
    {
        InitializeComponent();
        StatsStore.Instance.Changed += OnDataChanged;
        NotesStore.Instance.Changed += OnDataChanged;
        HistoryLearner.Instance.Changed += OnLearnerChanged;
        DictionaryStore.Instance.Changed += OnLearnInputsChanged;
        SettingsStore.Instance.Changed += OnLearnInputsChanged;

        // The grid shows as many whole weeks as fit, so it fills a wide window
        // and still fits a narrow one without a horizontal scroll bar.
        InsightHeatmap.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 1) return;
            var weeks = WeeksThatFit(e.NewSize.Width);
            if (weeks != _heatWeeks) BuildHeatmap(StatsStore.Instance, weeks);
        };
        BuildLegend();
    }

    public FrameworkElement SnapshotBody => Body;

    public void OnShown()
    {
        _shown = true;
        _learnStatus = null;
        RefreshLearnCard();
        RefreshLearnSources();
        RefreshInsights();
    }

    public void OnHidden() => _shown = false;

    public void Detach()
    {
        StatsStore.Instance.Changed -= OnDataChanged;
        NotesStore.Instance.Changed -= OnDataChanged;
        HistoryLearner.Instance.Changed -= OnLearnerChanged;
        DictionaryStore.Instance.Changed -= OnLearnInputsChanged;
        SettingsStore.Instance.Changed -= OnLearnInputsChanged;
    }

    private void OnDataChanged()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _refreshQueued = false;
            if (_shown) RefreshInsights();
        }, DispatcherPriority.Background);
    }

    private void RefreshInsights()
    {
        var stats = StatsStore.Instance;

        var wpm = Metrics.RecentWpm(NotesStore.Instance.All, stats.AverageWpm);
        InsightWpm.Text = wpm > 0 ? wpm.ToString("N0") : "—";
        InsightWpmCompare.Text = wpm > 0
            ? $"{wpm / (double)StatsStore.AverageTypingWpm:0.#}× the average typing speed of "
                + $"{StatsStore.AverageTypingWpm} wpm"
            : "Dictate something to see your pace.";
        InsightWpmBest.Text = stats.BestWpm > 0 ? $"Your best take: {stats.BestWpm:N0} wpm" : "";

        InsightFixes.Text = stats.TotalFixes.ToString("N0");
        if (stats.TotalFixes == 0 && stats.TotalUtterances > 0)
        {
            // Say why it is zero rather than looking broken: fixes have only
            // been counted since 1.6, so an existing user starts from nothing.
            InsightCorrected.Text = "Counted from version 1.6 onwards —";
            InsightDictFixes.Text = "this fills in as you dictate.";
            InsightFormatFixes.Text = "";
        }
        else
        {
            InsightCorrected.Text = $"{stats.WordsCorrected:N0} words corrected";
            InsightDictFixes.Text = $"{stats.DictionaryFixes:N0} dictionary fixes";
            InsightFormatFixes.Text = $"{stats.FormattingFixes:N0} formatting fixes";
        }

        InsightWords.Text = stats.TotalWords.ToString("N0");
        InsightTakes.Text = $"{stats.TotalUtterances:N0} {Ui.Plural(stats.TotalUtterances, "dictation", "dictations")}";
        var minutes = stats.TotalSpeakingSeconds / 60.0;
        InsightSpeaking.Text = minutes >= 60
            ? $"{minutes / 60.0:0.#} hours of speaking"
            : $"{minutes:0.#} minutes of speaking";

        InsightsSubtitle.Text = stats.TotalUtterances == 0
            ? "Nothing yet — everything here fills in as you dictate."
            : "Measured on this machine, from your own dictations. None of it leaves this PC.";

        BuildCategoryBars(stats);
        BuildTopApps(stats);
        BuildHeatmap(stats, _heatWeeks > 0 ? _heatWeeks : WeeksThatFit(InsightHeatmap.ActualWidth));
    }

    // ----- Learn from your writing -----

    /// <summary>
    /// Everything the learn card shows, gathered in one value so the card is
    /// drawn the same way from live state and from --snapshot's made-up state.
    /// </summary>
    /// <param name="Sources">What each history holds; null while still being counted.</param>
    /// <param name="SourcesProblem">Why counting failed, or null.</param>
    /// <param name="Running">A learning run (manual or the daily one) is in progress.</param>
    /// <param name="Pending">Suggestions waiting for review.</param>
    /// <param name="LastRunUtc">The last run, or null when it has never run.</param>
    /// <param name="Learned">Dictionary entries currently tagged as learned from history.</param>
    /// <param name="Daily">The daily background check is on.</param>
    /// <param name="AutoAdd">Confident new words are added without review.</param>
    internal sealed record LearnCardState(
        IReadOnlyList<SourceSummary>? Sources,
        string? SourcesProblem,
        bool Running,
        int Pending,
        DateTime? LastRunUtc,
        int Learned,
        bool Daily,
        bool AutoAdd);

    /// <summary>
    /// Source counts are cached this long. Counting opens Wispr Flow's
    /// database (read-only) — ~100 ms on a 400 MB file — which is cheap once
    /// but not on every visit to the page.
    /// </summary>
    private static readonly TimeSpan SourcesMaxAge = TimeSpan.FromMinutes(2);

    private IReadOnlyList<SourceSummary>? _learnSources;
    private string? _learnSourcesProblem;
    private DateTime _learnSourcesAt;
    private bool _learnSourcesLoading;
    private bool _learnSourcesAgain;
    private bool _learnRefreshQueued;
    private bool _learnRendering;
    private string? _learnStatus;
    private LearnCardState? _learnOverride;

    private void OnLearnerChanged() => Dispatcher.BeginInvoke(() =>
    {
        // A finished run may have read more dictations than the cached counts say.
        _learnSourcesAt = default;
        if (!_shown) return;
        RefreshLearnCard();
        RefreshLearnSources();
    }, DispatcherPriority.Background);

    /// <summary>The dictionary (learned count) or settings (switches) changed; coalesced, like the stats.</summary>
    private void OnLearnInputsChanged()
    {
        if (_learnRefreshQueued) return;
        _learnRefreshQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _learnRefreshQueued = false;
            if (_shown) RefreshLearnCard();
        }, DispatcherPriority.Background);
    }

    private void RefreshLearnCard()
    {
        if (_learnOverride != null) return;
        var learner = HistoryLearner.Instance;
        var settings = SettingsStore.Instance.Settings;
        RenderLearnCard(new LearnCardState(
            _learnSources, _learnSourcesProblem, learner.IsRunning, learner.PendingCount, learner.LastRunUtc,
            learner.LearnedEntryCount, settings.LearnInBackground, settings.AutoAddLearnedWords));
    }

    /// <summary>
    /// Count what each history holds on a worker thread and fill the line in
    /// when it arrives. The page never waits for it: until then the line says
    /// "Checking your history…".
    /// </summary>
    private async void RefreshLearnSources()
    {
        if (_learnSourcesLoading)
        {
            _learnSourcesAgain = true;
            return;
        }
        if (_learnSources != null && DateTime.UtcNow - _learnSourcesAt < SourcesMaxAge) return;

        _learnSourcesLoading = true;
        try
        {
            _learnSources = await Task.Run(() => HistoryLearner.Instance.SummarizeSources());
            _learnSourcesProblem = null;
        }
        catch (Exception)
        {
            _learnSourcesProblem = "Couldn't check your history just now.";
        }
        finally
        {
            _learnSourcesLoading = false;
            _learnSourcesAt = DateTime.UtcNow;
        }

        if (_shown) RefreshLearnCard();
        if (_learnSourcesAgain)
        {
            _learnSourcesAgain = false;
            _learnSourcesAt = default;
            if (_shown) RefreshLearnSources();
        }
    }

    private void RenderLearnCard(LearnCardState s)
    {
        _learnRendering = true;
        try
        {
            LearnSources.Text = SourcesLine(s);
            LearnLastRun.Text = _learnStatus ?? LastRunLine(s);
            LearnButton.Content = s.Pending > 0
                ? $"Review {s.Pending:N0} {Ui.Plural(s.Pending, "suggestion", "suggestions")}"
                : "Learn from my history";
            LearnDailySwitch.IsChecked = s.Daily;
            LearnAutoSwitch.IsChecked = s.AutoAdd;
            // Auto-add is something the daily check does; without it there is nothing to add.
            LearnAutoSwitch.IsEnabled = s.Daily;
            LearnUndo.Visibility = s.Learned > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (s.Learned > 0) Ui.SetIconContent(LearnUndo, "IconUndo", $"Undo everything learned ({s.Learned:N0})");
        }
        finally
        {
            _learnRendering = false;
        }
    }

    /// <summary>"Found 312 dictations in FlowType and 863 in Wispr Flow · 37 min of recordings".</summary>
    private static string SourcesLine(LearnCardState s)
    {
        if (s.SourcesProblem != null) return s.SourcesProblem;
        if (s.Sources == null) return "Checking your history…";

        var flowType = s.Sources.FirstOrDefault(x => x.Source == HistorySource.FlowType && LearnFormat.IsUsable(x));
        var wispr = s.Sources.FirstOrDefault(x => x.Source == HistorySource.WisprFlow && LearnFormat.IsUsable(x));
        if (flowType == null && wispr == null) return "No past dictations yet — this fills in as you dictate.";

        string text;
        if (flowType != null && wispr != null)
        {
            text = $"Found {flowType.Dictations:N0} {Ui.Plural(flowType.Dictations, "dictation", "dictations")} "
                + $"in FlowType and {wispr.Dictations:N0} in Wispr Flow";
        }
        else
        {
            var one = flowType ?? wispr!;
            text = $"Found {one.Dictations:N0} {Ui.Plural(one.Dictations, "dictation", "dictations")} in "
                + (one.Source == HistorySource.WisprFlow ? "Wispr Flow" : "FlowType");
        }
        var audio = (flowType?.AudioSeconds ?? 0) + (wispr?.AudioSeconds ?? 0);
        // Non-breaking spaces: "37 min of recordings" wraps as one piece or not at all.
        var recordings = $"{LearnFormat.Duration(audio)} of recordings".Replace(' ', '\u00A0');
        return audio >= 60 ? $"{text}  ·  {recordings}" : text;
    }

    private static string LastRunLine(LearnCardState s)
    {
        if (s.Running) return "Checking your history now…";
        if (s.LastRunUtc is not { } last) return "Not run yet.";
        var line = $"Last checked {LearnFormat.When(last)}";
        return s.Pending > 0
            ? $"{line}  ·  {s.Pending:N0} {Ui.Plural(s.Pending, "suggestion", "suggestions")} waiting for you"
            : line;
    }

    private void LearnButton_Click(object sender, RoutedEventArgs e) => LearnWindow.Open(Window.GetWindow(this));

    private void LearnDailySwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_learnRendering || _learnOverride != null) return;
        var settings = SettingsStore.Instance.Settings;
        var on = LearnDailySwitch.IsChecked == true;
        if (settings.LearnInBackground == on) return;
        settings.LearnInBackground = on;
        SettingsStore.Instance.Save();
    }

    private void LearnAutoSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_learnRendering || _learnOverride != null) return;
        var settings = SettingsStore.Instance.Settings;
        var on = LearnAutoSwitch.IsChecked == true;
        if (settings.AutoAddLearnedWords == on) return;
        settings.AutoAddLearnedWords = on;
        SettingsStore.Instance.Save();
    }

    /// <summary>
    /// One click removes everything learned from history — after a
    /// confirmation, because the user's own entries sit in the same list and
    /// "undo" must visibly promise to leave those alone.
    /// </summary>
    private void LearnUndo_Click(object sender, RoutedEventArgs e)
    {
        var learner = HistoryLearner.Instance;
        var count = learner.LearnedEntryCount;
        if (count <= 0) return;
        var answer = Ui.Message(this,
            $"Remove the {count:N0} {Ui.Plural(count, "entry", "entries")} FlowType added from your past writing?\n\n"
            + "Words, fixes and snippets you added yourself are kept.",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            var removed = learner.UndoAll();
            _learnStatus = $"Removed {removed:N0} {Ui.Plural(removed, "entry", "entries")} learned from your history.";
        }
        catch (Exception ex)
        {
            _learnStatus = $"Couldn't undo: {ex.Message}";
        }
        RefreshLearnCard();
    }

    /// <summary>
    /// Render the learn card in each state from made-up data
    /// (insights-learn-*.png), at the main window's default content width and
    /// at its narrowest. Only the card is saved: the rest of the page shows
    /// the user's own numbers, which --snapshot's main-insights.png already
    /// covers.
    /// </summary>
    internal static void SnapshotLearnCard(string dir)
    {
        var page = new InsightsPage();
        var bg = (Brush)page.FindResource("BgBrush");
        var host = new Window
        {
            Content = page,
            Width = 1180 - 216,
            Height = 900,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            ShowInTaskbar = false,
            Background = bg,
            FontFamily = (System.Windows.Media.FontFamily)page.FindResource("BodyFont"),
            FontSize = 15,
            UseLayoutRounding = true,
        };
        host.Show();
        try
        {
            var sources = LearnSamples.Sources();
            var now = DateTime.UtcNow;
            var states = new (string Name, LearnCardState State)[]
            {
                ("insights-learn", new LearnCardState(sources, null, false, 0, now.AddHours(-3), 24, true, false)),
                ("insights-learn-pending", new LearnCardState(sources, null, false, 7, now.AddDays(-1), 24, true, true)),
                ("insights-learn-never", new LearnCardState(null, null, false, 0, null, 0, false, false)),
                ("insights-learn-running", new LearnCardState(LearnSamples.Sources(wisprFound: false), null, true, 0,
                    now.AddDays(-9), 3, true, false)),
            };
            void SaveAll(string suffix)
            {
                foreach (var (name, state) in states)
                {
                    page._learnOverride = state;
                    page._learnStatus = null;
                    page.RenderLearnCard(state);
                    host.UpdateLayout();
                    Snapshot.Save(page.LearnCard, System.IO.Path.Combine(dir, $"{name}{suffix}.png"), bg);
                }
            }
            SaveAll("");
            host.Width = 960 - 216;
            host.UpdateLayout();
            SaveAll("-narrow");
        }
        finally
        {
            page.Detach();
            host.Close();
        }
    }

    // ----- Where you dictate -----

    private void BuildCategoryBars(StatsStore stats)
    {
        InsightCategories.Children.Clear();
        var usage = stats.CategoryUsage;
        var apps = stats.AppUsage.Count;
        InsightAppCount.Text = apps == 0 ? "" : $"{apps} {Ui.Plural(apps, "app", "apps")} used";

        if (usage.Count == 0)
        {
            InsightCategories.Children.Add(new TextBlock
            {
                Text = "No dictations recorded yet.",
                Style = Ui.StyleOf("Hint"),
            });
            return;
        }

        var total = Math.Max(1, usage.Sum(u => u.Words));
        foreach (var (kind, words, appCount) in usage)
        {
            var share = words / (double)total;
            var row = Ui.Columns(new GridLength(140), Ui.Star, new GridLength(118));
            row.Margin = new Thickness(0, 0, 0, 10);

            var label = Ui.Text(Capitalise(AppCategory.Label(kind)), 14, "SoftTextBrush");

            // A filled portion of a track rather than a ProgressBar, so it
            // stays proportional at any window width.
            var fill = new Border
            {
                Height = 10,
                CornerRadius = new CornerRadius(5),
                Background = Ui.BrushOf("BarBrush"),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            var track = new Border
            {
                Height = 10,
                CornerRadius = new CornerRadius(5),
                Background = Ui.BrushOf("Heat0Brush"),
                Margin = new Thickness(8, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = fill,
            };
            track.SizeChanged += (_, e) => fill.Width = Math.Max(4, e.NewSize.Width * share);

            var value = Ui.Text($"{share * 100:0}%  ·  {words:N0}", 13, "MutedBrush");
            value.HorizontalAlignment = HorizontalAlignment.Right;
            value.ToolTip = appCount > 1 ? $"{words:N0} words across {appCount} apps" : $"{words:N0} words";

            row.Children.Add(label.At(0));
            row.Children.Add(track.At(1));
            row.Children.Add(value.At(2));
            InsightCategories.Children.Add(row);
        }
    }

    private static string Capitalise(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private void BuildTopApps(StatsStore stats)
    {
        InsightTopApps.Children.Clear();
        var apps = stats.AppUsage.Take(6).ToList();
        if (apps.Count == 0)
        {
            InsightTopApps.Children.Add(new TextBlock { Text = "No apps yet.", Style = Ui.StyleOf("Hint") });
            return;
        }

        var top = Math.Max(1, apps[0].Words);
        foreach (var (app, words) in apps)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            var line = Ui.Columns(Ui.Star, Ui.Auto);
            line.Children.Add(Ui.Text(app, 14, "SoftTextBrush").At(0));
            line.Children.Add(Ui.Text($"{words:N0}", 13, "MutedBrush").At(1));
            panel.Children.Add(line);

            var share = words / (double)top;
            var fill = new Border
            {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = Ui.BrushOf("BarBrush"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Opacity = 0.75,
            };
            var track = new Border
            {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                Background = Ui.BrushOf("Heat0Brush"),
                Margin = new Thickness(0, 6, 0, 0),
                Child = fill,
            };
            track.SizeChanged += (_, e) => fill.Width = Math.Max(3, e.NewSize.Width * share);
            panel.Children.Add(track);
            InsightTopApps.Children.Add(panel);
        }
    }

    // ----- Activity heatmap -----

    private static int WeeksThatFit(double width) =>
        width <= 0 ? 26 : Math.Clamp((int)((width - DayLabelWidth) / (CellSize + CellGap)), 12, 53);

    private static readonly string[] HeatKeys = { "Heat0Brush", "Heat1Brush", "Heat2Brush", "Heat3Brush", "Heat4Brush" };

    private void BuildLegend()
    {
        HeatLegend.Children.Clear();
        HeatLegend.Children.Add(new TextBlock
        {
            Text = "Less",
            Style = Ui.StyleOf("Hint"),
            FontSize = 12,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var tips = new[] { "No dictation", "Under 250 words", "250–499 words", "500–749 words", "750 words or more" };
        for (var i = 0; i < HeatKeys.Length; i++)
        {
            HeatLegend.Children.Add(new Border
            {
                Width = 12,
                Height = 12,
                CornerRadius = new CornerRadius(3),
                Background = Ui.BrushOf(HeatKeys[i]),
                Margin = new Thickness(0, 0, 3, 0),
                ToolTip = tips[i],
            });
        }
        HeatLegend.Children.Add(new TextBlock
        {
            Text = "More",
            Style = Ui.StyleOf("Hint"),
            FontSize = 12,
            Margin = new Thickness(3, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
    }

    /// <summary>
    /// One square per day, one column per week ending this week. Levels are
    /// absolute word counts (0 / under 250 / under 500 / under 750 / 750+)
    /// rather than relative to the busiest day, so a square means the same
    /// thing next month as it does today.
    /// </summary>
    private void BuildHeatmap(StatsStore stats, int weeks)
    {
        _heatWeeks = weeks;
        InsightHeatmap.Children.Clear();

        var streak = stats.StreakDays;
        InsightStreak.Text = streak > 0 ? $"{streak} day streak" : "Activity";
        var longest = stats.LongestStreakDays;
        InsightLongest.Text = longest > 0 ? $"Longest streak {longest} {Ui.Plural(longest, "day", "days")}" : "";

        var perDay = stats.WordsPerDay;
        var lastDay = DateTime.Today;
        // Columns are Sunday-to-Saturday weeks and the last one is the week
        // containing today (its future days are left blank). 1.x anchored the
        // first column instead and then rounded it back to a Sunday, which
        // pushed up to six of the most recent days — today included — off the
        // right edge of the grid.
        var firstDay = lastDay.AddDays(-(int)lastDay.DayOfWeek).AddDays(-7 * (weeks - 1));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DayLabelWidth) });
        for (var w = 0; w < weeks; w++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CellSize + CellGap) });
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(20) });
        for (var d = 0; d < 7; d++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CellSize + CellGap) });
        }

        for (var d = 1; d < 7; d += 2)
        {
            var label = new TextBlock
            {
                Text = ((DayOfWeek)d).ToString()[..3],
                FontSize = 11,
                Foreground = Ui.BrushOf("FaintBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, CellGap),
            };
            Grid.SetRow(label, d + 1);
            grid.Children.Add(label);
        }

        var lastMonth = -1;
        var lastLabelColumn = -10;
        var activeDays = 0;
        long weekWords = 0;
        for (var w = 0; w < weeks; w++)
        {
            var weekStart = firstDay.AddDays(w * 7);
            // Label a column when its week contains the 1st of a month. The
            // first column is labelled too, unless a month starts within the
            // next two columns — three-letter labels need three columns, and
            // "Dec" crowding "Jan" reads as noise.
            var monthDay = Enumerable.Range(0, 7).Select(i => weekStart.AddDays(i))
                .FirstOrDefault(x => x.Day == 1 && x <= lastDay);
            var labelMonth = monthDay != default ? monthDay.Month
                : w == 0 && !Enumerable.Range(7, 14).Any(i => weekStart.AddDays(i).Day == 1)
                    ? weekStart.AddDays(6).Month
                    : -1;
            if (labelMonth > 0 && labelMonth != lastMonth && w < weeks - 1 && w - lastLabelColumn >= 3)
            {
                lastLabelColumn = w;
                var monthText = new TextBlock
                {
                    Text = new DateTime(2000, labelMonth, 1).ToString("MMM"),
                    FontSize = 11,
                    Foreground = Ui.BrushOf("FaintBrush"),
                    VerticalAlignment = VerticalAlignment.Top,
                };
                Grid.SetColumn(monthText, w + 1);
                Grid.SetColumnSpan(monthText, Math.Min(3, weeks - w));
                grid.Children.Add(monthText);
                lastMonth = labelMonth;
            }

            for (var d = 0; d < 7; d++)
            {
                var day = weekStart.AddDays(d);
                if (day > lastDay) continue;
                var words = perDay.GetValueOrDefault(day.ToString("yyyy-MM-dd"), 0);
                if (words > 0) activeDays++;
                if (day > lastDay.AddDays(-7)) weekWords += words;
                var cell = new Border
                {
                    Width = CellSize,
                    Height = CellSize,
                    CornerRadius = new CornerRadius(3),
                    Background = Ui.BrushOf(HeatKeys[Metrics.HeatLevel(words)]),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    ToolTip = $"{day:ddd, MMM d} — {words:N0} {Ui.Plural(words, "word", "words")}",
                };
                if (day == lastDay)
                {
                    cell.BorderBrush = Ui.BrushOf("StrokeHoverBrush");
                    cell.BorderThickness = new Thickness(1);
                }
                Grid.SetColumn(cell, w + 1);
                Grid.SetRow(cell, d + 1);
                grid.Children.Add(cell);
            }
        }

        InsightHeatmap.Children.Add(grid);
        InsightHeatmap.Children.Add(new TextBlock
        {
            Text = $"Last {weeks} weeks  ·  {activeDays:N0} active {Ui.Plural(activeDays, "day", "days")}"
                + $"  ·  {weekWords:N0} words in the last 7 days",
            Style = Ui.StyleOf("Hint"),
            FontSize = 12,
            Margin = new Thickness(DayLabelWidth, 6, 0, 0),
        });
    }
}
