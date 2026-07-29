using FlowType.Audio;
using FlowType.Core;
using FlowType.Engine;
using FlowType.Input;

namespace FlowType.Session;

public enum SessionState
{
    Idle,
    Recording,
    Processing,
    Error,
}

/// <summary>
/// The dictation state machine: hotkey → capture → whisper → format → insert.
///
/// Activation follows Wispr Flow's signature interaction ("smart" mode):
/// hold the hotkey to push-to-talk, or quick-tap it to go hands-free until the
/// next tap. Esc cancels. In push-to-talk, pressing any other key while
/// holding means "keyboard shortcut, not dictation" and cancels quietly.
/// All members run on the UI thread (hook events are posted here).
/// </summary>
public sealed class DictationSession
{
    private static readonly TimeSpan TapThreshold = TimeSpan.FromMilliseconds(350);
    private const double MinimumDurationSeconds = 0.3;

    /// <summary>
    /// Grace period before a modifier hotkey actually opens the mic. Modifiers
    /// like Ctrl are also the first key of ordinary shortcuts, so starting
    /// instantly would chime and record on every Ctrl+C. Waiting a beat lets
    /// <see cref="OnOtherKeyPressed"/> claim the press as a shortcut first —
    /// short enough that hold-to-talk still feels immediate.
    /// </summary>
    private static readonly TimeSpan ModifierGrace = TimeSpan.FromMilliseconds(180);

    /// <summary>Virtual-keys that double as shortcut modifiers.</summary>
    private static readonly HashSet<int> ModifierVks = new()
        { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C };

    public static DictationSession Instance { get; } = new();

    private HotkeyManager? _hotkeys;
    private DateTime _pressTime;
    private CancellationTokenSource? _processingCts;
    private readonly object _ctsGate = new();

    /// <summary>Incremented on every hotkey press/release so a pending grace-period
    /// start can tell whether it is still the current gesture.</summary>
    private int _gesture;
    private bool _armPending;

    public SessionState State { get; private set; } = SessionState.Idle;
    public bool IsHandsFree { get; private set; }
    public DateTime RecordingStartTime { get; private set; }

    public event Action<SessionState, string>? StateChanged;

    /// <summary>Transient toast for the flow bar ("✓ 42 words", "Canceled"…).</summary>
    public event Action<string, bool>? Flash;

    /// <summary>The user needs the model manager (nothing downloaded/loaded).</summary>
    public event Action? SetupNeeded;

    private DictationSession() { }

