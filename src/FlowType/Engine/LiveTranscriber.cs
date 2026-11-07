using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using FlowType.Audio;
using FlowType.Core;

namespace FlowType.Engine;

/// <summary>
/// Transcribes a long dictation while it is still being spoken, so that when
/// the hotkey is released only the last few seconds are left to decode.
///
/// <para><b>Why.</b> Wispr Flow streams audio to its servers during recording
/// and has text back about 0.9 s after release; its own HTTP fallback cuts
/// 28-second chunks and stitches them with the previous chunk's text as
/// context. The user's Wispr history averages about 47 s per dictation. FlowType
/// used to decode the whole WAV only after release, so exactly those long takes
/// were the ones that waited several seconds.</para>
///
/// <para><b>Shape.</b> The recorder hands every captured buffer to
/// <see cref="AudioRecorder.SamplesCaptured"/>. One worker filters and measures
/// the audio and decides where to cut; a second decodes the cut chunks one at a
/// time, in order, through <see cref="Transcriber.TranscribeSamplesAsync"/> —
/// the same decode path as a whole file. Both run off the capture thread, which
/// only ever enqueues an array.</para>
///
/// <para><b>Where to cut.</b> Nothing is cut until 20 s are pending, and never
/// before 15 s, so short and medium dictations never take this path at all and
/// come out byte-identical to before (<see cref="FinishAsync"/> returns null
/// and the caller decodes the file as it always did). From 20 s on, the cut
/// goes in a pause after the 15 s mark: a run of 20 ms windows whose RMS sits
/// well below the take's running speech level (see
/// <see cref="Run.QuietThreshold"/>), at least 600 ms long while there is time
/// to wait for one (<see cref="SentencePause"/>), at least 400 ms once the chunk
/// reaches 28 s. With no pause by 28 s the cut is forced at the quietest
/// 100 ms of the last eight seconds — a breath or a stop consonant, rather
/// than Wispr's blind cut at exactly 28 s that can split a word. 28 s plus
/// padding still fits in one 30-second whisper window, so every chunk is one
/// decoder pass. Cut decisions depend only on the audio (evaluated every
/// 100 ms of it), never on timing, so the same take always cuts the same way
/// (<see cref="PlanChunks"/>).</para>
///
/// <para><b>Conditioning.</b> The goal is that a chunk sounds to the model
/// exactly like the same stretch of the whole-file take would. So:
/// the 80 Hz high-pass runs once over the whole take as a stream (same
/// coefficients and arithmetic as <see cref="AudioConditioner"/>, so every
/// filtered sample is identical to the whole-file one, edges included); gain is
/// <see cref="AudioConditioner"/>'s formula applied to the take's running peak
/// and running speech-modulation test (never the chunk's own), so it can only
/// be equal to or slightly above the whole-file gain, never clips, and is
/// exactly the whole-file gain for the final chunk; trailing-silence trim only
/// applies to the final chunk (the only place the whole-file path trims); and
/// each chunk gets the same lead/tail padding. A chunk that on its own is
/// silence or room tone (a hands-free take left running) is not decoded at all
/// — handing whisper 28 s of nothing is how "Thank you." gets typed.</para>
///
/// <para><b>Joining.</b> Chunk texts are joined with a single space; a
/// <see cref="SmartFormatter.ParagraphMark"/> goes between two chunks when the
/// pause at the cut was at least <see cref="SmartFormatter.ParagraphPause"/> and
/// the earlier chunk ended a sentence — the same rule
/// <see cref="SmartFormatter.JoinSegments"/> applies between segments. The
/// verdict and metrics come from conditioning the whole take, so "no speech"
/// and "microphone muted" still work exactly as on the whole-file path.</para>
///
/// <para><b>Failure.</b> Any error in the live path makes
/// <see cref="FinishAsync"/> return null and the caller falls back to the
/// whole-file decode it would have done anyway: live transcription can make a
/// long take faster, never lose it. Cancellation (Esc) aborts the in-flight
/// chunk decode.</para>
///
/// <para><b>Lock sharing.</b> Chunk decodes take <see cref="Transcriber"/>'s
/// lock like any other decode. Chunks are at most 28 s, so at release the tail
/// waits behind at most one of them.</para>
/// </summary>
public sealed class LiveTranscriber
{
    public static LiveTranscriber Instance { get; } = new();

    // ----- Policy -----

    /// <summary>A chunk is never shorter than this (unless the take ends).</summary>
    internal static readonly TimeSpan MinChunk = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Looking for a cut starts once this much audio is pending. A take shorter
    /// than this is never chunked — the whole-file path handles it unchanged.
    /// </summary>
    internal static readonly TimeSpan SearchAfter = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Forced cut. 28 s + 0.6 s of padding stays inside whisper's 30 s window,
    /// and 28 s is also the chunk length Wispr Flow's own fallback uses.
    /// </summary>
    internal static readonly TimeSpan MaxChunk = TimeSpan.FromSeconds(28);

