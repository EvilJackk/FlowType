using System.IO;
using System.Text;
using System.Windows;
using FlowType.Audio;
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

        _hotkeys = new HotkeyManager(Dispatcher);
        Hotkeys = _hotkeys;
        DictationSession.Instance.Attach(_hotkeys);
        DictationSession.Instance.SetupNeeded +=
            () => Dispatcher.BeginInvoke(() => ShowMain(settings: true));
        DictationSession.Instance.StateChanged +=
            (_, _) => Dispatcher.BeginInvoke(UpdateTrayMenu);

        _bar = new FlowBarWindow();
        _bar.OpenRequested += () => ShowMain();
        _bar.SettingsRequested += () => ShowMain(settings: true);
        _bar.QuitRequested += QuitApp;

        SetupTray();

        // Warm the saved model so the first dictation is instant.
        _ = Transcriber.Instance.InitializeAsync();

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

    private void ShowMain(bool settings = false)
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
        if (settings) _main.NavigateToSettings();
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
        _hotkeys?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
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
                && TextFormatter.Apply("hmm, ok then", ptOpts) == "Ok then";
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

            var allPassed = pass == vectors.Length
                && variantPass == variantVectors.Length
                && guardPass == guardVectors.Length
                && vocabPass == vocabVectors.Length
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
