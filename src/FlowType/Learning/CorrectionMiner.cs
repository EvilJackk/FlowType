using System.Text;
using FlowType.Session;

namespace FlowType.Learning;

/// <summary>What <see cref="CorrectionMiner.Mine"/> found in the user's history.</summary>
/// <param name="FromEdits">Rules from fixes the user typed after a dictation landed (Wispr's <c>editedText</c>).</param>
/// <param name="FromRecognizer">Rules from mishearings Wispr's recogniser made and the final text corrected (never from FlowType's own notes).</param>
/// <param name="MisheardTerms">
/// Every term Wispr's recogniser got wrong, or the user fixed by hand, and the
/// final text spelled right (case-insensitive) → dictations it happened in.
/// Feeds the vocabulary ranking: a word that keeps being misheard is the one
/// the prompt should name.
/// </param>
public sealed record CorrectionFindings(
    IReadOnlyList<Suggestion> FromEdits,
    IReadOnlyList<Suggestion> FromRecognizer,
    IReadOnlyDictionary<string, int> MisheardTerms);

/// <summary>
/// Turns past mishearings into proposed rewrite rules.
///
/// The live learners see one correction at a time and must decide on the
/// spot. History offers what they never have: repetition. The same fix made
/// in three separate dictations is far stronger evidence than any one of
/// them, so this class only counts — every judgement about whether a pair is
/// a real term rather than grammar, a homophone or a word choice is
/// <see cref="EditLearner.DiffEdits"/>'s, unchanged, so a rule proposed from
/// history passes exactly the tests a rule learned live would.
///
/// Three kinds of evidence, each with its own bar:
/// - your edits (what was pasted → what you changed it to): your own hand, so
///   a pair with strong evidence counts at once and a weak one on its second
///   dictation — the same bar <see cref="EditLearner"/> uses live;
/// - Wispr's recogniser (what it heard → the final text): the fix came from
///   Wispr's formatter or your edit, not necessarily from you, so a pair needs
///   two dictations to be offered and three to be pre-ticked;
/// - your recordings (FlowType's own decode → the reference transcript): strong
///   at one, weak at two, pre-ticked at two.
///
/// FlowType's own notes are never recogniser evidence, although they carry a
/// raw transcript too. Their final text is FlowType's own output — its rules,
/// aliases, vocabulary snap, AI polish and style — so the raw → final diff only
/// replays FlowType's pipeline, including rules the user has since deleted. A
/// wrongly learned "Kara → Cara" that rewrote five notes would otherwise come
/// straight back, pre-ticked, the day after the user removed it.
///
/// A heard phrase that the user's dictionary already tells FlowType to write
/// (a vocabulary word, or a rule's target) is never made a trigger: the
/// prompt is steering whisper towards that spelling, and a rule on it would
/// rewrite the user's own term in every dictation. The benchmark selects for
/// exactly this — FlowType, with the dictionary, writes "Enfusion"; Wispr's
/// transcript, without it, says "Infusion".
///
/// One thing the live learner cannot weigh is what a rule costs. A one-word
/// trigger that whisper itself writes as a single token is an everyday word
/// ("cloud", "Shawn"), and a rule on it rewrites that word in every later
/// dictation. Such a rule is still offered, with that said plainly, but never
/// pre-ticked. On the user's own history (2026-10-19, counts only) this held
/// back 2 of the 6 rules that would otherwise have been pre-ticked, one of
/// them the most-sighted rule of all (17 dictations). The vocabulary
/// suggestion for the same name usually fixes the hearing without touching
/// the everyday word.
///
/// Everything stays in memory; the only output is the suggestions.
/// </summary>
public static class CorrectionMiner
{
    public const string EditsOrigin = "your edits";
    public const string RecognizerOrigin = "Wispr's recogniser";

