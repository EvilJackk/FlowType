using System.Diagnostics;
using FlowType.Core;
using FlowType.Engine;
using FlowType.Session;

namespace FlowType.Learning;

/// <summary>
/// "How well FlowType hears you": decodes the user's own recordings exactly
/// the way a dictation would be decoded, once with today's dictionary and once
/// with the proposed words and fixes, and compares both with an independent
/// transcript of the same audio.
///
/// <para><b>Why only Wispr Flow's recordings.</b> A benchmark needs a
/// reference that this recogniser did not produce. Wispr keeps its cloud
/// recogniser's raw output (<c>asrText</c>) beside each recording, so those
/// takes have one. A FlowType note's raw text is FlowType's own earlier
/// output; scoring against it would measure agreement with itself and report
/// it as accuracy. FlowType samples are therefore skipped, and the result says
/// "word agreement with Wispr's transcript", never "accuracy".</para>
///
/// <para><b>Proposed words are counted in the final text.</b> Word agreement
/// is measured against Wispr's raw transcript, but how often a proposed word
/// occurs — and so how many of those FlowType spells right — is counted in
/// the text the user stands behind (<see cref="HistorySample.Best"/>: their
/// edit, or what Wispr pasted). The words learning proposes are largely the
/// ones Wispr's recogniser got wrong; where it misheard one, the raw transcript
/// does not contain it, and counting there would leave out exactly the
/// occurrences the proposals are for.</para>
///
/// <para><b>Exactly like a dictation.</b> Live transcription on: the take is
/// replayed through <see cref="LiveTranscriber"/> (same chunk cuts, same
/// conditioning, same previous-text prompts); a take too short to be chunked,
/// or live transcription off, is decoded whole with the unpunctuated-mode
/// retry, as <c>DictationSession</c> does. The text then goes through the
/// dictionary's rewrite rules. Screen-context names and the formatter are not
/// applied — there is no screen to read for an old take, and formatting does
/// not change which words were heard.</para>
///
/// <para><b>The user comes first.</b> The decoder is one lock shared with
/// real dictation. Before each recording and between its two passes the
/// benchmark waits while a dictation is recording or processing, and a decode
/// already under way when the user starts dictating is cancelled at once —
/// with live transcription off it would otherwise hold the decoder for a whole
/// take, which can be minutes of audio — and that pass is decoded again once
/// the user is done (<see cref="GiveWayAsync"/>). So a dictation never waits
/// behind the benchmark. Cancelling the run stops the in-flight decode and
/// returns promptly.</para>
///
/// <para><b>Privacy.</b> Audio and transcripts live in memory for one
/// recording and are dropped before the next. Nothing here logs or writes
/// text; the result holds numbers plus suggested fixes for the review window.</para>
/// </summary>
public static class PersonalBench
{
    /// <summary>Newest recordings decoded at most — about 30 minutes of Wispr's 47 s average take.</summary>
    public const int MaxRecordings = 40;

    /// <summary>Shorter than this is a slip of the hotkey, not a dictation.</summary>
    private const double MinSeconds = 0.5;

    /// <summary>How often a waiting benchmark checks whether the user's dictation has finished.</summary>
    private static readonly TimeSpan YieldPoll = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// A pass interrupted this many times by the user's dictations is given
    /// up and the take skipped, so a busy user cannot keep one long recording
    /// restarting for ever.
    /// </summary>
    internal const int MaxInterruptions = 3;

    /// <summary>
    /// Whether the user needs the decoder now, and a way to hear the moment
    /// they start. <see cref="Session"/> watches the real dictation session;
    /// --selftest passes its own.
    /// </summary>
    /// <param name="IsBusy">True while a dictation is recording or processing.</param>
    /// <param name="WhenBusy">Calls the action whenever the user starts; dispose to stop.</param>
    /// <param name="Poll">How often a waiting benchmark looks again.</param>
    internal sealed record UserActivity(Func<bool> IsBusy, Func<Action, IDisposable> WhenBusy, TimeSpan Poll)
    {
        /// <summary>The real dictation session. <c>State</c> is only written on the UI thread; reading an enum is atomic.</summary>
        public static UserActivity Session { get; } = new(
            () => DictationSession.Instance.State != SessionState.Idle,
            onBusy =>
            {
                void Handler(SessionState state, string _)
                {
                    if (state != SessionState.Idle) onBusy();
                }
                DictationSession.Instance.StateChanged += Handler;
                return new Unsubscriber(() => DictationSession.Instance.StateChanged -= Handler);
            },
            YieldPoll);
    }

