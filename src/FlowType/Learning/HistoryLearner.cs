using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FlowType.Core;
using FlowType.Engine;
using FlowType.Session;

namespace FlowType.Learning;

/// <summary>
/// "Learn from your writing": looks back over past dictations — FlowType's own
/// History and, when the user allows it, Wispr Flow's — and proposes
/// dictionary words, rewrite rules, snippets and styles for the user to
/// approve. The live learners (<see cref="CorrectionWatcher"/>,
/// <see cref="EditLearner"/>) learn one fix at a time as it happens; this is
/// the same idea applied to everything already written.
///
/// <para><b>Nothing changes unasked.</b> A run only produces a
/// <see cref="LearningReport"/>; <see cref="Apply"/> is what writes, and it is
/// called by the review window with exactly the rows the user ticked. The one
/// exception is opt-in: a background run may add recommended <i>words</i>
/// when <see cref="AppSettings.AutoAddLearnedWords"/> is on (see
/// <see cref="BackgroundLearner"/>). Everything added is tagged
/// <see cref="DictionaryEntry.LearnedFromHistory"/> and <see cref="UndoAll"/>
/// removes it in one step. Whatever the user removes — by that undo or by
/// deleting one learned entry (<see cref="Forget"/>) — is remembered as "don't
/// suggest again", so neither the review nor the daily auto-add brings it
/// back.</para>
///
/// <para><b>Order of a run.</b> Read the sources (on a worker thread) → fixes
/// from edits and recogniser errors → styles → Wispr's own dictionary → words,
/// ranked within the prompt budget the user's dictionary, the per-dictation
/// prompt content and the pre-ticked fixes and Wispr entries leave → merge
/// what several miners proposed under one key → drop what Apply would skip
/// and what the user dismissed before → one rule per spoken phrase → untick
/// whatever no longer fits the prompt budget → optionally the personal
/// benchmark, whose recognition fixes join the list through the same filters
/// → persist the counts. <see cref="LearningReport.Suggestions"/> is the one
/// list the review shows.</para>
///
/// <para><b>Privacy.</b> Dictation text lives in memory for one run.
/// <c>learning.json</c> gets timestamps, counts and hashed dismissal keys;
/// <c>attempts.log</c> gets one counts-only line per run. The report — which
/// does carry the user's words, for the review window — is held in memory
/// only and is gone after a restart.</para>
/// </summary>
public sealed class HistoryLearner
{
    public static HistoryLearner Instance { get; } = new();

    /// <summary>The literal overhead of the spellings section in the decoder prompt.</summary>
    private const string SpellingsSectionShell = "Preferred spellings: .";

    /// <summary>The literal overhead of the spoken-forms section in the decoder prompt.</summary>
    private const string SpokenFormsSectionShell = "Possible spoken forms: .";

    /// <summary>
    /// Prompt characters kept free for the names FlowType reads off the screen
    /// at dictation time. They join the spellings after the dictionary
    /// (<c>DictationSession.MergeVocabulary</c>), so a dictionary filled to the
    /// brim would cut every one of them. About a dozen short names of the
    /// <see cref="FlowType.Context.FocusContext.MaxTerms"/> it can offer.
    /// </summary>
    internal const int ScreenTermsReserve = 120;

    /// <summary>
    /// At most this many words are pre-ticked in one report, whichever miner
    /// proposed them — the vocabulary miner's own cap, applied again after
    /// Wispr's dictionary words have joined the list.
    /// </summary>
    internal const int MaxRecommendedWords = 40;

    private readonly object _gate = new();
    private readonly LearningState _state;
    private LearningReport? _lastReport;
    private int _running;

    /// <summary>Raised after a run starts or ends, after Apply/Undo, and when the dictionary changes. Any thread.</summary>
    public event Action? Changed;

    private HistoryLearner()
    {
        _state = LearningState.Load();
        // The undo link shows a count of learned entries; deleting one by hand
        // on the Dictionary page has to update it too.
        DictionaryStore.Instance.Changed += RaiseChanged;
    }

    /// <summary>
    /// Diagnostics only (<c>--learncheck</c>, <c>--selftest</c>): run without
    /// persisting state and without the attempts.log line, so checking the
    /// feature never changes what the user sees.
    /// </summary>
    internal bool DryRun { get; set; }

    public bool IsRunning => Volatile.Read(ref _running) != 0;

    /// <summary>The last run's report (memory only), minus whatever has since been applied or dismissed.</summary>
    public LearningReport? LastReport
    {
        get { lock (_gate) return _lastReport; }
    }

    /// <summary>
    /// Recommended suggestions of the last run that the user has not applied
    /// or dismissed yet — the Insights dot and "Review N suggestions". Only
    /// counted while <see cref="LastReport"/> is held: the report is memory
    /// only, so after a restart this is 0 until the next run makes a new one,
    /// rather than a dot and a button that open a window with nothing to
    /// review. The stored count is kept as is; the next run overwrites it.
    /// </summary>
    public int PendingCount
    {
        get { lock (_gate) return PendingFor(_lastReport, _state.PendingCount); }
    }