    /// <summary>
    /// No longer produced (2.1 review): FlowType's own notes are not mined as
    /// recogniser evidence any more, because their raw → final diff only
    /// replays FlowType's own rules and polish. Kept so code that groups
    /// suggestions by origin (--learncheck's known origins) still compiles and
    /// still recognises the label on a report made by an older build.
    /// </summary>
    public const string FlowTypeRecognizerOrigin = "FlowType's recogniser";

    public const string RecordingsOrigin = "your recordings";

    /// <summary>Longest example excerpt shown as a tooltip.</summary>
    internal const int ExampleChars = 120;

    /// <summary>
    /// Mine the user's edits and the recogniser's mistakes from history,
    /// judging everyday triggers by the installed model's vocabulary
    /// (<see cref="WhisperVocabulary.ForCurrentModel"/>, cached).
    /// </summary>
    public static CorrectionFindings Mine(IReadOnlyList<HistorySample> samples, IReadOnlyList<DictionaryEntry> dictionary) =>
        Mine(samples, dictionary, WhisperVocabulary.ForCurrentModel());

    /// <summary>
    /// <see cref="Mine(IReadOnlyList{HistorySample}, IReadOnlyList{DictionaryEntry})"/> with an
    /// explicit vocabulary; null means none is known, and no rule is held back
    /// for having an everyday trigger.
    /// </summary>
    public static CorrectionFindings Mine(
        IReadOnlyList<HistorySample> samples, IReadOnlyList<DictionaryEntry> dictionary, WhisperVocabulary? vocabulary)
    {
        var known = DictionaryCoverage.From(dictionary);
        var edits = new Tally();
        var recognizer = new Tally();
        var misheard = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var sample in samples)
        {
            if (string.IsNullOrWhiteSpace(sample.Final)) continue;
            // A term counts once per dictation however many passes saw it.
            var writes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(sample.Edited))
            {
                foreach (var c in EditLearner.DiffEdits(sample.Final, sample.Edited!))
                {
                    edits.Add(c, sample.Edited!);
                    writes.Add(c.Correction.Write);
                }
            }

            // Only Wispr's raw transcript is independent of FlowType: see the
            // class comment for why a FlowType note's RawText → Text is not.
            var best = sample.Best;
            if (sample.Source == HistorySource.WisprFlow
                && !string.IsNullOrWhiteSpace(sample.Recognized) && !string.IsNullOrWhiteSpace(best))
            {
                foreach (var c in EditLearner.DiffEdits(sample.Recognized, best))
                {
                    recognizer.Add(c, best);
                    writes.Add(c.Correction.Write);
                }
            }

