using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FlowType.Audio;
using FlowType.Core;
using FlowType.Engine;
using FlowType.Input;
using FlowType.Session;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ProgressBar = System.Windows.Controls.ProgressBar;
using MessageBox = System.Windows.MessageBox;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using TextWrapping = System.Windows.TextWrapping;

namespace FlowType.UI;

/// <summary>Home / Notes / Dictionary / Settings. Plain code-behind, service-event driven.</summary>
public partial class MainWindow : Window
{
    private static readonly (string Code, string Name)[] Languages =
    {
        ("auto", "Auto-detect"),
        ("en", "English"), ("es", "Spanish"), ("fr", "French"), ("de", "German"),
        ("it", "Italian"), ("pt", "Portuguese"), ("nl", "Dutch"), ("hi", "Hindi"),
        ("ja", "Japanese"), ("ko", "Korean"), ("zh", "Chinese"), ("ru", "Russian"),
        ("pl", "Polish"), ("tr", "Turkish"), ("uk", "Ukrainian"), ("ar", "Arabic"),
        ("sv", "Swedish"), ("cs", "Czech"), ("vi", "Vietnamese"), ("id", "Indonesian"),
        ("th", "Thai"),
    };

    private bool _loading = true;
    private readonly Dictionary<string, ProgressBar> _downloadBars = new();
    private bool _micTesting;
    private bool _capturing;
    private readonly System.Windows.Threading.DispatcherTimer _liveTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(120),
    };

    public MainWindow()
    {
        InitializeComponent();

        // Read the version from the assembly so this line can never go stale.
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        var versionText = version == null
            ? ""
            : $"{version.Major}.{version.Minor}.{version.Build}";
        AboutText.Text = $"FlowType {versionText} — local AI voice typing for Windows. " +
            "Everything runs on this PC; audio never leaves it.";
        CreditText.Text = "Created by EvilJackk";

        LoadSettingsControls();
        RefreshModels();
        RefreshNotes();
        RefreshDictionary();
        RefreshHome();
        UpdateFooter();
        _loading = false;

        ModelDownloader.Instance.ProgressChanged += OnDownloadProgress;
        ModelDownloader.Instance.Completed += OnDownloadCompleted;
        Transcriber.Instance.StateChanged += OnTranscriberChanged;
        NotesStore.Instance.Changed += OnNotesChanged;
        DictionaryStore.Instance.Changed += OnDictionaryChanged;
        StatsStore.Instance.Changed += OnStatsChanged;
        SettingsStore.Instance.Changed += OnSettingsStoreChanged;
        AudioRecorder.Instance.LevelChanged += OnMicLevel;

        // Live hotkey truth: a held/not-held dot plus the last raw key the
        // hook observed, so "the key does nothing" is diagnosable on sight.
        if (App.Hotkeys != null) App.Hotkeys.KeyObserved += OnKeyObserved;
        _liveTimer.Tick += (_, _) => UpdateLiveHotkeyState();
        _liveTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        StopMicTest();
        _liveTimer.Stop();
        if (App.Hotkeys != null)
        {
            App.Hotkeys.KeyObserved -= OnKeyObserved;
            if (_capturing) App.Hotkeys.CancelCapture();
        }
        ModelDownloader.Instance.ProgressChanged -= OnDownloadProgress;
        ModelDownloader.Instance.Completed -= OnDownloadCompleted;
        Transcriber.Instance.StateChanged -= OnTranscriberChanged;
        NotesStore.Instance.Changed -= OnNotesChanged;
        DictionaryStore.Instance.Changed -= OnDictionaryChanged;
        StatsStore.Instance.Changed -= OnStatsChanged;
        SettingsStore.Instance.Changed -= OnSettingsStoreChanged;
        AudioRecorder.Instance.LevelChanged -= OnMicLevel;
        base.OnClosed(e);
    }

    // ----- Navigation -----

    public void NavigateToSettings()
    {
        NavSettings.IsChecked = true;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (HomePanel == null || SettingsPanel == null) return;

        HomePanel.Visibility = NavHome.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        InsightsPanel.Visibility = NavInsights.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        NotesPanel.Visibility = NavNotes.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DictionaryPanel.Visibility = NavDictionary.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (NavHome.IsChecked == true) RefreshHome();
        if (NavInsights.IsChecked == true) RefreshInsights();
        if (NavSettings.IsChecked != true) StopMicTest();
    }

    private void GoToSettings_Click(object sender, RoutedEventArgs e) => NavigateToSettings();

    // ----- Home -----

    private void RefreshHome()
    {
        var hour = DateTime.Now.Hour;
        GreetingText.Text = hour switch
        {
            < 5 => "Up late?",
            < 12 => "Good morning",
            < 18 => "Good afternoon",
            _ => "Good evening",
        };

        var hotkey = HotkeyDefs.CurrentDisplay(SettingsStore.Instance.Settings);
        HomeHintText.Text =
            $"Hold {hotkey} anywhere and talk — release to insert. Quick-tap it for hands-free.";
        TryHintText.Text =
            $"Click into the box, hold {hotkey}, and say something. Your words land wherever the cursor is.";

        var stats = StatsStore.Instance;
        StatWords.Text = stats.TotalWords.ToString("N0");
        StatWpm.Text = stats.AverageWpm > 0 ? stats.AverageWpm.ToString() : "—";
        StatStreak.Text = stats.StreakDays.ToString();
        StatTopApp.Text = stats.TopApp?.App ?? "—";

        RefreshEngineStatus();
    }

    private void RefreshEngineStatus()
    {
        var t = Transcriber.Instance;
        var model = ModelCatalog.ById(t.CurrentModelId);
        var hotkey = HotkeyDefs.CurrentDisplay(SettingsStore.Instance.Settings);
        EngineStatusText.Text = t.IsLoading
            ? "Loading the AI model…"
            : t.IsReady
                ? $"{model?.DisplayName ?? t.CurrentModelId} · {t.RuntimeLabel} · hotkey {hotkey}"
                : "No model loaded — pick one in Settings → AI model.";
    }

    private void OnStatsChanged() => Dispatcher.BeginInvoke(RefreshHome);

    // ----- Settings: load & save -----

    private void LoadSettingsControls()
    {
        var s = SettingsStore.Instance.Settings;

        LoadHotkeyCombo();

        ModeSmartRadio.IsChecked = s.ActivationMode == "smart";
        ModeHoldRadio.IsChecked = s.ActivationMode == "hold";
        ModeToggleRadio.IsChecked = s.ActivationMode == "toggle";

        LanguageCombo.Items.Clear();
        foreach (var (code, name) in Languages)
        {
            LanguageCombo.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        }
        var langIndex = Array.FindIndex(Languages, l => l.Code == s.Language);
        LanguageCombo.SelectedIndex = langIndex >= 0 ? langIndex : 0;

        FillCombo(EnglishVariantCombo, new (string, string)[]
        {
            (EnglishVariant.Off, "Leave as the AI writes it"),
            (EnglishVariant.Uk, "British — colour, realise, centre"),
            (EnglishVariant.Us, "American — color, realize, center"),
        }, s.EnglishVariant);

        FillCombo(BarPositionCombo, new (string, string)[]
        {
            ("bottom", "Bottom of screen"),
            ("top", "Top of screen"),
            ("hidden", "Hidden (tray + hotkey only)"),
        }, s.BarPosition);

        FillCombo(MaxLengthCombo, new (string, string)[]
        {
            ("2", "2 minutes"), ("5", "5 minutes"),
            ("15", "15 minutes"), ("60", "60 minutes"),
        }, s.MaxRecordingMinutes.ToString());

        LoadMicCombo();

        SoundsCheck.IsChecked = s.PlaySounds;
        GpuCheck.IsChecked = s.UseGpu;
        AccurateCheck.IsChecked = s.AccurateDecoding;
        SaveHistoryCheck.IsChecked = s.SaveHistory;
        DiagnosticLogCheck.IsChecked = s.DiagnosticLog;
        PauseCheck.IsChecked = s.DictationPaused;
        AutoInsertCheck.IsChecked = s.AutoInsert;
        PasteRadio.IsChecked = s.InsertMethod != "type";
        TypeRadio.IsChecked = s.InsertMethod == "type";
        RestoreClipboardCheck.IsChecked = s.RestoreClipboard;
        LearnCorrectionsCheck.IsChecked = s.LearnCorrections;
        AutoParagraphsCheck.IsChecked = s.AutoParagraphs;
        AutoListsCheck.IsChecked = s.AutoLists;
        SmartSymbolsCheck.IsChecked = s.SmartSymbols;
        KnownTermsCheck.IsChecked = s.KnownTermCasing;
        TrailingSpaceCheck.IsChecked = s.AppendTrailingSpace;
        FillersCheck.IsChecked = s.RemoveFillers;
        ScratchCheck.IsChecked = s.ScratchThat;
        LineCmdCheck.IsChecked = s.LineCommands;
        PunctCmdCheck.IsChecked = s.PunctuationCommands;
        SmartPunctCheck.IsChecked = s.SmartTrailingPunctuation;
        FuzzyVocabCheck.IsChecked = s.FuzzyVocabulary;
        StartupCheck.IsChecked = StartupHelper.IsEnabled;
        ShowBarCheck.IsChecked = s.ShowBarWhenIdle;
    }

    /// <summary>Populate a combo from (value, label) pairs and select a value.</summary>
    private static void FillCombo(ComboBox combo, (string Value, string Label)[] items,
        string selected)
    {
        combo.Items.Clear();
        var index = 0;
        for (var i = 0; i < items.Length; i++)
        {
            combo.Items.Add(new ComboBoxItem { Content = items[i].Label, Tag = items[i].Value });
            if (items[i].Value == selected) index = i;
        }
        combo.SelectedIndex = index;
    }

    private static string ComboValue(ComboBox combo, string fallback) =>
        combo.SelectedItem is ComboBoxItem { Tag: string value } ? value : fallback;

    private void LoadHotkeyCombo()
    {
        var s = SettingsStore.Instance.Settings;
        HotkeyCombo.Items.Clear();
        foreach (var def in HotkeyDefs.All)
        {
            HotkeyCombo.Items.Add(new ComboBoxItem { Content = def.Display, Tag = def.Id });
        }
        if (s.CustomVks.Length > 0)
        {
            HotkeyCombo.Items.Add(new ComboBoxItem
            {
                Content = $"Custom: {HotkeyDefs.CurrentDisplay(s)}"
                    .Replace("Custom: Custom", "Custom"),
                Tag = "Custom",
            });
        }
        var index = 0;
        for (var i = 0; i < HotkeyCombo.Items.Count; i++)
        {
            if ((string)((ComboBoxItem)HotkeyCombo.Items[i]).Tag == s.Hotkey)
            {
                index = i;
                break;
            }
        }
        HotkeyCombo.SelectedIndex = index;
    }

    // ----- Detect key (bind to whatever the keyboard actually sends) -----

    private void DetectKey_Click(object sender, RoutedEventArgs e)
    {
        var hotkeys = App.Hotkeys;
        if (hotkeys == null) return;

        if (_capturing)
        {
            hotkeys.CancelCapture();
            EndCapture("Detection canceled.");
            return;
        }

        _capturing = true;
        DetectKeyButton.Content = "Cancel";
        CaptureStatusText.Text = "Listening… press the key or mouse button you want to use.";

        hotkeys.CaptureNextKey(vk => Dispatcher.BeginInvoke(() =>
        {
            if (!_capturing) return;
            if (vk == 0x1B)  // Esc = keep current binding
            {
                EndCapture("Kept the current hotkey.");
                return;
            }

            var s = SettingsStore.Instance.Settings;
            s.Hotkey = "Custom";
            s.CustomVks = new[] { vk };
            s.CustomLabel = VkNames.Name(vk);
            SettingsStore.Instance.Save();

            var wasLoading = _loading;
            _loading = true;
            LoadHotkeyCombo();
            _loading = wasLoading;

            EndCapture($"Bound to “{VkNames.Name(vk)}” (code 0x{vk:X2}). Hold it and talk.");
        }));

        // If nothing arrives, that itself is the diagnosis.
        _ = Task.Delay(TimeSpan.FromSeconds(8)).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
        {
            if (!_capturing) return;
            hotkeys.CancelCapture();
            EndCapture("No key detected in 8s. If you pressed one, your keyboard software " +
                "(G HUB, Synapse, remapper) is swallowing it before it reaches Windows — " +
                "try a different key or a mouse button.");
        }));
    }

    private void EndCapture(string status)
    {
        _capturing = false;
        DetectKeyButton.Content = "Detect key…";
        CaptureStatusText.Text = status;
    }

    private void OnKeyObserved(int vk) => Dispatcher.BeginInvoke(() =>
    {
        LastKeyText.Text = $"last key seen: {VkNames.Name(vk)} (0x{vk:X2})";
    });

    private void UpdateLiveHotkeyState()
    {
        var hotkeys = App.Hotkeys;
        if (hotkeys == null || SettingsPanel.Visibility != Visibility.Visible) return;

        var held = hotkeys.IsHotkeyDown;
        var paused = SettingsStore.Instance.Settings.DictationPaused;
        HotkeyStateDot.Fill = held
            ? (Brush)FindResource(paused ? "DangerBrush" : "SuccessBrush")
            : new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
        HotkeyStateText.Text = held
            ? paused ? "hotkey detected — but dictation is paused" : "hotkey HELD — it works!"
            : "hotkey not pressed";
    }

    private void LoadMicCombo()
    {
        var saved = SettingsStore.Instance.Settings.MicDeviceName;
        MicCombo.Items.Clear();
        MicCombo.Items.Add(new ComboBoxItem { Content = "System default", Tag = "" });
        foreach (var name in AudioRecorder.GetInputDeviceNames())
        {
            MicCombo.Items.Add(new ComboBoxItem { Content = name, Tag = name });
        }
        var index = 0;
        for (var i = 1; i < MicCombo.Items.Count; i++)
        {
            if (saved.Length > 0 && (string)((ComboBoxItem)MicCombo.Items[i]).Tag == saved)
            {
                index = i;
                break;
            }
        }
        MicCombo.SelectedIndex = index;
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var s = SettingsStore.Instance.Settings;

        if (HotkeyCombo.SelectedItem is ComboBoxItem hk && hk.Tag is string hotkeyId)
        {
            s.Hotkey = hotkeyId;  // "Custom" keeps using the captured CustomVks
        }
        s.ActivationMode = ModeHoldRadio.IsChecked == true ? "hold"
            : ModeToggleRadio.IsChecked == true ? "toggle" : "smart";
        if (LanguageCombo.SelectedItem is ComboBoxItem lang && lang.Tag is string code)
        {
            s.Language = code;
        }
        if (MicCombo.SelectedItem is ComboBoxItem mic && mic.Tag is string device)
        {
            s.MicDeviceName = device;
        }
        s.EnglishVariant = ComboValue(EnglishVariantCombo, EnglishVariant.Off);
        s.BarPosition = ComboValue(BarPositionCombo, "bottom");
        s.MaxRecordingMinutes = int.TryParse(ComboValue(MaxLengthCombo, "5"), out var mins)
            ? mins : 5;
        s.PlaySounds = SoundsCheck.IsChecked == true;
        s.UseGpu = GpuCheck.IsChecked == true;
        s.AccurateDecoding = AccurateCheck.IsChecked == true;
        s.SaveHistory = SaveHistoryCheck.IsChecked == true;
        s.DiagnosticLog = DiagnosticLogCheck.IsChecked == true;
        s.DictationPaused = PauseCheck.IsChecked == true;
        s.AutoInsert = AutoInsertCheck.IsChecked == true;
        s.InsertMethod = TypeRadio.IsChecked == true ? "type" : "paste";
        s.RestoreClipboard = RestoreClipboardCheck.IsChecked == true;
        s.LearnCorrections = LearnCorrectionsCheck.IsChecked == true;
        s.AutoParagraphs = AutoParagraphsCheck.IsChecked == true;
        s.AutoLists = AutoListsCheck.IsChecked == true;
        s.SmartSymbols = SmartSymbolsCheck.IsChecked == true;
        s.KnownTermCasing = KnownTermsCheck.IsChecked == true;
        s.AppendTrailingSpace = TrailingSpaceCheck.IsChecked == true;
        s.RemoveFillers = FillersCheck.IsChecked == true;
        s.ScratchThat = ScratchCheck.IsChecked == true;
        s.LineCommands = LineCmdCheck.IsChecked == true;
        s.PunctuationCommands = PunctCmdCheck.IsChecked == true;
        s.SmartTrailingPunctuation = SmartPunctCheck.IsChecked == true;
        s.FuzzyVocabulary = FuzzyVocabCheck.IsChecked == true;
        s.ShowBarWhenIdle = ShowBarCheck.IsChecked == true;

        SettingsStore.Instance.Save();
        StartupHelper.SetEnabled(StartupCheck.IsChecked == true);
    }

    private void OnSettingsStoreChanged() => Dispatcher.BeginInvoke(() =>
    {
        UpdateFooter();
        if (NavHome.IsChecked == true) RefreshHome();
    });

    private void RefreshMics_Click(object sender, RoutedEventArgs e)
    {
        var wasLoading = _loading;
        _loading = true;
        LoadMicCombo();
        _loading = wasLoading;
    }

    // ----- Mic test -----

    private void MicTest_Click(object sender, RoutedEventArgs e)
    {
        if (_micTesting) StopMicTest();
        else
        {
            try
            {
                AudioRecorder.Instance.StartMonitor();
                _micTesting = true;
                MicTestButton.Content = "Stop";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't open the microphone:\n{ex.Message}",
                    "FlowType", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void StopMicTest()
    {
        if (!_micTesting) return;
        AudioRecorder.Instance.StopMonitor();
        _micTesting = false;
        MicTestButton.Content = "Test";
        MicLevelBar.Value = 0;
    }

    private void OnMicLevel(float level)
    {
        if (!_micTesting) return;
        Dispatcher.BeginInvoke(() => MicLevelBar.Value = Math.Min(1.0, level * 1.6));
    }

    // ----- Models -----

    private void RefreshModels()
    {
        ModelsList.Children.Clear();
        _downloadBars.Clear();

        var t = Transcriber.Instance;
        var decoding = SettingsStore.Instance.Settings.AccurateDecoding ? "accurate" : "fast";
        ModelsStatusText.Text = t.IsLoading
            ? "Loading model…"
            : t.IsReady
                ? $"Active: {ModelCatalog.ById(t.CurrentModelId)?.DisplayName} · running on {t.RuntimeLabel} · {decoding} decoding"
                : "Download a model, then click Use.";

        var recommended = ModelCatalog.Recommended();
        foreach (var model in ModelCatalog.All)
        {
            ModelsList.Children.Add(BuildModelRow(model, model.Id == recommended.Id));
        }
    }

    private Border BuildModelRow(WhisperModel model, bool isRecommended)
    {
        var s = SettingsStore.Instance.Settings;
        var downloading = ModelDownloader.Instance.IsDownloading(model.Id);
        var isActive = model.IsDownloaded
            && Transcriber.Instance.CurrentModelId == model.Id
            && s.SelectedModel == model.Id;

        var info = new StackPanel();
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = model.DisplayName,
            FontSize = 14.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (isRecommended) titleRow.Children.Add(Chip("Recommended", filled: false));
        if (isActive) titleRow.Children.Add(Chip("Active", filled: true));
        info.Children.Add(titleRow);
        info.Children.Add(new TextBlock
        {
            Text = $"{model.Blurb}  ·  {model.SizeLabel} · {model.LanguageLabel}",
            FontSize = 11.5,
            Foreground = (Brush)FindResource("MutedBrush"),
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (downloading)
        {
            var bar = new ProgressBar { Width = 140, Height = 7, Minimum = 0, Maximum = 1 };
            _downloadBars[model.Id] = bar;
            actions.Children.Add(bar);
            var cancel = RowButton("Cancel", "GhostButton");
            cancel.Click += (_, _) => ModelDownloader.Instance.Cancel(model.Id);
            actions.Children.Add(cancel);
        }
        else if (model.IsDownloaded)
        {
            if (!isActive)
            {
                var use = RowButton("Use", "AccentButton");
                use.Click += (_, _) => UseModel(model.Id);
                actions.Children.Add(use);
            }
            var delete = RowButton("Delete", "DangerButton");
            delete.Click += (_, _) =>
            {
                ModelDownloader.Instance.Delete(model.Id);
                RefreshModels();
                UpdateFooter();
            };
            actions.Children.Add(delete);
        }
        else
        {
            var download = RowButton("Download", "AccentButton");
            download.Click += (_, _) =>
            {
                _ = ModelDownloader.Instance.DownloadAsync(model.Id);
                RefreshModels();
            };
            actions.Children.Add(download);
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(info, 0);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(info);
        grid.Children.Add(actions);

        return new Border
        {
            Background = (Brush)FindResource("InputBrush"),
            BorderBrush = (Brush)FindResource("StrokeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 8),
            Child = grid,
        };
    }

    private async void UseModel(string modelId)
    {
        SettingsStore.Instance.Settings.SelectedModel = modelId;
        SettingsStore.Instance.Save();
        RefreshModels();
        try
        {
            await Transcriber.Instance.LoadModelAsync(modelId);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't load the model:\n{ex.Message}", "FlowType",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnDownloadProgress(string modelId, double progress) => Dispatcher.BeginInvoke(() =>
    {
        if (_downloadBars.TryGetValue(modelId, out var bar)) bar.Value = progress;
        else RefreshModels();
    });

    private void OnDownloadCompleted(string modelId, string? error) => Dispatcher.BeginInvoke(() =>
    {
        RefreshModels();
        if (error != null)
        {
            MessageBox.Show(this, $"Download failed:\n{error}", "FlowType",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        var model = ModelCatalog.ById(modelId);
        if (model is { IsDownloaded: true }
            && string.IsNullOrEmpty(SettingsStore.Instance.Settings.SelectedModel))
        {
            UseModel(modelId);
        }
    });

    private void OnTranscriberChanged() => Dispatcher.BeginInvoke(() =>
    {
        RefreshModels();
        RefreshEngineStatus();
        UpdateFooter();
    });

    private void OpenModelsFolder_Click(object sender, RoutedEventArgs e)
    {
        AppPaths.EnsureCreated();
        Process.Start(new ProcessStartInfo("explorer.exe", AppPaths.ModelsDir));
    }

    // ----- Notes -----

    private void OnNotesChanged() => Dispatcher.BeginInvoke(RefreshNotes);

    private void NotesSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading) RefreshNotes();
    }

    private void RefreshNotes()
    {
        NotesList.Children.Clear();
        var notes = NotesStore.Instance.Search(NotesSearchBox.Text);
        NotesEmptyText.Visibility = notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var note in notes.Take(200))
        {
            var info = new StackPanel();
            var app = string.IsNullOrEmpty(note.AppName) ? "" : $" · {note.AppName}";
            info.Children.Add(new TextBlock
            {
                Text = $"{note.Timestamp:g}{app} · {note.WordCount} words · {note.DurationSeconds:0.0}s",
                FontSize = 10.5,
                Foreground = (Brush)FindResource("FaintBrush"),
            });
            info.Children.Add(new TextBlock
            {
                Text = note.Text,
                FontSize = 13,
                Foreground = (Brush)FindResource("TextBrush"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            });

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Top,
            };
            var copy = RowButton("Copy", "GhostButton");
            var text = note.Text;
            copy.Click += (_, _) => TextInjector.CopyOnly(text);
            actions.Children.Add(copy);
            var delete = RowButton("Delete", "DangerButton");
            var id = note.Id;
            delete.Click += (_, _) => NotesStore.Instance.Delete(id);
            actions.Children.Add(delete);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(info, 0);
            Grid.SetColumn(actions, 1);
            grid.Children.Add(info);
            grid.Children.Add(actions);

            NotesList.Children.Add(new Border
            {
                Background = (Brush)FindResource("CardBrush"),
                BorderBrush = (Brush)FindResource("StrokeBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 0, 8),
                Child = grid,
            });
        }
    }

    private void ExportNotes_Click(object sender, RoutedEventArgs e)
    {
        if (NotesStore.Instance.All.Count == 0)
        {
            MessageBox.Show(this, "There's nothing to export yet.", "FlowType",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"flowtype-notes-{DateTime.Now:yyyy-MM-dd}.txt",
            DefaultExt = ".txt",
            Filter = "Text file (*.txt)|*.txt",
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            NotesStore.Instance.ExportTo(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't write the file:\n{ex.Message}", "FlowType",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearNotes_Click(object sender, RoutedEventArgs e)
    {
        if (NotesStore.Instance.All.Count == 0) return;
        var result = MessageBox.Show(this, "Delete all notes?", "FlowType",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes) NotesStore.Instance.Clear();
    }

    // ----- Insights -----

    /// <summary>
    /// The usage dashboard. Every number here is measured on this machine —
    /// there is deliberately no "top N% of users" figure, because FlowType
    /// cannot see any other users and inventing a percentile would be a lie.
    /// The comparison shown instead is against typing speed, which is a real
    /// number the user can check.
    /// </summary>
    private void RefreshInsights()
    {
        var stats = StatsStore.Instance;

        var wpm = stats.AverageWpm;
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
            InsightFormatFixes.Text = $"{stats.FormattingFixes:N0} paragraphs and list items";
        }

        InsightWords.Text = stats.TotalWords.ToString("N0");
        InsightTakes.Text = $"{stats.TotalUtterances:N0} dictations";
        var minutes = stats.TotalSpeakingSeconds / 60.0;
        InsightSpeaking.Text = minutes >= 60
            ? $"{minutes / 60.0:0.#} hours of speaking"
            : $"{minutes:0.#} minutes of speaking";

        InsightsSubtitle.Text = stats.TotalUtterances == 0
            ? "Nothing yet — everything here fills in as you dictate."
            : "Everything measured on this machine. Nothing leaves it.";

        BuildCategoryBars(stats);
        BuildHeatmap(stats);
    }

    private void BuildCategoryBars(StatsStore stats)
    {
        InsightCategories.Children.Clear();
        var usage = stats.CategoryUsage;
        var apps = stats.AppUsage.Count;
        InsightAppCount.Text = apps == 0 ? "" : $"{apps} app{(apps == 1 ? "" : "s")} used";

        if (usage.Count == 0)
        {
            InsightCategories.Children.Add(new TextBlock
            {
                Text = "No dictations recorded yet.",
                Style = (Style)FindResource("Hint"),
            });
            return;
        }

        var total = Math.Max(1, usage.Sum(u => u.Words));
        foreach (var (kind, words, appCount) in usage)
        {
            var share = words / (double)total;
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                Text = AppCategory.Label(kind),
                FontSize = 12.5,
                Foreground = (Brush)FindResource("MutedBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            // Drawn as a filled portion of a track rather than with a ProgressBar,
            // so it stays correct at any window width.
            var fill = new Border
            {
                Height = 22,
                CornerRadius = new CornerRadius(6),
                Background = (Brush)FindResource("AccentBrush"),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            var track = new Border
            {
                Height = 22,
                CornerRadius = new CornerRadius(6),
                Background = (Brush)FindResource("InputBrush"),
                Margin = new Thickness(10, 0, 10, 0),
                Child = fill,
            };
            track.SizeChanged += (_, e) => fill.Width = Math.Max(2, e.NewSize.Width * share);

            var value = new TextBlock
            {
                Text = $"{share * 100:0}%   {words:N0} words"
                    + (appCount > 1 ? $"   ({appCount} apps)" : ""),
                FontSize = 12,
                Foreground = (Brush)FindResource("MutedBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            };

            Grid.SetColumn(label, 0);
            Grid.SetColumn(track, 1);
            Grid.SetColumn(value, 2);
            row.Children.Add(label);
            row.Children.Add(track);
            row.Children.Add(value);
            InsightCategories.Children.Add(row);
        }
    }

    /// <summary>26 weeks of activity; the densest square is your busiest day.</summary>
    private void BuildHeatmap(StatsStore stats)
    {
        const int weeks = 26;
        InsightHeatmap.Children.Clear();
        InsightStreak.Text = $"{stats.StreakDays} day streak";
        var longest = stats.LongestStreakDays;
        InsightLongest.Text = longest > 0 ? $"longest {longest} day{(longest == 1 ? "" : "s")}" : "";

        var perDay = stats.WordsPerDay;
        var busiest = Math.Max(1, perDay.Values.DefaultIfEmpty(0).Max());

        // Start on the Sunday at or before the first day shown, so every column
        // is a whole week and the rows line up with the day names.
        var lastDay = DateTime.Today;
        var firstDay = lastDay.AddDays(-(weeks * 7 - 1));
        firstDay = firstDay.AddDays(-(int)firstDay.DayOfWeek);

        var dayLabels = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        for (var d = 0; d < 7; d++)
        {
            dayLabels.Children.Add(new TextBlock
            {
                Text = d % 2 == 1 ? ((DayOfWeek)d).ToString()[..3] : "",
                FontSize = 10,
                Height = 15,
                Foreground = (Brush)FindResource("FaintBrush"),
            });
        }

        var columns = new StackPanel { Orientation = Orientation.Horizontal };
        for (var w = 0; w < weeks; w++)
        {
            var column = new StackPanel { Margin = new Thickness(0, 0, 3, 0) };
            for (var d = 0; d < 7; d++)
            {
                var day = firstDay.AddDays(w * 7 + d);
                var future = day > lastDay;
                var words = perDay.GetValueOrDefault(day.ToString("yyyy-MM-dd"), 0);
                // A light day still has to read as activity next to a heavy one,
                // so the scale starts well above zero rather than at it.
                var strength = words == 0 ? 0.0 : 0.30 + 0.70 * Math.Min(1.0, words / (double)busiest);
                var cell = new Border
                {
                    Width = 12,
                    Height = 12,
                    Margin = new Thickness(0, 0, 0, 3),
                    CornerRadius = new CornerRadius(3),
                    Background = words > 0
                        ? new SolidColorBrush(Color.FromArgb((byte)(255 * strength), 244, 244, 244))
                        : (Brush)FindResource(future ? "BgBrush" : "InputBrush"),
                    ToolTip = future ? null : $"{day:ddd d MMM} — {words:N0} words",
                };
                column.Children.Add(cell);
            }
            columns.Children.Add(column);
        }

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(dayLabels, 0);
        Grid.SetColumn(columns, 1);
        grid.Children.Add(dayLabels);
        grid.Children.Add(columns);
        InsightHeatmap.Children.Add(grid);

        InsightHeatmap.Children.Add(new TextBlock
        {
            Text = $"Last {weeks} weeks · busiest day {busiest:N0} words",
            Style = (Style)FindResource("Hint"),
            FontSize = 11,
            Margin = new Thickness(0, 10, 0, 0),
        });
    }

    // ----- Dictionary -----

    private void OnDictionaryChanged() => Dispatcher.BeginInvoke(RefreshDictionary);

    private void RefreshDictionary()
    {
        DictList.Children.Clear();
        foreach (var entry in DictionaryStore.Instance.Entries)
        {
            var enabled = new CheckBox
            {
                IsChecked = entry.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var id = entry.Id;
            enabled.Checked += (_, _) => DictionaryStore.Instance.SetEnabled(id, true);
            enabled.Unchecked += (_, _) => DictionaryStore.Instance.SetEnabled(id, false);

            var label = entry.IsVocabularyOnly
                ? $"{entry.Phrase}   (vocabulary)"
                : $"{entry.Phrase}  →  {entry.Replacement}";
            // Say where an entry came from, so an automatically learned one that
            // got it wrong is obvious and one click from gone.
            if (entry.LearnedFromCorrection) label += "   (learned from your correction)";
            var text = new TextBlock
            {
                Text = label,
                FontSize = 13,
                Foreground = (Brush)FindResource("TextBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 12, 0),
                TextWrapping = TextWrapping.Wrap,
                Opacity = entry.Enabled ? 1.0 : 0.45,
            };

            var delete = RowButton("Delete", "DangerButton");
            delete.Click += (_, _) => DictionaryStore.Instance.Delete(id);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(enabled, 0);
            Grid.SetColumn(text, 1);
            Grid.SetColumn(delete, 2);
            grid.Children.Add(enabled);
            grid.Children.Add(text);
            grid.Children.Add(delete);

            DictList.Children.Add(new Border
            {
                Background = (Brush)FindResource("CardBrush"),
                BorderBrush = (Brush)FindResource("StrokeBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16, 10, 16, 10),
                Margin = new Thickness(0, 0, 0, 8),
                Child = grid,
            });
        }
    }

    private void AddDictEntry_Click(object sender, RoutedEventArgs e)
    {
        var phrase = PhraseBox.Text.Trim();
        if (phrase.Length == 0)
        {
            PhraseBox.Focus();
            return;
        }
        DictionaryStore.Instance.Add(phrase, ReplacementBox.Text,
            WholeWordCheck.IsChecked == true);
        PhraseBox.Clear();
        ReplacementBox.Clear();
        PhraseBox.Focus();
    }

    // ----- Snapshot (hidden diagnostic) -----

    /// <summary>Render every page to PNG; the settings page also at full scroll height.</summary>
    internal void SnapshotPages(string dir)
    {
        var root = (FrameworkElement)Content;
        var bg = (Brush)FindResource("BgBrush");
        foreach (var (nav, name) in new[]
        {
            (NavHome, "home"), (NavInsights, "insights"), (NavNotes, "notes"),
            (NavDictionary, "dictionary"), (NavSettings, "settings"),
        })
        {
            nav.IsChecked = true;
            UpdateLayout();
            Snapshot.Save(root, System.IO.Path.Combine(dir, $"main-{name}.png"), bg);
        }
        Snapshot.Save(SettingsPanel, System.IO.Path.Combine(dir, "main-settings-full.png"), bg);
        NavInsights.IsChecked = true;
        UpdateLayout();
        Snapshot.Save(InsightsPanel, System.IO.Path.Combine(dir, "main-insights-full.png"), bg);
    }

    // ----- Shared -----

    private void UpdateFooter()
    {
        var t = Transcriber.Instance;
        var model = ModelCatalog.ById(t.CurrentModelId);
        var modelLine = t.IsLoading ? "Loading model…"
            : t.IsReady ? $"{model?.DisplayName} · {t.RuntimeLabel}"
            : "No model loaded";
        var hotkey = HotkeyDefs.CurrentDisplay(SettingsStore.Instance.Settings);
        StatusFooter.Text = $"{modelLine}\nHotkey: {hotkey}";
    }

    private Button RowButton(string text, string styleKey) => new()
    {
        Content = text,
        Style = (Style)FindResource(styleKey),
        Margin = new Thickness(8, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Filled = white on black (the active state); outlined = quiet label.</summary>
    private static Border Chip(string text, bool filled) => new()
    {
        Child = new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeights.Medium,
            Foreground = filled
                ? new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0A))
                : new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8)),
        },
        Background = filled ? Brushes.White : Brushes.Transparent,
        BorderBrush = filled ? Brushes.White : new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(7, 1, 7, 2),
        Margin = new Thickness(8, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };
}