    /// <summary>The rule behind <see cref="PendingCount"/> (pure, for --selftest).</summary>
    internal static int PendingFor(LearningReport? report, int stored) => report == null ? 0 : Math.Max(0, stored);

    /// <summary>When the last run of either kind finished (UTC); null before the first.</summary>
    public DateTime? LastRunUtc
    {
        get { lock (_gate) return _state.LastRunUtc; }
    }

    /// <summary>When the user last ran it by hand (UTC).</summary>
    public DateTime? LastManualRunUtc
    {
        get { lock (_gate) return _state.LastManualRunUtc; }
    }

    /// <summary>The last benchmark's numbers, kept across restarts; null when it never ran.</summary>
    public BenchSummary? LastBench
    {
        get { lock (_gate) return _state.LastBench; }
    }

    /// <summary>Dictionary entries this feature added that are still there (the undo link's count).</summary>
    public int LearnedEntryCount => DictionaryStore.Instance.LearnedFromHistoryCount;

    /// <summary>Per-stage timings and per-source sample counts of the last run (for --learncheck).</summary>
    internal RunStats? LastStats { get; private set; }

    internal sealed record RunStats(int FlowTypeSamples, int WisprSamples, long SourcesMs, long MiningMs, long BenchMs, long TotalMs);

    /// <summary>
    /// What each history holds, counted without reading any text — for the
    /// Insights card and the review window's source checkboxes. Never throws:
    /// a source that cannot be read comes back unavailable with a reason.
    /// Touches the disk (and Wispr's database), so call it off the UI thread.
    /// </summary>
    public IReadOnlyList<SourceSummary> SummarizeSources(string? wisprPath = null) => new[]
    {
        SafeSummary(HistorySource.FlowType, AppPaths.NotesFile, () => FlowTypeHistory.Summarize()),
        SafeSummary(HistorySource.WisprFlow, wisprPath ?? WisprHistory.DefaultDatabasePath,
            () => WisprHistory.Summarize(wisprPath)),
    };

    private static SourceSummary SafeSummary(HistorySource source, string location, Func<SourceSummary> summarize)
    {
        try { return summarize(); }
        catch (Exception)
        {
            var name = source == HistorySource.WisprFlow ? "Wispr Flow's history" : "FlowType's history";
            return new SourceSummary(source, false, 0, 0, 0, 0, $"{name} couldn't be read.", location);
        }
    }

    // ----- Running -----

