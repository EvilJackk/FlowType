using System.IO;
using System.Text;
using System.Windows;
using FlowType.Audio;
using FlowType.Context;
using FlowType.Core;
using FlowType.Engine;
using FlowType.Input;
using FlowType.Session;
using FlowType.UI;
using WinForms = System.Windows.Forms;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace FlowType;

/// <summary>
/// Entry point: single-instance guard, hotkey hook, flow bar, tray icon,
/// background model warm-up, and first-run onboarding. FlowType lives in the
/// tray + flow bar; closing windows never quits it.
/// </summary>
public partial class App : Application
{
    private Mutex? _instanceMutex;
    private HotkeyManager? _hotkeys;

    /// <summary>The live hotkey watcher, for the settings page (detect-key, live monitor).</summary>
    public static HotkeyManager? Hotkeys { get; private set; }
    private WinForms.NotifyIcon? _tray;
    private WinForms.ToolStripMenuItem? _recordMenuItem;
    private WinForms.ToolStripMenuItem? _pauseMenuItem;
    private WinForms.ToolStripMenuItem? _statusMenuItem;
    private FlowBarWindow? _bar;
    private MainWindow? _main;
    private OnboardingWindow? _onboarding;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--selftest"))
        {
            RunSelfTest();
            return;
        }

        if (e.Args.Contains("--keylog"))
        {
            RunKeyLog();
            return;
        }

        if (e.Args.Contains("--pastetest"))
        {
            RunPasteTest();
            return;
        }

        var dictIndex = Array.IndexOf(e.Args, "--dictcheck");
        if (dictIndex >= 0)
        {
            RunDictCheck(e.Args.Skip(dictIndex + 1).ToArray());
            return;
        }

        // Before --bench: "--learncheck --bench" is this check with the
        // personal benchmark, not the clip-folder benchmark.
        if (e.Args.Contains("--learncheck"))
        {
            RunLearnCheck(bench: e.Args.Contains("--bench"), useWispr: !e.Args.Contains("--no-wispr"));
            return;
        }

        var conditionIndex = Array.IndexOf(e.Args, "--conditioncheck");
        if (conditionIndex >= 0)
        {
            RunConditionCheck(e.Args.ElementAtOrDefault(conditionIndex + 1) ?? "");
            return;
        }

        var benchIndex = Array.IndexOf(e.Args, "--bench");
        if (benchIndex >= 0)
        {
            RunBench(e.Args.ElementAtOrDefault(benchIndex + 1) ?? "");
            return;
        }

        if (e.Args.Contains("--llmcheck"))
        {
            RunLlmCheck();
            return;
        }

        var liveIndex = Array.IndexOf(e.Args, "--livecheck");
        if (liveIndex >= 0)
        {
            RunLiveCheck(e.Args.ElementAtOrDefault(liveIndex + 1) ?? "");
            return;
        }

        var snapshotIndex = Array.IndexOf(e.Args, "--snapshot");
        if (snapshotIndex >= 0)
        {
            RunSnapshot(e.Args.ElementAtOrDefault(snapshotIndex + 1)
                ?? Path.Combine(AppPaths.DataDir, "snapshot"));
            return;
        }

        _instanceMutex = new Mutex(initiallyOwned: true, "FlowType_SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("FlowType is already running — look for the bar at the bottom of your screen or the tray icon.",
                "FlowType", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        AppPaths.EnsureCreated();
        AudioRecorder.CleanupOldRecordings();
        // If FlowType died while other apps were muted, give them their sound back.
        MediaMuter.Instance.RestoreAfterCrash();
        // Keep the recordings archive in step with History (orphans, retention count).
        _ = Task.Run(() => NotesStore.Instance.ReconcileRecordings());

        _hotkeys = new HotkeyManager(Dispatcher);
        Hotkeys = _hotkeys;
        DictationSession.Instance.Attach(_hotkeys);
        CorrectionWatcher.Instance.Attach(_hotkeys);
        _hotkeys.PasteLastRequested += () => _ = UserActions.PasteLastAsync();
        // UI Automation's first call costs 50-230 ms; pay it now, not on the first dictation.
        FocusContext.Prewarm();
        _hotkeys.CopyLastRequested += UserActions.CopyLast;
        DictationSession.Instance.SetupNeeded +=
            () => Dispatcher.BeginInvoke(() => ShowMain(page: MainPage.Settings, section: "model"));
        DictationSession.Instance.StateChanged +=
            (_, _) => Dispatcher.BeginInvoke(UpdateTrayMenu);

        // A new microphone is worth a word; a vanished one mid-take is handled
        // by the recorder itself (it keeps what was captured).
        AudioDeviceWatcher.Instance.CaptureDeviceAdded += name =>
            Dispatcher.BeginInvoke(() => DictationSession.Instance.Notify($"New microphone: {name} — pick it in Settings → Microphone"));
        AudioDeviceWatcher.Instance.CaptureEndpointRemoved += endpoint =>
            Dispatcher.BeginInvoke(() => DictationSession.Instance.OnCaptureEndpointRemoved(endpoint));
        AudioDeviceWatcher.Instance.Start();
        // A crash must not leave the user's music muted until the next start.
        AppDomain.CurrentDomain.UnhandledException += (_, _) =>
        {
            try { MediaMuter.Instance.RestoreAsync().Wait(TimeSpan.FromSeconds(1)); } catch { }
        };

        _bar = new FlowBarWindow();
        _bar.OpenRequested += () => ShowMain();
        _bar.SettingsRequested += () => ShowMain(settings: true);
        _bar.QuitRequested += QuitApp;

        SetupTray();

        // Warm the saved models so the first dictation is instant. Speech first
        // (it is what every dictation needs), then the optional language model
        // — one at a time, so two large loads never compete for video memory.
        _ = Task.Run(async () =>
        {
            try { await Transcriber.Instance.InitializeAsync(); } catch { }
            // The daily "learn from your writing" check; its first look is
            // ten minutes out, well clear of the warm-up.
            Learning.BackgroundLearner.Start();
            await LocalLlm.Instance.InitializeAsync();
        });

        if (!SettingsStore.Instance.Settings.OnboardingComplete)
        {
            _onboarding = new OnboardingWindow();
            _onboarding.Completed += () => ShowMain();
            _onboarding.Show();
            _onboarding.Activate();
        }
        else if (string.IsNullOrEmpty(SettingsStore.Instance.Settings.SelectedModel)
            || ModelCatalog.ById(SettingsStore.Instance.Settings.SelectedModel)?.IsDownloaded != true)
        {
            ShowMain(settings: true);
        }
        else if (e.Args.Contains("--open"))
        {
            // Handy for shortcuts: launch straight into the main window.
            ShowMain(settings: e.Args.Contains("--settings"));
        }
    }

    // ----- Tray -----

    private void SetupTray()
    {
        var menu = BuildTrayMenu();

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "flowtype.ico");
        _tray = new WinForms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(iconPath),
            Text = "FlowType — local voice typing",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(() => ShowMain());

