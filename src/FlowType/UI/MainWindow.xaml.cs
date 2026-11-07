using System.Windows;
using System.Windows.Input;
using FlowType.Core;
using FlowType.Engine;
using FlowType.Input;
using FlowType.Learning;
using FlowType.UI.Controls;
using FlowType.UI.Pages;
using Brush = System.Windows.Media.Brush;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using RadioButton = System.Windows.Controls.RadioButton;

namespace FlowType.UI;

/// <summary>The pages of the main window, in sidebar order.</summary>
public enum MainPage
{
    Home,
    Insights,
    Dictionary,
    Snippets,
    Style,
    Ai,
    Settings,
}

/// <summary>
/// The hub: a sidebar (pages + live engine status) and one page at a time.
///
/// 2.0 split the old single-file window into one UserControl per page under
/// UI/Pages, because the window had grown to seven pages and ~1,100 lines of
/// handlers that all lived in one class and all refreshed on every service
/// event whether visible or not. Now each page owns its own controls and
/// subscriptions, is created the first time it is opened, and only the
/// visible one is in the tree. The window keeps navigation, the sidebar
/// footer, and the --snapshot entry point.
/// </summary>
public partial class MainWindow : Window
{
    private readonly Dictionary<MainPage, IMainPage> _pages = new();
    private readonly Dictionary<MainPage, RadioButton> _nav;
    private IMainPage? _visible;
    private bool _switching;

    public MainWindow()
    {
        InitializeComponent();

        _nav = new Dictionary<MainPage, RadioButton>
        {
            [MainPage.Home] = NavHome,
            [MainPage.Insights] = NavInsights,
            [MainPage.Dictionary] = NavDictionary,
            [MainPage.Snippets] = NavSnippets,
            [MainPage.Style] = NavStyle,
            [MainPage.Ai] = NavAi,
            [MainPage.Settings] = NavSettings,
        };

        Show(MainPage.Home);
        UpdateFooter();
        UpdateLearnDot();

        Transcriber.Instance.StateChanged += OnEngineChanged;
        LocalLlm.Instance.StateChanged += OnEngineChanged;
        SettingsStore.Instance.Changed += OnEngineChanged;
        HistoryLearner.Instance.Changed += OnLearnerChanged;
    }

    protected override void OnClosed(EventArgs e)
    {
        Transcriber.Instance.StateChanged -= OnEngineChanged;
        LocalLlm.Instance.StateChanged -= OnEngineChanged;
        SettingsStore.Instance.Changed -= OnEngineChanged;
        HistoryLearner.Instance.Changed -= OnLearnerChanged;
        _visible?.OnHidden();
        foreach (var page in _pages.Values)
        {
            try { page.Detach(); }
            catch { /* closing anyway; never let one page block the rest */ }
        }
        base.OnClosed(e);
    }

    // ----- Navigation -----

    /// <summary>The page currently shown.</summary>
    public MainPage CurrentPage { get; private set; } = MainPage.Home;

    /// <summary>Open Settings (tray "Settings…", first-run "pick a model", the flow bar menu).</summary>
    public void NavigateToSettings() => NavigateTo(MainPage.Settings);

    /// <summary>
    /// Open Settings scrolled to a section: "dictation", "microphone", "model",
    /// "output", "formatting", "context", "privacy", "system" or "about".
    /// </summary>
    public void NavigateToSettings(string section) => NavigateTo(MainPage.Settings, section);