    /// <summary>Runs an action once, on dispose.</summary>
    internal sealed class Unsubscriber : IDisposable
    {
        private Action? _action;
        public Unsubscriber(Action action) => _action = action;
        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }

    /// <summary>
    /// Run the benchmark. Null when the speech model is not loaded, or there
    /// is no recording with an independent transcript, or none could be
    /// decoded. Throws <see cref="OperationCanceledException"/> on
    /// <paramref name="ct"/>. Runs on the thread pool; safe to await from the
    /// UI thread.
    /// </summary>
    public static Task<BenchResult?> RunAsync(
        IReadOnlyList<HistorySample> samples,
        IReadOnlyList<DictionaryEntry> current,
        IReadOnlyList<Suggestion> proposed,
        IProgress<LearningProgress>? progress,
        CancellationToken ct,
        string? wisprPath = null) =>
        RunWithAudioAsync(samples, current, proposed, progress, ct, s => HistorySources.LoadAudio(s, wisprPath));

    /// <summary>
    /// <see cref="RunAsync"/> with the audio loader injected — and, optionally,
    /// the user-activity source and whether takes go through the live chunker
    /// (default: the user's live-transcription setting): --selftest feeds
    /// synthetic audio and exercises both decode paths.
    /// </summary>
    internal static Task<BenchResult?> RunWithAudioAsync(
        IReadOnlyList<HistorySample> samples,
        IReadOnlyList<DictionaryEntry> current,
        IReadOnlyList<Suggestion> proposed,
        IProgress<LearningProgress>? progress,
        CancellationToken ct,
        Func<HistorySample, float[]?> loadAudio,
        UserActivity? activity = null,
        bool? live = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(proposed);
        if (!Transcriber.Instance.IsReady) return Task.FromResult<BenchResult?>(null);
        return Task.Run(() => RunCoreAsync(samples, current, proposed, progress, ct, loadAudio,
            activity ?? UserActivity.Session, live), ct);
    }

    /// <summary>
    /// A recording can be benchmarked when it is still on disk and has an
    /// independent transcript (see the class remarks).
    /// </summary>
    public static bool IsUsable(HistorySample s) =>
        s.Source == HistorySource.WisprFlow && s.HasAudio && !string.IsNullOrWhiteSpace(s.Recognized);

    /// <summary>The recordings a run would decode: usable ones, newest first, at most <see cref="MaxRecordings"/>.</summary>
    public static IReadOnlyList<HistorySample> Select(IReadOnlyList<HistorySample> samples) =>
        samples.Where(IsUsable).OrderByDescending(s => s.TimestampUtc).Take(MaxRecordings).ToList();

    private sealed record Decoded(string Text, int Chunks, int ForcedCuts, long Ms);

    /// <summary>Everything one run decodes against, fixed before the first recording.</summary>
    private sealed record Plan(
        IReadOnlyList<DictionaryEntry> Current,
        IReadOnlyList<DictionaryEntry> Learned,
        DictionaryStore.VocabularyLists BaselineLists,
        DictionaryStore.VocabularyLists LearnedLists,
        bool SecondDecode,
        IReadOnlyList<string> Terms,
        AppSettings Settings,
        bool Live,
        Func<HistorySample, float[]?> LoadAudio,
        IProgress<LearningProgress>? Progress,
        UserActivity Activity);

    /// <summary>One recording's outcome: numbers and the pass-1 text for mining fixes.</summary>
    private sealed record Scored(
        string BaselineText,
        (int Errors, int Words, int TermOcc, int TermHits) Before,
        (int Errors, int Words, int TermOcc, int TermHits) After,
        double Seconds,
        Decoded First);

