using System.Text.RegularExpressions;
using FlowType.Audio;
using FlowType.Context;
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
/// holding means "keyboard shortcut, not dictation" and cancels quietly —
/// except Space, which upgrades the take to hands-free, and Alt, which turns it
/// into a Command Mode instruction (Wispr's Ctrl+Win+Space / Ctrl+Win+Alt).
///
/// 2.0 pipeline, in order, after the audio is in:
///   whisper (whole take, or the live chunker for long takes)
///   → whole-utterance snippet?  → deterministic formatter (+ dictionary)
///   → "press enter" command     → optional on-device AI polish
///   → per-app style             → fit to the caret (screen context)
///   → refocus the target app    → insert → learn from the user's fixes.
/// Every stage after whisper is an improvement, never a precondition: each one
/// fails open to the text it was given.
///
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

    /// <summary>
    /// UI Automation is asked for the focused field the moment the hotkey goes
    /// down — that is when the target is unambiguous — and given this long. A
    /// hung app costs nothing but the context.
    /// </summary>
    private static readonly TimeSpan ContextTimeout = TimeSpan.FromMilliseconds(450);

    /// <summary>Virtual-keys that double as shortcut modifiers.</summary>
    private static readonly HashSet<int> ModifierVks = new()
        { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C };

    private const int VkSpace = 0x20;
    private static bool IsAlt(int vk) => vk is 0x12 or 0xA4 or 0xA5;

    public static DictationSession Instance { get; } = new();

    private HotkeyManager? _hotkeys;
    private DateTime _pressTime;
    private CancellationTokenSource? _processingCts;
    private readonly object _ctsGate = new();

    /// <summary>Incremented on every hotkey press/release so a pending grace-period
    /// start can tell whether it is still the current gesture.</summary>
    private int _gesture;
    private bool _armPending;

    // Per-take state, set in StartCapture.
    private IntPtr _targetWindow;
    private Task<FocusSnapshot>? _contextTask;
    private bool _liveStarted;

    /// <summary>The Windows default mic's endpoint id at take start when following Windows (MicDeviceName ""); null otherwise.</summary>
    private string? _takeCaptureId;
    private bool _mediaMuted;

    public SessionState State { get; private set; } = SessionState.Idle;
    public bool IsHandsFree { get; private set; }
    public DateTime RecordingStartTime { get; private set; }

    /// <summary>True while the current take is a spoken instruction (Command Mode).</summary>
    public bool IsCommandMode { get; private set; }

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

        // A learned correction is worth saying out loud: it is the one thing
        // FlowType changes about itself without being asked.
        CorrectionWatcher.Instance.Learned += c =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                Flash?.Invoke($"Added to dictionary: {c.Heard} → {c.Write}", false));
        EditLearner.Instance.Learned += c =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                Flash?.Invoke($"Learned from your edit: {c.Heard} → {c.Write}", false));

        AudioRecorder.Instance.AutoStopRequested += () =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (State == SessionState.Recording) RequestStop();
            });

        // The device vanished mid-take: transcribe what it captured now.
        AudioRecorder.Instance.DeviceLost += () =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (State != SessionState.Recording) return;
                Flash?.Invoke("Microphone disconnected — transcribing what was captured", true);
                RequestStop();
            });

        // A muted or dead microphone delivers exact zeros; say so while the
        // user is still talking instead of after they have finished.
        AudioRecorder.Instance.SilentInput += _ =>
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (State == SessionState.Recording)
                {
                    Flash?.Invoke("No sound from the microphone — is it muted?", true);
                }
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
            _armPending = false;
            Interlocked.Increment(ref _gesture);
            // Only from rest: a chattering key or a double tap must never start
            // a second take on top of one that is still being transcribed.
            if (mode == "smart" && !SettingsStore.Instance.Settings.DictationPaused
                && State is SessionState.Idle or SessionState.Error
                && StartCapture(handsFree: true))
            {
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
            SetState(SessionState.Recording, IsCommandMode ? "command" : "");
            Flash?.Invoke("Hands-free — tap again to finish", false);
            return;
        }
        RequestStop();
    }

    private void OnOtherKeyPressed(int vk)
    {
        // Another key while *holding* the hotkey = a keyboard shortcut
        // (e.g. Ctrl+C). Cancel the pending arm, or bail out of a capture
        // already in flight. Hands-free typing is unaffected.
        if (_armPending)
        {
            // A shortcut (RightCtrl+C) claimed the press: the release that
            // follows is not a hands-free tap.
            _armPending = false;
            Interlocked.Increment(ref _gesture);
            return;
        }
        if (State != SessionState.Recording || IsHandsFree) return;

        if (vk == VkSpace)
        {
            // Hold-to-talk + Space: keep talking without holding anything.
            IsHandsFree = true;
            if (_hotkeys != null) _hotkeys.SessionActive = true;
            Flash?.Invoke("Hands-free — press the hotkey again to finish", false);
            SetState(SessionState.Recording, IsCommandMode ? "command" : "");
            return;
        }

        if (IsAlt(vk) && SettingsStore.Instance.Settings.CommandMode)
        {
            if (LocalLlm.Instance.IsReady)
            {
                if (!IsCommandMode)
                {
                    IsCommandMode = true;
                    SetState(SessionState.Recording, "command");
                }
            }
            else
            {
                Flash?.Invoke("Command Mode needs the on-device AI — set it up in FlowType → AI", true);
            }
            return;
        }

        CancelCapture(playCue: false);
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
            _userCanceled = true;
            lock (_ctsGate)
            {
                // Guarded: the token source is disposed the moment processing
                // finishes, and Esc can land in that window.
                try { _processingCts?.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }
    }

    /// <summary>The flow bar's ✕ button: the same as pressing Esc.</summary>
    public void Cancel() => OnCancelRequested();

    /// <summary>
    /// A capture device was unplugged mid-take. Stop and transcribe what was
    /// captured — but only when it is the mic this take is actually using:
    /// the saved device, or (when following Windows) the default endpoint
    /// recorded at take start, not whatever else happened to be removed.
    /// </summary>
    public void OnCaptureEndpointRemoved(AudioDeviceWatcher.CaptureEndpoint removed)
    {
        if (State != SessionState.Recording) return;
        var saved = SettingsStore.Instance.Settings.MicDeviceName;
        if (saved.Length > 0)
        {
            if (AudioDeviceWatcher.WaveInNameFor(removed.FriendlyName) != saved) return;
        }
        else if (_takeCaptureId != null
                 && !string.Equals(_takeCaptureId, removed.Id, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        Flash?.Invoke(removed.FriendlyName.Length > 0
            ? $"Microphone disconnected: {removed.FriendlyName}"
            : "Microphone disconnected", true);
        RequestStop();
    }

    /// <summary>Show a toast on the flow bar from anywhere in the app (UI thread).</summary>
    public void Notify(string message, bool isError = false) => Flash?.Invoke(message, isError);

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

    /// <summary>Open the mic for a new take. Returns false when it could not start.</summary>
    private bool StartCapture(bool handsFree)
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
            return false;
        }

        var settings = SettingsStore.Instance.Settings;
        try
        {
            // A new dictation invalidates any pending correction watch: the
            // caret is about to move somewhere we did not put it.
            CorrectionWatcher.Instance.Disarm();
            // EditLearner is deliberately left armed: it re-reads the field by
            // anchors, so the previous dictation's fix can still be learned
            // while this one is being spoken. Arm() replaces the watch.
            // A History recording playing through the speakers would end up in the take.
            RecordingArchive.Instance.StopPlayback();
            // Live dictation outranks re-transcribing an old note: free the
            // decoder now rather than make this take wait behind it.
            CancelRetranscription();

            // Remember where the text has to go. If the user clicks the flow
            // bar or another window while talking, the paste still lands in
            // the app they started in (Wispr stores and refocuses it too).
            _targetWindow = NativeMethods.GetForegroundWindow();
            _contextTask = settings.UseScreenContext
                ? FocusContext.CaptureAsync(ContextTimeout)
                : null;

            // The chunker subscribes before the mic opens, so no early buffer is
            // missed; names read from the field reach it once UIA answers.
            _liveStarted = false;
            if (settings.LiveTranscription)
            {
                var liveDictionary = DictionaryStore.Instance.VocabularySections;
                LiveTranscriber.Instance.Begin(settings.Language, liveDictionary.Spellings, liveDictionary.SpokenForms);
                _liveStarted = true;
                _contextTask?.ContinueWith(t =>
                {
                    if (t.Status == TaskStatus.RanToCompletion)
                    {
                        LiveTranscriber.Instance.UpdateVocabulary(
                            MergeVocabulary(liveDictionary.Spellings, t.Result.Terms), liveDictionary.SpokenForms);
                    }
                }, TaskScheduler.Default);
            }

            _takeCaptureId = settings.MicDeviceName.Length == 0
                ? AudioDeviceWatcher.DefaultCaptureEndpoint()?.Id
                : null;
            AudioRecorder.Instance.StartRecording();
            IsHandsFree = handsFree;
            IsCommandMode = false;
            RecordingStartTime = DateTime.Now;
            if (_hotkeys != null) _hotkeys.SessionActive = true;
            SetState(SessionState.Recording, "");
            SoundCues.Start();

            // The start cue plays first, then other apps go quiet — the same
            // order Wispr uses, so the cue itself is never muted.
            _mediaMuted = false;
            if (settings.MuteMediaWhileDictating)
            {
                _mediaMuted = true;
                _ = MediaMuter.Instance.MuteOthersAsync();
            }
            return true;
        }
        catch (Exception ex)
        {
            if (_liveStarted) { LiveTranscriber.Instance.Cancel(); _liveStarted = false; }
            RestoreMedia();
            if (_hotkeys != null) _hotkeys.SessionActive = false;
            SetState(SessionState.Error, $"Microphone unavailable: {ex.Message}");
            ResetAfterDelay();
            return false;
        }
    }

    private void RestoreMedia()
    {
        if (!_mediaMuted) return;
        _mediaMuted = false;
        _ = MediaMuter.Instance.RestoreAsync();
    }

    private void CancelCapture(bool playCue)
    {
        AudioRecorder.Instance.Cancel();
        if (_liveStarted) LiveTranscriber.Instance.Cancel();
        _liveStarted = false;
        RestoreMedia();
        if (_hotkeys != null) _hotkeys.SessionActive = false;
        IsHandsFree = false;
        IsCommandMode = false;
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
        var command = IsCommandMode;
        // SessionActive deliberately stays set until the finally below: Esc has
        // to keep cancelling while the model is still working.
        SetState(SessionState.Processing, command ? "Thinking…" : "");
        SoundCues.Stop();
        var released = System.Diagnostics.Stopwatch.StartNew();

        // Written on every exit path, including the exception one — the paths
        // that most need counting are the ones ad-hoc logging always misses.
        using var attempt = new DictationAttempt { Command = command };

        string? wavPath = null;
        var live = _liveStarted;
        _liveStarted = false;
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
            // Only now: the mic stays open for the release tail, and music
            // faded back in during it would end up in the take.
            RestoreMedia();
            if (take == null || take.Value.DurationSeconds < MinimumDurationSeconds)
            {
                if (live) LiveTranscriber.Instance.Cancel();
                attempt.Outcome = AttemptOutcome.TooShort;
                attempt.AudioSeconds = take?.DurationSeconds ?? 0;
                AudioRecorder.TryDelete(take?.Path);
                EndAs(SessionState.Idle);
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

            // The focused field was read when the key went down; by now it is
            // long since ready. Its names join the decoder prompt so a name on
            // screen is spelled the way the screen spells it.
            var focus = await ResolveContextAsync(attempt);
            var vocabulary = MergeVocabulary(dictionary.Spellings, focus.Terms);

            var decodeWatch = System.Diagnostics.Stopwatch.StartNew();
            Transcription? liveResult = null;
            if (live)
            {
                liveResult = await LiveTranscriber.Instance.FinishAsync(cts.Token);
                attempt.LiveChunks = LiveTranscriber.Instance.ChunksDecoded;
            }
            var result = liveResult ?? await Transcriber.Instance.TranscribeAsync(
                wavPath, settings.Language, vocabulary, cts.Token,
                spokenForms: dictionary.SpokenForms);
            // whisper large-v3 occasionally drops into an unpunctuated mode for a
            // whole take (seen 3/3 on one 67 s test take). One retry primed with
            // punctuated text fixes it; it only costs time when it happens.
            if (liveResult == null && Transcriber.LooksUnpunctuated(result.Text))
            {
                var primed = await Transcriber.Instance.TranscribeAsync(wavPath, settings.Language, vocabulary, cts.Token,
                    spokenForms: dictionary.SpokenForms, previousText: Transcriber.PunctuationPrimer);
                if (!primed.IsEmpty && !Transcriber.LooksUnpunctuated(primed.Text)) result = primed;
            }
            decodeWatch.Stop();

            attempt.DecodeMs = decodeWatch.ElapsedMilliseconds;
            attempt.Verdict = result.Verdict;
            attempt.Metrics = result.Metrics;
            attempt.Language = result.Language;
            attempt.RawLength = result.Text.Length;
            attempt.ModelId = Transcriber.Instance.CurrentModelId;
            attempt.Runtime = Transcriber.Instance.RuntimeLabel;
            attempt.Accurate = settings.AccurateDecoding;

            if (result.IsEmpty)
            {
                ReportNothing(attempt, result.Verdict);
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

            var appName = focus.ProcessName.Length > 0 ? focus.ProcessName : TextInjector.ForegroundAppName();
            var kind = AppCategory.Classify(appName);
            var style = WritingStyles.For(kind);
            var writing = new WritingContext(
                AppName: FriendlyAppName(appName, focus.WindowTitle),
                Kind: kind,
                Style: style,
                Language: result.Language ?? "en",
                Vocabulary: vocabulary,
                TextBefore: focus.Caret.Known ? focus.Caret.Before : "");

            if (command)
            {
                await RunCommandAsync(result.Text, focus, writing, attempt, take.Value, released, cts.Token);
                return;
            }

            var produced = await ProduceTextAsync(result, writing, focus, attempt, cts.Token);
            var text = produced.Text;
            attempt.FinalLength = text.Length;
            if (string.IsNullOrWhiteSpace(text) && produced.PressEnter && settings.AutoInsert)
            {
                // "Press enter" on its own: just send what is already typed.
                TextInjector.RefocusIfStolen(_targetWindow);
                await TextInjector.PressEnterAsync(cts.Token);
                attempt.Outcome = AttemptOutcome.Inserted;
                attempt.LatencyMs = released.ElapsedMilliseconds;
                Flash?.Invoke("✓ Enter", false);
                EndAs(SessionState.Idle);
                return;
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                ReportNothing(attempt, result.Verdict);
                return;
            }

            var words = TextFormatter.CountWords(text);
            attempt.AppName = appName;
            attempt.WordCount = words;

            // Last chance to honour Esc. Past this point the text is on its way
            // into someone else's document and cancelling would be a lie.
            cts.Token.ThrowIfCancellationRequested();

            // Re-read the caret just before inserting (2-3 ms warm): the user may
            // have clicked elsewhere in the field while talking, and fresh
            // anchors also make the edit learner's later re-read reliable.
            if (focus.Caret.Known && focus.Element != null)
            {
                var fresh = await FocusContext.ReadCaretAsync(focus, TimeSpan.FromMilliseconds(300));
                if (fresh.Known) focus = focus with { Caret = fresh };
            }
            var outgoing = FitToCaret(text, focus, vocabulary, produced.IsSnippet);
            var blocked = false;
            if (settings.AutoInsert)
            {
                TextInjector.RefocusIfStolen(_targetWindow);
                blocked = !await InsertAsync(outgoing, settings, waitUntilRead: produced.PressEnter);
            }
            if (settings.AutoInsert && !blocked)
            {
                if (produced.PressEnter) await TextInjector.PressEnterAsync(cts.Token);
                attempt.LatencyMs = released.ElapsedMilliseconds;
                Flash?.Invoke(SuccessMessage(words, produced), false);
                attempt.Outcome = AttemptOutcome.Inserted;
                // If the next thing that happens is the user fixing a word,
                // that fix is the best possible dictionary entry.
                CorrectionWatcher.Instance.Arm(outgoing);
                if (focus.Caret.Known && !focus.IsPassword) EditLearner.Instance.Arm(outgoing, focus);
            }
            else if (blocked)
            {
                attempt.LatencyMs = released.ElapsedMilliseconds;
                attempt.Outcome = AttemptOutcome.Copied;
            }
            else
            {
                TextInjector.CopyOnly(outgoing);
                attempt.LatencyMs = released.ElapsedMilliseconds;
                Flash?.Invoke($"Copied — {words} {(words == 1 ? "word" : "words")}", false);
                attempt.Outcome = AttemptOutcome.Copied;
            }

            if (settings.SaveHistory)
            {
                var note = new Note
                {
                    Text = text,
                    RawText = result.Text,
                    DurationSeconds = take.Value.DurationSeconds,
                    WordCount = words,
                    AppName = appName,
                    ModelId = Transcriber.Instance.CurrentModelId,
                    AiPolished = produced.AiPolished,
                    Style = style,
                    Language = result.Language ?? "",
                    DecodeMs = attempt.DecodeMs,
                    LatencyMs = attempt.LatencyMs,
                    // The other version, for History's "Undo AI edit".
                    FormattedText = produced.AiPolished && produced.BeforePolish != text ? produced.BeforePolish : null,
                };
                // Archive first, so the History row appears with its play button
                // already live. Keep() owns the take from here: moved or deleted.
                if (settings.KeepRecentRecordings > 0)
                {
                    RecordingArchive.Instance.Keep(note.Id, wavPath);
                    wavPath = null;
                }
                NotesStore.Instance.Add(note);
            }
            StatsStore.Instance.Record(words, take.Value.DurationSeconds, appName, produced.Report);

            EndAs(SessionState.Idle);
        }
        catch (OperationCanceledException)
        {
            if (live) LiveTranscriber.Instance.Cancel();
            attempt.Outcome = AttemptOutcome.Canceled;
            // Esc, or the decode budget breaking a hang — say which.
            Flash?.Invoke(_userCanceled ? "Canceled" : "That took too long, so it was stopped — try a shorter take", !_userCanceled);
            EndAs(SessionState.Idle);
        }
        catch (TimeoutException)
        {
            if (live) LiveTranscriber.Instance.Cancel();
            attempt.Outcome = AttemptOutcome.DeviceTimeout;
            // The recorder never reported back. Say so rather than sitting in
            // Processing.
            // The recorder never reported back: release its device and file
            // so the next take starts clean.
            AudioRecorder.Instance.Abandon();
            wavPath = null;
            EndAs(SessionState.Error, "The microphone stopped responding");
        }
        catch (Exception ex)
        {
            if (live) LiveTranscriber.Instance.Cancel();
            attempt.Outcome = AttemptOutcome.Failed;
            EndAs(SessionState.Error, ex.Message);
        }
        finally
        {
            if (live) LiveTranscriber.Instance.Cancel();   // no-op after FinishAsync; frees the take otherwise
            RestoreMedia();   // idempotent; covers the paths that never reached the stop
            // Every exit path, including the exception one — otherwise Esc stays
            // swallowed from the foreground app for the rest of the session.
            if (_hotkeys != null) _hotkeys.SessionActive = false;
            IsCommandMode = false;
            AudioRecorder.TryDelete(wavPath);
            lock (_ctsGate)
            {
                _processingCts = null;
                cts.Dispose();
            }
            // Only now, with every piece of this take torn down, may the bar
            // and the hotkey see "Idle": a key press queued behind this one
            // would otherwise start a new take that this cleanup then undoes
            // (unmuting its media, cancelling its live chunker).
            var (endState, endDetail) = _pendingEnd ?? (SessionState.Idle, "");
            _pendingEnd = null;
            _userCanceled = false;
            SetState(endState, endDetail);
            if (endState == SessionState.Error) ResetAfterDelay();
        }
    }

    /// <summary>The state to publish when the current take's cleanup has finished.</summary>
    private (SessionState State, string Detail)? _pendingEnd;

    /// <summary>Esc was pressed during processing (as opposed to the decode budget firing).</summary>
    private bool _userCanceled;

    private void EndAs(SessionState state, string detail = "") => _pendingEnd = (state, detail);

    private void ReportNothing(DictationAttempt attempt, TakeVerdict verdict)
    {
        attempt.Outcome = verdict switch
        {
            TakeVerdict.NoInput => AttemptOutcome.NoInput,
            TakeVerdict.NoSpeech => AttemptOutcome.NoSpeech,
            _ => AttemptOutcome.NothingToType,
        };
        // Saying *why* nothing arrived is the difference between "try
        // again" and "your microphone is muted".
        Flash?.Invoke(verdict switch
        {
            TakeVerdict.NoInput => "No sound from the microphone — check it's not muted",
            TakeVerdict.NoSpeech => "Didn't hear any speech",
            _ => "Didn't catch anything",
        }, verdict == TakeVerdict.NoInput);
        EndAs(SessionState.Idle);
    }

    private async Task<FocusSnapshot> ResolveContextAsync(DictationAttempt attempt)
    {
        var task = _contextTask;
        _contextTask = null;
        if (task == null) return FocusSnapshot.Unknown;
        try
        {
            var snapshot = await task.WaitAsync(ContextTimeout);
            attempt.ContextKnown = snapshot.Caret.Known;
            attempt.ContextMs = snapshot.ElapsedMs;
            return snapshot;
        }
        catch
        {
            return FocusSnapshot.Unknown;
        }
    }

    /// <summary>Dictionary spellings first (they are the user's word), then names seen on screen.</summary>
    private static IReadOnlyList<string> MergeVocabulary(IReadOnlyList<string> dictionary, IReadOnlyList<string> screen)
    {
        if (screen.Count == 0) return dictionary;
        var seen = new HashSet<string>(dictionary, StringComparer.OrdinalIgnoreCase);
        var merged = dictionary.ToList();
        // @handles and #channels are left out of the decoder prompt: they would
        // push whisper to write a literal "@devon" for a spoken name.
        foreach (var term in screen.Where(t => t.Length > 0 && t[0] is not ('@' or '#')))
        {
            if (seen.Add(term)) merged.Add(term);
        }
        return merged;
    }

    private static string FriendlyAppName(string processName, string windowTitle)
    {
        if (string.IsNullOrWhiteSpace(processName)) return "";
        return processName.ToLowerInvariant() switch
        {
            "chrome" or "msedge" or "firefox" or "brave" or "opera" or "vivaldi" or "arc"
                when !string.IsNullOrWhiteSpace(windowTitle) => $"{windowTitle} (browser)",
            "winword" => "Microsoft Word",
            "olk" or "outlook" => "Outlook",
            "ms-teams" or "teams" => "Microsoft Teams",
            _ => processName,
        };
    }

    // ----- Producing the text -----

    /// <param name="BeforePolish">The rule-formatted text the AI started from, so History can undo the AI edit.</param>
    private readonly record struct Produced(
        string Text, FormatterReport Report, bool AiPolished, bool IsSnippet, bool PressEnter, string AiNote,
        string BeforePolish = "");

    /// <summary>"…and that's it, press enter" — the last two words are a key, not text.</summary>
    internal static readonly Regex PressEnterTail = new(
        // Only a comma or spaces before it go with the command: the full stop
        // ending the sentence it follows belongs to the text.
        @"(?:\s*,)?\s*\bpress\s+(?:enter|return)\b[\s.!?]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private async Task<Produced> ProduceTextAsync(Transcription result, WritingContext writing,
        FocusSnapshot focus, DictationAttempt attempt, CancellationToken ct)
    {
        var settings = SettingsStore.Instance.Settings;

        // A snippet said on its own is a shortcut, not prose: no formatting,
        // no full stop, no capital-letter fixups.
        var snippet = DictionaryStore.Instance.WholeUtteranceSnippet(result.Text);
        if (snippet != null)
        {
            attempt.Snippet = true;
            return new Produced(snippet, new FormatterReport(), false, true, false, "", snippet);
        }

        // Formatting is an improvement on the transcript, never a precondition
        // for it: a bad regex — including one in the user's own dictionary —
        // must not turn words the model got right into a red error pill.
        string text;
        FormatterReport formatting = new();
        try
        {
            var model = ModelCatalog.ById(Transcriber.Instance.CurrentModelId);
            text = TextFormatter.Apply(
                result.Text,
                FormatterOptions.FromSettings(
                    writing.Vocabulary, result.Language, model?.EnglishOnly == true,
                    DictionaryStore.Instance.Aliases),
                DictionaryStore.Instance.Apply, out formatting);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            text = result.Text.Trim();
        }
        ct.ThrowIfCancellationRequested();

        var pressEnter = false;
        if (settings.PressEnterCommand && PressEnterTail.IsMatch(text))
        {
            text = PressEnterTail.Replace(text, "").TrimEnd();
            pressEnter = true;
        }

        // On-device polish: only when a model is loaded, only when asked, and
        // only kept if the guard agrees it is a cleanup of what was said.
        var aiPolished = false;
        var aiNote = "off";
        var beforePolish = text;
        var mode = settings.AiPolish;
        if (mode != "off" && LocalLlm.Instance.IsReady && !string.IsNullOrWhiteSpace(text)
            && !TextFormatter.HasStructuralGuard(result.Text))
        {
            if (mode == "always" || AiWriter.NeedsPolish(result.Text, out _))
            {
                SetState(SessionState.Processing, "Polishing…");
                var polish = await AiWriter.PolishAsync(text, writing, ct);
                attempt.AiMs = polish.Milliseconds;
                aiNote = polish.Reason;
                if (polish.Applied)
                {
                    text = polish.Text;
                    aiPolished = true;
                    formatting.AiPolishes++;
                }
            }
            else
            {
                aiNote = "skipped";
            }
        }
        attempt.Ai = aiNote;

        // The mechanical half of the style always applies — with or without AI.
        text = WritingStyles.Apply(text, writing.Style, writing.Vocabulary?.ToList());
        beforePolish = WritingStyles.Apply(beforePolish, writing.Style, writing.Vocabulary?.ToList());
        return new Produced(text, formatting, aiPolished, false, pressEnter, aiNote, beforePolish);
    }

    /// <summary>
    /// Fit the text to where it lands. With the field readable this is the
    /// context-aware pass (casing, spacing, punctuation against the caret);
    /// without it, the old rule of one space after a finished sentence.
    /// </summary>
    private static string FitToCaret(string text, FocusSnapshot focus, IReadOnlyList<string> vocabulary, bool snippet)
    {
        var settings = SettingsStore.Instance.Settings;
        if (focus.Caret.Known && !focus.IsPassword && !snippet)
        {
            var names = new HashSet<string>(vocabulary.SelectMany(v => v.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
                StringComparer.Ordinal);
            foreach (var t in focus.Terms)
            {
                foreach (var w in t.Split(' ', StringSplitOptions.RemoveEmptyEntries)) names.Add(w.TrimStart('@', '#'));
            }
            return ContextAwareInsertion.Fit(text, focus.Caret, w => names.Contains(w), settings.AppendTrailingSpace).Text;
        }
        return settings.AppendTrailingSpace && !snippet ? TextFormatter.WithSentenceSpace(text) : text;
    }

    /// <summary>
    /// Put text into the focused app. Returns false when the app cannot be
    /// typed into at all — it runs as administrator and FlowType does not, so
    /// Windows would silently drop the paste — in which case the text goes on
    /// the clipboard and the bar says why, instead of the dictation vanishing.
    /// </summary>
    private async Task<bool> InsertAsync(string text, AppSettings settings, bool waitUntilRead = false)
    {
        var target = NativeMethods.GetForegroundWindow();
        if (NativeMethods.IsElevatedBeyondUs(target))
        {
            TextInjector.CopyOnly(text);
            Flash?.Invoke("That app runs as administrator, so Windows blocks typing into it — copied instead (Ctrl+V)", true);
            return false;
        }

        if (settings.InsertMethod == "type")
        {
            await TextInjector.TypeAsync(text);
        }
        else if (TextInjector.IsCodingAgentTerminal(target)
                 && (text.Length > 600 || text.Count(c => c == '\n') >= 3))
        {
            await TextInjector.PasteChunkedAsync(text, settings.RestoreClipboard);
        }
        else
        {
            await TextInjector.PasteAsync(text, settings.RestoreClipboard, waitUntilRead);
        }
        return true;
    }

    private static string SuccessMessage(int words, Produced produced)
    {
        var count = $"{words} {(words == 1 ? "word" : "words")}";
        if (produced.IsSnippet) return "✓ Snippet";
        if (produced.PressEnter) return $"✓ {count} · sent";
        return produced.AiPolished ? $"✓ {count} · polished" : $"✓ {count}";
    }

    // ----- Command Mode -----

    /// <summary>
    /// The take was an instruction. With text selected in the target field it
    /// rewrites the selection; otherwise it writes at the caret. Everything
    /// happens on this PC — Wispr's equivalent goes to cloud models.
    /// </summary>
    private async Task RunCommandAsync(string instruction, FocusSnapshot focus, WritingContext writing,
        DictationAttempt attempt, RecordedTake take, System.Diagnostics.Stopwatch released, CancellationToken ct)
    {
        // Never rewrite text the model did not fully see: the paste would
        // replace the whole selection with a rewrite of only its first part.
        if (focus.SelectionTruncated)
        {
            attempt.Outcome = AttemptOutcome.NothingToType;
            attempt.Ai = "command: selection too long";
            Flash?.Invoke($"That selection is too long for Command Mode (max {FocusContext.MaxSelectionChars:N0} characters)", true);
            EndAs(SessionState.Idle);
            return;
        }

        string? selection = focus.Caret.Known ? focus.Caret.Selection : null;
        if (!focus.Caret.Known && !focus.IsPassword)
        {
            // UI Automation could not see the field (many Electron/Chromium
            // fields hide it): ask the app for its selection — but only in an
            // edit control, never a terminal, where Ctrl+C interrupts.
            TextInjector.RefocusIfStolen(_targetWindow);
            var probe = await TextInjector.GetSelectedTextViaCopyAsync(focus.ControlType);
            selection = probe.Result == TextInjector.CopyProbe.Selection ? probe.Text : null;
            if (selection != null && selection.Length > FocusContext.MaxSelectionChars)
            {
                attempt.Outcome = AttemptOutcome.NothingToType;
                attempt.Ai = "command: selection too long";
                Flash?.Invoke($"That selection is too long for Command Mode (max {FocusContext.MaxSelectionChars:N0} characters)", true);
                EndAs(SessionState.Idle);
                return;
            }
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var command = await AiWriter.CommandAsync(instruction, selection, writing, ct);
        ct.ThrowIfCancellationRequested();
        attempt.AiMs = watch.ElapsedMilliseconds;
        attempt.Ai = command.Note;
        if (!command.Succeeded)
        {
            // Timed out / too long / empty / no model / llama error: nothing is
            // inserted — a cut-off answer must never replace a selection.
            attempt.Outcome = command.Status == CommandStatus.Empty
                ? AttemptOutcome.NothingToType
                : AttemptOutcome.Failed;
            Flash?.Invoke(command.Failure ?? "The on-device AI failed", true);
            EndAs(SessionState.Idle);
            return;
        }
        var output = command.Text!;

        var settings = SettingsStore.Instance.Settings;
        TextInjector.RefocusIfStolen(_targetWindow);
        if (settings.AutoInsert)
        {
            attempt.Outcome = await InsertAsync(output, settings)
                ? AttemptOutcome.Inserted
                : AttemptOutcome.Copied;
        }
        else
        {
            TextInjector.CopyOnly(output);
            attempt.Outcome = AttemptOutcome.Copied;
        }
        attempt.LatencyMs = released.ElapsedMilliseconds;
        attempt.WordCount = TextFormatter.CountWords(output);
        attempt.FinalLength = output.Length;
        if (attempt.Outcome == AttemptOutcome.Inserted)
        {
            Flash?.Invoke(string.IsNullOrEmpty(selection) ? "✓ Written by on-device AI" : "✓ Selection rewritten", false);
        }
        else if (!settings.AutoInsert)
        {
            Flash?.Invoke("Copied — press Ctrl+V", false);
        }
        // else: InsertAsync already explained why it copied instead.

        if (settings.SaveHistory)
        {
            NotesStore.Instance.Add(new Note
            {
                Text = output,
                RawText = "⌘ " + instruction,
                DurationSeconds = take.DurationSeconds,
                WordCount = attempt.WordCount,
                AppName = focus.ProcessName,
                ModelId = LocalLlm.Instance.CurrentModelId,
                AiPolished = true,
                Style = "command",
                LatencyMs = attempt.LatencyMs,
            });
        }
        EndAs(SessionState.Idle);
    }

    // ----- Re-use of past dictations -----

    public Task PasteLastAsync()
    {
        var last = NotesStore.Instance.Last;
        if (last == null)
        {
            Flash?.Invoke("Nothing dictated yet", false);
            return Task.CompletedTask;
        }
        var text = SettingsStore.Instance.Settings.AppendTrailingSpace
            ? TextFormatter.WithSentenceSpace(last.Text)
            : last.Text;
        return TextInjector.PasteAsync(text, SettingsStore.Instance.Settings.RestoreClipboard);
    }

    public void CopyLast()
    {
        var last = NotesStore.Instance.Last;
        if (last == null)
        {
            Flash?.Invoke("Nothing dictated yet", false);
            return;
        }
        TextInjector.CopyOnly(last.Text);
        Flash?.Invoke("Copied your last dictation", false);
    }

    /// <summary>
    /// Transcribe a kept recording again with whatever model is loaded now and
    /// replace the note's text. Formatting and the note's style apply; screen
    /// context does not (it belonged to a moment that has passed).
    /// </summary>
    private CancellationTokenSource? _retranscribeCts;

    /// <summary>A new dictation takes the decoder from a History re-transcription.</summary>
    private void CancelRetranscription()
    {
        try { _retranscribeCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    public async Task<string?> RetranscribeAsync(Note note)
    {
        var path = RecordingArchive.Instance.PathFor(note.Id);
        if (path == null || !Transcriber.Instance.IsReady) return null;

        var settings = SettingsStore.Instance.Settings;
        var dictionary = DictionaryStore.Instance.VocabularySections;
        using var cts = new CancellationTokenSource();
        _retranscribeCts = cts;
        Transcription result;
        try
        {
            result = await Transcriber.Instance.TranscribeAsync(
                path, settings.Language, dictionary.Spellings, cts.Token,
                spokenForms: dictionary.SpokenForms);
        }
        catch (OperationCanceledException)
        {
            return null;   // a live dictation started; the note keeps its text
        }
        finally
        {
            if (ReferenceEquals(_retranscribeCts, cts)) _retranscribeCts = null;
        }
        if (result.IsEmpty) return null;

        var model = ModelCatalog.ById(Transcriber.Instance.CurrentModelId);
        var text = TextFormatter.Apply(result.Text,
            FormatterOptions.FromSettings(dictionary.Spellings, result.Language, model?.EnglishOnly == true,
                DictionaryStore.Instance.Aliases),
            DictionaryStore.Instance.Apply);
        if (!string.IsNullOrWhiteSpace(note.Style) && note.Style != "command")
        {
            text = WritingStyles.Apply(text, note.Style, dictionary.Spellings.ToList());
        }
        if (string.IsNullOrWhiteSpace(text)) return null;

        NotesStore.Instance.Update(note.Id, n =>
        {
            n.Text = text;
            n.RawText = result.Text;
            n.WordCount = TextFormatter.CountWords(text);
            n.ModelId = Transcriber.Instance.CurrentModelId;
            n.Retranscribed = true;
            n.AiPolished = false;
            n.FormattedText = null;   // the old AI version belongs to the old transcript
        });
        return text;
    }

    private void SetState(SessionState state, string detail)
    {
        State = state;
        if (_hotkeys != null) _hotkeys.RecordingActive = state == SessionState.Recording;
        StateChanged?.Invoke(state, detail);
    }

    private async void ResetAfterDelay()
    {
        await Task.Delay(3000);
        if (State == SessionState.Error) SetState(SessionState.Idle, "");
    }
}