    /// <summary>Shortest quiet stretch that counts as a pause rather than a gap between words.</summary>
    internal static readonly TimeSpan MinPause = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// A pause this long is very likely a sentence boundary, and is taken as
    /// soon as one is available. A 400–600 ms pause is just as often a comma —
    /// cutting there made whisper end the chunk with a full stop and start the
    /// next with a capital ("…interviews scheduled. And I expect…", measured on
    /// a rushed 58 s take) — so those are only used once the chunk is at its
    /// 28 s limit and nothing better came along. On the GPU a chunk's decode
    /// time barely depends on its length (the encoder always runs a full 30 s
    /// window), so waiting for a better seam costs almost no latency.
    /// </summary>
    internal static readonly TimeSpan SentencePause = TimeSpan.FromMilliseconds(600);

    private const int Window = AudioConditioner.WindowSamples;                 // 20 ms
    private const int WindowsPerSecond = AudioConditioner.SampleRate / Window;  // 50
    private static int Windows(TimeSpan t) => (int)Math.Round(t.TotalSeconds * WindowsPerSecond);

    private static readonly int MinChunkWindows = Windows(MinChunk);
    private static readonly int SearchAfterWindows = Windows(SearchAfter);
    private static readonly int MaxChunkWindows = Windows(MaxChunk);
    private static readonly int MinPauseWindows = Windows(MinPause);
    private static readonly int SentencePauseWindows = Windows(SentencePause);
    private static readonly int ParagraphPauseWindows = Windows(SmartFormatter.ParagraphPause);

    /// <summary>
    /// How much of the pause stays on the end of the chunk being cut (300 ms,
    /// the context the whole-file trim keeps). The rest leads the next chunk.
    /// </summary>
    private const int PauseKeptWindows = 15;

    /// <summary>A forced cut looks for the quietest point in this many trailing windows (8 s).</summary>
    private const int ForcedSearchWindows = 8 * WindowsPerSecond;

    /// <summary>Speech and noise levels are measured over the last 60 s of the take.</summary>
    private const int LevelHistoryWindows = 60 * WindowsPerSecond;

    /// <summary>Cut decisions are re-evaluated every 100 ms of audio.</summary>
    private const int EvaluateEveryWindows = 5;

    /// <summary>Recorder buffer size the simulator feeds (50 ms, as captured).</summary>
    private const int SimulatedBufferSamples = AudioConditioner.SampleRate / 20;

    // Mirrors of AudioConditioner's private constants — kept identical so a
    // chunk is conditioned the way the whole file would be. The simulator
    // reports the final chunk's gain beside the whole-file one, which is where
    // a drift between the two copies would show.
    private const float TargetPeak = 0.9f;
    private const float StandardMaxGain = 16f;
    private const float SpeechMaxGain = 32f;
    private const float TrailingSilenceRms = AudioConditioner.VoiceRmsFloor * 0.25f;
    private const int MinTrailingSilenceWindows = 35;
    private const int RetainTrailingWindows = 15;
    private const int LeadInSamples = (int)(0.15 * AudioConditioner.SampleRate);
    private const int TailSamples = (int)(0.45 * AudioConditioner.SampleRate);
    private const int MinimumSamples = (int)(1.25 * AudioConditioner.SampleRate);

    /// <summary>Mirrors SmartFormatter.JoinSegments: no paragraphs in a short note.</summary>
    private const int MinWordsForParagraphs = 25;

    private static readonly Regex LeadingDash = new(@"^[-–—]\s+", RegexOptions.Compiled);

    private readonly object _gate = new();
    private Run? _current;
    private bool _subscribed;
    private int _lastChunksDecoded;

    /// <summary>
    /// Chunks decoded so far in the current take, or in the last finished one
    /// (for the attempt log). Zero when the take was short enough to go
    /// whole-file.
    /// </summary>
    public int ChunksDecoded => Volatile.Read(ref _current)?.Decoded ?? Volatile.Read(ref _lastChunksDecoded);

    private LiveTranscriber() { }

    /// <summary>
    /// Start following a new take. Call immediately <b>before</b>
    /// <see cref="AudioRecorder.StartRecording"/> so the first buffer is not
    /// missed. Does nothing (and <see cref="FinishAsync"/> then returns null)
    /// when live transcription is switched off or no model is loaded. The
    /// vocabulary is snapshotted: dictionary edits mid-take apply next time.
    /// </summary>
    public void Begin(string language, IReadOnlyList<string> vocabulary, IReadOnlyList<string>? spokenForms)
    {
        Run? previous;
        lock (_gate)
        {
            previous = _current;
            _current = null;
            _lastChunksDecoded = 0;
            if (SettingsStore.Instance.Settings.LiveTranscription && Transcriber.Instance.IsReady)
            {
                if (!_subscribed)
                {
                    AudioRecorder.Instance.SamplesCaptured += OnSamplesCaptured;
                    _subscribed = true;
                }
                var run = new Run(language, vocabulary.ToArray(), spokenForms?.ToArray(), accurate: null);
                run.Start();
                Volatile.Write(ref _current, run);
            }
        }
        previous?.Abort();
    }