    private static async Task<BenchResult?> RunCoreAsync(
        IReadOnlyList<HistorySample> samples,
        IReadOnlyList<DictionaryEntry> current,
        IReadOnlyList<Suggestion> proposed,
        IProgress<LearningProgress>? progress,
        CancellationToken ct,
        Func<HistorySample, float[]?> loadAudio,
        UserActivity activity,
        bool? live)
    {
        var runUtc = DateTime.UtcNow;
        var chosen = Select(samples);
        if (chosen.Count == 0) return null;

        var modelId = Transcriber.Instance.CurrentModelId;
        var settings = SettingsStore.Instance.Settings;
        var variantPrompt = EnglishVariant.RecognitionPrompt(settings.EnglishVariant);

        // Pass 2's dictionary is today's plus exactly what Apply would append.
        var learned = WithProposals(current, proposed);
        var plan = new Plan(
            current, learned,
            DictionaryStore.SectionsFor(current), DictionaryStore.SectionsFor(learned),
            PromptChanges(current, learned, variantPrompt),
            proposed.Where(p => p.Kind == SuggestionKind.Vocabulary).Select(p => p.Phrase).ToList(),
            settings, live ?? settings.LiveTranscription, loadAudio, progress, activity);

        int recordings = 0, words = 0, baselineErrors = 0, learnedErrors = 0;
        int termOcc = 0, hitsBaseline = 0, hitsLearned = 0, chunks = 0, forced = 0;
        double seconds = 0;
        long baselineMs = 0;
        var pairs = new List<(string Hypothesis, string Reference)>();

        for (var i = 0; i < chosen.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            // The model was switched or unloaded mid-run: stop rather than
            // mix two models' numbers into one result.
            if (!Transcriber.Instance.IsReady || Transcriber.Instance.CurrentModelId != modelId) break;

            progress?.Report(new LearningProgress("Testing on your recordings", i / (double)chosen.Count,
                $"Recording {i + 1} of {chosen.Count}"));
            var take = await BenchOneAsync(chosen[i], plan, ct).ConfigureAwait(false);
            if (take == null) continue;

            recordings++;
            seconds += take.Seconds;
            words += take.Before.Words;
            baselineErrors += take.Before.Errors;
            learnedErrors += take.After.Errors;
            termOcc += take.Before.TermOcc;
            hitsBaseline += take.Before.TermHits;
            hitsLearned += take.After.TermHits;
            chunks += take.First.Chunks;
            forced += take.First.ForcedCuts;
            baselineMs += take.First.Ms;
            pairs.Add((take.BaselineText, chosen[i].Best));
        }

        progress?.Report(new LearningProgress("Testing on your recordings", 1.0, null));
        if (recordings == 0 || words == 0) return null;

        var fixes = CorrectionMiner.MineRecognitionErrors(pairs, current);
        return new BenchResult(
            Recordings: recordings,
            AudioSeconds: Math.Round(seconds, 1),
            ReferenceWords: words,
            BaselineAccuracy: Accuracy(baselineErrors, words),
            LearnedAccuracy: Accuracy(learnedErrors, words),
            TermOccurrences: termOcc,
            TermHitsBaseline: hitsBaseline,
            TermHitsLearned: hitsLearned,
            LiveChunks: chunks,
            ForcedCuts: forced,
            RealTimeFactor: seconds > 0 ? Math.Round(baselineMs / 1000.0 / seconds, 4) : 0,
            ModelId: modelId,
            RunUtc: runUtc,
            RecognitionFixes: fixes);
    }