            foreach (var w in writes) misheard[w] = misheard.GetValueOrDefault(w) + 1;
        }

        var fromEdits = edits.Suggestions(known, Bar.Edits, vocabulary);
        var fromRecognizer = recognizer.Suggestions(known, Bar.Recognizer, vocabulary);

        // The user's own fix outranks the recogniser's: when they disagree on
        // what a heard phrase should become, only the user's version is offered.
        var editTargets = fromEdits.ToDictionary(s => s.Phrase, s => s.Replacement, StringComparer.OrdinalIgnoreCase);
        fromRecognizer = fromRecognizer
            .Where(s => !editTargets.TryGetValue(s.Phrase, out var w) || w.Equals(s.Replacement, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new CorrectionFindings(fromEdits, fromRecognizer, misheard);
    }

    /// <summary>
    /// Rules for words FlowType's own model got wrong on the user's
    /// recordings: <paramref name="pairs"/> are (what FlowType wrote, the
    /// reference transcript of the same take).
    /// </summary>
    public static IReadOnlyList<Suggestion> MineRecognitionErrors(
        IReadOnlyList<(string Hypothesis, string Reference)> pairs, IReadOnlyList<DictionaryEntry> dictionary) =>
        MineRecognitionErrors(pairs, dictionary, WhisperVocabulary.ForCurrentModel());

    /// <summary>As above, with an explicit vocabulary (null: none known).</summary>
    public static IReadOnlyList<Suggestion> MineRecognitionErrors(
        IReadOnlyList<(string Hypothesis, string Reference)> pairs, IReadOnlyList<DictionaryEntry> dictionary,
        WhisperVocabulary? vocabulary)
    {
        var known = DictionaryCoverage.From(dictionary);
        var tally = new Tally();
        foreach (var (hypothesis, reference) in pairs)
        {
            if (string.IsNullOrWhiteSpace(hypothesis) || string.IsNullOrWhiteSpace(reference)) continue;
            foreach (var c in EditLearner.DiffEdits(hypothesis, reference))
            {
                tally.Add(c, reference);
            }
        }
        return tally.Suggestions(known, Bar.Recordings, vocabulary);
    }

    // ----- Thresholds -----

    /// <summary>Where a correction was seen, which decides its bar and how it is described.</summary>
    private enum Evidence { Edits, Recognizer, Recordings }

    /// <summary>How much evidence one kind of source needs.</summary>
    private sealed record Bar(
        Evidence Source, int StrongToSuggest, int WeakToSuggest, int StrongToRecommend, int WeakToRecommend, double Weight)
    {
        /// <summary>The user's own fixes: the live learner's bar (strong at once, weak on a second dictation).</summary>
        public static readonly Bar Edits = new(Evidence.Edits, 1, 2, 1, 2, 1.5);

        /// <summary>Someone else's fix: two dictations to offer, three to pre-tick.</summary>
        public static readonly Bar Recognizer = new(Evidence.Recognizer, 2, 2, 3, 3, 1.0);

        /// <summary>FlowType's own mistakes on real recordings: strong at one, weak at two, pre-ticked at two.</summary>
        public static readonly Bar Recordings = new(Evidence.Recordings, 1, 2, 2, 2, 1.25);
    }

    // ----- Counting -----

    /// <summary>
    /// Sightings per (heard, write) pair, case-insensitive. Each caller adds a
    /// pair at most once per dictation — <see cref="EditLearner.DiffEdits"/>
    /// already returns each pair once per comparison.
    /// </summary>
    private sealed class Tally
    {
        private sealed class Entry
        {
            public string Heard = "";
            public readonly Dictionary<string, int> Spellings = new(StringComparer.Ordinal);
            public int Count;
            public bool Strong;
            public string? Example;
        }

        private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

        public void Add(EditLearner.EditCandidate candidate, string context)
        {
            var c = candidate.Correction;
            var key = c.Heard + "\u0001" + c.Write;
            if (!_entries.TryGetValue(key, out var e))
            {
                e = new Entry { Heard = c.Heard, Example = Excerpt(context, c.Write) };
                _entries[key] = e;
            }
            e.Count++;
            e.Strong |= candidate.Strong;
            e.Spellings[c.Write] = e.Spellings.GetValueOrDefault(c.Write) + 1;
        }

        public List<Suggestion> Suggestions(DictionaryCoverage known, Bar bar, WhisperVocabulary? vocabulary)
        {
            var result = new List<Suggestion>();
            foreach (var group in _entries.Values.GroupBy(e => e.Heard, StringComparer.OrdinalIgnoreCase))
            {
                // One heard phrase, several fixes: the most frequent wins; a tie
                // means the evidence does not say which, so neither is offered.
                var ranked = group.OrderByDescending(e => e.Count).ToList();
                if (ranked.Count > 1 && ranked[0].Count == ranked[1].Count) continue;
                var e = ranked[0];

                var write = e.Spellings.OrderByDescending(kv => kv.Value).First().Key;
                // Already a rule, or the heard side is a spelling the user's
                // dictionary insists on (a word, or a rule's target): a rule
                // on it would rewrite their own term (see the class comment).
                if (known.HasPair(e.Heard, write) || known.HasTrigger(e.Heard) || known.HasSpelling(e.Heard)) continue;

                var needed = e.Strong ? bar.StrongToSuggest : bar.WeakToSuggest;
                if (e.Count < needed) continue;
                var recommended = e.Count >= (e.Strong ? bar.StrongToRecommend : bar.WeakToRecommend);

                var score = Math.Log2(1 + e.Count) * bar.Weight * (e.Strong ? 1.25 : 1.0);
                var (why, origin) = Describe(bar, e.Count);
                if (IsEverydayTrigger(e.Heard, vocabulary))
                {
                    recommended = false;
                    why += $" · would change every “{e.Heard}” you say";
                }
                result.Add(new Suggestion(
                    SuggestionKind.Correction,
                    Suggestion.KeyFor(SuggestionKind.Correction, e.Heard, write),
                    e.Heard,
                    write,
                    e.Count,
                    score,
                    recommended,
                    why,
                    origin,
                    e.Example));
            }
            return result
                .OrderByDescending(s => s.Score)
                .ThenByDescending(s => s.Sightings)
                .ThenBy(s => s.Phrase, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// The "why" line and origin label, worded by who actually made the
        /// mistake. Recogniser evidence is only ever Wispr's (FlowType's own
        /// notes are not mined for it), so it is labelled as such.
        /// </summary>
        private static (string Why, string Origin) Describe(Bar bar, int count)
        {
            var times = count == 1 ? "once" : $"in {count} dictations";
            return bar.Source switch
            {
                Evidence.Edits => ($"You fixed this by hand {times}", EditsOrigin),
                Evidence.Recordings => ($"FlowType heard it this way in {count} of your recordings", RecordingsOrigin),
                _ => ($"Wispr heard it this way {times}", RecognizerOrigin),
            };
        }
    }

    /// <summary>
    /// A one-word trigger the model writes as a single token, as spelled or in
    /// lower case: a word whisper meets constantly, so a rule on it rewrites
    /// real speech. A multi-word trigger ("armour forger") is a phrase nobody
    /// says on purpose and is not held back.
    /// </summary>
    internal static bool IsEverydayTrigger(string heard, WhisperVocabulary? vocabulary)
    {
        if (vocabulary == null || heard.Contains(' ')) return false;
        return vocabulary.IsSingleToken(heard) || vocabulary.IsSingleToken(heard.ToLowerInvariant());
    }

    // ----- Examples -----

    /// <summary>
    /// A one-line excerpt of <paramref name="text"/> of at most
    /// <paramref name="max"/> characters around the first use of
    /// <paramref name="phrase"/>, cut at word boundaries and marked "…" where
    /// it was cut. For a tooltip only — never logged or written.
    /// </summary>
    internal static string? Excerpt(string? text, string phrase, int max = ExampleChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = OneLine(text);
        if (line.Length <= max) return line;

        var available = max - 2;   // room for an ellipsis on each side
        var at = string.IsNullOrEmpty(phrase) ? -1 : line.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return line[..(max - 1)].TrimEnd() + "…";
        if (phrase.Length >= available) return line.Substring(at, Math.Min(phrase.Length, max));

        var start = Math.Max(0, at - (available - phrase.Length) / 2);
        var end = Math.Min(line.Length, start + available);
        start = Math.Max(0, end - available);

        // Never begin or end on half a word.
        if (start > 0)
        {
            var space = line.IndexOf(' ', start);
            if (space >= 0 && space < at) start = space + 1;
        }
        if (end < line.Length)
        {
            var space = line.LastIndexOf(' ', end - 1);
            if (space >= at + phrase.Length) end = space;
        }

        var body = line[start..end].Trim();
        return (start > 0 ? "…" : "") + body + (end < line.Length ? "…" : "");
    }

    /// <summary>Line breaks and runs of whitespace as single spaces.</summary>
    private static string OneLine(string text)
    {
        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                if (sb.Length > 0 && !space) sb.Append(' ');
                space = true;
                continue;
            }
            sb.Append(c);
            space = false;
        }
        return sb.ToString().TrimEnd();
    }
}