    /// <summary>
    /// Look through the requested histories and propose what to learn. One
    /// run at a time: a second call while one is running throws
    /// <see cref="InvalidOperationException"/>. Runs on the thread pool; the
    /// progress callbacks arrive on whatever context
    /// <paramref name="progress"/> captured. Throws
    /// <see cref="OperationCanceledException"/> on <paramref name="ct"/>.
    /// </summary>
    public Task<LearningReport> RunAsync(LearningRequest request, IProgress<LearningProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            throw new InvalidOperationException("Already learning from your history — wait for that run to finish.");
        }
        // The Wispr opt-in (AppSettings.LearnFromWispr) is recorded by the
        // review window, which knows whether the Wispr box was a choice or
        // greyed out because Wispr was briefly unreadable; a request alone
        // cannot tell "unticked" from "unavailable".
        RaiseChanged();
        return RunGuardedAsync(request, progress, ct);
    }

    private async Task<LearningReport> RunGuardedAsync(LearningRequest request, IProgress<LearningProgress>? progress, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var outcome = "failed";
        LearningReport? report = null;
        try
        {
            report = await Task.Run(() => RunCoreAsync(request, progress, ct), ct).ConfigureAwait(false);
            outcome = "ok";
            return report;
        }
        catch (OperationCanceledException)
        {
            outcome = "canceled";
            throw;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
            if (!DryRun) WriteAttemptLine(request, report, outcome, watch.ElapsedMilliseconds);
            RaiseChanged();
        }
    }

    private async Task<LearningReport> RunCoreAsync(LearningRequest request, IProgress<LearningProgress>? progress, CancellationToken ct)
    {
        var runUtc = DateTime.UtcNow;
        var total = Stopwatch.StartNew();
        var notes = new List<string>();

        // 1. Sources.
        progress?.Report(new LearningProgress("Reading your history", -1));
        var stage = Stopwatch.StartNew();
        var read = ReadSources(request, notes, ct);
        var sourcesMs = stage.ElapsedMilliseconds;
        ct.ThrowIfCancellationRequested();

        // 2. Miners.
        progress?.Report(new LearningProgress("Looking for words and fixes", -1));
        stage.Restart();
        var dictionary = DictionaryStore.Instance.Entries;
        var settings = SettingsStore.Instance.Settings;
        var corrections = CorrectionMiner.Mine(read.Samples, dictionary);
        ct.ThrowIfCancellationRequested();
        var styles = StyleMiner.Mine(read.Samples, read.WisprStyles, settings);
        var imports = WisprDictionaryImport.Suggest(read.WisprDictionary, dictionary);
        var budget = PromptBudget(dictionary, settings.EnglishVariant);
        // Pre-ticked fixes and the user's own Wispr dictionary are spent from
        // the prompt budget before any mined word: a fix's target becomes a
        // spelling too, and a Wispr word is the user's deliberate choice. The
        // miner gets what is left, so it never packs words into room these
        // need (the final trim below would otherwise have to untick them).
        var ahead = Filtered(dictionary, corrections.FromEdits.Concat(corrections.FromRecognizer).Concat(imports));
        var wordBudget = Math.Max(0, budget - SpellingsCost(dictionary, ahead.Where(s => s.Recommended)));
        var vocabulary = VocabularyMiner.Mine(read.Samples, dictionary, corrections.MisheardTerms, wordBudget,
            WhisperVocabulary.ForCurrentModel());
        ct.ThrowIfCancellationRequested();
        var suggestions = FitToPromptBudget(dictionary, Filtered(dictionary,
            corrections.FromEdits.Concat(corrections.FromRecognizer).Concat(vocabulary).Concat(styles).Concat(imports)),
            budget);
        var miningMs = stage.ElapsedMilliseconds;

        // 3. The optional benchmark. Its recognition fixes go through the same
        // filters as everything else, so the report stays the one list the
        // review shows (the raw fixes in BenchResult skip dismissals, known
        // entries and the one-rule-per-phrase check).
        stage.Restart();
        BenchResult? bench = null;
        if (request.RunBench && !request.Background)
        {
            bench = await RunBenchAsync(read.Samples, dictionary, suggestions, progress, request.WisprPath, notes, ct)
                .ConfigureAwait(false);
            if (bench is { RecognitionFixes.Count: > 0 })
            {
                suggestions = FitToPromptBudget(dictionary,
                    Filtered(dictionary, suggestions.Concat(bench.RecognitionFixes)), budget);
            }
        }
        var benchMs = stage.ElapsedMilliseconds;

        var report = new LearningReport(runUtc, read.Summaries, read.Samples.Count, suggestions, bench, total.Elapsed, notes);
        LastStats = new RunStats(read.FlowTypeSamples, read.WisprSamples, sourcesMs, miningMs, benchMs, total.ElapsedMilliseconds);

        // 4. Remember.
        lock (_gate)
        {
            _lastReport = report;
            if (!DryRun)
            {
                _state.LastRunUtc = DateTime.UtcNow;
                if (!request.Background) _state.LastManualRunUtc = _state.LastRunUtc;
                if (bench != null) _state.LastBench = BenchSummary.From(bench);
                _state.PendingCount = CountPending(suggestions);
                foreach (var s in read.Summaries.Where(s => s.Available))
                {
                    if (s.Source == HistorySource.FlowType)
                    {
                        _state.LastFlowTypeDictations = s.Dictations;
                        _state.LastFlowTypeNewestUtc = NewestOf(read.Samples, HistorySource.FlowType);
                    }
                    else _state.LastWisprDictations = s.Dictations;
                }
                SaveStateLocked();
            }
        }
        progress?.Report(new LearningProgress("Done", 1.0));
        return report;
    }

    private sealed record SourceRead(
        IReadOnlyList<SourceSummary> Summaries,
        List<HistorySample> Samples,
        IReadOnlyList<WisprDictionaryEntry> WisprDictionary,
        IReadOnlyList<WisprStyleChoice> WisprStyles,
        int FlowTypeSamples,
        int WisprSamples);

    /// <summary>
    /// Read each requested source. A source that fails costs its own
    /// dictations and adds a plain-language note; the run goes on with the
    /// rest. Exception messages are not shown — they can be technical, and a
    /// database error message can quote the query — except the
    /// <see cref="IOException"/>s <see cref="WisprHistory"/> words for people.
    /// </summary>
    private static SourceRead ReadSources(LearningRequest request, List<string> notes, CancellationToken ct)
    {
        var summaries = new List<SourceSummary>();
        var samples = new List<HistorySample>();
        IReadOnlyList<WisprDictionaryEntry> wisprDictionary = Array.Empty<WisprDictionaryEntry>();
        IReadOnlyList<WisprStyleChoice> wisprStyles = Array.Empty<WisprStyleChoice>();
        int flowTypeCount = 0, wisprCount = 0;

        if (request.UseFlowType)
        {
            var summary = SafeSummary(HistorySource.FlowType, AppPaths.NotesFile, () => FlowTypeHistory.Summarize());
            summaries.Add(summary);
            try
            {
                var read = FlowTypeHistory.ReadSamples();
                flowTypeCount = read.Count;
                samples.AddRange(read);
            }
            catch (Exception)
            {
                notes.Add("FlowType's history couldn't be read, so it was left out.");
            }
        }
        ct.ThrowIfCancellationRequested();

        if (request.UseWispr)
        {
            var path = request.WisprPath;
            var summary = SafeSummary(HistorySource.WisprFlow, path ?? WisprHistory.DefaultDatabasePath,
                () => WisprHistory.Summarize(path));
            summaries.Add(summary);
            if (!summary.Available)
            {
                notes.Add(summary.Problem ?? "Wispr Flow's history couldn't be opened, so it was left out.");
            }
            else
            {
                try
                {
                    var read = WisprHistory.ReadSamples(path, ct);
                    wisprCount = read.Count;
                    samples.AddRange(read);
                }
                catch (OperationCanceledException) { throw; }
                catch (IOException ex) when (!string.IsNullOrWhiteSpace(ex.Message))
                {
                    // WisprHistory words its IOExceptions for people ("busy",
                    // "damaged"…). Shown in the window only; never logged.
                    notes.Add(ex.Message);
                }
                catch (Exception)
                {
                    notes.Add("Wispr Flow's history couldn't be read just now — if Wispr Flow is busy, try again in a minute.");
                }
                // Its dictionary and style settings are extras: a failure there
                // must not cost the dictations already read.
                try { wisprDictionary = WisprHistory.ReadDictionary(path); }
                catch (Exception) { }
                try { wisprStyles = WisprHistory.ReadStyleChoices(path); }
                catch (Exception) { }
            }
        }

        return new SourceRead(summaries, samples, wisprDictionary, wisprStyles, flowTypeCount, wisprCount);
    }

    private static async Task<BenchResult?> RunBenchAsync(IReadOnlyList<HistorySample> samples,
        IReadOnlyList<DictionaryEntry> dictionary, IReadOnlyList<Suggestion> suggestions,
        IProgress<LearningProgress>? progress, string? wisprPath, List<string> notes, CancellationToken ct)
    {
        if (!Transcriber.Instance.IsReady)
        {
            notes.Add("The speech model isn't loaded yet, so your recordings weren't tested.");
            return null;
        }
        if (PersonalBench.Select(samples).Count == 0)
        {
            notes.Add("None of your recordings has a second transcript to compare with, so nothing was tested.");
            return null;
        }
        var proposed = suggestions
            .Where(s => s.Recommended && s.Kind is SuggestionKind.Vocabulary or SuggestionKind.Correction)
            .ToList();
        try
        {
            var bench = await PersonalBench.RunAsync(samples, dictionary, proposed, progress, ct, wisprPath)
                .ConfigureAwait(false);
            if (bench == null) notes.Add("Your recordings couldn't be decoded, so nothing was tested.");
            return bench;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            notes.Add("Testing on your recordings stopped with an error, so only your writing was used.");
            return null;
        }
    }

    // ----- Applying -----

    /// <summary>
    /// Apply what the user ticked and remember what they asked not to see
    /// again. Called on the UI thread by the review window: one dictionary
    /// save, at most one settings save and one state save, so it stays quick.
    /// Resets <see cref="PendingCount"/> to 0 — the user has been through the
    /// report — and drops the handled rows from <see cref="LastReport"/>.
    /// </summary>
    public ApplyResult Apply(IReadOnlyList<Suggestion> chosen, IReadOnlyList<Suggestion> dismissed)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        ArgumentNullException.ThrowIfNull(dismissed);
        var result = ApplyCore(chosen);
        lock (_gate)
        {
            _state.Dismiss(dismissed);
            _state.AppliedTotal += result.Total;
            _state.PendingCount = 0;
            var handled = new HashSet<string>(chosen.Concat(dismissed).Select(LearningState.KeyOf), StringComparer.Ordinal);
            if (_lastReport != null)
            {
                _lastReport = _lastReport with
                {
                    Suggestions = _lastReport.Suggestions.Where(s => !handled.Contains(LearningState.KeyOf(s))).ToList(),
                };
            }
            if (!DryRun) SaveStateLocked();
        }
        RaiseChanged();
        return result;
    }

    /// <summary>
    /// A background run's opt-in auto-add: apply these (recommended words)
    /// without a review. Unlike <see cref="Apply"/>, everything else in the
    /// report stays pending for the user to review. Call on the UI thread.
    /// </summary>
    internal ApplyResult ApplyAutomatically(IReadOnlyList<Suggestion> words)
    {
        var result = ApplyCore(words.Where(w => w.Kind == SuggestionKind.Vocabulary).ToList());
        lock (_gate)
        {
            _state.AppliedTotal += result.Total;
            var applied = new HashSet<string>(words.Select(LearningState.KeyOf), StringComparer.Ordinal);
            if (_lastReport != null)
            {
                _lastReport = _lastReport with
                {
                    Suggestions = _lastReport.Suggestions.Where(s => !applied.Contains(LearningState.KeyOf(s))).ToList(),
                };
                _state.PendingCount = CountPending(_lastReport.Suggestions);
            }
            if (!DryRun) SaveStateLocked();
        }
        RaiseChanged();
        return result;
    }

    private static ApplyResult ApplyCore(IReadOnlyList<Suggestion> chosen)
    {
        var items = DictionaryItems(chosen);
        var added = items.Count > 0
            ? DictionaryStore.Instance.AddFromHistoryEntries(items)
            : Array.Empty<DictionaryEntry>();
        var words = added.Count(e => e.IsVocabularyOnly);
        var snippets = added.Count(e => !e.IsVocabularyOnly && e.IsSnippet);
        var fixes = added.Count - words - snippets;
        var styles = ApplyStyles(chosen.Where(s => s.Kind == SuggestionKind.Style));
        return new ApplyResult(words, fixes, snippets, styles);
    }

    /// <summary>
    /// The dictionary side of a set of suggestions, in the order they will
    /// land: words first (so they get the prompt budget first) — the user's
    /// own Wispr Flow dictionary words ahead of mined ones, because those
    /// were chosen deliberately and must not be the ones a full prompt cuts —
    /// then fixes, then snippets; otherwise in the order given.
    /// </summary>
    internal static List<(string Phrase, string Replacement, bool IsSnippet)> DictionaryItems(IEnumerable<Suggestion> chosen) =>
        InLandingOrder(chosen.ToList())
            .Select(s => s.Kind switch
            {
                SuggestionKind.Vocabulary => (s.Phrase, "", false),
                SuggestionKind.Snippet => (s.Phrase, s.Replacement, true),
                _ => (s.Phrase, s.Replacement, false),
            })
            .ToList();

    /// <summary>
    /// Indexes into <paramref name="list"/> of its dictionary kinds (styles
    /// left out) in the order <see cref="DictionaryItems"/> lands them.
    /// </summary>
    private static IEnumerable<int> LandingOrder(IReadOnlyList<Suggestion> list)
    {
        var all = Enumerable.Range(0, list.Count).ToList();
        bool FromWisprDictionary(int i) => string.Equals(list[i].Origin, WisprDictionaryImport.Origin, StringComparison.Ordinal);
        return all.Where(i => list[i].Kind == SuggestionKind.Vocabulary && FromWisprDictionary(i))
            .Concat(all.Where(i => list[i].Kind == SuggestionKind.Vocabulary && !FromWisprDictionary(i)))
            .Concat(all.Where(i => list[i].Kind == SuggestionKind.Correction))
            .Concat(all.Where(i => list[i].Kind == SuggestionKind.Snippet));
    }

    private static IEnumerable<Suggestion> InLandingOrder(IReadOnlyList<Suggestion> list) =>
        LandingOrder(list).Select(i => list[i]);

    /// <summary>
    /// Style suggestions carry a bucket id and a style id. Unknown ids are
    /// ignored rather than normalised: <see cref="WritingStyles.Normalize"/>
    /// would quietly turn a typo into "formal". One save for all of them.
    /// </summary>
    private static int ApplyStyles(IEnumerable<Suggestion> styles)
    {
        var settings = SettingsStore.Instance.Settings;
        var changed = 0;
        foreach (var s in styles)
        {
            var style = s.Replacement.Trim();
            if (!WritingStyles.All.Any(a => a.Id == style)) continue;
            switch (s.Phrase.Trim().ToLowerInvariant())
            {
                case "personal": settings.StylePersonal = style; break;
                case "work": settings.StyleWork = style; break;
                case "email": settings.StyleEmail = style; break;
                case "other": settings.StyleOther = style; break;
                default: continue;
            }
            changed++;
        }
        if (changed > 0) SettingsStore.Instance.Save();
        return changed;
    }

    /// <summary>
    /// Remove every dictionary entry this feature added. Styles it changed
    /// stay (they are ordinary settings, visible and changeable on the Style
    /// page). The removed entries are remembered as "don't suggest again":
    /// the history they came from is still there, so without that the next
    /// run would offer them again — and with words added automatically, the
    /// daily run would quietly put them back. Returns how many entries were
    /// removed.
    /// </summary>
    public int UndoAll()
    {
        var removed = DictionaryStore.Instance.RemoveLearnedFromHistoryEntries();
        if (removed.Count > 0) RememberRemoved(removed);
        RaiseChanged();
        return removed.Count;
    }

    /// <summary>
    /// The user is deleting <paramref name="entry"/> from the Dictionary or
    /// Snippets page: if this feature added it, remember it as "don't suggest
    /// again", for the same reason as <see cref="UndoAll"/>. Call before the
    /// delete, on the UI thread. An entry the user added or edited by hand was
    /// never a suggestion and is left alone.
    /// </summary>
    public void Forget(DictionaryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.LearnedFromHistory) return;
        RememberRemoved(new[] { entry });
    }

    private void RememberRemoved(IEnumerable<DictionaryEntry> removed)
    {
        lock (_gate)
        {
            if (_state.DismissKeys(removed.Select(DismissalKey)) > 0 && !DryRun) SaveStateLocked();
        }
    }

    /// <summary>
    /// The suggestion key that produced a learned entry — the key the miners
    /// will produce for it again. <see cref="DictionaryStore.PlanFromHistory"/>
    /// stored the phrase and replacement trimmed, and
    /// <see cref="Suggestion.KeyFor"/> trims and lower-cases, so the two match.
    /// </summary>
    internal static string DismissalKey(DictionaryEntry e) =>
        e.IsVocabularyOnly ? Suggestion.KeyFor(SuggestionKind.Vocabulary, e.Phrase)
        : e.IsSnippet ? Suggestion.KeyFor(SuggestionKind.Snippet, e.Phrase)
        : Suggestion.KeyFor(SuggestionKind.Correction, e.Phrase, e.Replacement);

    // ----- Pure helpers (also used by --selftest) -----

    /// <summary>
    /// Several miners can propose the same thing — Wispr's dictionary and the
    /// user's own writing both suggesting "Reforger". One row per key: the
    /// higher-scored proposal wins, the reasons are joined, it is recommended
    /// if either was, and sightings are not added up (they are often the same
    /// dictations seen twice). Each kind comes out best first.
    /// </summary>
    internal static List<Suggestion> Merge(IEnumerable<Suggestion> suggestions)
    {
        var byKey = new Dictionary<string, Suggestion>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var s in suggestions)
        {
            var key = LearningState.KeyOf(s);
            var normalised = s.Key == key ? s : s with { Key = key };
            if (byKey.TryGetValue(key, out var existing))
            {
                byKey[key] = Combine(existing, normalised);
            }
            else
            {
                byKey[key] = normalised;
                order.Add(key);
            }
        }
        return order.Select(k => byKey[k])
            .OrderBy(s => s.Kind)
            .ThenByDescending(s => s.Score)
            .ThenByDescending(s => s.Sightings)
            .ThenBy(s => s.Phrase, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Suggestion Combine(Suggestion a, Suggestion b)
    {
        var (best, other) = b.Score > a.Score ? (b, a) : (a, b);
        return best with
        {
            Recommended = a.Recommended || b.Recommended,
            Sightings = Math.Max(a.Sightings, b.Sightings),
            Why = JoinWhy(best.Why, other.Why),
            Example = best.Example ?? other.Example,
        };
    }

    /// <summary>"Used in 6 dictations" + "In your Wispr Flow dictionary" → both, once each, separated by " · ".</summary>
    internal static string JoinWhy(string first, string second)
    {
        var parts = new List<string>();
        foreach (var part in $"{first} · {second}".Split('·'))
        {
            var p = part.Trim();
            if (p.Length > 0 && !parts.Contains(p, StringComparer.OrdinalIgnoreCase)) parts.Add(p);
        }
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// Drop dictionary suggestions that <see cref="Apply"/> would skip as
    /// already present (<see cref="DictionaryStore.PlanFromHistory"/>'s rules:
    /// a word the dictionary already spells, a second rule for a phrase that
    /// has one, an entry the user switched off). The miners check presence
    /// their own way; this makes "in the report" mean "would be added", so a
    /// ticked row never silently does nothing. Styles pass through.
    /// </summary>
    internal static List<Suggestion> WithoutKnown(IReadOnlyList<DictionaryEntry> dictionary, IEnumerable<Suggestion> suggestions) =>
        suggestions.Where(s => s.Kind == SuggestionKind.Style
            || DictionaryStore.PlanFromHistory(dictionary, DictionaryItems(new[] { s })).Count > 0).ToList();

    /// <summary><see cref="Filter"/> with the user's own dismissals.</summary>
    private List<Suggestion> Filtered(IReadOnlyList<DictionaryEntry> dictionary, IEnumerable<Suggestion> suggestions)
    {
        lock (_gate) return Filter(dictionary, suggestions, _state.IsDismissed);
    }

    /// <summary>
    /// Everything a report row goes through, whichever miner (or the
    /// benchmark) proposed it: one row per key (<see cref="Merge"/>), nothing
    /// Apply would skip (<see cref="WithoutKnown"/>), nothing the user
    /// dismissed, then one rule per spoken phrase (<see cref="OnePerTrigger"/>).
    /// The phrase check comes last so a dismissed or already-covered rule
    /// does not also take a different proposal for the same phrase with it.
    /// </summary>
    internal static List<Suggestion> Filter(IReadOnlyList<DictionaryEntry> dictionary, IEnumerable<Suggestion> suggestions,
        Func<Suggestion, bool> isDismissed) =>
        OnePerTrigger(WithoutKnown(dictionary, Merge(suggestions)).Where(s => !isDismissed(s)));

    /// <summary>
    /// One rule (or snippet) per spoken phrase. Different miners can propose
    /// different targets for one phrase — a fix the user typed and a rule from
    /// Wispr's dictionary, or a benchmark fix — and their keys differ, so
    /// <see cref="Merge"/> keeps both. <see cref="DictionaryStore.PlanFromHistory"/>
    /// would then add only the first that reaches Apply and silently skip the
    /// other, though both were ticked. Kept: a fix over a snippet (fixes land
    /// first), then the higher score, then a deliberate source (the user's own
    /// edit or Wispr dictionary), then more sightings, then the earlier row.
    /// Phrases compare trimmed and case-insensitively, as PlanFromHistory does.
    /// Words and styles pass through; the order is kept.
    /// </summary>
    internal static List<Suggestion> OnePerTrigger(IEnumerable<Suggestion> suggestions)
    {
        var list = suggestions.ToList();
        static bool IsRule(Suggestion s) => s.Kind is SuggestionKind.Correction or SuggestionKind.Snippet;
        var winner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < list.Count; i++)
        {
            if (!IsRule(list[i])) continue;
            var phrase = list[i].Phrase.Trim();
            if (!winner.TryGetValue(phrase, out var best) || Beats(list[i], list[best])) winner[phrase] = i;
        }
        return list.Where((s, i) => !IsRule(s) || winner[s.Phrase.Trim()] == i).ToList();
    }

    /// <summary>True when <paramref name="a"/> should replace <paramref name="b"/> as a phrase's one rule.</summary>
    private static bool Beats(Suggestion a, Suggestion b)
    {
        if (a.Kind != b.Kind) return a.Kind < b.Kind;
        if (a.Score != b.Score) return a.Score > b.Score;
        var deliberateA = IsDeliberate(a);
        if (deliberateA != IsDeliberate(b)) return deliberateA;
        return a.Sightings > b.Sightings;
    }

    private static bool IsDeliberate(Suggestion s) =>
        string.Equals(s.Origin, WisprDictionaryImport.Origin, StringComparison.Ordinal)
        || string.Equals(s.Origin, CorrectionMiner.EditsOrigin, StringComparison.Ordinal);

    /// <summary>
    /// The last say on what is pre-ticked: walking the recommended rows in the
    /// order Apply lands them (<see cref="DictionaryItems"/>), untick any that
    /// would push the dictionary's spellings past <paramref name="budget"/>
    /// (<see cref="PromptBudget"/>) and any word past the
    /// <see cref="MaxRecommendedWords"/>th. The vocabulary miner only packs its
    /// own words; this also counts Wispr dictionary words, the targets of
    /// fixes (they become spellings too) and rows <see cref="Merge"/> re-ticked
    /// — so applying every pre-ticked row, run after run, never eats into the
    /// room kept for the previous-text context, the on-screen names and the
    /// user's spoken forms. Costs are measured on the spellings the dictionary
    /// would really have (<see cref="DictionaryStore.SectionsFor"/>), so a
    /// spelling it already has costs nothing. An unticked row stays offered.
    /// </summary>
    internal static List<Suggestion> FitToPromptBudget(IReadOnlyList<DictionaryEntry> dictionary,
        IReadOnlyList<Suggestion> suggestions, int budget)
    {
        var result = suggestions.ToList();
        var limit = JoinedLength(DictionaryStore.SectionsFor(dictionary).Spellings) + Math.Max(0, budget);
        var accepted = new List<Suggestion>();
        var words = 0;
        foreach (var i in LandingOrder(result).ToList())
        {
            var s = result[i];
            if (!s.Recommended) continue;
            var isWord = s.Kind == SuggestionKind.Vocabulary;
            var fits = (!isWord || words < MaxRecommendedWords)
                && SpellingsLength(dictionary, accepted.Append(s)) <= limit;
            if (fits)
            {
                accepted.Add(s);
                if (isWord) words++;
                // Re-ticked by a later merge (the benchmark's fixes) and fitting now.
                if (s.Why.Contains(NoRoomWhy, StringComparison.Ordinal))
                {
                    result[i] = s with { Why = JoinWhy(s.Why.Replace(NoRoomWhy, "", StringComparison.Ordinal), "") };
                }
            }
            else
            {
                result[i] = s with { Recommended = false, Why = JoinWhy(s.Why, NoRoomWhy) };
            }
        }
        return result;
    }

    /// <summary>The reason shown on a row <see cref="FitToPromptBudget"/> unticked.</summary>
    internal const string NoRoomWhy = "No room left among the words FlowType listens for";

    /// <summary>
    /// Characters these suggestions would add to the dictionary's spellings
    /// (joined by ", ", as the prompt joins them) if applied together.
    /// </summary>
    internal static int SpellingsCost(IReadOnlyList<DictionaryEntry> dictionary, IEnumerable<Suggestion> suggestions) =>
        Math.Max(0, SpellingsLength(dictionary, suggestions) - JoinedLength(DictionaryStore.SectionsFor(dictionary).Spellings));

    private static int SpellingsLength(IReadOnlyList<DictionaryEntry> dictionary, IEnumerable<Suggestion> suggestions)
    {
        var planned = DictionaryStore.PlanFromHistory(dictionary, DictionaryItems(suggestions));
        var entries = planned.Count == 0 ? dictionary : dictionary.Concat(planned).ToList();
        return JoinedLength(DictionaryStore.SectionsFor(entries).Spellings);
    }

    private static int JoinedLength(IReadOnlyList<string> terms) =>
        terms.Count == 0 ? 0 : terms.Sum(t => t.Length) + 2 * (terms.Count - 1);

    /// <summary>What the Insights dot counts: recommended rows.</summary>
    internal static int CountPending(IEnumerable<Suggestion> suggestions) => suggestions.Count(s => s.Recommended);

    /// <summary>
    /// Characters of decoder prompt that learned words (and the spellings
    /// learned fixes bring) may take: what is left once the English variant
    /// sentence and the user's own spellings are in, minus what every
    /// dictation still needs after the spellings (<see cref="PerDictationReserve"/>).
    /// The vocabulary miner recommends only what fits here and
    /// <see cref="FitToPromptBudget"/> holds the whole report to it, so a
    /// learned word never pushes out a term the user typed in, the previous-
    /// text context, the names on screen or the user's spoken forms. Mirrors
    /// <see cref="Transcriber.BuildPrompt"/>'s budgeting: spellings right after
    /// the variant sentence, then the previous text, then the spoken forms.
    /// </summary>
    internal static int PromptBudget(IReadOnlyList<DictionaryEntry> dictionary, string englishVariant)
    {
        var budget = Transcriber.MaxPromptChars;
        var variant = EnglishVariant.RecognitionPrompt(englishVariant);
        if (!string.IsNullOrWhiteSpace(variant)) budget -= variant.Trim().Length + 1;
        budget -= SpellingsSectionShell.Length;
        var sections = DictionaryStore.SectionsFor(dictionary);
        if (sections.Spellings.Count > 0) budget -= string.Join(", ", sections.Spellings).Length;
        budget -= PerDictationReserve(sections.SpokenForms);
        return Math.Max(0, budget);
    }

    /// <summary>
    /// Prompt characters held back for what each dictation adds after the
    /// dictionary's spellings, counted the way <see cref="Transcriber.BuildPrompt"/>
    /// spends them:
    /// <list type="bullet">
    /// <item>names read off the screen, which join the spellings list itself
    /// (<see cref="ScreenTermsReserve"/>);</item>
    /// <item>the space after the spellings section, then the previous-text
    /// slot: offered <c>min(MaxPreviousTextChars, left − 1)</c> characters and
    /// costing its length plus a space. A live chunk carries the sentence just
    /// spoken there, and the unpunctuated-mode retry its punctuation primer —
    /// below 20 characters the slot is dropped and that retry decodes the same
    /// prompt again for nothing. Reserved whether or not live transcription is
    /// on, because the retry uses it either way;</item>
    /// <item>the user's spoken forms (the phrases of their rewrite rules),
    /// budgeted last: "Possible spoken forms: " … "." when there are any.</item>
    /// </list>
    /// </summary>
    internal static int PerDictationReserve(IReadOnlyList<string> spokenForms)
    {
        var reserve = ScreenTermsReserve + 1 + Transcriber.MaxPreviousTextChars + 1;
        if (spokenForms.Count > 0) reserve += SpokenFormsSectionShell.Length + string.Join(", ", spokenForms).Length;
        return reserve;
    }

    // ----- Plumbing -----

    /// <summary>Background-run bookkeeping: dictations each source held at the last run that read it.</summary>
    internal (int FlowType, int Wispr) LastSourceCounts
    {
        get { lock (_gate) return (_state.LastFlowTypeDictations, _state.LastWisprDictations); }
    }

    /// <summary>Background-run bookkeeping: the newest FlowType dictation the last run read (see <see cref="LearningState.LastFlowTypeNewestUtc"/>).</summary>
    internal DateTime? LastFlowTypeNewestUtc
    {
        get { lock (_gate) return _state.LastFlowTypeNewestUtc; }
    }

    /// <summary>When the newest dictation from <paramref name="source"/> was made; null when there is none.</summary>
    internal static DateTime? NewestOf(IEnumerable<HistorySample> samples, HistorySource source) =>
        samples.Where(s => s.Source == source).Select(s => (DateTime?)s.TimestampUtc).Max();

    private void SaveStateLocked()
    {
        try { _state.Save(); }
        catch
        {
            // Bookkeeping only: a failed write costs the dot after a restart,
            // never the suggestions on screen.
        }
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch
        {
            // A subscriber that forgot to marshal to its dispatcher must not
            // break a run or an apply.
        }
    }

    /// <summary>
    /// One counts-only JSON line per run in attempts.log, beside the per-
    /// dictation lines, when the diagnostic log is on. Kinds and origins are
    /// fixed labels; no term, no text, no path.
    /// </summary>
    private static void WriteAttemptLine(LearningRequest request, LearningReport? report, string outcome, long ms)
    {
        try
        {
            if (!SettingsStore.Instance.Settings.DiagnosticLog) return;
            var suggestions = report?.Suggestions ?? Array.Empty<Suggestion>();
            int Count(SuggestionKind kind) => suggestions.Count(s => s.Kind == kind);
            var line = JsonSerializer.Serialize(new
            {
                t = DateTime.UtcNow.ToString("o"),
                ms,
                outcome = "Learn:" + outcome,
                background = request.Background,
                flowtype = request.UseFlowType,
                wispr = request.UseWispr,
                bench = request.RunBench,
                samples = report?.SamplesRead ?? 0,
                words = Count(SuggestionKind.Vocabulary),
                fixes = Count(SuggestionKind.Correction),
                snippets = Count(SuggestionKind.Snippet),
                styles = Count(SuggestionKind.Style),
                recommended = suggestions.Count(s => s.Recommended),
                notes = report?.Notes.Count ?? 0,
                bench_recordings = report?.Bench?.Recordings ?? 0,
                bench_audio_s = report?.Bench?.AudioSeconds ?? 0,
            });
            lock (AttemptLogGate)
            {
                AppPaths.EnsureCreated();
                File.AppendAllText(AppPaths.AttemptLog, line + Environment.NewLine);
            }
        }
        catch
        {
            // Diagnostics must never break the feature.
        }
    }

    private static readonly object AttemptLogGate = new();
}
