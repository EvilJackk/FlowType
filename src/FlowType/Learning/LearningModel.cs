namespace FlowType.Learning;

// The shared vocabulary of "Learn from your writing" (2.1). Every part of the
// feature — the history readers, the miners, the personal benchmark, the
// orchestrator and the review window — speaks in these types, so each can be
// built and tested on its own. Pure data: no I/O and no behaviour beyond the
// one-line conveniences below.

/// <summary>Where a past dictation came from.</summary>
public enum HistorySource
{
    /// <summary>FlowType's own History (notes.json, plus kept recordings).</summary>
    FlowType,

    /// <summary>
    /// Wispr Flow's local history database (<c>%APPDATA%\Wispr Flow\flow.sqlite</c>),
    /// read-only, only when the user ticks it.
    /// </summary>
    WisprFlow,
}

/// <summary>
/// One past dictation from any source, normalised. Nothing here is ever
/// written to disk or to a log by the learning feature: samples live in memory
/// for the length of one run.
/// </summary>
/// <param name="Source">Which history it came from.</param>
/// <param name="Id">The note's Guid ("D" format) or Wispr's <c>transcriptEntityId</c>; used to load audio.</param>
/// <param name="TimestampUtc">When it was dictated, UTC.</param>
/// <param name="App">Process-style app name ("claude", "msedge", "Discord"), or "" when unknown.</param>
/// <param name="Recognized">
/// What the speech recogniser produced before any formatting — FlowType's
/// <c>RawText</c> or Wispr's <c>asrText</c>. "" when unknown (1.x notes).
/// </param>
/// <param name="Final">What was inserted into the other app.</param>
/// <param name="Edited">
/// The same text after the user's own corrections in the target app, when
/// those were observed and differ from <paramref name="Final"/> (Wispr's
/// <c>editedText</c>); null otherwise. FlowType does not keep this.
/// </param>
/// <param name="Language">"en", or "" when unknown.</param>
/// <param name="DurationSeconds">Length of the take, 0 when unknown.</param>
/// <param name="HasAudio">A recording of the take is still on this PC.</param>
public sealed record HistorySample(
    HistorySource Source,
    string Id,
    DateTime TimestampUtc,
    string App,
    string Recognized,
    string Final,
    string? Edited,
    string Language,
    double DurationSeconds,
    bool HasAudio)
{
    /// <summary>The text the user stands behind: their edit when there is one, else what was inserted.</summary>
    public string Best => Edited ?? Final;
}

/// <summary>What one history source holds, counted without reading any text (for the Insights card).</summary>
/// <param name="Source">Which history.</param>
/// <param name="Available">It exists and could be opened.</param>
/// <param name="Dictations">Usable dictations (with text).</param>
/// <param name="WithAudio">How many of those still have their recording.</param>
/// <param name="AudioSeconds">Total length of those recordings.</param>
/// <param name="Edited">How many carry an observed user edit.</param>
/// <param name="Problem">Why it is unavailable or partial, in plain words; null when fine.</param>
/// <param name="Location">The file or folder it was read from (shown in the UI).</param>
public sealed record SourceSummary(
    HistorySource Source,
    bool Available,
    int Dictations,
    int WithAudio,
    double AudioSeconds,
    int Edited,
    string? Problem,
    string Location);

/// <summary>An entry in Wispr Flow's own dictionary (its <c>Dictionary</c> table, deleted rows excluded).</summary>
/// <param name="Phrase">The word, or the spoken trigger.</param>
/// <param name="Replacement">"" (or the phrase itself) for a plain word; otherwise what to write.</param>
/// <param name="IsSnippet">Wispr marks it as a snippet.</param>
/// <param name="Origin">Wispr's <c>source</c> column ("manual", "user_edits", "default"…).</param>
public sealed record WisprDictionaryEntry(string Phrase, string Replacement, bool IsSnippet, string Origin);

