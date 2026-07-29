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
    }

    // ----- Tray -----

    private void SetupTray()
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

    // ----- Self-test (hidden diagnostic: FlowType.exe --selftest) -----

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
            log.AppendLine($"settings: model={s.SelectedModel} gpu={s.UseGpu} mic='{s.MicDeviceName}' hotkey={s.Hotkey} mode={s.ActivationMode}");

            await Transcriber.Instance.InitializeAsync();
            log.AppendLine($"model: loaded={Transcriber.Instance.IsReady} id={Transcriber.Instance.CurrentModelId} runtime={Transcriber.Instance.RuntimeLabel}");

            if (Transcriber.Instance.IsReady)
            {
                AudioRecorder.Instance.StartRecording();
                await Task.Delay(2000);
                var take = await AudioRecorder.Instance.StopAsync();
                log.AppendLine($"recording: duration={take?.DurationSeconds:0.00}s");
                if (take != null)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var raw = await Transcriber.Instance.TranscribeAsync(
                        take.Value.Path, s.Language, DictionaryStore.Instance.VocabularyWords);
                    sw.Stop();
                    log.AppendLine($"transcribe: {sw.ElapsedMilliseconds}ms text='{raw}'");
                    AudioRecorder.TryDelete(take.Value.Path);
                }
            }

            // Formatter vectors — every Wispr-style transform in one pass.
            var opts = new FormatterOptions(
                RemoveFillers: true, ScratchThat: true, LineCommands: true,
                PunctuationCommands: false, SmartTrailingPunctuation: true);
            var vectors = new (string Input, string Expected)[]
            {
                ("Um, hello world, uh, this is great.", "Hello world, this is great."),
                ("Send the report. New line. Thanks again.", "Send the report.\nThanks again."),
                ("roy@example.com.", "roy@example.com"),
                ("this is wrong, scratch that, this is right.", "This is right."),
                ("First point. New paragraph. Second point.", "First point.\n\nSecond point."),
                ("Hello there.", "Hello there."),
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

            var allPassed = pass == vectors.Length
                && variantPass == variantVectors.Length
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