    /// <summary>
    /// The take has ended: decode what is left and return the whole text, or
    /// null when the caller should transcribe the WAV itself — the take was too
    /// short to have been chunked (under ~20 s), live transcription was off, or
    /// anything in the live path failed. Call after
    /// <see cref="AudioRecorder.StopAsync"/> has returned, so every buffer has
    /// been delivered. Throws <see cref="OperationCanceledException"/> when
    /// <paramref name="ct"/> fires.
    /// </summary>
    public async Task<Transcription?> FinishAsync(CancellationToken ct)
    {
        Run? run;
        lock (_gate)
        {
            run = _current;
            Volatile.Write(ref _current, null);
        }
        if (run == null) return null;
        try
        {
            return await run.FinishAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _lastChunksDecoded, run.Decoded);
        }
    }

    /// <summary>
    /// Replace the decoder vocabulary for the rest of the current take — for
    /// terms that only become known after <see cref="Begin"/>, such as names
    /// read from the focused window. Chunks already decoded keep what they
    /// had; the next chunk (and the tail) use the new lists. No-op when no take
    /// is live. Any thread.
    /// </summary>
    public void UpdateVocabulary(IReadOnlyList<string> vocabulary, IReadOnlyList<string>? spokenForms)
    {
        Volatile.Read(ref _current)?.SetVocabulary(vocabulary.ToArray(), spokenForms?.ToArray());
    }

    /// <summary>
    /// Abandon the current take (Esc, shortcut-cancel, mic failure, or a
    /// <c>finally</c> after <see cref="FinishAsync"/>). Safe to call any time;
    /// with no live take it does nothing.
    /// </summary>
    public void Cancel()
    {
        Run? run;
        lock (_gate)
        {
            run = _current;
            if (run == null) return;
            Volatile.Write(ref _current, null);
            _lastChunksDecoded = 0;
        }
        run.Abort();
    }

    /// <summary>Capture thread: enqueue and return. Must never throw.</summary>
    private void OnSamplesCaptured(short[] pcm)
    {
        try
        {
            Volatile.Read(ref _current)?.Post(pcm);
        }
        catch
        {
            // A lost buffer costs the live path accuracy at worst; the WAV —
            // and so the whole-file fallback — still has it.
        }
    }

    // ----- Simulation (diagnostics, --livecheck) -----

    /// <summary>
    /// Replay a 16 kHz mono WAV through the live path as if it were being
    /// dictated, using the loaded model, the user's language and decoding
    /// settings and no dictionary (like --bench). Chunks are decoded as they
    /// would be during recording; <c>Ms</c> is then the time from "release" to
    /// text — the tail decode plus the join — assuming the decoder kept up with
    /// real time. Chunks is 0 (and Text empty) when the take is too short to
    /// be chunked. Independent of <see cref="Instance"/>'s live take.
    /// </summary>
    public static async Task<(int Chunks, string Text, long Ms)> SimulateAsync(
        string wavPath, CancellationToken ct = default)
    {
        var report = await SimulateDetailedAsync(
            wavPath, SettingsStore.Instance.Settings.Language,
            Array.Empty<string>(), null, accurate: null, ct).ConfigureAwait(false);
        return (report.Result == null ? 0 : report.Chunks.Count,
            report.Result?.Text ?? "", report.ReleaseToTextMs);
    }

    /// <summary>One chunk's story, for the simulator report.</summary>
    internal sealed record ChunkStat(
        int Index, double StartSeconds, double EndSeconds, double PauseAfterSeconds,
        bool Forced, bool Silent, bool Final, float Gain, long DecodeMs, string Text, bool Retried = false);

    internal sealed record LiveReport(
        Transcription? Result, IReadOnlyList<ChunkStat> Chunks, long ReleaseToTextMs,
        float FinalChunkGain, float WholeFileGain, float FilterMaxDelta);

    /// <summary>
    /// <see cref="SimulateAsync"/> with every input explicit and the per-chunk
    /// detail kept. Also checks the claim the conditioning rests on: the
    /// streamed high-pass, at the final gain, must reproduce
    /// <see cref="AudioConditioner.Prepare"/>'s samples
    /// (<see cref="LiveReport.FilterMaxDelta"/> ≈ 0).
    /// </summary>
    internal static async Task<LiveReport> SimulateDetailedAsync(
        string wavPath, string language, IReadOnlyList<string> vocabulary,
        IReadOnlyList<string>? spokenForms, bool? accurate, CancellationToken ct)
    {
        var samples = await Task.Run(() => Transcriber.ReadSamples(wavPath), ct).ConfigureAwait(false);
        return await SimulateDetailedAsync(samples, language, vocabulary, spokenForms, accurate, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The same replay over 16 kHz mono samples already in memory (−1..1) —
    /// for audio that never was a WAV file on disk, such as a recording read
    /// out of another app's history for the personal benchmark
    /// (<c>Learning/PersonalBench</c>). The samples are not modified. The
    /// capture buffers are cut on the calling thread before the first await,
    /// which is a few milliseconds even for a long take; a caller on the UI
    /// thread should still call this from a worker.
    /// </summary>
    internal static async Task<LiveReport> SimulateDetailedAsync(
        float[] samples, string language, IReadOnlyList<string> vocabulary,
        IReadOnlyList<string>? spokenForms, bool? accurate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var run = new Run(language, vocabulary.ToArray(), spokenForms?.ToArray(), accurate);
        run.Start();
        try
        {
            foreach (var buffer in AsCaptureBuffers(samples)) run.Post(buffer);

            // Everything cut during "recording" is decoded before the clock
            // starts — that is what happens live as long as decoding outpaces
            // speech, which the per-chunk DecodeMs shows.
            await run.WaitUntilIdleAsync(ct).ConfigureAwait(false);
            var sw = Stopwatch.StartNew();
            var result = await run.FinishAsync(ct).ConfigureAwait(false);
            sw.Stop();

            var whole = AudioConditioner.Prepare(samples);
            var delta = FilterDelta(samples, whole);
            return new LiveReport(result, run.Stats, sw.ElapsedMilliseconds,
                run.FinalGain, whole.Metrics.AppliedGain, delta);
        }
        finally
        {
            run.Abort();
        }
    }

    /// <summary>
    /// Where the live path would cut this audio (16 kHz floats), without
    /// decoding anything. Deterministic and fast — the cut policy's test hook.
    /// Empty when the take would go whole-file. DecodeMs and Text are unset.
    /// </summary>
    internal static IReadOnlyList<ChunkStat> PlanChunks(float[] samples)
    {
        var run = new Run("en", Array.Empty<string>(), null, accurate: null, planOnly: true);
        return run.Plan(samples);
    }

    /// <summary>
    /// Back to the 50 ms PCM16 buffers the recorder would have delivered.
    /// WAV samples were read as s / 32768f, so this round trip is exact.
    /// </summary>
    private static IEnumerable<short[]> AsCaptureBuffers(float[] samples)
    {
        for (var offset = 0; offset < samples.Length; offset += SimulatedBufferSamples)
        {
            var length = Math.Min(SimulatedBufferSamples, samples.Length - offset);
            var buffer = new short[length];
            for (var i = 0; i < length; i++)
            {
                buffer[i] = (short)Math.Clamp(MathF.Round(samples[offset + i] * 32768f), short.MinValue, short.MaxValue);
            }
            yield return buffer;
        }
    }

    /// <summary>
    /// Largest difference between the streamed high-pass (times the whole-file
    /// gain) and the samples <see cref="AudioConditioner.Prepare"/> produced.
    /// </summary>
    private static float FilterDelta(float[] raw, ConditionedTake whole)
    {
        if (whole.Samples.Length == 0) return 0;
        var filter = new HighPassFilter();
        var gain = whole.Metrics.AppliedGain;
        // Past the trim point Prepare's output is padding, not signal; TrimmedMs
        // is rounded down, so stay one millisecond clear of it.
        var trimmed = (whole.Metrics.TrimmedMs + 1) * AudioConditioner.SampleRate / 1000;
        var count = Math.Min(raw.Length - trimmed, whole.Samples.Length - LeadInSamples - TailSamples);
        float worst = 0;
        for (var i = 0; i < count; i++)
        {
            var y = filter.Process(raw[i]);
            var expected = gain == 1f ? y : Math.Clamp(y * gain, -1f, 1f);
            var d = Math.Abs(expected - whole.Samples[LeadInSamples + i]);
            if (d > worst) worst = d;
        }
        return worst;
    }

    // ----- Shared DSP -----

    /// <summary>
    /// <see cref="AudioConditioner"/>'s 80 Hz second-order Butterworth high-pass
    /// (RBJ cookbook), as a stream: same coefficients, same double-precision
    /// arithmetic in the same order, state carried across buffers — so filtering
    /// the take in pieces produces exactly the samples filtering it whole does.
    /// </summary>
    private struct HighPassFilter
    {
        private static readonly double B0, B1, B2, A1, A2;

        static HighPassFilter()
        {
            const double cutoffHz = 80;
            const double q = 0.7071;
            var w0 = 2 * Math.PI * cutoffHz / AudioConditioner.SampleRate;
            var alpha = Math.Sin(w0) / (2 * q);
            var cos = Math.Cos(w0);
            var a0 = 1 + alpha;
            B0 = (1 + cos) / 2 / a0;
            B1 = -(1 + cos) / a0;
            B2 = (1 + cos) / 2 / a0;
            A1 = -2 * cos / a0;
            A2 = (1 - alpha) / a0;
        }

        private double _x1, _x2, _y1, _y2;

        public float Process(float sample)
        {
            double xi = sample;
            var y = B0 * xi + B1 * _x1 + B2 * _x2 - A1 * _y1 - A2 * _y2;
            _x2 = _x1; _x1 = xi;
            _y2 = _y1; _y1 = y;
            return (float)y;
        }
    }

    /// <summary>Mirrors AudioConditioner's private rule: 300 ms, or half a short take.</summary>
    private static int RequiredVoicedWindows(int windowCount) =>
        Math.Min(AudioConditioner.SustainedVoiceWindows, Math.Max(3, windowCount / 2));

    private static bool HasSustainedVoice(IReadOnlyList<float> windows, int required, float gain = 1f)
    {
        var run = 0;
        foreach (var w in windows)
        {
            if (w * gain >= AudioConditioner.VoiceRmsFloor)
            {
                if (++run >= required) return true;
            }
            else run = 0;
        }
        return false;
    }

    // ----- One take -----

    private sealed record Vocabulary(IReadOnlyList<string> Spellings, IReadOnlyList<string>? Spoken);

    private sealed record Chunk(
        int Index, float[] Samples, bool Silent, bool Final, bool Forced,
        double StartSeconds, double EndSeconds, double PauseAfterSeconds, float Gain);

    /// <summary>
    /// Everything about one take. The capture thread only calls
    /// <see cref="Post"/>; <see cref="AnalyzeAsync"/> owns the audio state and
    /// <see cref="DecodeAsync"/> owns the text state, so neither needs a lock.
    /// </summary>
    private sealed class Run
    {
        private readonly string _language;
        // Swapped whole by SetVocabulary (any thread); read once per chunk decode.
        private volatile Vocabulary _vocabulary;
        private readonly bool? _accurate;
        private readonly CancellationTokenSource _cts = new();
        private readonly Channel<short[]> _audio =
            Channel.CreateUnbounded<short[]>(new UnboundedChannelOptions { SingleReader = true });
        private readonly Channel<Chunk> _chunks =
            Channel.CreateUnbounded<Chunk>(new UnboundedChannelOptions { SingleReader = true });
        private Task _analysis = Task.CompletedTask;
        private Task _decoder = Task.CompletedTask;

        // Analysis worker state.
        private readonly List<short[]> _raw = new();
        private long _rawCount;
        private HighPassFilter _filter;
        private readonly List<float> _pending = new(MaxChunkWindows * Window + 4 * Window);
        private long _pendingStart;                     // absolute sample index of _pending[0]
        private readonly List<float> _windows = new();  // whole take, filtered, pre-gain
        private double _windowSum;
        private int _windowFill;
        private float _peak;
        private int _cuts;
        private volatile bool _cutEver;

        // Decoder worker state.
        private readonly List<(string Text, double PauseAfter, bool Empty, bool Unpunctuated)> _texts = new();
        private readonly List<ChunkStat> _stats = new();
        private string? _lockedLanguage;
        private string? _detected;

        private volatile bool _failed;
        private long _posted, _analyzed;
        private int _enqueued, _decoded;
        private float _finalGain;

        /// <summary>Plan-only mode (<see cref="PlanChunks"/>): chunks are recorded, never decoded.</summary>
        private readonly List<ChunkStat>? _plan;

        public Run(string language, IReadOnlyList<string> vocabulary,
            IReadOnlyList<string>? spokenForms, bool? accurate, bool planOnly = false)
        {
            _language = language;
            _vocabulary = new Vocabulary(vocabulary, spokenForms);
            _accurate = accurate;
            if (planOnly) _plan = new List<ChunkStat>();
        }

        public void SetVocabulary(IReadOnlyList<string> spellings, IReadOnlyList<string>? spokenForms) =>
            _vocabulary = new Vocabulary(spellings, spokenForms);

        /// <summary>Plan-only: run the cut policy over the whole take synchronously.</summary>
        public IReadOnlyList<ChunkStat> Plan(float[] samples)
        {
            foreach (var buffer in AsCaptureBuffers(samples)) Ingest(buffer);
            if (_cuts == 0) return Array.Empty<ChunkStat>();
            CutFinal();
            return _plan!;
        }

        public int Decoded => Volatile.Read(ref _decoded);
        public float FinalGain => _finalGain;

        public IReadOnlyList<ChunkStat> Stats
        {
            get { lock (_stats) return _stats.ToArray(); }
        }

        public void Start()
        {
            _analysis = Task.Run(AnalyzeAsync);
            _decoder = Task.Run(DecodeAsync);
        }

        /// <summary>Capture thread. An unbounded channel never blocks the caller.</summary>
        public void Post(short[] pcm)
        {
            if (_audio.Writer.TryWrite(pcm)) Interlocked.Increment(ref _posted);
        }

        public void Abort()
        {
            try { _cts.Cancel(); } catch { }
            _audio.Writer.TryComplete();
            _chunks.Writer.TryComplete();
        }

        /// <summary>Simulator only: every posted buffer analysed and every cut chunk decoded.</summary>
        public async Task WaitUntilIdleAsync(CancellationToken ct)
        {
            while (Interlocked.Read(ref _analyzed) < Interlocked.Read(ref _posted)
                || Volatile.Read(ref _decoded) < Volatile.Read(ref _enqueued))
            {
                if (_failed || _cts.IsCancellationRequested) return;
                await Task.Delay(10, ct).ConfigureAwait(false);
            }
        }

        public async Task<Transcription?> FinishAsync(CancellationToken ct)
        {
            using var registration = ct.Register(Abort);
            _audio.Writer.TryComplete();
            try
            {
                await _analysis.ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (!_cutEver || _failed)
                {
                    // Never chunked (or broke): the caller decodes the file.
                    Abort();
                    return null;
                }

                // Judge the whole take while the tail decodes: the verdict and
                // metrics must be the whole-file ones, not any one chunk's.
                var judged = Task.Run(() => AudioConditioner.Prepare(AllSamples()));
                await _decoder.ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var whole = await judged.ConfigureAwait(false);
                if (_failed || _cts.IsCancellationRequested) return null;

                if (whole.Verdict is TakeVerdict.NoSpeech or TakeVerdict.NoInput)
                {
                    return new Transcription("", whole.Verdict, whole.Metrics, null);
                }
                var language = _detected ?? (_language == "auto" ? null : _language);
                return new Transcription(Join(), whole.Verdict, whole.Metrics, language);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Aborted from elsewhere (a new take began): not this caller's
                // cancellation, so fall back rather than report "Canceled".
                return null;
            }
        }

        // ----- Analysis worker -----

        private async Task AnalyzeAsync()
        {
            try
            {
                var reader = _audio.Reader;
                while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var buffer))
                    {
                        Ingest(buffer);
                        Interlocked.Increment(ref _analyzed);
                    }
                }
                if (_cuts > 0 && !_cts.IsCancellationRequested) CutFinal();
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                _failed = true;
            }
            finally
            {
                _chunks.Writer.TryComplete();
            }
        }

        private void Ingest(short[] buffer)
        {
            _raw.Add(buffer);
            _rawCount += buffer.Length;
            foreach (var s in buffer)
            {
                // s / 32768f is exactly what NAudio's reader produces for the
                // WAV, so the filter sees the same input the whole-file path does.
                var y = _filter.Process(s / 32768f);
                _pending.Add(y);
                var magnitude = Math.Abs(y);
                if (magnitude > _peak) _peak = magnitude;

                double v = y;
                _windowSum += v * v;
                if (++_windowFill == Window)
                {
                    _windows.Add((float)Math.Sqrt(_windowSum / Window));
                    _windowSum = 0;
                    _windowFill = 0;
                    OnWindow();
                }
            }
        }

        /// <summary>A 20 ms window just completed: time to look for a cut?</summary>
        private void OnWindow()
        {
            var end = _windows.Count;
            var first = (int)(_pendingStart / Window);
            var pending = end - first;
            if (pending < SearchAfterWindows || pending % EvaluateEveryWindows != 0) return;

            var force = pending >= MaxChunkWindows;
            var threshold = QuietThreshold(end);
            var searchStart = first + MinChunkWindows;

            // Longest qualifying pause wins; ties go to the later one (longer
            // chunks mean fewer seams).
            int bestCut = -1, bestLength = 0;
            var i = searchStart;
            while (i < end)
            {
                if (_windows[i] > threshold) { i++; continue; }
                var runStart = i;
                while (runStart > first && _windows[runStart - 1] <= threshold) runStart--;
                var j = i;
                while (j < end && _windows[j] <= threshold) j++;
                var length = j - runStart;
                var open = j == end;

                // Before the limit, only a sentence-length pause will do; at
                // the limit, any real pause beats a forced cut. A pause still
                // in progress is only taken once it is already paragraph-long
                // (so the paragraph decision at the seam is sound) — otherwise
                // wait to see how long it gets — unless the chunk must be cut now.
                var qualifies = length >= (force ? MinPauseWindows : SentencePauseWindows)
                    && (!open || force || length >= ParagraphPauseWindows);
                if (qualifies && length >= bestLength)
                {
                    bestLength = length;
                    bestCut = Math.Min(end, Math.Max(searchStart, runStart + Math.Min(length / 2, PauseKeptWindows)));
                }
                i = j;
            }

            if (bestCut > first)
            {
                Cut(bestCut, bestLength / (double)WindowsPerSecond, forced: false);
            }
            else if (force)
            {
                Cut(QuietestPoint(Math.Max(searchStart, end - ForcedSearchWindows), end), 0, forced: true);
            }
        }

        /// <summary>
        /// The level below which a 20 ms window counts as quiet.
        ///
        /// Speech level is the 90th-percentile window RMS of the last 60 s (the
        /// loud vowels), noise floor the 10th (the gaps). A pause has to be at
        /// least 20 dB under the speech level — below every voiced sound — but in
        /// a noisy room the gaps only sit at the noise floor, so the threshold
        /// may rise to twice the floor, as long as it stays 12 dB under speech.
        /// Gaps between words (50–150 ms) also pass this test; the 400 ms
        /// minimum is what separates a pause from a word boundary. Measured on
        /// the filtered, un-gained signal, so it is independent of mic volume.
        /// </summary>
        internal float QuietThreshold(int end)
        {
            var from = Math.Max(0, end - LevelHistoryWindows);
            var span = _windows.GetRange(from, end - from).ToArray();
            Array.Sort(span);
            var speech = span[span.Length * 9 / 10];
            var noise = span[span.Length / 10];
            var threshold = Math.Max(speech * 0.1f, Math.Min(noise * 2f, speech * 0.25f));
            // Digital zeros are always quiet, even in a take that is all zeros.
            return Math.Max(threshold, 1e-5f);
        }

        /// <summary>Centre of the quietest 100 ms in [from, end) — the least bad place to force a cut.</summary>
        private int QuietestPoint(int from, int end)
        {
            const int span = 5;
            var best = end;
            var bestEnergy = double.MaxValue;
            for (var i = from; i + span <= end; i++)
            {
                double energy = 0;
                for (var k = 0; k < span; k++) energy += _windows[i + k];
                if (energy <= bestEnergy)
                {
                    bestEnergy = energy;
                    best = i + span / 2;
                }
            }
            return best;
        }

        private void Cut(int cutWindow, double pauseSeconds, bool forced)
        {
            var cutSample = (long)cutWindow * Window;
            var length = (int)(cutSample - _pendingStart);
            if (length <= 0) return;

            var samples = _pending.GetRange(0, length).ToArray();
            _pending.RemoveRange(0, length);
            var firstWindow = (int)(_pendingStart / Window);
            var start = _pendingStart / (double)AudioConditioner.SampleRate;
            _pendingStart = cutSample;

            Emit(samples, firstWindow, cutWindow, start, cutSample / (double)AudioConditioner.SampleRate,
                pauseSeconds, forced, final: false);
            _cuts++;
            _cutEver = true;
        }

        /// <summary>Whatever is left when the take ends.</summary>
        private void CutFinal()
        {
            var samples = _pending.ToArray();
            _pending.Clear();
            var firstWindow = (int)(_pendingStart / Window);
            var start = _pendingStart / (double)AudioConditioner.SampleRate;
            Emit(samples, firstWindow, _windows.Count, start, _rawCount / (double)AudioConditioner.SampleRate,
                0, forced: false, final: true);
        }

        private void Emit(float[] samples, int firstWindow, int endWindow, double start, double end,
            double pauseAfter, bool forced, bool final)
        {
            var (conditioned, silent, gain) = Condition(samples, firstWindow, endWindow, final);
            if (final) _finalGain = gain;
            if (_plan != null)
            {
                _plan.Add(new ChunkStat(_cuts, start, end, pauseAfter, forced, silent, final, gain, 0, ""));
                return;
            }
            var chunk = new Chunk(_cuts, conditioned, silent, final, forced, start, end, pauseAfter, gain);
            Interlocked.Increment(ref _enqueued);
            if (!_chunks.Writer.TryWrite(chunk)) Interlocked.Decrement(ref _enqueued);
        }

        /// <summary>
        /// Condition one chunk the way the whole-file path would have
        /// conditioned this stretch of the take (see the class summary).
        /// The samples arrive already high-passed.
        /// </summary>
        private (float[] Samples, bool Silent, float Gain) Condition(
            float[] samples, int firstWindow, int endWindow, bool final)
        {
            var windows = _windows.GetRange(firstWindow, Math.Max(0, endWindow - firstWindow)).ToArray();

            // Is there anything in this chunk alone worth decoding?
            var required = RequiredVoicedWindows(windows.Length);
            var verdict = AudioConditioner.Classify(
                AudioConditioner.Rms(samples), AudioConditioner.Peak(samples),
                windows.Count(w => w >= AudioConditioner.VoiceRmsFloor) * (1000 / WindowsPerSecond),
                HasSustainedVoice(windows, required),
                AudioConditioner.HasSpeechLikeModulation(windows, required));

            // Gain from the whole take so far: the running peak and the running
            // modulation test, i.e. AudioConditioner's formula on everything
            // heard up to this cut. It can only fall as the take goes on, so a
            // chunk is never louder than full scale and the final chunk gets
            // exactly the whole-file gain.
            var take = _windows.ToArray();
            var takeRequired = RequiredVoicedWindows(take.Length);
            var modulated = AudioConditioner.HasSpeechLikeModulation(take, takeRequired);
            var gain = _peak > 0 ? Math.Min(modulated ? SpeechMaxGain : StandardMaxGain, TargetPeak / _peak) : 1f;
            if (gain <= 1.02f) gain = 1f;

            if (verdict is TakeVerdict.NoSpeech or TakeVerdict.NoInput)
            {
                return (Array.Empty<float>(), true, gain);
            }

            if (gain != 1f)
            {
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] = Math.Clamp(samples[i] * gain, -1f, 1f);
                }
            }

            if (final && HasSustainedVoice(take, takeRequired, gain))
            {
                samples = TrimTrailingSilence(samples);
            }
            return (Pad(samples), false, gain);
        }

        /// <summary>
        /// The whole-file trim, applied to the final chunk: more than 700 ms of
        /// quiet on the end is cut back to 300 ms. Windows here are aligned to
        /// the take's, because every cut lands on a window boundary.
        /// </summary>
        private static float[] TrimTrailingSilence(float[] samples)
        {
            var windows = AudioConditioner.WindowRms(samples);
            var silentTail = 0;
            for (var i = windows.Length - 1; i >= 0 && windows[i] <= TrailingSilenceRms; i--) silentTail++;
            if (silentTail < MinTrailingSilenceWindows) return samples;
            var cutWindow = Math.Min(windows.Length - silentTail + RetainTrailingWindows, windows.Length);
            var cutSamples = Math.Min(cutWindow * Window, samples.Length);
            return samples[..cutSamples];
        }

        private static float[] Pad(float[] x)
        {
            var total = Math.Max(MinimumSamples, LeadInSamples + x.Length + TailSamples);
            var padded = new float[total];
            Array.Copy(x, 0, padded, LeadInSamples, x.Length);
            return padded;
        }

        private float[] AllSamples()
        {
            var all = new float[_rawCount];
            var index = 0;
            foreach (var buffer in _raw)
            {
                foreach (var s in buffer) all[index++] = s / 32768f;
            }
            return all;
        }

        // ----- Decode worker -----

        private async Task DecodeAsync()
        {
            try
            {
                var reader = _chunks.Reader;
                while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var chunk))
                    {
                        await DecodeOneAsync(chunk).ConfigureAwait(false);
                        Interlocked.Increment(ref _decoded);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch
            {
                _failed = true;
            }
        }

        private Task<Transcription> DecodeChunkAsync(Chunk chunk, string? previousText)
        {
            var vocabulary = _vocabulary;
            return Transcriber.Instance.TranscribeSamplesAsync(
                chunk.Samples, _lockedLanguage ?? _language, vocabulary.Spellings, _cts.Token,
                _accurate, vocabulary.Spoken, previousText, skipConditioning: true);
        }

        private static bool LooksUnpunctuated(string text) => Transcriber.LooksUnpunctuated(text);

        /// <summary>
        /// Decode one chunk. If it comes back in whisper's unpunctuated mode
        /// (<see cref="Transcriber.LooksUnpunctuated"/>), decode it once more
        /// with <see cref="Transcriber.PunctuationPrimer"/> as its previous text
        /// and keep that if it is punctuated — on the live path the style would
        /// otherwise spread, because each chunk's text prompts the next. An
        /// unpunctuated chunk that stays unpunctuated is never used as context.
        /// </summary>
        private async Task DecodeOneAsync(Chunk chunk)
        {
            var text = "";
            var unpunctuated = false;
            var retried = false;
            var sw = Stopwatch.StartNew();
            if (!chunk.Silent && !_failed)
            {
                try
                {
                    var result = await DecodeChunkAsync(chunk, PreviousText()).ConfigureAwait(false);
                    if (LooksUnpunctuated(result.Text))
                    {
                        retried = true;
                        var retry = await DecodeChunkAsync(chunk, Transcriber.PunctuationPrimer).ConfigureAwait(false);
                        if (!LooksUnpunctuated(retry.Text) && !retry.IsEmpty) result = retry;
                    }
                    text = result.Text.Trim();
                    unpunctuated = LooksUnpunctuated(text);
                    // A dialogue dash ("- Hey team") is a subtitle habit of the
                    // model, never dictation; mid-take it would land mid-paragraph.
                    if (chunk.Index > 0) text = LeadingDash.Replace(text, "");
                    if (!string.IsNullOrWhiteSpace(result.Language)) _detected ??= result.Language;
                    // whisper identifies the language from the first window of
                    // a file and keeps it; per-chunk detection could flip
                    // mid-take, so the first chunk's answer is kept the same way.
                    if (_language == "auto" && _lockedLanguage == null && !string.IsNullOrWhiteSpace(_detected))
                    {
                        _lockedLanguage = _detected;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    _failed = true;
                }
            }
            sw.Stop();
            _texts.Add((text, chunk.PauseAfterSeconds, text.Length == 0, unpunctuated));
            lock (_stats)
            {
                _stats.Add(new ChunkStat(chunk.Index, chunk.StartSeconds, chunk.EndSeconds,
                    chunk.PauseAfterSeconds, chunk.Forced, chunk.Silent, chunk.Final, chunk.Gain,
                    sw.ElapsedMilliseconds, text, retried));
            }
        }

        /// <summary>
        /// The recent text the next chunk is prompted with (Transcriber trims it
        /// to size). Stops at an unpunctuated chunk rather than skipping it:
        /// text on either side of a gap is not "what was just said".
        /// </summary>
        private string? PreviousText()
        {
            var sb = new StringBuilder();
            for (var i = _texts.Count - 1; i >= 0 && sb.Length < Transcriber.MaxPreviousTextChars * 2; i--)
            {
                if (_texts[i].Empty) continue;
                if (_texts[i].Unpunctuated) break;
                sb.Insert(0, _texts[i].Text + " ");
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        /// <summary>
        /// One space between chunks, and a paragraph mark where the speaker
        /// paused long enough after finishing a sentence — the rule
        /// <see cref="SmartFormatter.JoinSegments"/> applies between segments.
        /// A chunk that produced no text at all stands for at least 15 s
        /// without words, which is a pause by any measure.
        /// </summary>
        private string Join()
        {
            var totalWords = _texts.Sum(t =>
                t.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
            var sb = new StringBuilder();
            var gapIsPause = false;
            for (var i = 0; i < _texts.Count; i++)
            {
                var (text, _, empty, _) = _texts[i];
                if (empty)
                {
                    gapIsPause = true;
                    continue;
                }
                if (sb.Length > 0)
                {
                    var previous = sb.ToString().TrimEnd();
                    var endsSentence = previous.Length > 0 && previous[^1] is '.' or '!' or '?';
                    var pause = gapIsPause || _texts[i - 1].PauseAfter >= SmartFormatter.ParagraphPause.TotalSeconds;
                    sb.Append(' ');
                    if (totalWords >= MinWordsForParagraphs && pause && endsSentence)
                    {
                        sb.Append(SmartFormatter.ParagraphMark).Append(' ');
                    }
                }
                sb.Append(text);
                gapIsPause = false;
            }
            // Whole-file decoding runs the loop and duplicate guards over the
            // whole take; chunks were each guarded alone, so a repeat that
            // straddles a seam is caught here.
            return HallucinationFilter.Apply(sb.ToString(), null);
        }
    }
}