/// <summary>
/// A writing style Wispr Flow applied, from its per-dictation
/// <c>personalizationStyleSettings</c> (<c>{"personalizationAppType": "other", "personalizationStyle": "formal"}</c>),
/// aggregated: one row per (app type, style).
/// </summary>
/// <param name="AppType">Wispr's app type as stored ("personal", "work", "email", "other"…).</param>
/// <param name="Style">Wispr's style as stored ("formal", "casual", "very_casual", "excited"…).</param>
/// <param name="Count">Dictations it was applied to.</param>
/// <param name="LastUsedUtc">The most recent of those.</param>
public sealed record WisprStyleChoice(string AppType, string Style, int Count, DateTime LastUsedUtc);

/// <summary>What a suggestion would change if applied.</summary>
public enum SuggestionKind
{
    /// <summary>A word or name to recognise: a vocabulary-only dictionary entry (Phrase = the term).</summary>
    Vocabulary,

    /// <summary>A rewrite rule: Phrase (as it gets heard) → Replacement (as it should be written).</summary>
    Correction,

    /// <summary>A voice shortcut: Phrase (spoken trigger) → Replacement (text).</summary>
    Snippet,

    /// <summary>
    /// A writing style for one kind of app: Phrase = bucket id ("personal",
    /// "work", "email", "other"), Replacement = style id (WritingStyles ids).
    /// </summary>
    Style,
}

/// <summary>
/// One thing the learner proposes. The user sees every suggestion before it
/// takes effect; nothing is applied unasked except, when the user turned it
/// on, recommended Vocabulary from a background run.
/// </summary>
/// <param name="Kind">What it changes.</param>
/// <param name="Key">
/// Stable identity for "don't suggest this again" and for de-duplication,
/// built with <see cref="Suggestion.KeyFor"/>.
/// </param>
/// <param name="Phrase">See <see cref="SuggestionKind"/>.</param>
/// <param name="Replacement">See <see cref="SuggestionKind"/>; "" for Vocabulary.</param>
/// <param name="Sightings">Separate dictations (or recordings) that support it.</param>
/// <param name="Score">Ranking within its kind; higher first. Comparable only within one kind.</param>
/// <param name="Recommended">Pre-ticked in the review: confident enough to apply as is.</param>
/// <param name="Why">One plain line for the UI ("Used in 14 dictations · Wispr misheard it 3 times").</param>
/// <param name="Origin">Short label: "your writing", "your edits", "your recordings", "Wispr Flow dictionary", "Wispr Flow settings".</param>
/// <param name="Example">
/// A short excerpt around one use (≤ 120 chars) for a tooltip, or null. In
/// memory only — never logged, never written.
/// </param>
public sealed record Suggestion(
    SuggestionKind Kind,
    string Key,
    string Phrase,
    string Replacement,
    int Sightings,
    double Score,
    bool Recommended,
    string Why,
    string Origin,
    string? Example = null)
{
    /// <summary>
    /// The identity of a suggestion: kind plus the case-folded phrase (and
    /// replacement, for kinds that have one). Two miners proposing the same
    /// word produce the same key.
    /// </summary>
    public static string KeyFor(SuggestionKind kind, string phrase, string replacement = "")
    {
        var p = (phrase ?? "").Trim().ToLowerInvariant();
        var r = (replacement ?? "").Trim().ToLowerInvariant();
        return kind switch
        {
            SuggestionKind.Vocabulary => $"v|{p}",
            SuggestionKind.Style => $"s|{p}|{r}",
            SuggestionKind.Snippet => $"n|{p}",
            _ => $"c|{p}|{r}",
        };
    }
}

/// <summary>Progress for the review window: a stage line and a fraction (−1 = indeterminate).</summary>
public sealed record LearningProgress(string Stage, double Fraction, string? Detail = null);

