using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FlowType.Audio;
using FlowType.Core;
using FlowType.Engine;
using FlowType.Input;
using FlowType.Session;
using FlowType.UI.Controls;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Orientation = System.Windows.Controls.Orientation;
using ProgressBar = System.Windows.Controls.ProgressBar;
using TextWrapping = System.Windows.TextWrapping;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace FlowType.UI.Pages;

/// <summary>
/// Every preference, in nine sections: Dictation, Microphone, Speech model,
/// Output, Formatting, Context, Privacy, System, About. All of 1.x's settings
/// handlers live here unchanged in behaviour (hotkey combo and Detect key,
/// the live key monitor, mic test meter, model download/use/delete), plus the
/// 2.0 additions.
///
/// One handler, <see cref="Setting_Changed"/>, writes every control back to
/// the settings object, as in 1.x. That keeps the page impossible to get
/// half-saved, at the cost of rewriting values that did not change — which
/// is why <see cref="SyncFromSettings"/> exists: when something else (the
/// tray's Pause item, the flow bar's menu) changes a setting while this page
/// is open, the controls are brought up to date first, so the next save here
/// cannot put the old value back.
/// </summary>
public partial class SettingsPage : IMainPage
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

    private static readonly (string Value, string Label)[] VariantChoices =
    {
        (EnglishVariant.Off, "Leave as the AI writes it"),
        (EnglishVariant.Uk, "British — colour, realise, centre"),
        (EnglishVariant.Us, "American — color, realize, center"),
    };

    private static readonly (string Value, string Label)[] BarPositions =
    {
        ("bottom", "Bottom of screen"),
        ("top", "Top of screen"),
        ("hidden", "Hidden (tray + hotkey only)"),
    };

    private static readonly (string Value, string Label)[] MaxLengths =
    {
        ("2", "2 minutes"), ("5", "5 minutes"), ("15", "15 minutes"), ("60", "60 minutes"),
    };

    private static readonly (string Value, string Label)[] KeepRecordings =
    {
        ("0", "Don't keep audio"), ("10", "Last 10"), ("30", "Last 30"), ("100", "Last 100"),
    };

    private bool _loading = true;
    private bool _shown;
    private bool _micTesting;
    private bool _capturing;

    /// <summary>
    /// Bumped whenever a Detect-key capture starts or ends. The 8 s timeout and
    /// the captured-key callback each remember the generation they belong to,
    /// so a stale one (cancel, then Detect again within 8 s) cannot end or
    /// bind the newer capture.
    /// </summary>
    private int _captureGeneration;
    private readonly Dictionary<string, ProgressBar> _downloadBars = new();
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };

    public SettingsPage()
    {
        InitializeComponent();

        // Read the version from the assembly so this line can never go stale.
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        var versionText = version == null ? "" : $" {version.Major}.{version.Minor}.{version.Build}";
        AboutText.Text = $"FlowType{versionText} — local AI voice typing for Windows. "
            + "Everything runs on this PC; your voice and your words never leave it.";
        CreditText.Text = "Created by EvilJackk";

        PasteLastKeys.Children.Add(Ui.Keys("pastes it again", "Alt", "Shift", "Z"));
        PasteLastKeys.Children.Add(Ui.Keys("copies it", "Alt", "Shift", "X"));

        LoadSettingsControls();
        RefreshModels();
        _loading = false;

        ModelDownloader.Instance.ProgressChanged += OnDownloadProgress;
        ModelDownloader.Instance.Completed += OnDownloadCompleted;
        Transcriber.Instance.StateChanged += OnTranscriberChanged;
        SettingsStore.Instance.Changed += OnSettingsStoreChanged;
        AudioRecorder.Instance.LevelChanged += OnMicLevel;
        RecordingArchive.Instance.Changed += OnArchiveChanged;
        UpdateArchiveUsage();

        // Live hotkey truth: a held/not-held dot plus the last raw key the
        // hook observed, so "the key does nothing" is diagnosable on sight.
        if (App.Hotkeys != null) App.Hotkeys.KeyObserved += OnKeyObserved;
        _liveTimer.Tick += (_, _) => UpdateLiveHotkeyState();
    }

    public FrameworkElement SnapshotBody => Body;

    public void OnShown()
    {
        _shown = true;
        SyncFromSettings();
        RefreshModels();
        _liveTimer.Start();
    }

    public void OnHidden()
    {
        _shown = false;
        _liveTimer.Stop();
        StopMicTest();
    }

    public void Detach()
    {
        StopMicTest();
        _liveTimer.Stop();
        if (App.Hotkeys != null)
        {
            App.Hotkeys.KeyObserved -= OnKeyObserved;
            if (_capturing) App.Hotkeys.CancelCapture();
        }
        _capturing = false;
        _captureGeneration++;
        ModelDownloader.Instance.ProgressChanged -= OnDownloadProgress;
        ModelDownloader.Instance.Completed -= OnDownloadCompleted;
        Transcriber.Instance.StateChanged -= OnTranscriberChanged;
        SettingsStore.Instance.Changed -= OnSettingsStoreChanged;
        AudioRecorder.Instance.LevelChanged -= OnMicLevel;
        RecordingArchive.Instance.Changed -= OnArchiveChanged;
    }

    // ----- Sections -----

    /// <summary>Scroll so a section heading sits at the top ("model", "privacy"…).</summary>
    public void ScrollToSection(string section)
    {
        FrameworkElement? target = section.ToLowerInvariant() switch
        {
            "dictation" or "hotkey" => SecDictation,
            "microphone" or "mic" => SecMicrophone,
            "model" or "speech" => SecModel,
            "output" => SecOutput,
            "formatting" => SecFormatting,
            "context" => SecContext,
            "privacy" => SecPrivacy,
            "system" => SecSystem,
            "about" => SecAbout,
            _ => null,
        };
        if (target == null) return;
        UpdateLayout();
        try
        {
            var y = target.TranslatePoint(new System.Windows.Point(0, 0), Body).Y;
            Scroller.ScrollToVerticalOffset(Math.Max(0, y - 18));
        }
        catch (InvalidOperationException)
        {
            // Not in the tree yet; the caller retries after layout.
        }
    }

    private void Jump_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string section }) ScrollToSection(section);
    }

    // ----- Load & save -----

    private void LoadSettingsControls()
    {
        var s = SettingsStore.Instance.Settings;

        LoadHotkeyCombo();

        LanguageCombo.Items.Clear();
        foreach (var (code, name) in Languages)
        {
            LanguageCombo.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        }

        Ui.FillCombo(EnglishVariantCombo, VariantChoices, s.EnglishVariant);
        Ui.FillCombo(BarPositionCombo, BarPositions, s.BarPosition);
        Ui.FillCombo(MaxLengthCombo, MaxLengths, s.MaxRecordingMinutes.ToString(), v => $"{v} minutes");
        Ui.FillCombo(KeepRecordingsCombo, KeepRecordings, s.KeepRecentRecordings.ToString(), v => $"Last {v}");

        LoadMicCombo();
        SyncFromSettings();
    }

    /// <summary>
    /// Put every control in step with the saved settings without rebuilding
    /// lists (an open drop-down would close). Safe to call any time.
    /// </summary>
    private void SyncFromSettings()
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            var s = SettingsStore.Instance.Settings;

            if (SelectedTag(HotkeyCombo) != s.Hotkey || s.Hotkey == "Custom") LoadHotkeyCombo();
            ModeSmartRadio.IsChecked = s.ActivationMode is not ("hold" or "toggle");
            ModeHoldRadio.IsChecked = s.ActivationMode == "hold";
            ModeToggleRadio.IsChecked = s.ActivationMode == "toggle";

            var langIndex = Array.FindIndex(Languages, l => l.Code == s.Language);
            LanguageCombo.SelectedIndex = langIndex >= 0 ? langIndex : 0;
            Select(EnglishVariantCombo, s.EnglishVariant);
            Select(BarPositionCombo, s.BarPosition);
            Select(MaxLengthCombo, s.MaxRecordingMinutes.ToString());
            Select(KeepRecordingsCombo, s.KeepRecentRecordings.ToString());
            Select(MicCombo, s.MicDeviceName);
            // Saved elsewhere (onboarding) as a device this list does not have:
            // rebuild so it gets its "(not connected)" entry, rather than leaving
            // the old selection for the next save here to write back.
            if (SelectedTag(MicCombo) != (s.MicDeviceName ?? "")) LoadMicCombo();

            SoundsCheck.IsChecked = s.PlaySounds;
            MuteMediaCheck.IsChecked = s.MuteMediaWhileDictating;
            GpuCheck.IsChecked = s.UseGpu;
            AccurateCheck.IsChecked = s.AccurateDecoding;
            LiveTranscriptionCheck.IsChecked = s.LiveTranscription;
            SaveHistoryCheck.IsChecked = s.SaveHistory;
            DiagnosticLogCheck.IsChecked = s.DiagnosticLog;
            PauseCheck.IsChecked = s.DictationPaused;
            AutoInsertCheck.IsChecked = s.AutoInsert;
            PasteRadio.IsChecked = s.InsertMethod != "type";
            TypeRadio.IsChecked = s.InsertMethod == "type";
            PasteRadio.IsEnabled = TypeRadio.IsEnabled = s.AutoInsert;
            RestoreClipboardCheck.IsChecked = s.RestoreClipboard;
            PressEnterCheck.IsChecked = s.PressEnterCommand;
            PasteLastCheck.IsChecked = s.PasteLastShortcut;
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
            ScreenContextCheck.IsChecked = s.UseScreenContext;
            StartupCheck.IsChecked = StartupHelper.IsEnabled;
            ShowBarCheck.IsChecked = s.ShowBarWhenIdle;
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private static string? SelectedTag(ComboBox combo) =>
        combo.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : null;

    /// <summary>Select the item whose Tag is <paramref name="value"/>, if present.</summary>
    private static void Select(ComboBox combo, string value)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem { Tag: string tag } && tag == value)
            {
                if (combo.SelectedIndex != i) combo.SelectedIndex = i;
                return;
            }
        }
    }

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

    private void LoadMicCombo()
    {
        IReadOnlyList<string> devices;
        try
        {
            devices = AudioRecorder.GetInputDeviceNames();
        }
        catch
        {
            // No audio subsystem (a locked-down VM, a driver mid-update):
            // "System default" still works once one appears.
            devices = Array.Empty<string>();
        }

        var (items, selected) = MicChoices(SettingsStore.Instance.Settings.MicDeviceName, devices);
        MicCombo.Items.Clear();
        foreach (var (value, label) in items)
        {
            MicCombo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        }
        MicCombo.SelectedIndex = selected;
    }

    /// <summary>
    /// The microphone list as (value, label) pairs and the index to select.
    ///
    /// <para>A saved microphone that is not plugged in right now keeps its own
    /// "(not connected)" entry and stays selected. Falling back to "System
    /// default" instead looked harmless, but <see cref="Setting_Changed"/>
    /// writes every control back on any change, so the next unrelated toggle
    /// would have replaced the saved headset with "" — and plugging it back in
    /// would no longer bring it back. The recorder already falls back to the
    /// default device on its own while the saved one is missing.</para>
    /// </summary>
    internal static (List<(string Value, string Label)> Items, int Selected) MicChoices(
        string? saved, IEnumerable<string> devices)
    {
        saved ??= "";
        var items = new List<(string Value, string Label)> { ("", "System default") };
        var selected = 0;
        foreach (var name in devices)
        {
            if (string.IsNullOrEmpty(name) || items.Any(i => i.Value == name)) continue;
            if (saved.Length > 0 && name == saved) selected = items.Count;
            items.Add((name, name));
        }
        if (saved.Length > 0 && selected == 0)
        {
            selected = items.Count;
            items.Add((saved, $"{saved} (not connected)"));
        }
        return (items, selected);
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var s = SettingsStore.Instance.Settings;
        var keepBefore = s.KeepRecentRecordings;

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
        s.EnglishVariant = Ui.ComboValue(EnglishVariantCombo, EnglishVariant.Off);
        s.BarPosition = Ui.ComboValue(BarPositionCombo, "bottom");
        s.MaxRecordingMinutes = int.TryParse(Ui.ComboValue(MaxLengthCombo, "5"), out var mins) ? mins : 5;
        s.KeepRecentRecordings = int.TryParse(Ui.ComboValue(KeepRecordingsCombo, "30"), out var keep)
            ? keep : keepBefore;
        s.PlaySounds = SoundsCheck.IsChecked == true;
        s.MuteMediaWhileDictating = MuteMediaCheck.IsChecked == true;
        s.UseGpu = GpuCheck.IsChecked == true;
        s.AccurateDecoding = AccurateCheck.IsChecked == true;
        s.LiveTranscription = LiveTranscriptionCheck.IsChecked == true;
        s.SaveHistory = SaveHistoryCheck.IsChecked == true;
        s.DiagnosticLog = DiagnosticLogCheck.IsChecked == true;
        s.DictationPaused = PauseCheck.IsChecked == true;
        s.AutoInsert = AutoInsertCheck.IsChecked == true;
        s.InsertMethod = TypeRadio.IsChecked == true ? "type" : "paste";
        s.RestoreClipboard = RestoreClipboardCheck.IsChecked == true;
        s.PressEnterCommand = PressEnterCheck.IsChecked == true;
        s.PasteLastShortcut = PasteLastCheck.IsChecked == true;
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
        s.UseScreenContext = ScreenContextCheck.IsChecked == true;
        s.ShowBarWhenIdle = ShowBarCheck.IsChecked == true;

        PasteRadio.IsEnabled = TypeRadio.IsEnabled = s.AutoInsert;

        SettingsStore.Instance.Save();
        StartupHelper.SetEnabled(StartupCheck.IsChecked == true);

        // Fewer recordings to keep takes effect now, not at the next dictation.
        if (s.KeepRecentRecordings < keepBefore)
        {
            try { RecordingArchive.Instance.Prune(); }
            catch { /* the archive retries on its own next time */ }
        }
    }

    private void OnSettingsStoreChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (_shown) SyncFromSettings();
    }, DispatcherPriority.Background);

    private void RefreshMics_Click(object sender, RoutedEventArgs e)
    {
        var wasLoading = _loading;
        _loading = true;
        LoadMicCombo();
        _loading = wasLoading;
    }

    private void OnArchiveChanged() => Dispatcher.BeginInvoke(UpdateArchiveUsage, DispatcherPriority.Background);

    /// <summary>"12 recordings kept · 4.3 MB" — what the Keep setting is costing right now.</summary>
    private void UpdateArchiveUsage()
    {
        try
        {
            var (count, bytes) = RecordingArchive.Instance.Usage();
            ArchiveUsageText.Text = count == 0
                ? "No recordings kept right now."
                : $"{count:N0} {Ui.Plural(count, "recording", "recordings")} kept · {bytes / (1024.0 * 1024.0):0.#} MB";
            DeleteAudioButton.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        catch
        {
            ArchiveUsageText.Text = "";
        }
    }

    private void OpenAudioFolder_Click(object sender, RoutedEventArgs e) =>
        OpenFolder(RecordingArchive.Instance.ArchiveDir);

    private void DeleteAudio_Click(object sender, RoutedEventArgs e)
    {
        var answer = Ui.Message(this,
            "Delete every saved recording now? Your transcripts stay; you just won't be able to play them back or transcribe them again.",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        try
        {
            RecordingArchive.Instance.DeleteAll();
            UpdateArchiveUsage();
        }
        catch (Exception ex)
        {
            Ui.Message(this, $"Couldn't delete every recording:\n{ex.Message}", MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.DataDir);

    private void OpenModelsFolder_Click(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.ModelsDir);

    private void OpenFolder(string path)
    {
        try
        {
            AppPaths.EnsureCreated();
            Process.Start(new ProcessStartInfo("explorer.exe", path));
        }
        catch (Exception ex)
        {
            Ui.Message(this, $"Couldn't open the folder:\n{ex.Message}", MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
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
        var generation = ++_captureGeneration;
        DetectKeyButton.Content = "Cancel";
        CaptureStatusText.Text = "Listening… press the key or mouse button you want to use.";

        hotkeys.CaptureNextKey(vk => Dispatcher.BeginInvoke(() =>
        {
            if (!_capturing || generation != _captureGeneration) return;
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

        // If nothing arrives, that itself is the diagnosis. Only for this
        // capture: a timer left over from one the user cancelled must not cut
        // short the one they started after it.
        _ = Task.Delay(TimeSpan.FromSeconds(8)).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
        {
            if (!_capturing || generation != _captureGeneration) return;
            hotkeys.CancelCapture();
            EndCapture("No key detected in 8s. If you pressed one, your keyboard software " +
                "(G HUB, Synapse, remapper) is swallowing it before it reaches Windows — " +
                "try a different key or a mouse button.");
        }));
    }

    private void EndCapture(string status)
    {
        _capturing = false;
        _captureGeneration++;
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
        if (hotkeys == null || !_shown) return;

        var held = hotkeys.IsHotkeyDown;
        var paused = SettingsStore.Instance.Settings.DictationPaused;
        HotkeyStateDot.Fill = held
            ? Ui.BrushOf(paused ? "DangerBrush" : "SuccessBrush")
            : Ui.BrushOf("StrokeStrongBrush");
        HotkeyStateText.Text = held
            ? paused ? "hotkey detected — but dictation is paused" : "hotkey held — it works!"
            : "hotkey not pressed";
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
                Ui.Message(this, $"Couldn't open the microphone:\n{ex.Message}", MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }

    private void StopMicTest()
    {
        if (!_micTesting) return;
        try { AudioRecorder.Instance.StopMonitor(); }
        catch { /* device already gone */ }
        _micTesting = false;
        MicTestButton.Content = "Test";
        MicLevelBar.Value = 0;
    }

    private void OnMicLevel(float level)
    {
        if (!_micTesting) return;
        Dispatcher.BeginInvoke(() => MicLevelBar.Value = Math.Min(1.0, level * 1.6));
    }

    // ----- Speech models -----

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
        titleRow.Children.Add(Ui.Text(model.DisplayName, 15, "TextBrush", FontWeights.Bold));
        if (isRecommended) titleRow.Children.Add(Ui.Chip("Recommended"));
        if (isActive) titleRow.Children.Add(Ui.Chip("Active", ChipTone.Accent));
        info.Children.Add(titleRow);
        info.Children.Add(new TextBlock
        {
            Text = model.Blurb,
            Style = Ui.StyleOf("Hint"),
            Margin = new Thickness(0, 4, 0, 0),
        });
        info.Children.Add(new TextBlock
        {
            Text = $"{model.SizeLabel}  ·  {model.LanguageLabel}",
            FontSize = 12.5,
            Foreground = Ui.BrushOf("FaintBrush"),
            Margin = new Thickness(0, 5, 0, 0),
        });

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
        };

        if (downloading)
        {
            var bar = new ProgressBar
            {
                Width = 140, Height = 6, Minimum = 0, Maximum = 1,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _downloadBars[model.Id] = bar;
            actions.Children.Add(bar);
            var cancel = Ui.TextButton("Cancel", "GhostButton", 12);
            cancel.Click += (_, _) => ModelDownloader.Instance.Cancel(model.Id);
            actions.Children.Add(cancel);
        }
        else if (model.IsDownloaded)
        {
            if (!isActive)
            {
                var use = Ui.TextButton("Use", "AccentButton", 0);
                use.Click += (_, _) => UseModel(model.Id);
                actions.Children.Add(use);
            }
            var delete = Ui.TextButton("Delete", "DangerButton", isActive ? 0 : 8);
            delete.Click += (_, _) =>
            {
                try
                {
                    ModelDownloader.Instance.Delete(model.Id);
                }
                catch (Exception ex)
                {
                    Ui.Message(this, $"Couldn't delete the model:\n{ex.Message}", MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                RefreshModels();
            };
            actions.Children.Add(delete);
        }
        else
        {
            var download = Ui.TextButton("Download", "AccentButton", 0);
            download.Click += (_, _) =>
            {
                _ = ModelDownloader.Instance.DownloadAsync(model.Id);
                RefreshModels();
            };
            actions.Children.Add(download);
        }

        var grid = Ui.Columns(Ui.Star, Ui.Auto);
        grid.Children.Add(info.At(0));
        grid.Children.Add(actions.At(1));

        var row = new Border { Style = Ui.StyleOf("InsetRow"), Child = grid };
        if (isActive) row.BorderBrush = Ui.BrushOf("AccentLineBrush");
        return row;
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
            Ui.Message(this, $"Couldn't load the model:\n{ex.Message}", MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnDownloadProgress(string modelId, double progress) => Dispatcher.BeginInvoke(() =>
    {
        if (_downloadBars.TryGetValue(modelId, out var bar)) bar.Value = progress;
        else if (_shown) RefreshModels();
    });

    private void OnDownloadCompleted(string modelId, string? error) => Dispatcher.BeginInvoke(() =>
    {
        RefreshModels();
        if (error != null)
        {
            Ui.Message(this, $"Download failed:\n{error}", MessageBoxButton.OK, MessageBoxImage.Error);
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
        if (_shown) RefreshModels();
    });
}