    /// <summary>
    /// Load, decode (once or twice) and score one recording. Its own method
    /// so the audio array belongs to this call alone: an async method keeps
    /// its locals alive in its state machine, and in the caller's loop the
    /// previous take's audio would otherwise stay reachable through the next
    /// wait for the user. Null when the take could not be loaded or decoded —
    /// one bad file costs that file, not the run.
    /// </summary>
    private static async Task<Scored?> BenchOneAsync(HistorySample sample, Plan plan, CancellationToken ct)
    {
        await YieldToUserAsync(plan.Activity, plan.Progress, ct).ConfigureAwait(false);

        float[]? audio;
        try { audio = plan.LoadAudio(sample); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
        if (audio == null || audio.Length < MinSeconds * AudioConditioner.SampleRate) return null;

        try
        {
            var first = await GiveWayAsync(t => DecodeAsync(audio, plan.BaselineLists, plan.Settings, plan.Live, t),
                plan.Activity, plan.Progress, ct).ConfigureAwait(false);
            if (first == null) return null;
            var baselineText = DictionaryStore.ApplyRules(first.Text, plan.Current);
            string learnedText;
            if (plan.SecondDecode)
            {
                var second = await GiveWayAsync(t => DecodeAsync(audio, plan.LearnedLists, plan.Settings, plan.Live, t),
                    plan.Activity, plan.Progress, ct).ConfigureAwait(false);
                if (second == null) return null;
                learnedText = DictionaryStore.ApplyRules(second.Text, plan.Learned);
            }
            else
            {
                learnedText = DictionaryStore.ApplyRules(first.Text, plan.Learned);
            }

            // Word agreement against Wispr's raw transcript; proposed words
            // counted in the final text (see the class remarks).
            return new Scored(
                baselineText,
                Score(sample.Recognized, baselineText, plan.Terms, sample.Best),
                Score(sample.Recognized, learnedText, plan.Terms, sample.Best),
                audio.Length / (double)AudioConditioner.SampleRate,
                first);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Wait while the user is dictating. Being one poll late costs a quarter
    /// of a second; a decode that is already running is stopped by
    /// <see cref="GiveWayAsync"/> instead.
    /// </summary>
    private static async Task YieldToUserAsync(UserActivity activity, IProgress<LearningProgress>? progress,
        CancellationToken ct)
    {
        var told = false;
        while (activity.IsBusy())
        {
            if (!told)
            {
                progress?.Report(new LearningProgress("Paused while you dictate", -1, null));
                told = true;
            }
            await Task.Delay(activity.Poll, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Run one decode pass, giving way to the user. Waits while they are
    /// dictating, then decodes with a token that is cancelled the moment they
    /// start again, which frees the decoder for their dictation at once
    /// instead of after the rest of the pass. An interrupted pass is waited
    /// out and decoded again from the start; after
    /// <see cref="MaxInterruptions"/> interruptions the take is skipped (null).
    /// It is never counted as a failed take, and only cancelling
    /// <paramref name="ct"/> ends the run (<see cref="OperationCanceledException"/>).
    /// </summary>
    internal static async Task<T?> GiveWayAsync<T>(Func<CancellationToken, Task<T>> decode, UserActivity activity,
        IProgress<LearningProgress>? progress, CancellationToken ct) where T : class
    {
        var interruptions = 0;
        while (true)
        {
            await YieldToUserAsync(activity, progress, ct).ConfigureAwait(false);
            using var yieldCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using (activity.WhenBusy(() => CancelOnPool(yieldCts)))
            {
                // A dictation that started between the wait and the subscription.
                if (activity.IsBusy()) continue;
                try
                {
                    return await decode(yieldCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (yieldCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    if (++interruptions >= MaxInterruptions) return null;
                }
            }
        }
    }

    /// <summary>
    /// The session reports a dictation starting on the UI thread; cancelling
    /// there would run the decoder's cancellation callbacks on it, so the
    /// cancel goes to the thread pool. The source may already be disposed by
    /// then — the pass finished first — which is fine.
    /// </summary>
    private static void CancelOnPool(CancellationTokenSource cts) =>
        ThreadPool.QueueUserWorkItem(static state =>
        {
            try { ((CancellationTokenSource)state!).Cancel(); }
            catch (ObjectDisposedException) { }
        }, cts);

    /// <summary>
    /// One decode the way <c>DictationSession</c> would do it: the live
    /// chunker when <paramref name="live"/> (the user's live-transcription
    /// setting) and the take is long enough to be chunked, otherwise the whole
    /// take with the unpunctuated-mode retry.
    /// </summary>
    private static async Task<Decoded> DecodeAsync(float[] audio, DictionaryStore.VocabularyLists lists,
        AppSettings settings, bool live, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        if (live)
        {
            var report = await LiveTranscriber.SimulateDetailedAsync(
                audio, settings.Language, lists.Spellings, lists.SpokenForms, accurate: null, ct).ConfigureAwait(false);
            if (report.Result is { } chunked)
            {
                watch.Stop();
                return new Decoded(chunked.Text, report.Chunks.Count, report.Chunks.Count(c => c.Forced),
                    watch.ElapsedMilliseconds);
            }
        }

        var whole = await Transcriber.Instance.TranscribeSamplesAsync(
            audio, settings.Language, lists.Spellings, ct, accurate: null, spokenForms: lists.SpokenForms)
            .ConfigureAwait(false);
        if (Transcriber.LooksUnpunctuated(whole.Text))
        {
            var primed = await Transcriber.Instance.TranscribeSamplesAsync(
                audio, settings.Language, lists.Spellings, ct, accurate: null, spokenForms: lists.SpokenForms,
                previousText: Transcriber.PunctuationPrimer).ConfigureAwait(false);
            if (!primed.IsEmpty && !Transcriber.LooksUnpunctuated(primed.Text)) whole = primed;
        }
        watch.Stop();
        return new Decoded(whole.Text, 0, 0, watch.ElapsedMilliseconds);
    }

    /// <summary>
    /// Today's entries plus the proposed words and fixes, appended exactly as
    /// <see cref="DictionaryStore.AddFromHistory"/> would append them — so
    /// pass 2 measures the dictionary the user would actually get.
    /// </summary>
    internal static List<DictionaryEntry> WithProposals(IReadOnlyList<DictionaryEntry> current,
        IReadOnlyList<Suggestion> proposed)
    {
        // The same items, in the same order, as HistoryLearner.Apply would add.
        var items = HistoryLearner.DictionaryItems(
            proposed.Where(p => p.Kind is SuggestionKind.Vocabulary or SuggestionKind.Correction));
        return current.Concat(DictionaryStore.PlanFromHistory(current, items)).ToList();
    }

    /// <summary>
    /// Whether the proposals change what the decoder is told. Words change the
    /// prompt, and so can a fix (its target becomes a spelling); but a word
    /// that no longer fits in the 900-character budget, or a fix whose target
    /// is not a term, does not — and then decoding again would only reproduce
    /// pass 1, so the rules are re-applied to pass 1's text instead.
    /// </summary>
    internal static bool PromptChanges(IReadOnlyList<DictionaryEntry> current,
        IReadOnlyList<DictionaryEntry> learned, string? variantPrompt)
    {
        var before = DictionaryStore.SectionsFor(current);
        var after = DictionaryStore.SectionsFor(learned);
        return !string.Equals(
            Transcriber.BuildPrompt(variantPrompt, before.Spellings, before.SpokenForms),
            Transcriber.BuildPrompt(variantPrompt, after.Spellings, after.SpokenForms),
            StringComparison.Ordinal);
    }

    /// <summary>1 − WER, clamped to 0..1 (a hypothesis full of insertions can exceed 100 % WER).</summary>
    internal static double Accuracy(int errors, int words) =>
        words <= 0 ? 0 : Math.Round(Math.Clamp(1.0 - errors / (double)words, 0, 1), 4);

    /// <summary>
    /// Word errors against <paramref name="reference"/> (<see cref="WordErrorRate"/>),
    /// plus, for each proposed term, how often it occurs in
    /// <paramref name="termReference"/> (the reference itself when null) and
    /// how many of those occurrences the hypothesis also has. The bench passes
    /// Wispr's raw transcript as the reference and the final text as the term
    /// reference: the raw transcript lacks exactly the words its recogniser
    /// misheard (see the class remarks). Terms are compared as normalised word
    /// sequences (case, punctuation and apostrophes ignored), so
    /// "Arma Reforger" matches "arma reforger." but not "armor forger", and
    /// "Arma" never matches inside "Armageddon". A term the hypothesis writes
    /// more often than the term reference says gets no extra credit.
    /// </summary>
    internal static (int Errors, int Words, int TermOcc, int TermHits) Score(
        string reference, string hypothesis, IReadOnlyList<string> terms, string? termReference = null)
    {
        var (errors, words) = WordErrorRate.Count(reference ?? "", hypothesis ?? "");
        var refWords = WordErrorRate.Normalize(termReference ?? reference ?? "");
        var hypWords = WordErrorRate.Normalize(hypothesis ?? "");
        int occurrences = 0, hits = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var term in terms)
        {
            var tokens = WordErrorRate.Normalize(term ?? "");
            if (tokens.Length == 0 || !seen.Add(string.Join(' ', tokens))) continue;
            var inReference = Occurrences(refWords, tokens);
            if (inReference == 0) continue;
            occurrences += inReference;
            hits += Math.Min(inReference, Occurrences(hypWords, tokens));
        }
        return (errors, words, occurrences, hits);
    }

    /// <summary>Non-overlapping occurrences of a word sequence.</summary>
    private static int Occurrences(string[] words, string[] sequence)
    {
        var count = 0;
        for (var i = 0; i + sequence.Length <= words.Length;)
        {
            var match = true;
            for (var k = 0; k < sequence.Length && match; k++) match = words[i + k] == sequence[k];
            if (match) { count++; i += sequence.Length; }
            else i++;
        }
        return count;
    }
}