        UpdateTrayMenu();
    }

    private WinForms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new WinForms.ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            BackColor = DarkMenuRenderer.Background,
            ForeColor = DarkMenuRenderer.Text,
            Font = new System.Drawing.Font("Segoe UI", 9.75f),
            ShowImageMargin = false,
            Padding = new WinForms.Padding(4, 6, 4, 6),
            DropShadowEnabled = true,
        };

        // Status caption: what FlowType is doing right now, at a glance.
        _statusMenuItem = new WinForms.ToolStripMenuItem("FlowType")
        {
            Enabled = false,
            Font = new System.Drawing.Font("Segoe UI", 8.5f),
        };
        menu.Items.Add(_statusMenuItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());

        _recordMenuItem = new WinForms.ToolStripMenuItem("Start dictating")
        {
            Font = new System.Drawing.Font("Segoe UI", 9.75f, System.Drawing.FontStyle.Bold),
        };
        _recordMenuItem.Click += (_, _) =>
            Dispatcher.BeginInvoke(() => DictationSession.Instance.Toggle());
        menu.Items.Add(_recordMenuItem);

        // Copy, not paste: opening the tray menu makes FlowType the foreground
        // window, so a paste from here would land in FlowType itself.
        // Alt+Shift+Z pastes straight into the app you are in.
        var copyLast = new WinForms.ToolStripMenuItem("Copy last dictation   (Alt+Shift+Z pastes it)");
        copyLast.Click += (_, _) => Dispatcher.BeginInvoke(UserActions.CopyLast);
        menu.Items.Add(copyLast);

        // No checkbox glyph: the icon gutter is switched off for a cleaner look,
        // so this item states its state in words instead.
        _pauseMenuItem = new WinForms.ToolStripMenuItem("Pause dictation");
        _pauseMenuItem.Click += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            var settings = SettingsStore.Instance.Settings;
            settings.DictationPaused = !settings.DictationPaused;
            SettingsStore.Instance.Save();
            UpdateTrayMenu();
        });
        menu.Items.Add(_pauseMenuItem);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        var open = new WinForms.ToolStripMenuItem("Open FlowType");
        open.Click += (_, _) => Dispatcher.BeginInvoke(() => ShowMain());
        menu.Items.Add(open);

        var settings = new WinForms.ToolStripMenuItem("Settings");
        settings.Click += (_, _) => Dispatcher.BeginInvoke(() => ShowMain(settings: true));
        menu.Items.Add(settings);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        var quit = new WinForms.ToolStripMenuItem("Quit FlowType");
        quit.Click += (_, _) => Dispatcher.BeginInvoke(QuitApp);
        menu.Items.Add(quit);

        // Refresh the caption each time it opens, not on a timer.
        menu.Opening += (_, _) => UpdateTrayMenu();
        menu.Opened += (_, _) => TrayMenuStyle.ApplyRoundedCorners(menu);

        foreach (var item in menu.Items.OfType<WinForms.ToolStripMenuItem>())
        {
            item.Padding = new WinForms.Padding(6, 3, 6, 3);
        }

        return menu;
    }

    private void UpdateTrayMenu()
    {
        var recording = DictationSession.Instance.State == SessionState.Recording;

        if (_recordMenuItem != null)
        {
            _recordMenuItem.Text = recording ? "Stop dictating" : "Start dictating";
        }
        if (_pauseMenuItem != null)
        {
            _pauseMenuItem.Text = SettingsStore.Instance.Settings.DictationPaused
                ? "Resume dictation"
                : "Pause dictation";
        }
        if (_statusMenuItem != null)
        {
            var transcriber = Transcriber.Instance;
            var hotkey = HotkeyDefs.CurrentDisplay(SettingsStore.Instance.Settings);
            _statusMenuItem.Text = transcriber.IsLoading
                ? "Loading model…"
                : !transcriber.IsReady
                    ? "No AI model — open Settings"
                    : SettingsStore.Instance.Settings.DictationPaused
                        ? "Paused"
                        : recording
                            ? "Listening…"
                            : $"Ready · hold {hotkey}";
        }
    }

    // ----- Windows -----

    private void ShowMain(bool settings = false, MainPage? page = null, string? section = null)
    {
        if (_main == null)
        {
            _main = new MainWindow();
            _main.Closed += (_, _) => _main = null;
            _main.Show();
        }
        else
        {
            _main.Show();
            if (_main.WindowState == WindowState.Minimized)
            {
                _main.WindowState = WindowState.Normal;
            }
        }
        _main.Activate();
        if (page != null) _main.NavigateTo(page.Value, section);
        else if (settings) _main.NavigateToSettings();
    }

    private void QuitApp()
    {
        _bar?.AllowClose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        RecordingArchive.Instance.StopPlayback();
        Learning.BackgroundLearner.Stop();
        // Never leave other apps silenced behind us.
        try { MediaMuter.Instance.RestoreAsync().Wait(TimeSpan.FromSeconds(2)); } catch { }
        AudioDeviceWatcher.Instance.Dispose();
        _hotkeys?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    // ----- On-device AI check (hidden diagnostic: FlowType.exe --llmcheck) -----

    /// <summary>
    /// Run the AI polish over a fixed set of realistic transcripts and report
    /// what came back, how long it took, and whether the guard kept it. Writes
    /// %APPDATA%\FlowType\llmcheck.txt. This is the instrument for prompt and
    /// guard changes: run it before and after, compare.
    /// </summary>
    private async void RunLlmCheck()
    {
        var log = new StringBuilder();
        try
        {
            var model = LlmCatalog.ById(SettingsStore.Instance.Settings.AiModel) ?? LlmCatalog.BestDownloaded();
            if (model == null)
            {
                log.AppendLine("No on-device model installed — download one in FlowType → AI.");
                return;
            }
            var load = System.Diagnostics.Stopwatch.StartNew();
            await LocalLlm.Instance.LoadAsync(model.Id);
            log.AppendLine($"model {model.Id} loaded in {load.ElapsedMilliseconds} ms on {LocalLlm.Instance.RuntimeLabel}");

            var cases = new (string Text, AppKind Kind)[]
            {
                ("So um basically I was thinking we should uh move the meeting to Thursday, no wait, Friday at like 3 PM. And um can you also send me the the quarterly report before then? Thanks.", AppKind.Email),
                ("Hey can you tell me what time the deploy is going out today", AppKind.WorkMessages),
                ("I think the the issue is in the hotkey manager. Actually no, it's in the audio recorder, the callback fires after dispose.", AppKind.Code),
                ("Buy milk eggs bread and also coffee beans", AppKind.Other),
                ("What is the capital of France", AppKind.AiPrompts),
                ("Hi Sarah, thanks for the update. I'll review the doc tonight and uh get back to you tomorrow morning. Best, Alex", AppKind.Email),
                ("lol yeah that was so funny i cant believe he did that", AppKind.PersonalMessages),
                ("Write a function that returns the sum of two numbers", AppKind.AiPrompts),
                ("We need to fix three things first the hotkey second the paste and third the um the bar position", AppKind.WorkMessages),
                ("Okay so the plan is we ship on Monday, sorry, I mean Tuesday, and then we do the retro on Wednesday.", AppKind.WorkMessages),
            };
            foreach (var (text, kind) in cases)
            {
                var style = WritingStyles.For(kind);
                var ctx = new WritingContext(AppCategory.Label(kind), kind, style, "en",
                    DictionaryStore.Instance.VocabularySections.Spellings);
                var result = await AiWriter.PolishAsync(text, ctx, CancellationToken.None);
                var styled = WritingStyles.Apply(result.Text, style, ctx.Vocabulary?.ToList());
                log.AppendLine($"[{result.Milliseconds,5} ms] [{style}] {(result.Applied ? "APPLIED " : "KEPT    ")} {result.Reason}");
                log.AppendLine($"   in : {text}");
                log.AppendLine($"   out: {styled.Replace("\n", " ⏎ ")}");
            }

            var command = await AiWriter.CommandAsync("make this more formal",
                "hey can u send me the report by friday thx", new WritingContext("Outlook", AppKind.Email), CancellationToken.None);
            log.AppendLine($"command (rewrite selection): {command.Text ?? command.Note}");
            var write = await AiWriter.CommandAsync("write a two sentence reply saying I can't make the meeting but will send notes",
                null, new WritingContext("Slack", AppKind.WorkMessages), CancellationToken.None);
            log.AppendLine($"command (write at cursor): {write.Text ?? write.Note}");
            log.AppendLine("LLMCHECK DONE");
        }
        catch (Exception ex)
        {
            log.AppendLine($"LLMCHECK FAILED: {ex}");
        }
        finally
        {
            AppPaths.EnsureCreated();
            File.WriteAllText(Path.Combine(AppPaths.DataDir, "llmcheck.txt"), log.ToString());
            Shutdown();
        }
    }

    // ----- Learn-from-history check (hidden diagnostic: FlowType.exe --learncheck [--bench] [--no-wispr]) -----

    /// <summary>
    /// Runs "Learn from your writing" over the real histories exactly as the
    /// button does — FlowType's notes and, unless --no-wispr, Wispr Flow's
    /// database (read-only) — and, with --bench, the personal benchmark on the
    /// user's selected speech model (--model &lt;id&gt; overrides, as in
    /// --livecheck). Writes %APPDATA%\FlowType\learncheck.txt with counts,
    /// timings and benchmark numbers only: no term, no dictation text, no
    /// path, no source message — the file is safe to paste anywhere. A dry
    /// run: nothing is applied and learning.json, attempts.log and settings
    /// are left as they were. Exit code 0 when the run completed, 1 if not.
    /// </summary>
    private async void RunLearnCheck(bool bench, bool useWispr)
    {
        var log = new StringBuilder();
        var ok = false;
        try
        {
            var settings = SettingsStore.Instance.Settings;
            log.AppendLine($"FlowType learn check {DateTime.Now:yyyy-MM-dd HH:mm}");
            log.AppendLine($"request: flowtype=yes wispr={(useWispr ? "yes" : "no")} bench={(bench ? "yes" : "no")} " +
                $"live-transcription={settings.LiveTranscription} accurate={settings.AccurateDecoding}");

            if (bench)
            {
                var args = Environment.GetCommandLineArgs();
                var modelIndex = Array.IndexOf(args, "--model");
                var modelId = modelIndex >= 0 ? args.ElementAtOrDefault(modelIndex + 1) ?? "" : "";
                if (string.IsNullOrWhiteSpace(modelId)) modelId = settings.SelectedModel;
                var loadWatch = System.Diagnostics.Stopwatch.StartNew();
                await Transcriber.Instance.LoadModelAsync(modelId);
                log.AppendLine($"model: {modelId} loaded in {loadWatch.ElapsedMilliseconds} ms on {Transcriber.Instance.RuntimeLabel}");
            }

            var learner = Learning.HistoryLearner.Instance;
            learner.DryRun = true;
            var report = await learner.RunAsync(
                new Learning.LearningRequest(UseFlowType: true, UseWispr: useWispr, RunBench: bench),
                progress: null, CancellationToken.None);

            foreach (var s in report.Sources)
            {
                log.AppendLine($"source {s.Source}: available={s.Available} dictations={s.Dictations} " +
                    $"with-audio={s.WithAudio} audio={s.AudioSeconds / 60:0.0} min edited={s.Edited} " +
                    $"problem={(s.Problem == null ? "no" : "yes")}");
            }
            var stats = learner.LastStats;
            log.AppendLine($"read: {report.SamplesRead} dictations " +
                $"(FlowType {stats?.FlowTypeSamples ?? 0}, Wispr Flow {stats?.WisprSamples ?? 0})");
            log.AppendLine($"suggestions: {report.Suggestions.Count} (recommended {report.Suggestions.Count(x => x.Recommended)})");
            foreach (var kind in Enum.GetValues<Learning.SuggestionKind>())
            {
                var ofKind = report.OfKind(kind).ToList();
                // Origins are fixed labels set by the miners; anything else is
                // counted as "other" so no free text can reach the file.
                var origins = ofKind.GroupBy(x => KnownOrigins.Contains(x.Origin) ? x.Origin : "other")
                    .OrderBy(g => g.Key, StringComparer.Ordinal)
                    .Select(g => $"{g.Key} {g.Count()}");
                log.AppendLine($"  {kind}: {ofKind.Count} (recommended {ofKind.Count(x => x.Recommended)})" +
                    (ofKind.Count > 0 ? $" — {string.Join(", ", origins)}" : ""));
            }
            log.AppendLine($"notes: {report.Notes.Count}");
            if (stats != null)
            {
                log.AppendLine($"timings: sources {stats.SourcesMs} ms, mining {stats.MiningMs} ms, " +
                    $"bench {stats.BenchMs} ms, total {stats.TotalMs} ms");
            }
            if (report.Bench is { } b)
            {
                log.AppendLine($"bench: recordings={b.Recordings} audio={b.AudioSeconds / 60:0.0} min reference-words={b.ReferenceWords} " +
                    $"agreement before={b.BaselineAccuracy:P1} after={b.LearnedAccuracy:P1}");
                log.AppendLine($"bench: proposed-word occurrences={b.TermOccurrences} right before={b.TermHitsBaseline} " +
                    $"after={b.TermHitsLearned}");
                log.AppendLine($"bench: live-chunks={b.LiveChunks} forced-cuts={b.ForcedCuts} " +
                    $"real-time-factor={b.RealTimeFactor:0.000} model={b.ModelId} recognition-fixes={b.RecognitionFixes.Count}");
            }
            else if (bench)
            {
                log.AppendLine("bench: not run (see the notes count; the review window shows why)");
            }
            log.AppendLine("LEARNCHECK OK");
            ok = true;
        }
        catch (Exception ex)
        {
            // Type and stack only: a message can quote a database error or a term.
            log.AppendLine($"LEARNCHECK FAILED: {ex.GetType().FullName}");
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
            {
                log.AppendLine($"  inner: {inner.GetType().FullName}");
            }
            log.AppendLine(ex.StackTrace ?? "");
        }
        finally
        {
            try
            {
                AppPaths.EnsureCreated();
                File.WriteAllText(Path.Combine(AppPaths.DataDir, "learncheck.txt"), log.ToString());
            }
            catch { }
            Shutdown(ok ? 0 : 1);
        }
    }

    /// <summary>
    /// The origin labels the miners and the benchmark use (spec §4.2), taken
    /// from their own constants so a new origin cannot quietly be counted as
    /// "other". All are compile-time labels, so no free text reaches the file.
    /// (StyleMiner.EditsOrigin has CorrectionMiner.EditsOrigin's value; the
    /// set keeps one.)
    /// </summary>
    internal static readonly HashSet<string> KnownOrigins = new(StringComparer.Ordinal)
    {
        Learning.VocabularyMiner.Origin,
        Learning.CorrectionMiner.EditsOrigin,
        Learning.CorrectionMiner.RecognizerOrigin,
        Learning.CorrectionMiner.FlowTypeRecognizerOrigin,
        Learning.CorrectionMiner.RecordingsOrigin,
        Learning.StyleMiner.EditsOrigin,
        Learning.StyleMiner.WisprOrigin,
        Learning.WisprDictionaryImport.Origin,
    };

    // ----- Live transcription check (hidden diagnostic: FlowType.exe --livecheck <wav|folder>) -----

    /// <summary>
    /// Replays long WAVs through the live (chunked) path and through the
    /// whole-file path with the same model and settings, and reports where the
    /// cuts fell, word error rate of each against the sibling .txt (and of live
    /// against whole-file), and the latency left after release. Pure checks run
    /// first — the prompt with previous text, the echo guard, the recorder's
    /// dead-mic and meter helpers — so a run without a model still says
    /// something. Options: --model &lt;id&gt; (default: the selected model),
    /// --out &lt;file&gt; (default %APPDATA%\FlowType\livecheck.txt), --mic (also
    /// record 1.5 s from the microphone — after opening the mic-test monitor,
    /// the sequence that used to kill the take — and check that
    /// SamplesCaptured delivered exactly the WAV; only counts are logged).
    /// </summary>
    private async void RunLiveCheck(string target)
    {
        var log = new StringBuilder();
        var args = Environment.GetCommandLineArgs();
        string Arg(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 ? args.ElementAtOrDefault(i + 1) ?? "" : "";
        }
        var outPath = Arg("--out");
        if (string.IsNullOrWhiteSpace(outPath)) outPath = Path.Combine(AppPaths.DataDir, "livecheck.txt");
        var allOk = true;
        try
        {
            var s = SettingsStore.Instance.Settings;
            log.AppendLine($"FlowType live check {DateTime.Now:yyyy-MM-dd HH:mm}");
            log.AppendLine($"language={s.Language} variant={s.EnglishVariant} accurate={s.AccurateDecoding} gpu={s.UseGpu}");
            log.AppendLine();

            // --- Prompt with previous text (pure) ---
            var variant = EnglishVariant.RecognitionPrompt(EnglishVariant.Us) ?? "";
            var previous = "and that is why the migration slipped by a week. " + SmartFormatter.ParagraphMark
                + " We moved the billing service over on Tuesday, and so far the error rate has stayed flat in every region.";
            var withPrevious = Transcriber.BuildPrompt(variant, new[] { "Arma Reforger" }, new[] { "armor forger" }, previous) ?? "";
            var withoutPrevious = Transcriber.BuildPrompt(variant, new[] { "Arma Reforger" }, new[] { "armor forger" }) ?? "";
            var longVocabulary = Enumerable.Range(0, 200).Select(i => $"Term{i:000}").ToArray();
            var cramped = Transcriber.BuildPrompt(variant, longVocabulary, new[] { "spoken one" }, previous) ?? "";
            var context = Transcriber.PreviousContext(previous, Transcriber.MaxPreviousTextChars) ?? "";
            var promptOk =
                withPrevious.StartsWith(variant, StringComparison.Ordinal)
                && withPrevious.EndsWith("Preferred spellings: Arma Reforger.", StringComparison.Ordinal)
                && withPrevious.IndexOf("stayed flat", StringComparison.Ordinal) > variant.Length
                && withPrevious.IndexOf("stayed flat", StringComparison.Ordinal)
                    < withPrevious.IndexOf("Possible spoken forms:", StringComparison.Ordinal)
                && !withPrevious.Contains(SmartFormatter.ParagraphMark)
                && withPrevious.Length <= Transcriber.MaxPromptChars
                && context.Length <= Transcriber.MaxPreviousTextChars
                && cramped.Length <= Transcriber.MaxPromptChars
                && cramped.EndsWith('.')
                && cramped.Contains("Preferred spellings:")
                && withoutPrevious == variant + " Possible spoken forms: armor forger. Preferred spellings: Arma Reforger."
                // The --selftest vectors, unchanged by the new parameter.
                && Transcriber.BuildPrompt(null, new[] { "Arma Reforger" }, new[] { "armor forger" })
                    == "Possible spoken forms: armor forger. Preferred spellings: Arma Reforger."
                && Transcriber.BuildPrompt(null, Array.Empty<string>()) == null
                && Transcriber.BuildPrompt(null, Array.Empty<string>(), null, "too short") == null;
            allOk &= promptOk;
            log.AppendLine($"prompt+previous {(promptOk ? "PASS" : "FAIL")}: context({context.Length})='{context}'");
            log.AppendLine($"  with previous ({withPrevious.Length}): {withPrevious}");
            log.AppendLine($"  cramped ({cramped.Length}) ends: …{cramped[^Math.Min(60, cramped.Length)..]}");

            // --- Echo guard sees the previous text (pure) ---
            var echoed = HallucinationFilter.Apply(
                "We moved the billing service over on Tuesday, and so far the error rate has stayed flat. Next we test the workers.",
                variant + " " + context);
            var echoOk = !echoed.Contains("billing") && echoed.Contains("Next we test the workers.");
            allOk &= echoOk;
            log.AppendLine($"echo of previous text {(echoOk ? "PASS" : "FAIL")}: '{echoed}'");

            // --- Recorder measurement helpers (pure) ---
            long zeroRun = 0;
            double firedAt = -1;
            for (var b = 0; b < 140; b++)                 // 50 ms buffers: 0.5 s of signal, then zeros
            {
                zeroRun = AudioRecorder.ExtendZeroRun(zeroRun, 800, b < 10 ? 0 : 800);
                if (firedAt < 0 && zeroRun >= (long)(AudioRecorder.SilentInputThreshold.TotalSeconds * 16000)) firedAt = (b + 1) * 0.05;
            }
            var partialRun = AudioRecorder.ExtendZeroRun(12345, 800, 300);   // signal then 300 zeros
            float floorDb = float.MaxValue;
            var l1 = AudioRecorder.NormalizeLevel(-200f, ref floorDb);
            var floorAfterZero = floorDb;
            var l2 = AudioRecorder.NormalizeLevel(-50f, ref floorDb);
            var l3 = AudioRecorder.NormalizeLevel(-20f, ref floorDb);
            float quietFloor = float.MaxValue;
            AudioRecorder.NormalizeLevel(-48f, ref quietFloor);
            var l4 = AudioRecorder.NormalizeLevel(-38f, ref quietFloor);
            var recorderOk = Math.Abs(firedAt - 5.5) < 1e-9 && partialRun == 300
                && l1 == 0f && floorAfterZero == AudioRecorder.LevelFloorClampDb
                && Math.Abs(l2 - 0.5f) < 1e-6 && l3 == 1f && Math.Abs(l4 - 0.5f) < 1e-6
                && Math.Abs(AudioRecorder.RmsDbfs(0, 800) - -200f) < 1e-3
                && Math.Abs(AudioRecorder.RmsDbfs(800.0 * 32768 * 32768, 800)) < 1e-3;
            allOk &= recorderOk;
            log.AppendLine($"recorder helpers {(recorderOk ? "PASS" : "FAIL")}: dead-mic fired at {firedAt:0.00}s " +
                $"(expect 5.50), partial run={partialRun}, levels={l1}/{l2:0.00}/{l3}/{l4:0.00} floor={floorAfterZero}");

            // --- Cut policy on synthetic takes (pure, deterministic) ---
            // A 200 Hz tone whose loudness swings like syllables but never
            // drops into a pause, optionally with silent gaps every 6 s.
            static float[] Take(double seconds, double gapSeconds, int seed = 0, bool roomTone = false)
            {
                var rng = new Random(seed);
                var n = (int)(seconds * 16000);
                var x = new float[n];
                for (var i = 0; i < n; i++)
                {
                    var t = i / 16000.0;
                    if (roomTone) { x[i] = (float)(rng.NextDouble() - 0.5) * 0.002f; continue; }
                    var inGap = gapSeconds > 0 && t % 6.0 < gapSeconds && t > 1;
                    x[i] = inGap ? 0f : (float)((0.2 + 0.1 * Math.Sin(2 * Math.PI * 4 * t)) * Math.Sin(2 * Math.PI * 200 * t));
                }
                return x;
            }
            bool InGap(double t, double gap) => t % 6.0 <= gap + 0.02;
            var continuous = LiveTranscriber.PlanChunks(Take(62, 0));
            var paused = LiveTranscriber.PlanChunks(Take(62, 0.7));
            var commas = LiveTranscriber.PlanChunks(Take(62, 0.45));
            var shortPlan = LiveTranscriber.PlanChunks(Take(19.9, 0));
            var roomTone = LiveTranscriber.PlanChunks(Take(62, 0, seed: 1, roomTone: true));
            string Describe(IReadOnlyList<LiveTranscriber.ChunkStat> plan) => plan.Count == 0 ? "whole-file" :
                string.Join(" ", plan.Select(c => $"[{c.StartSeconds:0.0}-{c.EndSeconds:0.0}{(c.Forced ? " F" : "")}{(c.Silent ? " S" : "")}]"));
            bool Lengths(IReadOnlyList<LiveTranscriber.ChunkStat> plan) => plan.Where(c => !c.Final)
                .All(c => c.EndSeconds - c.StartSeconds >= 15 && c.EndSeconds - c.StartSeconds <= 28.11);
            var planOk =
                continuous.Count >= 3 && continuous.Where(c => !c.Final).All(c => c.Forced) && Lengths(continuous)
                && paused.Count >= 3 && paused.All(c => !c.Forced) && Lengths(paused)
                && paused.Where(c => !c.Final).All(c => InGap(c.EndSeconds, 0.7))
                && paused.Where(c => !c.Final).All(c => c.EndSeconds - c.StartSeconds <= 20.01)
                && commas.Count >= 3 && commas.All(c => !c.Forced) && Lengths(commas)
                && commas.Where(c => !c.Final).All(c => InGap(c.EndSeconds, 0.45))
                && shortPlan.Count == 0
                && roomTone.Count >= 3 && roomTone.All(c => c.Silent);
            allOk &= planOk;
            log.AppendLine($"cut policy {(planOk ? "PASS" : "FAIL")}:");
            log.AppendLine($"  continuous: {Describe(continuous)}");
            log.AppendLine($"  0.7 s pauses: {Describe(paused)}");
            log.AppendLine($"  0.45 s pauses: {Describe(commas)}");
            log.AppendLine($"  19.9 s: {Describe(shortPlan)}");
            log.AppendLine($"  room tone: {Describe(roomTone)}");

            // --- Real capture (opt-in): SamplesCaptured is exactly the WAV ---
            if (args.Contains("--mic"))
            {
                var captured = new List<short>();
                var normalizedEvents = 0;
                void OnSamples(short[] buffer) { lock (captured) captured.AddRange(buffer); }
                void OnNormalized(float _) => Interlocked.Increment(ref normalizedEvents);
                var faultsBefore = AudioRecorder.Instance.CallbackFaults;
                AudioRecorder.Instance.SamplesCaptured += OnSamples;
                AudioRecorder.Instance.LevelNormalized += OnNormalized;
                try
                {
                    // Monitor first, then record: the old recorder let the
                    // monitor's late RecordingStopped tear the new take down.
                    AudioRecorder.Instance.StartMonitor();
                    await Task.Delay(400);
                    var monitorSamples = captured.Count;
                    AudioRecorder.Instance.StartRecording();
                    await Task.Delay(1500);
                    var take = await AudioRecorder.Instance.StopAsync().WaitAsync(TimeSpan.FromSeconds(8));
                    var wav = take == null ? Array.Empty<float>() : Transcriber.ReadSamples(take.Value.Path);
                    short[] got;
                    lock (captured) got = captured.ToArray();
                    var identical = got.Length == wav.Length
                        && got.Select((v, i) => v / 32768f == wav[i]).All(x => x);
                    var faults = AudioRecorder.Instance.CallbackFaults - faultsBefore;
                    var micOk = take != null && monitorSamples == 0 && identical && wav.Length > 16000
                        && faults == 0 && normalizedEvents > 0;
                    allOk &= micOk;
                    log.AppendLine($"mic capture {(micOk ? "PASS" : "FAIL")}: take={(take == null ? "null" : $"{take.Value.DurationSeconds:0.00}s {take.Value.Outcome}")} " +
                        $"wav={wav.Length} captured={got.Length} identical={identical} " +
                        $"samplesWhileMonitoring={monitorSamples} normalizedEvents={normalizedEvents} faults={faults}");
                    AudioRecorder.TryDelete(take?.Path);
                }
                finally
                {
                    AudioRecorder.Instance.SamplesCaptured -= OnSamples;
                    AudioRecorder.Instance.LevelNormalized -= OnNormalized;
                    AudioRecorder.Instance.StopMonitor();
                }
            }

            // --- Live vs whole-file on real audio ---
            var clips = Directory.Exists(target)
                ? Directory.GetFiles(target, "*.wav").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray()
                : File.Exists(target) ? new[] { target } : Array.Empty<string>();
            if (clips.Length == 0)
            {
                log.AppendLine($"no WAVs at '{target}' — model checks skipped");
            }
            else
            {
                var modelId = Arg("--model");
                if (string.IsNullOrWhiteSpace(modelId)) modelId = s.SelectedModel;
                var loadSw = System.Diagnostics.Stopwatch.StartNew();
                await Transcriber.Instance.LoadModelAsync(modelId);
                log.AppendLine();
                log.AppendLine($"model {modelId} loaded in {loadSw.ElapsedMilliseconds} ms on {Transcriber.Instance.RuntimeLabel}");

                // Esc while chunks are decoding: the run must stop promptly,
                // throw OperationCanceledException, and leave the decoder free.
                {
                    var longest = clips.OrderByDescending(c => new FileInfo(c).Length).First();
                    using var cancelCts = new CancellationTokenSource(1500);
                    var cancelSw = System.Diagnostics.Stopwatch.StartNew();
                    var threw = false;
                    try
                    {
                        await LiveTranscriber.SimulateDetailedAsync(longest, s.Language, Array.Empty<string>(), null, null, cancelCts.Token);
                    }
                    catch (OperationCanceledException) { threw = true; }
                    var cancelMs = cancelSw.ElapsedMilliseconds;
                    var afterSw = System.Diagnostics.Stopwatch.StartNew();
                    var after = await Transcriber.Instance.TranscribeAsync(clips.OrderBy(c => new FileInfo(c).Length).First(),
                        s.Language, Array.Empty<string>()).WaitAsync(TimeSpan.FromSeconds(30));
                    var cancelOk = threw && cancelMs < 1500 + 3000 && !after.IsEmpty;
                    allOk &= cancelOk;
                    log.AppendLine($"cancel mid-take {(cancelOk ? "PASS" : "FAIL")}: threw={threw} after {cancelMs} ms (token at 1500 ms); " +
                        $"next decode {afterSw.ElapsedMilliseconds} ms, text={(after.IsEmpty ? "EMPTY" : "ok")}");
                }

                foreach (var clip in clips)
                {
                    var name = Path.GetFileName(clip);
                    var txt = Path.ChangeExtension(clip, ".txt");
                    var reference = File.Exists(txt) ? File.ReadAllText(txt) : null;
                    var seconds = Transcriber.ReadSamples(clip).Length / 16000.0;

                    var wholeSw = System.Diagnostics.Stopwatch.StartNew();
                    var whole = await Transcriber.Instance.TranscribeAsync(clip, s.Language, Array.Empty<string>());
                    wholeSw.Stop();

                    // Evidence for the integrator: does the primer rescue an
                    // unpunctuated whole-file result too?
                    string? wholeRetry = null;
                    long wholeRetryMs = 0;
                    if (Transcriber.LooksUnpunctuated(whole.Text))
                    {
                        var retrySw = System.Diagnostics.Stopwatch.StartNew();
                        var again = await Transcriber.Instance.TranscribeAsync(clip, s.Language, Array.Empty<string>(),
                            previousText: Transcriber.PunctuationPrimer);
                        wholeRetryMs = retrySw.ElapsedMilliseconds;
                        wholeRetry = again.Text;
                    }

                    var report = await LiveTranscriber.SimulateDetailedAsync(
                        clip, s.Language, Array.Empty<string>(), null, null, default);

                    log.AppendLine();
                    log.AppendLine($"== {name}: {seconds:0.0} s, whole-file decode {wholeSw.ElapsedMilliseconds} ms, verdict {whole.Verdict}");
                    if (report.Result is not { } live)
                    {
                        var shortOk = seconds < LiveTranscriber.SearchAfter.TotalSeconds + 8;
                        allOk &= shortOk;
                        log.AppendLine($"   live: not chunked -> caller uses the whole-file path " +
                            $"(identical output by construction) {(shortOk ? "PASS" : "FAIL: a long take was not chunked")}");
                        continue;
                    }

                    var stats = report.Chunks;
                    foreach (var c in stats)
                    {
                        log.AppendLine($"   chunk {c.Index}: {c.StartSeconds,6:0.00}-{c.EndSeconds,6:0.00} s " +
                            $"({c.EndSeconds - c.StartSeconds,5:0.0} s) {(c.Final ? "tail  " : c.Forced ? "forced" : "pause ")} " +
                            $"pauseAfter={c.PauseAfterSeconds:0.00}s gain={c.Gain:0.00} decode={c.DecodeMs} ms" +
                            (c.Silent ? " SILENT(skipped)" : "") + (c.Retried ? " RETRIED(unpunctuated)" : ""));
                    }
                    var nonTail = stats.Where(c => !c.Final).ToList();
                    var lastCut = nonTail.LastOrDefault();
                    // Worst realistic case: release lands just after the last
                    // cut, so its decode is still running when the tail starts.
                    var worst = report.ReleaseToTextMs + (lastCut?.DecodeMs ?? 0);
                    var keptUp = nonTail.Zip(stats.Skip(1), (c, next) => c.DecodeMs < (next.EndSeconds - next.StartSeconds) * 1000).All(x => x);
                    log.AppendLine($"   live: {stats.Count} chunks, release->text {report.ReleaseToTextMs} ms " +
                        $"(worst case incl. last chunk still decoding: {worst} ms) vs whole-file {wholeSw.ElapsedMilliseconds} ms; " +
                        $"decoder kept up with real time: {keptUp}");
                    log.AppendLine($"   conditioning: final-chunk gain {report.FinalChunkGain:0.000} vs whole-file {report.WholeFileGain:0.000}, " +
                        $"streamed high-pass max delta {report.FilterMaxDelta:E2}");
                    var verdictOk = live.Verdict == whole.Verdict
                        && Math.Abs(report.FinalChunkGain - report.WholeFileGain) < 1e-4
                        && report.FilterMaxDelta < 1e-6;
                    allOk &= verdictOk;
                    log.AppendLine($"   verdict/gain/filter parity {(verdictOk ? "PASS" : "FAIL")}: live {live.Verdict} whole {whole.Verdict}");
                    if (reference != null)
                    {
                        var (le, ln) = WordErrorRate.Count(reference, live.Text);
                        var (we, wn) = WordErrorRate.Count(reference, whole.Text);
                        log.AppendLine($"   WER vs reference: live {100.0 * le / Math.Max(1, ln):0.0}% ({le}/{ln})  " +
                            $"whole-file {100.0 * we / Math.Max(1, wn):0.0}% ({we}/{wn})");
                    }
                    if (wholeRetry != null)
                    {
                        var (re, rn) = reference != null ? WordErrorRate.Count(reference, wholeRetry) : (0, 0);
                        log.AppendLine($"   whole-file came back UNPUNCTUATED; primed retry {wholeRetryMs} ms -> sentence marks " +
                            $"{wholeRetry.Count(ch => ch is '.' or '!' or '?')}, WER {100.0 * re / Math.Max(1, rn):0.0}%");
                    }
                    var (de, dn) = WordErrorRate.Count(whole.Text, live.Text);
                    log.AppendLine($"   sentence marks: live {live.Text.Count(ch => ch is '.' or '!' or '?')}, whole-file {whole.Text.Count(ch => ch is '.' or '!' or '?')}");
                    log.AppendLine($"   live vs whole-file word difference: {100.0 * de / Math.Max(1, dn):0.0}% ({de}/{dn})");
                    log.AppendLine($"   live : {live.Text.Replace(SmartFormatter.ParagraphMark, '¶')}");
                    log.AppendLine($"   whole: {whole.Text.Replace(SmartFormatter.ParagraphMark, '¶')}");
                }
            }
            log.AppendLine();
            log.AppendLine(allOk ? "LIVECHECK OK" : "LIVECHECK FAILED");
        }
        catch (Exception ex)
        {
            log.AppendLine($"LIVECHECK FAILED: {ex}");
        }
        finally
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
                File.WriteAllText(outPath, log.ToString());
            }
            catch { }
            Shutdown();
        }
    }

    // ----- Key log (hidden diagnostic: FlowType.exe --keylog) -----

    /// <summary>
    /// Watches the keyboard for 20 s and records every event the hook sees plus
    /// every hotkey press/release both detection paths report. Writes
    /// %APPDATA%\FlowType\keylog.txt. Use this when the hotkey "does nothing":
    /// it shows whether events arrive at all, what vk codes the keyboard really
    /// sends, and whether they are flagged as injected.
    /// </summary>
    private async void RunKeyLog()
    {
        var log = new StringBuilder();
        var events = 0;
        try
        {
            var hotkeys = new HotkeyManager(Dispatcher);
            log.AppendLine($"watching for: {SettingsStore.Instance.Settings.Hotkey} " +
                $"(hook installed={hotkeys.HookInstalled})");
            // Exercise the detect-key capture path too: first key pressed
            // during the window is reported the way the settings UI would see it.
            _ = Task.Delay(2000).ContinueWith(_ => hotkeys.CaptureNextKey(vk =>
            {
                log.AppendLine($"CAPTURED vk=0x{vk:X2} ({VkNames.Name(vk)})");
            }), TaskScheduler.Default);
            hotkeys.Trace += line =>
            {
                if (Interlocked.Increment(ref events) <= 400) log.AppendLine("  " + line);
            };
            hotkeys.Pressed += () => log.AppendLine(">>> HOTKEY PRESSED");
            hotkeys.Released += () => log.AppendLine("<<< HOTKEY RELEASED");

            await Task.Delay(TimeSpan.FromSeconds(20));
            log.AppendLine($"total hook events: {events}; hook saw input: {hotkeys.HookSawInput}");
            hotkeys.Dispose();
        }
        catch (Exception ex)
        {
            log.AppendLine($"KEYLOG FAILED: {ex}");
        }
        finally
        {
            AppPaths.EnsureCreated();
            File.WriteAllText(Path.Combine(AppPaths.DataDir, "keylog.txt"), log.ToString());
            Shutdown();
        }
    }

    // ----- Snapshot (hidden diagnostic: FlowType.exe --snapshot <folder>) -----

    /// <summary>
    /// Renders every window and page to PNG without using the screen: the
    /// windows are laid out far off-screen, never activated, and rasterised
    /// with RenderTargetBitmap. Lets the look be verified on a machine where
    /// a game or remote session owns the display.
    /// </summary>
    private async void RunSnapshot(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);

            var main = new MainWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000, Top = -20000,
                ShowActivated = false, ShowInTaskbar = false,
            };
            main.Show();
            await Task.Delay(400);
            main.SnapshotPages(dir);
            main.Close();

            var onboarding = new OnboardingWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000, Top = -20000,
                ShowActivated = false, ShowInTaskbar = false,
            };
            onboarding.Show();
            await Task.Delay(200);
            onboarding.SnapshotSteps(dir);
            onboarding.Close();

            var bar = new FlowBarWindow();
            await Task.Delay(200);
            bar.SnapshotStates(dir);
            bar.AllowClose();

            // "Learn from your writing": every state of the review window,
            // from a synthetic report (learn-*.png).
            try
            {
                LearnWindow.SnapshotAll(dir);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(dir, "learn-error.txt"), ex.ToString());
            }

            try
            {
                var menu = BuildTrayMenu();
                UpdateTrayMenuOf(menu);
                menu.PerformLayout();
                var size = menu.GetPreferredSize(System.Drawing.Size.Empty);
                menu.Size = size;
                using var bmp = new System.Drawing.Bitmap(size.Width, size.Height);
                menu.DrawToBitmap(bmp, new System.Drawing.Rectangle(System.Drawing.Point.Empty, size));
                bmp.Save(Path.Combine(dir, "tray-menu.png"), System.Drawing.Imaging.ImageFormat.Png);
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(dir, "tray-menu-error.txt"), ex.ToString());
            }
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(dir, "snapshot-error.txt"), ex.ToString());
        }
        finally
        {
            Shutdown();
        }
    }

    /// <summary>The tray menu's status caption for a menu that isn't the live one.</summary>
    private static void UpdateTrayMenuOf(WinForms.ContextMenuStrip menu)
    {
        if (menu.Items.Count > 0 && menu.Items[0] is WinForms.ToolStripMenuItem status)
        {
            status.Text = $"Ready · hold {HotkeyDefs.CurrentDisplay(SettingsStore.Instance.Settings)}";
        }
    }

    // ----- Benchmark (hidden diagnostic: FlowType.exe --bench <folder>) -----

    /// <summary>
    /// Accuracy and speed harness. Every 16 kHz mono WAV in the folder with a
    /// sibling .txt reference is transcribed by each downloaded model in both
    /// decoding modes; word error rate and wall time go to
    /// %APPDATA%\FlowType\bench.txt. Clip names ending in "_condition" are
    /// additionally broken down per condition (the synthetic set built for
    /// 1.3.0 uses clean / muffled / noisy / rushed). The pipeline under test
    /// is exactly the production one: conditioning, prompt, decoding params.
    /// </summary>
    private async void RunBench(string dir)
    {
        var log = new StringBuilder();
        try
        {
            if (!Directory.Exists(dir)) throw new DirectoryNotFoundException(dir);
            var clips = Directory.GetFiles(dir, "*.wav")
                .Where(w => File.Exists(Path.ChangeExtension(w, ".txt")))
                .OrderBy(w => w, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (clips.Length == 0) throw new FileNotFoundException("No .wav + .txt pairs in " + dir);

            var s = SettingsStore.Instance.Settings;
            log.AppendLine($"FlowType bench {DateTime.Now:yyyy-MM-dd HH:mm} — {clips.Length} clips in {dir}");
            log.AppendLine($"gpu={s.UseGpu} variant={s.EnglishVariant} language={s.Language} " +
                $"adapters={string.Join(" | ", GpuInfo.AdapterNames)}");
            log.AppendLine();
            log.AppendLine($"{"model",-22} {"mode",-9} {"WER",7} {"avg ms",8} {"max ms",8}  per-condition WER");

            var models = ModelCatalog.All.Where(m => m.IsDownloaded).OrderBy(m => m.MinBytes).ToList();
            var worst = new StringBuilder();
            var coldStarts = new StringBuilder();
            foreach (var model in models)
            {
                var loadSw = System.Diagnostics.Stopwatch.StartNew();
                await Transcriber.Instance.LoadModelAsync(model.Id);
                var loadMs = loadSw.ElapsedMilliseconds;
                // First call on a model compiles GPU kernels; timed separately
                // as the "cold start" the user pays once per session.
                var coldSw = System.Diagnostics.Stopwatch.StartNew();
                await Transcriber.Instance.TranscribeAsync(clips[0], "en", Array.Empty<string>(), default, true);
                coldStarts.AppendLine($"   {model.Id,-22} load {loadMs,6} ms   first transcription {coldSw.ElapsedMilliseconds,6} ms");

                foreach (var accurate in new[] { false, true })
                {
                    int errors = 0, words = 0, rejected = 0;
                    long totalMs = 0, maxMs = 0;
                    var byCondition = new Dictionary<string, (int Err, int Words)>();
                    var clipResults = new List<(int Err, string Name, string Hyp)>();
                    foreach (var clip in clips)
                    {
                        var reference = File.ReadAllText(Path.ChangeExtension(clip, ".txt"));
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var result = await Transcriber.Instance.TranscribeAsync(
                            clip, "en", Array.Empty<string>(), default, accurate);
                        sw.Stop();
                        var hyp = result.Text;
                        // A clip the pre-model gate threw away is a total loss:
                        // counting them separately is how a too-eager silence
                        // gate gets caught before it ships.
                        if (result.Verdict is TakeVerdict.NoSpeech or TakeVerdict.NoInput) rejected++;
                        var (err, n) = WordErrorRate.Count(reference, hyp);
                        errors += err; words += n;
                        totalMs += sw.ElapsedMilliseconds; maxMs = Math.Max(maxMs, sw.ElapsedMilliseconds);

                        var name = Path.GetFileNameWithoutExtension(clip);
                        var cond = name.Contains('_') ? name[(name.LastIndexOf('_') + 1)..] : "all";
                        byCondition.TryGetValue(cond, out var c);
                        byCondition[cond] = (c.Err + err, c.Words + n);
                        clipResults.Add((err, name, hyp));
                    }
                    var mode = accurate ? "accurate" : "fast";
                    var perCond = string.Join("  ", byCondition.OrderBy(k => k.Key)
                        .Select(k => $"{k.Key}={100.0 * k.Value.Err / Math.Max(1, k.Value.Words):0.0}%"));
                    log.AppendLine($"{model.Id,-22} {mode,-9} {100.0 * errors / Math.Max(1, words),6:0.0}% " +
                        $"{totalMs / clips.Length,8} {maxMs,8}  {perCond}" +
                        (rejected > 0 ? $"  REJECTED {rejected}/{clips.Length}" : ""));

                    worst.AppendLine($"-- {model.Id} / {mode}: worst clips");
                    foreach (var (err, name, hyp) in clipResults.OrderByDescending(c => c.Err).Take(3))
                    {
                        worst.AppendLine($"   {name} ({err} err): {hyp}");
                    }
                }
            }
            log.AppendLine();
            log.AppendLine("cold start per model (model load, then first transcription incl. GPU kernel compile):");
            log.Append(coldStarts);
            log.AppendLine();
            log.AppendLine($"runtime: {Transcriber.Instance.RuntimeLabel}");
            foreach (var line in Transcriber.Instance.NativeLog.Where(l =>
                l.Contains("vulkan", StringComparison.OrdinalIgnoreCase)).Take(8))
            {
                log.AppendLine($"  native: {line}");
            }
            log.AppendLine();
            log.Append(worst);
            log.AppendLine("BENCH OK");
        }
        catch (Exception ex)
        {
            log.AppendLine($"BENCH FAILED: {ex}");
        }
        finally
        {
            AppPaths.EnsureCreated();
            File.WriteAllText(Path.Combine(AppPaths.DataDir, "bench.txt"), log.ToString());
            Shutdown();
        }
    }

    // ----- Self-test (hidden diagnostic: FlowType.exe --selftest) -----

    /// <summary>
    /// Runs one or more phrases through the *live* dictionary and formatter and
    /// shows every stage, so "the dictionary isn't working" becomes a question
    /// with an answer. With no arguments it reports what the dictionary is
    /// currently telling the recogniser and runs a built-in set of plausible
    /// mis-hearings of each of the user's own terms.
    /// Writes %APPDATA%\FlowType\dictcheck.txt.
    /// </summary>
    private void RunDictCheck(string[] phrases)
    {
        var log = new StringBuilder();
        try
        {
            var sections = DictionaryStore.Instance.VocabularySections;
            var entries = DictionaryStore.Instance.Entries;
            var settings = SettingsStore.Instance.Settings;

            log.AppendLine("FlowType dictionary check");
            log.AppendLine();
            log.AppendLine($"entries: {entries.Count}  (fuzzy matching: {(settings.FuzzyVocabulary ? "on" : "OFF")})");
            foreach (var entry in entries)
            {
                var kind = entry.IsVocabularyOnly ? "vocabulary" : "rule";
                log.AppendLine($"  [{(entry.Enabled ? "on " : "off")}] {kind,-10} '{entry.Phrase}'" +
                    (entry.IsVocabularyOnly ? "" : $" -> '{entry.Replacement}'") +
                    $" (whole word: {entry.MatchWholeWord})");
            }
            log.AppendLine();
            log.AppendLine($"told to the recogniser as spellings   : {string.Join(", ", sections.Spellings)}");
            log.AppendLine($"told to the recogniser as spoken forms: {string.Join(", ", sections.SpokenForms)}");
            log.AppendLine($"fuzzy-matched mis-hearings           : " +
                string.Join(", ", DictionaryStore.Instance.Aliases.Select(a => $"{a.Heard} -> {a.Write}")));
            log.AppendLine();
            var prompt = Transcriber.BuildPrompt(
                EnglishVariant.RecognitionPrompt(settings.EnglishVariant),
                sections.Spellings, sections.SpokenForms);
            log.AppendLine($"initial prompt ({prompt?.Length ?? 0} chars):");
            log.AppendLine($"  {prompt ?? "(none)"}");
            log.AppendLine();

            // With no phrases given, generate plausible mis-hearings of each of
            // the user's own terms — the question is never "does the exact rule
            // fire", it is "how far off can the model be and still be caught".
            var cases = phrases.Length > 0
                ? phrases.ToList()
                : sections.Spellings.SelectMany(MisHearings).ToList();

            log.AppendLine($"{"heard",-28} {"after rules",-28} {"after fuzzy",-28} caught");
            var caught = 0;
            foreach (var heard in cases)
            {
                var afterRules = DictionaryStore.Instance.Apply(heard);
                var afterFuzzy = settings.FuzzyVocabulary
                    ? VocabularyMatcher.Apply(afterRules, sections.Spellings,
                        DictionaryStore.Instance.Aliases)
                    : afterRules;
                var hit = sections.Spellings.Any(t =>
                    afterFuzzy.Contains(t, StringComparison.OrdinalIgnoreCase));
                if (hit) caught++;
                log.AppendLine($"{Trunc(heard),-28} {Trunc(afterRules),-28} {Trunc(afterFuzzy),-28} " +
                    (hit ? "yes" : "NO"));
            }
            log.AppendLine();
            log.AppendLine($"caught {caught}/{cases.Count}");
        }
        catch (Exception ex)
        {
            log.AppendLine($"DICTCHECK FAILED: {ex}");
        }
        finally
        {
            AppPaths.EnsureCreated();
            File.WriteAllText(Path.Combine(AppPaths.DataDir, "dictcheck.txt"), log.ToString());
            Shutdown();
        }
    }

    private static string Trunc(string s) => s.Length <= 27 ? s : s[..24] + "...";

    /// <summary>
    /// Ways a recogniser plausibly mangles a term: run the words together, split
    /// them, drop a syllable, swap a vowel, insert a filler word between them.
    /// </summary>
    private static IEnumerable<string> MisHearings(string term)
    {
        var words = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        yield return term;
        yield return term.ToLowerInvariant();
        yield return string.Concat(words);
        if (words.Length >= 2)
        {
            yield return string.Join(" ", words[0], "of", words[1]);
            yield return string.Join(" ", words[0], words[1][..Math.Max(1, words[1].Length - 1)]);
            yield return string.Join(" ", words[0] + "e", words[1]);
        }
        if (term.Length > 4)
        {
            yield return term.Replace("a", "o").Replace("A", "O");
            yield return term[..^1];
        }
    }

    /// <summary>
    /// Runs the conditioner alone over a folder of 16 kHz WAVs and reports what
    /// it decided for each — verdict, gain, how much tail it trimmed. Seconds
    /// rather than the minutes <c>--bench</c> takes, so a threshold change can
    /// be checked against a whole corpus before spending a decode run on it.
    /// Writes %APPDATA%\FlowType\conditioncheck.txt.
    /// </summary>
    private void RunConditionCheck(string folder)
    {
        var log = new StringBuilder();
        try
        {
            var clips = Directory.Exists(folder)
                ? Directory.GetFiles(folder, "*.wav").OrderBy(f => f).ToArray()
                : Array.Empty<string>();
            log.AppendLine($"FlowType condition check — {clips.Length} clips in {folder}");
            log.AppendLine();
            log.AppendLine($"{"clip",-24} {"verdict",-10} {"gain",6} {"trim ms",8} {"rms",9} {"peak",7} " +
                $"{"msVoice",8} {"modul",6}");

            var rejected = 0;
            var trimmed = 0;
            var boosted = 0;
            foreach (var clip in clips)
            {
                var take = AudioConditioner.Prepare(Transcriber.ReadSamples(clip));
                var m = take.Metrics;
                if (!take.ShouldTranscribe) rejected++;
                if (m.TrimmedMs > 0) trimmed++;
                if (m.AppliedGain > 10.001f) boosted++;
                log.AppendLine($"{Path.GetFileNameWithoutExtension(clip),-24} {take.Verdict,-10} " +
                    $"{m.AppliedGain,6:0.00} {m.TrimmedMs,8} {m.Rms,9:0.00000} {m.Peak,7:0.000} " +
                    $"{m.MsAboveVoiceFloor,8} {m.SpeechLikeModulation,6}");
            }

            log.AppendLine();
            log.AppendLine($"rejected {rejected}/{clips.Length}, trimmed {trimmed}, " +
                $"boosted past 10x {boosted}");
        }
        catch (Exception ex)
        {
            log.AppendLine($"CONDITIONCHECK FAILED: {ex}");
        }
        finally
        {
            AppPaths.EnsureCreated();
            File.WriteAllText(Path.Combine(AppPaths.DataDir, "conditioncheck.txt"), log.ToString());
            Shutdown();
        }
    }

    /// <summary>
    /// Exercises the receipt-sequenced clipboard paste end to end without
    /// firing Ctrl+V into whatever the user has focused: the transaction is run
    /// with the chord suppressed and this process reads the clipboard instead,
    /// which is the same GetClipboardData call a target application makes and
    /// therefore produces the same read receipt.
    /// Writes %APPDATA%\FlowType\pastetest.txt.
    /// </summary>
    private async void RunPasteTest()
    {
        var log = new StringBuilder();
        var pass = 0;
        var fail = 0;

        void Check(string label, bool ok, string detail)
        {
            if (ok) pass++; else fail++;
            log.AppendLine($"{(ok ? "PASS" : "FAIL")} [{label}] {detail}");
        }

        ReliablePaste.Tracing = true;
        // The test scribbles all over the clipboard; put back whatever the user
        // actually had there.
        var userClipboard = ReliablePaste.ReadClipboardAsConsumer();
        try
        {
            const string previous = "FLOWTYPE-PREVIOUS-CLIPBOARD";
            const string transcript = "the quick brown fox — ünïcödé ✓";

            // 1. Reader present, restore on: the target gets the transcript and
            //    the user gets their clipboard back.
            Check("seed", ReliablePaste.SeedClipboard(previous),
                $"clipboard = '{ReliablePaste.ReadClipboardAsConsumer()}'");

            var started = await ReliablePaste.PasteAsync(transcript, restoreClipboard: true, sendChord: false);
            Check("transaction started", started, "");

            var readBack = ReliablePaste.ReadClipboardAsConsumer();
            Check("target reads transcript", readBack == transcript, $"got '{readBack}'");

            await Task.Delay(700);      // quiet period (200 ms) plus margin
            var restored = ReliablePaste.ReadClipboardAsConsumer();
            Check("clipboard restored", restored == previous, $"got '{restored}'");

            // 2. Restore off: the transcript is meant to stay on the clipboard.
            ReliablePaste.SeedClipboard(previous);
            await ReliablePaste.PasteAsync(transcript, restoreClipboard: false, sendChord: false);
            var kept = ReliablePaste.ReadClipboardAsConsumer();
            await Task.Delay(700);
            var stillKept = ReliablePaste.ReadClipboardAsConsumer();
            Check("transcript kept when restore is off",
                kept == transcript && stillKept == transcript, $"got '{stillKept}'");

            // 3. Nobody ever reads it: the promise must still become real text
            //    rather than leaving an empty handle on the clipboard.
            ReliablePaste.SeedClipboard(previous);
            await ReliablePaste.PasteAsync(transcript, restoreClipboard: false, sendChord: false);
            await Task.Delay(9000);     // past the 8 s restore timeout
            var afterTimeout = ReliablePaste.ReadClipboardAsConsumer();
            Check("unread promise materialises", afterTimeout == transcript, $"got '{afterTimeout}'");

            log.AppendLine(fail == 0 ? "PASTETEST OK" : "PASTETEST FAILED");
        }
        catch (Exception ex)
        {
            log.AppendLine($"PASTETEST FAILED: {ex}");
        }
        finally
        {
            if (userClipboard != null) ReliablePaste.SeedClipboard(userClipboard);
            log.AppendLine();
            log.AppendLine("trace:");
            lock (ReliablePaste.Trace)
            {
                foreach (var line in ReliablePaste.Trace) log.AppendLine($"  {line}");
            }
            AppPaths.EnsureCreated();
            File.WriteAllText(Path.Combine(AppPaths.DataDir, "pastetest.txt"), log.ToString());
            Shutdown();
        }
    }

    /// <summary>Show line breaks in a one-line test log.</summary>
    private static string Escape(string text) =>
        text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    // ----- Synthetic signals for the conditioning vectors -----
    // Deterministic (no Random) so a failure is always reproducible.

    private static float SelfTestNoiseSample(int i)
    {
        unchecked
        {
            var h = (uint)i * 2654435761u;
            h ^= h >> 13;
            h *= 2246822519u;
            h ^= h >> 16;
            return h / (float)uint.MaxValue * 2f - 1f;
        }
    }

    private static float[] SelfTestNoise(double seconds, float rms)
    {
        var n = new float[(int)(seconds * AudioConditioner.SampleRate)];
        var amplitude = rms * MathF.Sqrt(3f);   // uniform noise in [-a,a] has rms a/sqrt(3)
        for (var i = 0; i < n.Length; i++) n[i] = SelfTestNoiseSample(i) * amplitude;
        return n;
    }

    private static float[] SelfTestTone(double seconds, float amplitude, double hz = 220)
    {
        var n = new float[(int)(seconds * AudioConditioner.SampleRate)];
        for (var i = 0; i < n.Length; i++)
        {
            n[i] = amplitude * (float)Math.Sin(2 * Math.PI * hz * i / AudioConditioner.SampleRate);
        }
        return n;
    }

    /// <summary>Bursts of energy separated by near-silence — the envelope of speech.</summary>
    private static float[] SelfTestSpeech(double seconds, float peak)
    {
        var rate = AudioConditioner.SampleRate;
        var n = new float[(int)(seconds * rate)];
        var period = (int)(0.5 * rate);
        var burst = (int)(0.3 * rate);
        for (var i = 0; i < n.Length; i++)
        {
            var envelope = i % period < burst
                ? peak * (0.55f + 0.45f * MathF.Sin(i / 300f))
                : peak * 0.004f;
            n[i] = SelfTestNoiseSample(i + 7919) * envelope;
        }
        return n;
    }

    /// <summary>
    /// Headless pipeline check: settings → model load → 2s mic capture →
    /// transcription → formatter test vectors. Writes %APPDATA%\FlowType\selftest.txt.
    /// </summary>
    private async void RunSelfTest()
    {
        var log = new StringBuilder();
        try
        {
            var s = SettingsStore.Instance.Settings;
            log.AppendLine($"settings: model={s.SelectedModel} gpu={s.UseGpu} accurate={s.AccurateDecoding} mic='{s.MicDeviceName}' hotkey={s.Hotkey} mode={s.ActivationMode}");
            log.AppendLine($"adapters: {string.Join(" | ", GpuInfo.AdapterNames)} (discrete={GpuInfo.HasDiscreteGpu}, recommended={ModelCatalog.Recommended().Id})");

            await Transcriber.Instance.InitializeAsync();
            log.AppendLine($"model: loaded={Transcriber.Instance.IsReady} id={Transcriber.Instance.CurrentModelId} runtime={Transcriber.Instance.RuntimeLabel}");
            // Which device whisper.cpp actually picked lives only in its own log.
            foreach (var line in Transcriber.Instance.NativeLog.Where(l =>
                l.Contains("vulkan", StringComparison.OrdinalIgnoreCase)
                || l.Contains("device", StringComparison.OrdinalIgnoreCase)
                || l.Contains("backend", StringComparison.OrdinalIgnoreCase)
                || l.Contains("flash", StringComparison.OrdinalIgnoreCase)).Take(20))
            {
                log.AppendLine($"  native: {line}");
            }

            if (Transcriber.Instance.IsReady)
            {
                AudioRecorder.Instance.StartRecording();
                await Task.Delay(2000);
                var take = await AudioRecorder.Instance.StopAsync();
                log.AppendLine($"recording: duration={take?.DurationSeconds:0.00}s " +
                    $"outcome={take?.Outcome} callbackFaults={AudioRecorder.Instance.CallbackFaults}");
                if (take != null)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var result = await Transcriber.Instance.TranscribeAsync(
                        take.Value.Path, s.Language, DictionaryStore.Instance.VocabularyWords);
                    sw.Stop();
                    log.AppendLine($"transcribe: {sw.ElapsedMilliseconds}ms verdict={result.Verdict} " +
                        $"rms={result.Metrics.Rms:0.0000} peak={result.Metrics.Peak:0.000} " +
                        $"gain={result.Metrics.AppliedGain:0.0} trimmed={result.Metrics.TrimmedMs}ms " +
                        $"text='{result.Text}'");
                    AudioRecorder.TryDelete(take.Value.Path);
                }
            }

            // Audio conditioning. The cases that matter: a dead microphone and
            // room tone are rejected before the model (both used to be
            // transcribed into invented text), a steady tone never unlocks the
            // big gain, real speech does, short takes are padded past
            // whisper.cpp's 1 s floor, and a long silent tail is trimmed.
            var conditionResults = new List<(string Name, bool Ok, string Detail)>();
            void Condition(string name, float[] input, Func<ConditionedTake, bool> assert)
            {
                var take = AudioConditioner.Prepare(input);
                var ok = assert(take);
                conditionResults.Add((name, ok,
                    $"verdict={take.Verdict} samples={take.Samples.Length} " +
                    $"rms={take.Metrics.Rms:0.00000} peak={take.Metrics.Peak:0.000} " +
                    $"gain={take.Metrics.AppliedGain:0.0} trimmed={take.Metrics.TrimmedMs}ms"));
            }

            Condition("dead mic", new float[AudioConditioner.SampleRate * 2],
                t => t.Verdict == TakeVerdict.NoInput && !t.ShouldTranscribe);
            Condition("room tone", SelfTestNoise(2.0, 0.0007f),
                t => t.Verdict == TakeVerdict.NoSpeech && !t.ShouldTranscribe);
            Condition("steady tone not boosted", SelfTestTone(2.0, 0.02f),
                t => t.ShouldTranscribe && !t.Metrics.SpeechLikeModulation && t.Metrics.AppliedGain <= 16.001f);
            Condition("faint speech boosted", SelfTestSpeech(2.0, 0.02f),
                t => t.Verdict == TakeVerdict.Speech && t.Metrics.SpeechLikeModulation
                    && t.Metrics.AppliedGain > 16f);
            Condition("short take padded", SelfTestSpeech(0.45, 0.2f),
                t => t.Samples.Length >= (int)(1.25 * AudioConditioner.SampleRate));
            Condition("silent tail trimmed",
                SelfTestSpeech(1.5, 0.2f).Concat(new float[AudioConditioner.SampleRate * 3]).ToArray(),
                t => t.Metrics.TrimmedMs > 2000);

            var conditionOk = conditionResults.All(r => r.Ok);
            foreach (var (name, ok, detail) in conditionResults)
            {
                log.AppendLine($"conditioning {(ok ? "PASS" : "FAIL")} [{name}]: {detail}");
            }

            // Formatter vectors — every Wispr-style transform in one pass.
            // Language is the *decoded* language now, not the setting: the
            // English filler list only runs on evidence of English.
            var opts = new FormatterOptions(
                RemoveFillers: true, ScratchThat: true, LineCommands: true,
                PunctuationCommands: false, SmartTrailingPunctuation: true,
                Language: "en");
            var vectors = new (string Input, string Expected)[]
            {
                ("Um, hello world, uh, this is great.", "Hello world, this is great."),
                ("Send the report. New line. Thanks again.", "Send the report.\nThanks again."),
                ("roy@example.com.", "roy@example.com"),
                ("this is wrong, scratch that, this is right.", "This is right."),
                ("First point. New paragraph. Second point.", "First point.\n\nSecond point."),
                ("Hello there.", "Hello there."),
                // Fillers beyond um/uh, and the guards that keep real words.
                ("Hmm, ehm, so I was ah thinking.", "So I was thinking."),
                ("The gap is 5 mm across.", "The gap is 5 mm across."),
                ("Ah-ha, that explains it.", "Ah-ha, that explains it."),
                // Nothing but a filler: keep what was said rather than typing
                // nothing. (The trailing period goes the way it does for any
                // single-token dictation — see StripSmartTrailingPeriod.)
                ("Hmm.", "Hmm"),
            };
            var pass = 0;
            foreach (var (input, expected) in vectors)
            {
                var actual = TextFormatter.Apply(input, opts);
                var ok = actual == expected;
                if (ok) pass++;
                log.AppendLine($"format {(ok ? "PASS" : "FAIL")}: '{input}' -> '{actual}'" +
                    (ok ? "" : $" (expected '{expected}')"));
            }
            log.AppendLine($"formatter: {pass}/{vectors.Length} passed");

            // English variant conversion, both directions.
            var variantVectors = new (string Input, string Variant, string Expected)[]
            {
                ("The color of my favorite theater.", EnglishVariant.Uk,
                    "The colour of my favourite theatre."),
                ("I realized we traveled to the center.", EnglishVariant.Uk,
                    "I realised we travelled to the centre."),
                ("Our defense is organized.", EnglishVariant.Uk,
                    "Our defence is organised."),
                ("The colour of my favourite theatre.", EnglishVariant.Us,
                    "The color of my favorite theater."),
                ("I realised we travelled to the centre.", EnglishVariant.Us,
                    "I realized we traveled to the center."),
                ("COLOR and Color stay cased.", EnglishVariant.Uk,
                    "COLOUR and Colour stay cased."),
                ("The color of my favorite theater.", EnglishVariant.Off,
                    "The color of my favorite theater."),
            };
            var variantPass = 0;
            foreach (var (input, variant, expected) in variantVectors)
            {
                var actual = EnglishVariant.Convert(input, variant);
                var ok = actual == expected;
                if (ok) variantPass++;
                log.AppendLine($"variant[{variant}] {(ok ? "PASS" : "FAIL")}: '{actual}'" +
                    (ok ? "" : $" (expected '{expected}')"));
            }
            log.AppendLine($"english variant: {variantPass}/{variantVectors.Length} passed");

            // Hallucination guard: prompt echo and phrase loops seen on
            // unintelligible audio in the 1.3.0 bench; real speech untouched.
            const string prompt = "The following is American English, using American spelling: "
                + "color, favorite, realize, organized, center, theater.";
            var vocab = new[] { "John Smith", "Acme Corp", "Arma Reforger", "FigJam" };
            var guardVectors = new (string Input, string Expected)[]
            {
                ("The following is American English, and the following is American English, and the following is American English.", ""),
                ("color, favorite, realize, organized, center.", ""),
                ("I'm going to give you a little bit of a lesson. I'm going to give you a little bit of a lesson.",
                    "I'm going to give you a little bit of a lesson."),
                ("the quarter of the quarter of the quarter of the quarter, then the numbers.",
                    "the quarter of the quarter, then the numbers."),
                ("Send the report to finance. Thanks.", "Send the report to finance. Thanks."),
                ("Let me know if the following is American English or something else entirely, please.",
                    "Let me know if the following is American English or something else entirely, please."),
                ("John Smith Acme Corp", "John Smith Acme Corp"),
                ("John Smith, Acme Corp, Arma Reforger, FigJam.", ""),
                // Whisper's stutter on a hesitant start.
                ("I I I I think so.", "I think so."),
            };
            var guardPass = 0;
            foreach (var (input, expected) in guardVectors)
            {
                var actual = HallucinationFilter.Apply(input, prompt, vocab);
                var ok = actual == expected;
                if (ok) guardPass++;
                log.AppendLine($"guard {(ok ? "PASS" : "FAIL")}: '{actual}'" +
                    (ok ? "" : $" (expected '{expected}')"));
            }
            log.AppendLine($"hallucination guard: {guardPass}/{guardVectors.Length} passed");

            // The attempt log: exactly one line per attempt, on every exit path
            // including a throw, and never any transcript text in it.
            var logPath = AppPaths.AttemptLog;
            var linesBefore = File.Exists(logPath) ? File.ReadAllLines(logPath).Length : 0;
            const string canary = "CANARY-TRANSCRIPT-TEXT";
            try
            {
                using var probe = new DictationAttempt { AppName = "selftest" };
                probe.RawLength = canary.Length;
                throw new InvalidOperationException("selftest");
            }
            catch (InvalidOperationException) { }
            var linesAfter = File.Exists(logPath) ? File.ReadAllLines(logPath) : Array.Empty<string>();
            var attemptOk = linesAfter.Length == linesBefore + 1
                && linesAfter[^1].Contains("AbortedBeforeResult")
                && !linesAfter.Any(l => l.Contains(canary));
            log.AppendLine($"attempt log {(attemptOk ? "PASS" : "FAIL")}: " +
                $"{linesBefore} -> {linesAfter.Length} lines");

            // Does the library actually report a decoded language? The filler
            // gate now depends on it, so this is checked, not assumed.
            var probed = Transcriber.Instance.IsReady
                ? await Transcriber.Instance.ProbeLanguageAsync()
                : null;
            log.AppendLine($"language read-back: '{probed ?? "(none)"}' " +
                (probed == "en" ? "PASS" : "CHECK — long auto-language takes will fall back"));

            // Language evidence. The English filler list must run on English
            // and on an English-only model with no answer, and must not touch
            // a language whose "um" is a real word.
            var languageVectors = new (string Setting, bool EnglishOnly, double Seconds, string? Last, string Expected)[]
            {
                ("auto", false, 2.0, "en", "en"),
                ("auto", false, 30.0, "en", "auto"),
                ("auto", false, 2.0, null, "en"),
                ("de", true, 30.0, null, "en"),
                ("de", false, 2.0, "en", "de"),
            };
            var languagePass = 0;
            foreach (var (setting, englishOnly, seconds, last, expected) in languageVectors)
            {
                var actual = Transcriber.ResolveLanguage(setting, englishOnly, seconds, last);
                var ok = actual == expected;
                if (ok) languagePass++;
                log.AppendLine($"language {(ok ? "PASS" : "FAIL")}: " +
                    $"({setting}, en-only={englishOnly}, {seconds}s, last={last ?? "null"}) -> {actual}" +
                    (ok ? "" : $" (expected {expected})"));
            }

            var ptOpts = opts with { Language = "pt" };
            var unknownMultilingual = opts with { Language = "", EnglishOnlyModel = false };
            var unknownEnglishModel = opts with { Language = "", EnglishOnlyModel = true };
            const string portuguese = "eu vi um carro na rua";
            var fillerGateOk =
                TextFormatter.Apply(portuguese, ptOpts) == "Eu vi um carro na rua"
                && TextFormatter.Apply(portuguese, unknownMultilingual) == "Eu vi um carro na rua"
                && TextFormatter.Apply("um, hello there", unknownEnglishModel) == "Hello there"
                && TextFormatter.Apply("hmm, ok then", ptOpts) == "OK then";
            if (fillerGateOk) languagePass++;
            log.AppendLine($"filler gate {(fillerGateOk ? "PASS" : "FAIL")}: " +
                $"pt='{TextFormatter.Apply(portuguese, ptOpts)}' " +
                $"unknown='{TextFormatter.Apply(portuguese, unknownMultilingual)}'");
            log.AppendLine($"language evidence: {languagePass}/{languageVectors.Length + 1} passed");

            // Sentence-boundary space, and the structural guard that keeps the
            // tidy-up passes away from dictated code.
            var spaceVectors = new (string Input, string Expected)[]
            {
                ("one two three.", "one two three. "),
                ("Done!", "Done! "),
                ("Really?", "Really? "),
                ("He said \"yes.\"", "He said \"yes.\" "),
                ("Hello world,", "Hello world,"),
                ("no punctuation", "no punctuation"),
                ("https://example.com.", "https://example.com."),
                ("mail me at roy@example.com.", "mail me at roy@example.com."),
                ("visit example.com.", "visit example.com."),
                ("x = y.", "x = y."),
                ("line one\nline two.", "line one\nline two."),
                ("", ""),
            };
            var spacePass = 0;
            foreach (var (input, expected) in spaceVectors)
            {
                var actual = TextFormatter.WithSentenceSpace(input);
                var ok = actual == expected;
                if (ok) spacePass++;
                log.AppendLine($"space {(ok ? "PASS" : "FAIL")}: '{Escape(input)}' -> '{Escape(actual)}'" +
                    (ok ? "" : $" (expected '{Escape(expected)}')"));
            }
            log.AppendLine($"trailing space: {spacePass}/{spaceVectors.Length} passed");

            var structuralVectors = new (string Input, string Expected)[]
            {
                ("hello  ,  world", "hello  ,  world"),
                ("let x  =  1", "let x  =  1"),
                ("foo :: bar", "foo :: bar"),
                ("run `npm  test` now", "run `npm  test` now"),
                ("Um, hello world, uh, this is great.", "Hello world, this is great."),
            };
            var structuralPass = 0;
            foreach (var (input, expected) in structuralVectors)
            {
                var actual = TextFormatter.Apply(input, opts);
                var ok = actual == expected;
                if (ok) structuralPass++;
                log.AppendLine($"structural {(ok ? "PASS" : "FAIL")}: '{Escape(input)}' -> '{Escape(actual)}'" +
                    (ok ? "" : $" (expected '{Escape(expected)}')"));
            }
            log.AppendLine($"structural guard: {structuralPass}/{structuralVectors.Length} passed");

            // Non-speech markers. Whisper's own annotations go; bracketed text
            // the user actually dictated stays.
            var markerVectors = new (string Input, string Expected)[]
            {
                ("[BLANK_AUDIO]", ""),
                // Not whisper's exact form, so it is the user's own text (trimmed).
                (" [ blank_audio ] ", "[ blank_audio ]"),
                ("(silence)", ""),
                ("The intro has [MUSIC] before speech.", "The intro has before speech."),
                ("See note [1] and [2].", "See note [1] and [2]."),
                ("add a footnote [sic] there", "add a footnote [sic] there"),
                ("Compare a[0] to a[1].", "Compare a[0] to a[1]."),
                // The blank line "new paragraph" produces must survive: the old
                // \s{2,} collapse ate it.
                ("Line one\n\nLine two", "Line one\n\nLine two"),
                ("♪ la la ♪", "la la"),
            };
            var markerPass = 0;
            foreach (var (input, expected) in markerVectors)
            {
                var actual = Transcriber.CleanNonSpeechMarkers(input);
                var ok = actual == expected;
                if (ok) markerPass++;
                log.AppendLine($"marker {(ok ? "PASS" : "FAIL")}: '{Escape(input)}' -> " +
                    $"'{Escape(actual)}'" + (ok ? "" : $" (expected '{Escape(expected)}')"));
            }
            log.AppendLine($"non-speech markers: {markerPass}/{markerVectors.Length} passed");

            // Decode budget: generous enough never to cut a working decode short,
            // bounded enough that a wedge cannot strand the session.
            var budgetVectors = new (double Seconds, int Expected)[]
            {
                (1.0, 180), (10.0, 180), (30.0, 180), (60.0, 300), (435.0, 1800), (600.0, 1800),
            };
            var budgetPass = 0;
            foreach (var (seconds, expected) in budgetVectors)
            {
                var actual = (int)Transcriber.DecodeBudget(seconds).TotalSeconds;
                var ok = actual == expected;
                if (ok) budgetPass++;
                log.AppendLine($"budget {(ok ? "PASS" : "FAIL")}: {seconds}s -> {actual}s" +
                    (ok ? "" : $" (expected {expected}s)"));
            }
            log.AppendLine($"decode budget: {budgetPass}/{budgetVectors.Length} passed");

            // Fuzzy vocabulary. The first half must be corrected; the second
            // half is ordinary prose that must survive untouched — that is the
            // half that catches an over-eager matcher.
            var terms = new[] { "Arma Reforger", "Enfusion", "Workbench", "Nightjar", "FlowType" };
            var vocabVectors = new (string Input, string Expected)[]
            {
                ("I was playing Armour Forger last night.", "I was playing Arma Reforger last night."),
                ("open Arma Reforge and load the mod", "open Arma Reforger and load the mod"),
                ("the in fusion engine", "the Enfusion engine"),
                ("open work bench now", "open Workbench now"),
                ("flow type is running", "FlowType is running"),
                ("I LOVE ARMOUR FORGER!", "I LOVE ARMA REFORGER!"),
                ("we are going to the workshop tomorrow", "we are going to the workshop tomorrow"),
                ("the reform of the workplace bench marking process",
                    "the reform of the workplace bench marking process"),
                ("I need to fix the armour on that vehicle.", "I need to fix the armour on that vehicle."),
                ("mail me at roy@example.com today", "mail me at roy@example.com today"),
                ("she reforged the sword at the forge", "she reforged the sword at the forge"),
                // The word in front of a term is not part of it (2.1 fix: these
                // used to lose "an" and "a").
                ("we need an Arma Reforger mod", "we need an Arma Reforger mod"),
                ("I bought a workbench today", "I bought a Workbench today"),
                // …but a mishearing split across words is still one term.
                ("open Arm a Reforger now", "open Arma Reforger now"),
            };
            var vocabPass = 0;
            foreach (var (input, expected) in vocabVectors)
            {
                var actual = VocabularyMatcher.Apply(input, terms);
                var ok = actual == expected;
                if (ok) vocabPass++;
                log.AppendLine($"vocabulary {(ok ? "PASS" : "FAIL")}: '{actual}'" +
                    (ok ? "" : $" (expected '{expected}')"));
            }
            log.AppendLine($"fuzzy vocabulary: {vocabPass}/{vocabVectors.Length} passed");

            // Automatic formatting. Every rule gets a "must fire" row and a
            // "must not fire" row, because a wrong rewrite costs far more than
            // a missed one — a missed one just leaves what you said.
            var smartOpts = opts with { PunctuationCommands = false };
            var smartVectors = new (string Name, string Input, string Expected)[]
            {
                // Addresses and links
                ("email", "my email is roy at example dot com",
                    "My email is roy@example.com"),
                ("domain only", "look at example dot com",
                    "Look at example.com"),
                ("subdomain", "go to www dot example dot co dot uk",
                    "Go to www.example.co.uk"),
                ("not a domain", "the dot matrix printer is old",
                    "The dot matrix printer is old"),
                ("not an address", "we arrive at Heathrow tomorrow",
                    "We arrive at Heathrow tomorrow"),
                // Symbols, money, time
                ("percent", "it grew 25 percent last year", "It grew 25% last year"),
                ("money", "it cost 40 dollars", "It cost $40"),
                ("time", "the meeting is at 3 30 p.m. sharp",
                    "The meeting is at 3:30 PM sharp"),
                ("time no minutes", "call me at 9 am", "Call me at 9 AM"),
                ("quote", "she said quote this is fine unquote and left",
                    "She said “this is fine” and left"),
                ("percent word untouched", "a large percent of them agree",
                    "A large percent of them agree"),
                // Known terms
                ("acronyms", "the api returns json over https",
                    "The API returns JSON over HTTPS"),
                ("products", "I pushed it to github from my iphone",
                    "I pushed it to GitHub from my iPhone"),
                ("ambiguous left alone", "he told us it was a ram",
                    "He told us it was a ram"),
                // Lists
                ("numbered list", "I need three things. First, buy milk. Second, call Mum. Third, finish the report.",
                    "I need three things.\n1. Buy milk.\n2. Call Mum.\n3. Finish the report."),
                ("single marker is not a list", "First of all, I think we should wait.",
                    "First of all, I think we should wait."),
                ("number one form", "Two points. Number one, it is late. Number two, it is over budget.",
                    "Two points.\n1. It is late.\n2. It is over budget."),
            };
            var smartPass = 0;
            foreach (var (name, input, expected) in smartVectors)
            {
                var actual = TextFormatter.Apply(input, smartOpts);
                var ok = actual == expected;
                if (ok) smartPass++;
                log.AppendLine($"smart[{name}] {(ok ? "PASS" : "FAIL")}: '{Escape(actual)}'" +
                    (ok ? "" : $" (expected '{Escape(expected)}')"));
            }
            log.AppendLine($"automatic formatting: {smartPass}/{smartVectors.Length} passed");

            // Paragraphs come from the pause between spoken segments, so this
            // is tested on segment timings rather than on text.
            static SpeechSegment Seg(string t, double a, double b) =>
                new(t, TimeSpan.FromSeconds(a), TimeSpan.FromSeconds(b));
            var longEnough = " This sentence exists only to push the word count over the minimum so that "
                + "paragraph detection is allowed to run at all on this take.";
            var paraSegments = new[]
            {
                Seg("The first thought goes here." + longEnough, 0.0, 6.0),
                Seg(" And then after a long pause the second thought arrives.", 8.0, 11.0),
            };
            var noPauseSegments = new[]
            {
                Seg("The first thought goes here." + longEnough, 0.0, 6.0),
                Seg(" It continues straight on with no pause at all.", 6.1, 9.0),
            };
            var paraJoined = SmartFormatter.JoinSegments(paraSegments);
            var noPauseJoined = SmartFormatter.JoinSegments(noPauseSegments);
            var shortJoined = SmartFormatter.JoinSegments(new[]
            {
                Seg("Short note.", 0.0, 1.0), Seg(" Second bit.", 5.0, 6.0),
            });
            var paraOut = TextFormatter.Apply(paraJoined, smartOpts);
            var noPauseOut = TextFormatter.Apply(noPauseJoined, smartOpts);
            var paragraphsOk =
                paraOut.Contains('\n')
                && !noPauseOut.Contains('\n')
                && !shortJoined.Contains(SmartFormatter.ParagraphMark)
                && !TextFormatter.Apply(paraJoined, smartOpts with { AutoParagraphs = false })
                    .Contains(SmartFormatter.ParagraphMark);
            log.AppendLine($"paragraphs {(paragraphsOk ? "PASS" : "FAIL")}: " +
                $"pause->{(paraOut.Contains('\n') ? "break" : "none")}, " +
                $"no pause->{(noPauseOut.Contains('\n') ? "break" : "none")}, " +
                $"short take->{(shortJoined.Contains(SmartFormatter.ParagraphMark) ? "break" : "none")}");

            // Learning from a typed correction. The whole feature rests on this
            // one function: given what we inserted, how many backspaces and what
            // was typed, what did the user actually replace?
            var correctionVectors = new (string Inserted, int Back, string Typed, string? Heard, string? Write)[]
            {
                // Whole last word replaced.
                ("playing Armor Forger", 6, "Reforger", "Forger", "Reforger"),
                // Backspaced only the wrong half of a word: the part still in the
                // document belongs to both sides, so the rule is whole-word.
                ("open the Workbanch", 5, "bench", "Workbanch", "Workbench"),
                // Two words replaced.
                ("I use Charge B", 8, "ChargeBee", "Charge B", "ChargeBee"),
                // Nothing typed — just a deletion, not a correction.
                ("playing Armor Forger", 6, "", null, null),
                // Typed the same thing back.
                ("playing Forger", 6, "Forger", null, null),
                // Ate past our own text: we cannot know what was there.
                ("short", 40, "something", null, null),
                // A whole sentence is a rewrite, not a term.
                ("the quick brown fox jumps over it", 30, "a completely different sentence here", null, null),
                // Addresses are never learned.
                ("mail roy@example.com", 11, "someone@example.com", null, null),
            };
            var correctionPass = 0;
            foreach (var (inserted, back, typed, heard, write) in correctionVectors)
            {
                var got = CorrectionWatcher.Diff(inserted, back, typed);
                var ok = heard == null
                    ? got == null
                    : got != null && got.Value.Heard == heard && got.Value.Write == write;
                if (ok) correctionPass++;
                log.AppendLine($"correction {(ok ? "PASS" : "FAIL")}: '{inserted}' -{back} +'{typed}' -> " +
                    (got == null ? "(none)" : $"'{got.Value.Heard}' -> '{got.Value.Write}'") +
                    (ok ? "" : $" (expected {(heard == null ? "(none)" : $"'{heard}' -> '{write}'")})"));
            }
            log.AppendLine($"learned corrections: {correctionPass}/{correctionVectors.Length} passed");

            // A rule the user wrote is also a *sample of how the model mishears
            // them*, and a much closer anchor to the next mis-hearing than the
            // correct spelling. One rule has to cover the family, or the user
            // ends up writing one rule per mangling forever.
            var aliases = new[] { new VocabularyMatcher.Alias("Armor Forger", "Arma Reforger") };
            var aliasVectors = new (string Input, string Expected)[]
            {
                ("playing Armour Forger tonight", "playing Arma Reforger tonight"),
                ("playing arm of forger tonight", "playing Arma Reforger tonight"),
                ("playing Armored Forger tonight", "playing Arma Reforger tonight"),
                ("playing Armor Forge her tonight", "playing Arma Reforger tonight"),
                // Precision: ordinary prose near the alias must survive.
                ("the armor on the tank", "the armor on the tank"),
                ("the armour plating is thin", "the armour plating is thin"),
                ("he was a former arms dealer", "he was a former arms dealer"),
                ("I need to reforge this sword", "I need to reforge this sword"),
            };
            var aliasPass = 0;
            foreach (var (input, expected) in aliasVectors)
            {
                var actual = VocabularyMatcher.Apply(input, Array.Empty<string>(), aliases);
                var ok = actual == expected;
                if (ok) aliasPass++;
                log.AppendLine($"alias {(ok ? "PASS" : "FAIL")}: '{actual}'" +
                    (ok ? "" : $" (expected '{expected}')"));
            }
            log.AppendLine($"mis-hearing aliases: {aliasPass}/{aliasVectors.Length} passed");

            // The wiring itself: the live settings must actually carry the
            // dictionary into the formatter, which is where the fuzzy pass runs
            // during a real dictation.
            var wired = TextFormatter.Apply(
                "um, I was playing armour forger last night",
                FormatterOptions.FromSettings(terms, decodedLanguage: "en"));
            var wiringOk = wired == "I was playing Arma Reforger last night";
            log.AppendLine($"formatter wiring {(wiringOk ? "PASS" : "FAIL")}: '{wired}'");

            // Dictionary rules: they rewrite prose, never the inside of an
            // address or a link.
            var rules = new List<DictionaryEntry>
            {
                new() { Phrase = "figjam", Replacement = "FigJam" },
                new() { Phrase = "my email", Replacement = "roy@example.com" },
                new() { Phrase = "type", Replacement = "Type" },
            };
            var ruleVectors = new (string Input, string Expected)[]
            {
                ("open figjam please", "open FigJam please"),
                ("send it to my email", "send it to roy@example.com"),
                ("mail me at roy@type.com now", "mail me at roy@type.com now"),
                ("see https://example.com/type today", "see https://example.com/type today"),
                ("the type of file", "the Type of file"),
                ("figjam", "FigJam"),
            };
            // Rules must not feed each other, and the longest match must win.
            var cascadeRules = new List<DictionaryEntry>
            {
                new() { Phrase = "react", Replacement = "React" },
                new() { Phrase = "React", Replacement = "React.js" },
            };
            var longestRules = new List<DictionaryEntry>
            {
                new() { Phrase = "typer", Replacement = "Typer" },
                new() { Phrase = "voice typer", Replacement = "FlowType" },
            };
            var dollarRules = new List<DictionaryEntry>
            {
                new() { Phrase = "the price", Replacement = "$1 and $2" },
            };
            var spaceRules = new List<DictionaryEntry>
            {
                new() { Phrase = "-", Replacement = " ", MatchWholeWord = false },
            };
            var ruleExtraOk =
                DictionaryStore.ApplyRules("i use react daily", cascadeRules) == "i use React daily"
                && DictionaryStore.ApplyRules("voice typer here", longestRules) == "FlowType here"
                && DictionaryStore.ApplyRules("the price today", dollarRules) == "$1 and $2 today"
                && DictionaryStore.ApplyRules("state-of-the-art", spaceRules) == "state of the art"
                && !DictionaryStore.SectionsFor(spaceRules).Spellings.Contains("-");
            log.AppendLine($"dictionary extras {(ruleExtraOk ? "PASS" : "FAIL")}: " +
                $"cascade='{DictionaryStore.ApplyRules("i use react daily", cascadeRules)}' " +
                $"longest='{DictionaryStore.ApplyRules("voice typer here", longestRules)}' " +
                $"dollar='{DictionaryStore.ApplyRules("the price today", dollarRules)}' " +
                $"dehyphen='{DictionaryStore.ApplyRules("state-of-the-art", spaceRules)}'");
            var rulePass = 0;
            foreach (var (input, expected) in ruleVectors)
            {
                var actual = DictionaryStore.ApplyRules(input, rules);
                var ok = actual == expected;
                if (ok) rulePass++;
                log.AppendLine($"dictionary {(ok ? "PASS" : "FAIL")}: '{actual}'" +
                    (ok ? "" : $" (expected '{expected}')"));
            }
            log.AppendLine($"dictionary rules: {rulePass}/{ruleVectors.Length} passed");

            // What the dictionary tells the recogniser, split by role. Spoken
            // forms must reach the prompt and must NOT reach the fuzzy matcher,
            // which would otherwise rewrite correct text into the mis-hearing.
            var sectionFixture = new List<DictionaryEntry>
            {
                new() { Phrase = "armor forger", Replacement = "Arma Reforger" },
                new() { Phrase = "FigJam", Replacement = "" },
                new() { Phrase = "figjam", Replacement = "FigJam" },   // casing-only: no spoken form
                new() { Phrase = "my email", Replacement = "roy@example.com" },
            };
            var sections = DictionaryStore.SectionsFor(sectionFixture);
            var sectionsOk =
                sections.Spellings.Contains("Arma Reforger")
                && sections.Spellings.Contains("FigJam")
                && !sections.Spellings.Contains("armor forger")
                && !sections.Spellings.Any(t => t.Contains('@'))
                && sections.SpokenForms.Contains("armor forger")
                && !sections.SpokenForms.Contains("figjam")
                && !sections.SpokenForms.Contains("my email");
            log.AppendLine($"dictionary sections {(sectionsOk ? "PASS" : "FAIL")}: " +
                $"spellings=[{string.Join(", ", sections.Spellings)}] " +
                $"spoken=[{string.Join(", ", sections.SpokenForms)}]");

            // The initial prompt must stay inside whisper's window. Whisper keeps
            // the *tail* of an over-long prompt, so the spellings — the half that
            // must survive — are rendered last.
            var longVocabulary = Enumerable.Range(0, 200).Select(i => $"Term{i:000}").ToArray();
            var builtPrompt = Transcriber.BuildPrompt(
                EnglishVariant.RecognitionPrompt(EnglishVariant.Us),
                longVocabulary, new[] { "spoken one", "spoken two" }) ?? "";
            var shortPrompt = Transcriber.BuildPrompt(
                null, new[] { "Arma Reforger" }, new[] { "armor forger" }) ?? "";
            var promptOk = builtPrompt.Length <= Transcriber.MaxPromptChars
                && builtPrompt.Contains("American")
                && builtPrompt.Contains("Preferred spellings:")
                && builtPrompt.EndsWith('.')
                && builtPrompt.IndexOf("Possible spoken forms:", StringComparison.Ordinal)
                    < builtPrompt.IndexOf("Preferred spellings:", StringComparison.Ordinal)
                && shortPrompt == "Possible spoken forms: armor forger. Preferred spellings: Arma Reforger."
                && Transcriber.SanitizeTerm("Mac Book\n Pro\0") == "Mac Book Pro"
                && Transcriber.BuildPrompt(null, Array.Empty<string>()) == null;
            log.AppendLine($"prompt {(promptOk ? "PASS" : "FAIL")}: {builtPrompt.Length} chars " +
                $"(cap {Transcriber.MaxPromptChars}); short='{shortPrompt}'");

            // ----- Live transcription + recorder measurement (pure; no model, no mic) -----
            // Paste into RunSelfTest beside the existing "prompt" vectors, then AND
            // `liveOk` into allPassed. Same checks as --livecheck's pure section.

            // previousText goes after the variant sentence, before both dictionary
            // sections; without it the prompt is unchanged.
            var liveVariant = EnglishVariant.RecognitionPrompt(EnglishVariant.Us) ?? "";
            var livePrevious = "and that is why the migration slipped by a week. " + SmartFormatter.ParagraphMark
                + " We moved the billing service over on Tuesday, and so far the error rate has stayed flat in every region.";
            var livePrompt = Transcriber.BuildPrompt(liveVariant, new[] { "Arma Reforger" }, new[] { "armor forger" }, livePrevious) ?? "";
            var liveContext = Transcriber.PreviousContext(livePrevious, Transcriber.MaxPreviousTextChars) ?? "";
            var livePromptOk =
                livePrompt.StartsWith(liveVariant, StringComparison.Ordinal)
                && livePrompt.EndsWith("Preferred spellings: Arma Reforger.", StringComparison.Ordinal)
                && livePrompt.IndexOf("stayed flat", StringComparison.Ordinal)
                    < livePrompt.IndexOf("Possible spoken forms:", StringComparison.Ordinal)
                && !livePrompt.Contains(SmartFormatter.ParagraphMark)
                && livePrompt.Length <= Transcriber.MaxPromptChars
                && (Transcriber.BuildPrompt(liveVariant, Enumerable.Range(0, 200).Select(i => $"Term{i:000}").ToArray(),
                        new[] { "spoken one" }, livePrevious) ?? "").Length <= Transcriber.MaxPromptChars
                && Transcriber.BuildPrompt(null, Array.Empty<string>(), null, "too short") == null;

            // An echo of the previous chunk's text is stripped like a prompt echo.
            var liveEchoOk = HallucinationFilter.Apply(
                    "We moved the billing service over on Tuesday, and so far the error rate has stayed flat. Next we test the workers.",
                    liveVariant + " " + liveContext)
                == "Next we test the workers.";

            // Unpunctuated-mode detector.
            var livePunctOk = Transcriber.LooksUnpunctuated(
                    "hey team i wanted to give a quick update on where the migration stands before tomorrow's meeting we moved")
                && !Transcriber.LooksUnpunctuated("Hey team, I wanted to give a quick update. We moved the billing service over.")
                && !Transcriber.LooksUnpunctuated("ok sounds good see you then");

            // Dead-mic run (exact zeros) and the normalized meter.
            long zeroRun = 0;
            double firedAt = -1;
            for (var b = 0; b < 140; b++)   // 50 ms buffers: 0.5 s of signal, then zeros
            {
                zeroRun = AudioRecorder.ExtendZeroRun(zeroRun, 800, b < 10 ? 0 : 800);
                if (firedAt < 0 && zeroRun >= (long)(AudioRecorder.SilentInputThreshold.TotalSeconds * 16000)) firedAt = (b + 1) * 0.05;
            }
            float meterFloor = float.MaxValue;
            var m0 = AudioRecorder.NormalizeLevel(-200f, ref meterFloor);   // floor clamps to -60
            var m1 = AudioRecorder.NormalizeLevel(-50f, ref meterFloor);    // 10 dB above floor -> 0.5
            var m2 = AudioRecorder.NormalizeLevel(-20f, ref meterFloor);    // 40 dB above -> clamps to 1
            var liveRecorderOk = Math.Abs(firedAt - 5.5) < 1e-9
                && AudioRecorder.ExtendZeroRun(12345, 800, 300) == 300
                && m0 == 0f && meterFloor == AudioRecorder.LevelFloorClampDb
                && Math.Abs(m1 - 0.5f) < 1e-6 && m2 == 1f
                && AudioRecorder.SilentInputDue(5 * 16000, sawNonZero: false)
                && !AudioRecorder.SilentInputDue(5 * 16000, sawNonZero: true)
                && !AudioRecorder.SilentInputDue(29 * 16000, sawNonZero: true)
                && AudioRecorder.SilentInputDue(30 * 16000, sawNonZero: true);

            // Cut policy on synthetic takes: a 200 Hz tone whose loudness swings
            // like syllables but never pauses, optionally with silent gaps every 6 s.
            static float[] LiveTake(double seconds, double gap, bool roomTone = false)
            {
                var rng = new Random(1);
                var x = new float[(int)(seconds * 16000)];
                for (var i = 0; i < x.Length; i++)
                {
                    var t = i / 16000.0;
                    if (roomTone) { x[i] = (float)(rng.NextDouble() - 0.5) * 0.002f; continue; }
                    x[i] = gap > 0 && t > 1 && t % 6.0 < gap ? 0f
                        : (float)((0.2 + 0.1 * Math.Sin(2 * Math.PI * 4 * t)) * Math.Sin(2 * Math.PI * 200 * t));
                }
                return x;
            }
            var planContinuous = LiveTranscriber.PlanChunks(LiveTake(62, 0));
            var planPaused = LiveTranscriber.PlanChunks(LiveTake(62, 0.7));
            var planCommas = LiveTranscriber.PlanChunks(LiveTake(62, 0.45));
            var planShort = LiveTranscriber.PlanChunks(LiveTake(19.9, 0));
            var planRoom = LiveTranscriber.PlanChunks(LiveTake(62, 0, roomTone: true));
            bool PlanLengths(IReadOnlyList<LiveTranscriber.ChunkStat> plan) => plan.Where(c => !c.Final)
                .All(c => c.EndSeconds - c.StartSeconds >= 15 && c.EndSeconds - c.StartSeconds <= 28.11);
            var livePlanOk =
                planContinuous.Count >= 3 && planContinuous.Where(c => !c.Final).All(c => c.Forced) && PlanLengths(planContinuous)
                && planPaused.Count >= 3 && planPaused.All(c => !c.Forced) && PlanLengths(planPaused)
                && planPaused.Where(c => !c.Final).All(c => c.EndSeconds % 6.0 <= 0.72)
                && planCommas.Count >= 3 && planCommas.All(c => !c.Forced) && PlanLengths(planCommas)
                && planCommas.Where(c => !c.Final).All(c => c.EndSeconds % 6.0 <= 0.47)
                && planShort.Count == 0
                && planRoom.Count >= 3 && planRoom.All(c => c.Silent);

            var liveOk = livePromptOk && liveEchoOk && livePunctOk && liveRecorderOk && livePlanOk;
            log.AppendLine($"live transcription {(liveOk ? "PASS" : "FAIL")}: prompt={livePromptOk} echo={liveEchoOk} " +
                $"unpunctuated={livePunctOk} recorder={liveRecorderOk} cuts={livePlanOk} " +
                $"(cuts on 0.7 s pauses: {string.Join(" ", planPaused.Select(c => $"{c.EndSeconds:0.0}"))})");

            // ----- 2.0 components -----
            var v2Ok = Diagnostics.V2SelfTest.Run(log);

            var contextTerms = ContextTerms.SelfTest();
            foreach (var line in contextTerms.Lines) log.AppendLine("terms " + line);
            log.AppendLine($"context terms: {contextTerms.Passed}/{contextTerms.Total} passed");

            var edits = EditLearner.SelfTest();
            foreach (var line in edits.Lines) log.AppendLine("edits " + line);
            log.AppendLine($"edit learning: {edits.Passed}/{edits.Total} passed");

            // Read-only: lists the audio sessions MediaMuter would silence.
            foreach (var line in MediaMuter.DescribeSessions().Take(12)) log.AppendLine("media: " + line);
            var waveInOk =
                AudioDeviceWatcher.MatchWaveInName("Headset Microphone (DualSense Wireless Controller)",
                    new[] { "Headset Microphone (DualSense W" }) == "Headset Microphone (DualSense W"
                && AudioDeviceWatcher.MatchWaveInName("Microphone (2- USB Audio Device)",
                    new[] { "Microphone" }) == "Microphone (2- USB Audio Device"
                && AudioDeviceWatcher.MatchWaveInName("Mic (Short)", new[] { "Mic (Short)", "Mic" }) == "Mic (Short)";
            log.AppendLine($"waveIn name mapping {(waveInOk ? "PASS" : "FAIL")}");
            foreach (var f in AudioDeviceWatcher.ActiveCaptureDevices())
            {
                log.AppendLine($"mic: {f} -> waveIn '{AudioDeviceWatcher.WaveInNameFor(f)}'");
            }

            var barResults = FlowType.UI.Bar.BarSelfTest.Run();
            foreach (var (name, ok, detail) in barResults)
            {
                log.AppendLine($"bar: {(ok ? "PASS" : "FAIL")} {name} [{detail}]");
            }
            var barOk = barResults.All(r => r.Ok);

            var llmOk = Environment.GetCommandLineArgs().Contains("--fast")
                || await Diagnostics.V2SelfTest.LlmSmokeAsync(log);

            var allPassed = pass == vectors.Length
                && v2Ok
                && contextTerms.Passed == contextTerms.Total
                && edits.Passed == edits.Total
                && waveInOk
                && barOk
                && liveOk
                && llmOk
                && variantPass == variantVectors.Length
                && guardPass == guardVectors.Length
                && vocabPass == vocabVectors.Length
                && aliasPass == aliasVectors.Length
                && correctionPass == correctionVectors.Length
                && smartPass == smartVectors.Length
                && paragraphsOk
                && rulePass == ruleVectors.Length
                && ruleExtraOk
                && sectionsOk
                && attemptOk
                && languagePass == languageVectors.Length + 1
                && spacePass == spaceVectors.Length
                && structuralPass == structuralVectors.Length
                && markerPass == markerVectors.Length
                && budgetPass == budgetVectors.Length
                && wiringOk
                && promptOk
                && conditionOk
                && Transcriber.Instance.IsReady;
            log.AppendLine(allPassed ? "SELFTEST OK" : "SELFTEST INCOMPLETE");
        }
        catch (Exception ex)
        {
            log.AppendLine($"SELFTEST FAILED: {ex}");
        }
        finally
        {
            AppPaths.EnsureCreated();
            File.WriteAllText(Path.Combine(AppPaths.DataDir, "selftest.txt"), log.ToString());
            Shutdown();
        }
    }
}
