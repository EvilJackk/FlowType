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

    /// <summary>
    /// How long the mic stays open after "stop" is requested. People release
    /// the key on the last syllable, not after it, so without this the final
    /// word is routinely clipped ("see you tomor…"). Short enough that the
    /// result still feels instant; the stop chime plays immediately.
    /// </summary>
    private static readonly TimeSpan ReleaseTail = TimeSpan.FromMilliseconds(280);

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
            if (_hotkeys != null) _hotkeys.SessionActive = true;
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
            if (_hotkeys != null) _hotkeys.SessionActive = true;
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
        if (_hotkeys != null) _hotkeys.SessionActive = false;
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
        // SessionActive deliberately stays set until the finally below: Esc has
        // to keep cancelling while the model is still working.
        SetState(SessionState.Processing, "");
        SoundCues.Stop();

        // Written on every exit path, including the exception one — the paths
        // that most need counting are the ones ad-hoc logging always misses.
        using var attempt = new DictationAttempt();

        string? wavPath = null;
        var cts = new CancellationTokenSource();
        lock (_ctsGate) _processingCts = cts;
        try
        {
            // Esc during the tail still works: the recorder stops normally and
            // the already-cancelled token makes the transcription throw.
            await Task.Delay(ReleaseTail);
            // Bounded: the stop result comes from the device's own callback, and
            // a device that never reports back would otherwise hold the session
            // in Processing forever. Eight seconds is far past the 280 ms tail
            // plus writer teardown on any machine that is actually working.
            var take = await AudioRecorder.Instance.StopAsync()
                .WaitAsync(TimeSpan.FromSeconds(8));
            if (take == null || take.Value.DurationSeconds < MinimumDurationSeconds)
            {
                attempt.Outcome = AttemptOutcome.TooShort;
                attempt.AudioSeconds = take?.DurationSeconds ?? 0;
                AudioRecorder.TryDelete(take?.Path);
                SetState(SessionState.Idle, "");
                return;
            }
            attempt.AudioSeconds = take.Value.DurationSeconds;
            attempt.TakeOutcome = take.Value.Outcome;
            wavPath = take.Value.Path;
            if (take.Value.Outcome == TakeOutcome.RecoverableDeviceError)
            {
                Flash?.Invoke("Microphone disconnected — transcribing what was captured", true);
            }

            // A wedged decode used to strand the session in Processing forever,
            // and with no hotkey handling for that state the app was dead until
            // it was restarted. The budget is generous — it exists to break a
            // hang, not to cut a slow-but-working transcription short.
            cts.CancelAfter(Transcriber.DecodeBudget(take.Value.DurationSeconds));

            var settings = SettingsStore.Instance.Settings;
            var dictionary = DictionaryStore.Instance.VocabularySections;
            var vocabulary = dictionary.Spellings;
            var decodeWatch = System.Diagnostics.Stopwatch.StartNew();
            var result = await Transcriber.Instance.TranscribeAsync(
                wavPath, settings.Language, vocabulary, cts.Token,
                spokenForms: dictionary.SpokenForms);
            decodeWatch.Stop();

            attempt.DecodeMs = decodeWatch.ElapsedMilliseconds;
            attempt.Verdict = result.Verdict;
            attempt.Metrics = result.Metrics;
            attempt.Language = result.Language;
            attempt.RawLength = result.Text.Length;
            attempt.ModelId = Transcriber.Instance.CurrentModelId;
            attempt.Runtime = Transcriber.Instance.RuntimeLabel;
            attempt.Accurate = settings.AccurateDecoding;

            // Formatting is an improvement on the transcript, never a
            // precondition for it: a bad regex — including one in the user's own
            // dictionary — must not turn words the model got right into a red
            // error pill.
            string text;
            try
            {
                var model = ModelCatalog.ById(Transcriber.Instance.CurrentModelId);
                text = TextFormatter.Apply(
                    result.Text,
                    FormatterOptions.FromSettings(
                        vocabulary, result.Language, model?.EnglishOnly == true),
                    DictionaryStore.Instance.Apply);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                text = result.Text.Trim();
            }
            cts.Token.ThrowIfCancellationRequested();

            attempt.FinalLength = text.Length;
            if (string.IsNullOrWhiteSpace(text))
            {
                attempt.Outcome = result.Verdict switch
                {
                    TakeVerdict.NoInput => AttemptOutcome.NoInput,
                    TakeVerdict.NoSpeech => AttemptOutcome.NoSpeech,
                    _ => AttemptOutcome.NothingToType,
                };
                // Saying *why* nothing arrived is the difference between "try
                // again" and "your microphone is muted".
                Flash?.Invoke(result.Verdict switch
                {
                    TakeVerdict.NoInput => "No sound from the microphone — check it's not muted",
                    TakeVerdict.NoSpeech => "Didn't hear any speech",
                    _ => "Didn't catch anything",
                }, result.Verdict == TakeVerdict.NoInput);
                SetState(SessionState.Idle, "");
                return;
            }

            // Remember what a clip long enough to identify actually decoded as,
            // so the next two-second take does not have to guess.
            if (!string.IsNullOrWhiteSpace(result.Language)
                && take.Value.DurationSeconds >= Transcriber.LanguageDetectionSeconds
                && result.Language != settings.LastDetectedLanguage)
            {
                settings.LastDetectedLanguage = result.Language!;
                SettingsStore.Instance.Save();
            }

            var appName = TextInjector.ForegroundAppName();
            var words = TextFormatter.CountWords(text);
            attempt.AppName = appName;
            attempt.WordCount = words;

            // Last chance to honour Esc. Past this point the text is on its way
            // into someone else's document and cancelling would be a lie.
            cts.Token.ThrowIfCancellationRequested();

            // Notes and stats keep the clean text; only what goes into someone
            // else's document gets the sentence-boundary space.
            var outgoing = settings.AppendTrailingSpace
                ? TextFormatter.WithSentenceSpace(text)
                : text;

            if (settings.AutoInsert)
            {
                if (settings.InsertMethod == "type")
                {
                    await TextInjector.TypeAsync(outgoing);
                }
                else
                {
                    await TextInjector.PasteAsync(outgoing, settings.RestoreClipboard);
                }
                Flash?.Invoke($"✓ {words} {(words == 1 ? "word" : "words")}", false);
                attempt.Outcome = AttemptOutcome.Inserted;
            }
            else
            {
                TextInjector.CopyOnly(outgoing);
                Flash?.Invoke($"Copied — {words} {(words == 1 ? "word" : "words")}", false);
                attempt.Outcome = AttemptOutcome.Copied;
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
            attempt.Outcome = AttemptOutcome.Canceled;
            Flash?.Invoke("Canceled", false);
            SetState(SessionState.Idle, "");
        }
        catch (TimeoutException)
        {
            attempt.Outcome = AttemptOutcome.DeviceTimeout;
            // The recorder never reported back. Say so rather than sitting in
            // Processing; the file is left on disk for the age-based cleanup.
            wavPath = null;
            SetState(SessionState.Error, "The microphone stopped responding");
            ResetAfterDelay();
        }
        catch (Exception ex)
        {
            attempt.Outcome = AttemptOutcome.Failed;
            SetState(SessionState.Error, ex.Message);
            ResetAfterDelay();
        }
        finally
        {
            // Every exit path, including the exception one — otherwise Esc stays
            // swallowed from the foreground app for the rest of the session.
            if (_hotkeys != null) _hotkeys.SessionActive = false;
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