/// <summary>
/// "How well FlowType hears you", measured on the user's own recordings.
/// Accuracy is word agreement (1 − WER, after normalisation) with an
/// independent transcript — Wispr Flow's raw cloud recogniser output,
/// <c>asrText</c>, from before Wispr's own fixes. It is a comparison, not
/// ground truth, and it is biased against learning: where Wispr's recogniser
/// got a word wrong, writing it right counts as a disagreement. The term
/// counts are the fairer measure of the learned words: they are taken against
/// the final text the user stood behind (<see cref="HistorySample.Best"/>).
/// </summary>
/// <param name="Recordings">Recordings decoded.</param>
/// <param name="AudioSeconds">Their total length.</param>
/// <param name="ReferenceWords">Words in the reference transcripts.</param>
/// <param name="BaselineAccuracy">0..1 with today's dictionary.</param>
/// <param name="LearnedAccuracy">0..1 with today's dictionary plus the proposed words and fixes; equals baseline when nothing was proposed.</param>
/// <param name="TermOccurrences">Times a proposed word occurs in the final texts of those recordings.</param>
/// <param name="TermHitsBaseline">Of those, how many today's FlowType wrote correctly.</param>
/// <param name="TermHitsLearned">…and how many it writes correctly with the proposals.</param>
/// <param name="LiveChunks">Chunks the live transcriber cut these takes into (real-voice check of the pause policy).</param>
/// <param name="ForcedCuts">Of those cuts, how many found no pause and were forced.</param>
/// <param name="RealTimeFactor">Decode time ÷ audio length for the baseline pass (0.05 = 20× faster than real time).</param>
/// <param name="ModelId">Speech model used.</param>
/// <param name="RunUtc">When it ran.</param>
/// <param name="RecognitionFixes">Rewrite rules for mishearings FlowType made repeatedly on these recordings.</param>
public sealed record BenchResult(
    int Recordings,
    double AudioSeconds,
    int ReferenceWords,
    double BaselineAccuracy,
    double LearnedAccuracy,
    int TermOccurrences,
    int TermHitsBaseline,
    int TermHitsLearned,
    int LiveChunks,
    int ForcedCuts,
    double RealTimeFactor,
    string ModelId,
    DateTime RunUtc,
    IReadOnlyList<Suggestion> RecognitionFixes);

/// <summary>What to learn from, and whether to run the (slow) personal benchmark.</summary>
/// <param name="UseFlowType">Read FlowType's own History.</param>
/// <param name="UseWispr">Read Wispr Flow's history database.</param>
/// <param name="RunBench">Decode the user's recordings to measure accuracy before/after (needs the speech model loaded).</param>
/// <param name="WisprPath">Override for the Wispr database path (tests); null = the default location.</param>
/// <param name="Background">A quiet daily run: no benchmark, suggestions wait for review.</param>
public sealed record LearningRequest(
    bool UseFlowType,
    bool UseWispr,
    bool RunBench,
    string? WisprPath = null,
    bool Background = false);

/// <summary>The outcome of one learning run.</summary>
/// <param name="RunUtc">When it started.</param>
/// <param name="Sources">What each source held.</param>
/// <param name="SamplesRead">Dictations read across all sources.</param>
/// <param name="Suggestions">Everything proposed, dismissed ones and already-known entries removed, each kind best first.</param>
/// <param name="Bench">The benchmark, when requested and possible; else null.</param>
/// <param name="Elapsed">Wall-clock time of the run.</param>
/// <param name="Notes">Plain-language notes for the UI ("Wispr Flow's database is locked — try again in a minute").</param>
public sealed record LearningReport(
    DateTime RunUtc,
    IReadOnlyList<SourceSummary> Sources,
    int SamplesRead,
    IReadOnlyList<Suggestion> Suggestions,
    BenchResult? Bench,
    TimeSpan Elapsed,
    IReadOnlyList<string> Notes)
{
    public IEnumerable<Suggestion> OfKind(SuggestionKind kind) => Suggestions.Where(s => s.Kind == kind);
}

/// <summary>What <c>HistoryLearner.Apply</c> changed.</summary>
public sealed record ApplyResult(int Words, int Fixes, int Snippets, int Styles)
{
    public int Total => Words + Fixes + Snippets + Styles;
}