    /// <summary>Open a page, optionally at a named section (Settings only).</summary>
    public void NavigateTo(MainPage page, string? section = null)
    {
        _nav[page].IsChecked = true;   // raises Nav_Checked → Show
        Show(page);                    // no-op when already there; covers re-selecting the same page
        if (section != null && _pages.GetValueOrDefault(page) is SettingsPage settings)
        {
            // After layout, or the section has no position yet.
            Dispatcher.BeginInvoke(() => settings.ScrollToSection(section),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        // Fires once during InitializeComponent (Home is checked in XAML)
        // before the map exists; the constructor shows Home itself.
        if (_nav == null || _switching) return;
        foreach (var (page, button) in _nav)
        {
            if (ReferenceEquals(button, sender))
            {
                Show(page);
                return;
            }
        }
    }

    private void Show(MainPage page)
    {
        var target = GetOrCreate(page);
        if (ReferenceEquals(target, _visible)) return;

        _switching = true;
        try
        {
            _nav[page].IsChecked = true;
            _visible?.OnHidden();
            PageHost.Content = target;
            _visible = target;
            CurrentPage = page;
            target.OnShown();
        }
        finally
        {
            _switching = false;
        }
    }

    private IMainPage GetOrCreate(MainPage page)
    {
        if (_pages.TryGetValue(page, out var existing)) return existing;
        IMainPage created = page switch
        {
            MainPage.Home => new HomePage(),
            MainPage.Insights => new InsightsPage(),
            MainPage.Dictionary => new DictionaryPage(),
            MainPage.Snippets => new SnippetsPage(),
            MainPage.Style => new StylePage(),
            MainPage.Ai => new AiPage(),
            _ => new SettingsPage(),
        };
        _pages[page] = created;
        return created;
    }

    private void EngineFooter_Click(object sender, RoutedEventArgs e) => NavigateToSettings("model");

    /// <summary>Ctrl+F jumps to the history search; Ctrl+1…7 switch pages.</summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.F)
        {
            NavigateTo(MainPage.Home);
            (_visible as HomePage)?.FocusSearch();
            e.Handled = true;
            return;
        }
        var index = e.Key - Key.D1;
        if (index is >= 0 and <= 6)
        {
            NavigateTo((MainPage)index);
            e.Handled = true;
        }
    }

    // ----- Sidebar footer: live engine status -----

    private void OnEngineChanged() => Dispatcher.BeginInvoke(UpdateFooter);

    /// <summary>
    /// Speech model and where it runs, the optional AI model and where it
    /// runs, the hotkey, and a red "paused" badge — the four things that
    /// explain "why didn't anything happen" without opening a page.
    /// </summary>
    private void UpdateFooter()
    {
        var s = SettingsStore.Instance.Settings;
        var t = Transcriber.Instance;
        var whisper = ModelCatalog.ById(t.CurrentModelId);

        if (t.IsLoading)
        {
            SpeechModelText.Text = "Loading speech model…";
            SpeechRuntimeText.Text = "one moment";
            SpeechDot.Fill = Ui.BrushOf("FaintBrush");
        }
        else if (t.IsReady)
        {
            SpeechModelText.Text = whisper?.DisplayName ?? t.CurrentModelId;
            SpeechRuntimeText.Text = string.IsNullOrEmpty(t.RuntimeLabel) ? "Speech" : $"Speech · {t.RuntimeLabel}";
            SpeechDot.Fill = Ui.BrushOf("AccentBrush");
        }
        else if (ModelCatalog.ById(s.SelectedModel) is { IsDownloaded: true } chosen)
        {
            // Downloaded but not (yet) in memory: the start-up load is queued
            // or a load failed. Not the same as "nothing to use".
            SpeechModelText.Text = chosen.DisplayName;
            SpeechRuntimeText.Text = "Not loaded yet";
            SpeechDot.Fill = Ui.BrushOf("FaintBrush");
        }
        else
        {
            SpeechModelText.Text = "No speech model";
            SpeechRuntimeText.Text = "Click to choose one";
            SpeechDot.Fill = Ui.BrushOf("DangerBrush");
        }

        var llm = LocalLlm.Instance;
        if (llm.IsLoading)
        {
            AiModelText.Text = "Loading AI model…";
            AiRuntimeText.Text = "one moment";
            AiDot.Fill = Ui.BrushOf("FaintBrush");
        }
        else if (llm.IsReady)
        {
            AiModelText.Text = llm.CurrentModel?.DisplayName ?? llm.CurrentModelId;
            AiRuntimeText.Text = s.AiPolish == "off"
                ? $"AI polish off · {llm.RuntimeLabel}"
                : $"AI · {llm.RuntimeLabel}";
            AiDot.Fill = Ui.BrushOf("AccentBrush");
        }
        else
        {
            AiModelText.Text = "On-device AI off";
            AiRuntimeText.Text = llm.LastLoadError != null ? "Couldn't load — see AI" : "Rules-only formatting";
            AiDot.Fill = Ui.BrushOf(llm.LastLoadError != null ? "DangerBrush" : "FaintBrush");
        }

        HotkeyFooterText.Text = $"Hotkey · {HotkeyDefs.CurrentDisplay(s)}";
        PausedBadge.Visibility = s.DictationPaused ? Visibility.Visible : Visibility.Collapsed;
    }

    // ----- Sidebar: "suggestions waiting" dot on Insights -----

    /// <summary>Raised on any thread (the daily check runs in the background).</summary>
    private void OnLearnerChanged() => Dispatcher.BeginInvoke(UpdateLearnDot);

    /// <summary>
    /// A small amber dot beside "Insights" while a learning run has left
    /// suggestions unreviewed — the only sign the quiet daily check gives,
    /// so it never interrupts dictation with a pop-up.
    /// </summary>
    private void UpdateLearnDot() => ShowLearnDot(HistoryLearner.Instance.PendingCount);

    private void ShowLearnDot(int pending)
    {
        InsightsDot.Visibility = pending > 0 ? Visibility.Visible : Visibility.Collapsed;
        InsightsDot.ToolTip = pending > 0
            ? $"{pending:N0} {Ui.Plural(pending, "suggestion", "suggestions")} from your writing to review"
            : null;
    }

    // ----- Snapshot (hidden diagnostic) -----

    /// <summary>
    /// Render every page to PNG at the window's size (main-{page}.png), and
    /// each page's whole content column at full height (main-{page}-full.png).
    /// Also renders Home with one history row's hover actions showing, since
    /// hover can't be produced off-screen any other way.
    /// </summary>
    internal void SnapshotPages(string dir)
    {
        var root = (FrameworkElement)Content;
        var bg = (Brush)FindResource("BgBrush");
        var names = new (MainPage Page, string Name)[]
        {
            (MainPage.Home, "home"), (MainPage.Insights, "insights"),
            (MainPage.Dictionary, "dictionary"), (MainPage.Snippets, "snippets"),
            (MainPage.Style, "style"), (MainPage.Ai, "ai"), (MainPage.Settings, "settings"),
        };
        foreach (var (page, name) in names)
        {
            NavigateTo(page);
            UpdateLayout();
            Snapshot.Save(root, System.IO.Path.Combine(dir, $"main-{name}.png"), bg);
            Snapshot.Save(_pages[page].SnapshotBody,
                System.IO.Path.Combine(dir, $"main-{name}-full.png"), bg);
        }

        NavigateTo(MainPage.Home);
        if (_visible is HomePage home)
        {
            home.RevealFirstRowActions(true);
            UpdateLayout();
            Snapshot.Save(root, System.IO.Path.Combine(dir, "main-home-hover.png"), bg);
            home.RevealFirstRowActions(false);
        }

        SnapshotNavDot(dir);
    }

    /// <summary>
    /// The sidebar alone with the "suggestions waiting" dot showing
    /// (main-nav-dot.png), whatever the learner's real state is. Only the
    /// sidebar is saved, so nothing from History is in the picture.
    /// </summary>
    internal void SnapshotNavDot(string dir)
    {
        var bg = (Brush)FindResource("BgBrush");
        ShowLearnDot(7);
        UpdateLayout();
        Snapshot.Save(Sidebar, System.IO.Path.Combine(dir, "main-nav-dot.png"), bg);
        UpdateLearnDot();
    }
}