    public void Attach(HotkeyManager hotkeys)
    {
        _hotkeys = hotkeys;
        hotkeys.Pressed += OnPressed;
        hotkeys.Released += OnReleased;
        hotkeys.OtherKeyPressed += OnOtherKeyPressed;
        hotkeys.CancelRequested += OnCancelRequested;

        AudioRecorder.Instance.AutoStopRequested += () =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (State == SessionState.Recording) RequestStop();
            });
    }

    // ----- Hotkey handling -----

    private async void OnPressed()
    {
        var gesture = Interlocked.Increment(ref _gesture);

        switch (State)
        {
            case SessionState.Idle:
            case SessionState.Error:
                if (SettingsStore.Instance.Settings.DictationPaused) return;

                _pressTime = DateTime.UtcNow;
                var mode = SettingsStore.Instance.Settings.ActivationMode;

                // A modifier hotkey waits out the grace period so ordinary
                // shortcuts (Ctrl+C, Ctrl+V) never open the mic.
                if (UsesModifierHotkey())
                {
                    _armPending = true;
                    await Task.Delay(ModifierGrace);
                    _armPending = false;
                    // Superseded by a release or a shortcut? Then do nothing.
                    if (Volatile.Read(ref _gesture) != gesture) return;
                    if (State is not (SessionState.Idle or SessionState.Error)) return;
                }

                StartCapture(handsFree: mode == "toggle");
                break;

            case SessionState.Recording when IsHandsFree:
                RequestStop();  // tap ends a hands-free session
                break;
        }
    }

    private void OnReleased()
    {
        var mode = SettingsStore.Instance.Settings.ActivationMode;

        // Released inside the grace period: too quick to be speech. In smart
        // mode that's the hands-free tap; otherwise it was just a shortcut
        // modifier and we never opened the mic at all.
        if (_armPending)
        {
            Interlocked.Increment(ref _gesture);
            if (mode == "smart" && !SettingsStore.Instance.Settings.DictationPaused)
            {
                StartCapture(handsFree: true);
                Flash?.Invoke("Hands-free — tap again to finish", false);
            }
            return;
        }

        if (State != SessionState.Recording || IsHandsFree) return;

        if (mode == "smart" && DateTime.UtcNow - _pressTime < TapThreshold)
        {
            // Quick tap → hands-free until the next tap (or Esc).
            IsHandsFree = true;
            if (_hotkeys != null) _hotkeys.RecordingActive = true;
            Flash?.Invoke("Hands-free — tap again to finish", false);
            return;
        }
        RequestStop();
    }

    private void OnOtherKeyPressed()
    {
        // Another key while *holding* the hotkey = a keyboard shortcut
        // (e.g. Ctrl+C). Cancel the pending arm, or bail out of a capture
        // already in flight. Hands-free typing is unaffected.
        if (_armPending)
        {
            Interlocked.Increment(ref _gesture);
            return;
        }
        if (State == SessionState.Recording && !IsHandsFree)
        {
            CancelCapture(playCue: false);
        }
    }

    /// <summary>Whether the bound hotkey is (or starts with) a shortcut modifier.</summary>
    private static bool UsesModifierHotkey()
    {
        var def = HotkeyDefs.Resolve(SettingsStore.Instance.Settings);
        // Multi-group chords (Ctrl+Win) are already unambiguous.
        if (def.Groups.Length != 1) return false;
        return def.Groups[0].All(vk => ModifierVks.Contains(vk));
    }

    private void OnCancelRequested()
    {
        if (State == SessionState.Recording)
        {
            CancelCapture(playCue: true);
        }
        else if (State == SessionState.Processing)
        {
            lock (_ctsGate)
            {
                // Guarded: the token source is disposed the moment processing
                // finishes, and Esc can land in that window.
                try { _processingCts?.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }
    }

    /// <summary>Click target for the flow bar / tray.</summary>
    public void Toggle()
    {
        if (State == SessionState.Recording) RequestStop();
        else if (State is SessionState.Idle or SessionState.Error)
        {
            // Clicking the bar is an explicit request: honour it even when the
            // hotkey is paused.
            Interlocked.Increment(ref _gesture);
            StartCapture(handsFree: true);
        }
    }

    // ----- Capture lifecycle -----

    private void StartCapture(bool handsFree)
    {
        if (!Transcriber.Instance.IsReady)
        {
            var model = ModelCatalog.ById(SettingsStore.Instance.Settings.SelectedModel);
            if (model is { IsDownloaded: true })
            {
                if (!Transcriber.Instance.IsLoading)
                {
                    _ = Transcriber.Instance.LoadModelAsync(model.Id);
                }
                Flash?.Invoke("Warming up the AI model — try again in a moment", true);
            }
            else
            {
                Flash?.Invoke("No AI model yet — let's set one up", true);
                SetupNeeded?.Invoke();
            }
            return;
        }

        try
        {
            AudioRecorder.Instance.StartRecording();
            IsHandsFree = handsFree;
            RecordingStartTime = DateTime.Now;
            if (_hotkeys != null) _hotkeys.RecordingActive = true;
            SetState(SessionState.Recording, "");
            SoundCues.Start();
        }
        catch (Exception ex)
        {
            SetState(SessionState.Error, $"Microphone unavailable: {ex.Message}");
            ResetAfterDelay();
        }
    }

    private void CancelCapture(bool playCue)
    {
        AudioRecorder.Instance.Cancel();
        if (_hotkeys != null) _hotkeys.RecordingActive = false;
        IsHandsFree = false;
        if (playCue)
        {
            SoundCues.Cancel();
            Flash?.Invoke("Canceled", false);
        }
        SetState(SessionState.Idle, "");
    }

    private void RequestStop()
    {
        if (State == SessionState.Recording) _ = StopAndTranscribeAsync();
    }

    private async Task StopAndTranscribeAsync()
    {
        IsHandsFree = false;
        if (_hotkeys != null) _hotkeys.RecordingActive = false;
        SetState(SessionState.Processing, "");
        SoundCues.Stop();

        string? wavPath = null;
        var cts = new CancellationTokenSource();
        lock (_ctsGate) _processingCts = cts;
        try
        {
            var take = await AudioRecorder.Instance.StopAsync();
            if (take == null || take.Value.DurationSeconds < MinimumDurationSeconds)
            {
                AudioRecorder.TryDelete(take?.Path);
                SetState(SessionState.Idle, "");
                return;
            }
            wavPath = take.Value.Path;

            var settings = SettingsStore.Instance.Settings;
            var raw = await Transcriber.Instance.TranscribeAsync(
                wavPath, settings.Language,
                DictionaryStore.Instance.VocabularyWords, cts.Token);

            var text = TextFormatter.Apply(
                raw, FormatterOptions.FromSettings(), DictionaryStore.Instance.Apply);

            if (string.IsNullOrWhiteSpace(text))
            {
                Flash?.Invoke("Didn't catch anything", false);
                SetState(SessionState.Idle, "");
                return;
            }

            var appName = TextInjector.ForegroundAppName();
            var words = TextFormatter.CountWords(text);

            if (settings.AutoInsert)
            {
                if (settings.InsertMethod == "type")
                {
                    await TextInjector.TypeAsync(text);
                }
                else
                {
                    await TextInjector.PasteAsync(text, settings.RestoreClipboard);
                }
                Flash?.Invoke($"✓ {words} {(words == 1 ? "word" : "words")}", false);
            }
            else
            {
                TextInjector.CopyOnly(text);
                Flash?.Invoke($"Copied — {words} {(words == 1 ? "word" : "words")}", false);
            }

            if (settings.SaveHistory)
            {
                NotesStore.Instance.Add(new Note
                {
                    Text = text,
                    DurationSeconds = take.Value.DurationSeconds,
                    WordCount = words,
                    AppName = appName,
                    ModelId = Transcriber.Instance.CurrentModelId,
                });
            }
            StatsStore.Instance.Record(words, take.Value.DurationSeconds, appName);

            SetState(SessionState.Idle, "");
        }
        catch (OperationCanceledException)
        {
            Flash?.Invoke("Canceled", false);
            SetState(SessionState.Idle, "");
        }
        catch (Exception ex)
        {
            SetState(SessionState.Error, ex.Message);
            ResetAfterDelay();
        }
        finally
        {
            AudioRecorder.TryDelete(wavPath);
            lock (_ctsGate)
            {
                _processingCts = null;
                cts.Dispose();
            }
        }
    }

    private void SetState(SessionState state, string detail)
    {
        State = state;
        StateChanged?.Invoke(state, detail);
    }

    private async void ResetAfterDelay()
    {
        await Task.Delay(3000);
        if (State == SessionState.Error) SetState(SessionState.Idle, "");
    }
}
