using FlowType.Context;
using FlowType.Engine;
using FlowType.Session;

namespace FlowType.Learning;

/// <summary>
/// Finds the names and terms the user says again and again that whisper is
/// unlikely to know, and ranks which of them are worth the decoder's prompt.
///
/// The prompt is ~900 characters and the user's own dictionary comes first,
/// so the question is not "which words are names" but "which names would
/// change what whisper writes". Three things answer it: how often the user
/// says the term (more dictations, more benefit), whether a recogniser has
/// actually misheard it (direct evidence it needs help), and whether the
/// model's own vocabulary already holds it as one piece (" GitHub" — then the
/// prompt adds little). A term that passes all three is pre-ticked while it
/// still fits in the budget left over — and only when it is backed by two
/// dictations (or two mishearings), and when the fuzzy matcher, which
/// corrects towards every dictionary word, would not start rewriting a word
/// the user writes ("Wispr" would turn "whisper" into "Wispr"). Either way it
/// is still offered, unticked.
///
/// Candidates are what <see cref="ContextTerms.Extract"/> calls a term — the
/// same local rules that pick names off the screen — so a capital at the start
/// of a sentence ("So", "Okay", "Basically") is never one. One more filter is
/// needed over a whole history that a single screen does not: a word the user
/// also writes in lower case mid-sentence is an ordinary word that happened to
/// be capitalised ("Flow" in a heading, "flow" in prose), not a name — unless
/// whisper does not know the lower-case word either. On the user's own history
/// (2026-10-19, counts only) "workbench" is written in lower case 21 times
/// mid-sentence and capitalised 6; it is still the Reforger tool, and
/// " workbench" is not a whisper token, so it stays — spelled "workbench", the
/// way the user mostly writes it. " flow" is one, so "Flow" goes.
///
/// Lower-case jargon ("prefab") cannot be told from English by its shape; it
/// becomes a candidate only when a recogniser misheard it and the text was
/// fixed (<see cref="CorrectionFindings.MisheardTerms"/>), and only when
/// whisper does not already write it in one piece. Most of those mishearings
/// are ordinary uncommon words a cloud formatter touched once ("colour" →
/// "color"); the model's vocabulary is what separates them from jargon.
///
/// Pure: no I/O, nothing logged. Dictation text is only read.
/// </summary>
public static class VocabularyMiner
{
    public const string Origin = "your writing";

    /// <summary>Most suggestions returned; the review list stays readable.</summary>
    private const int MaxSuggestions = 80;

    /// <summary>Most pre-ticked; past this the prompt is a list nobody reads, the model included.</summary>
    private const int MaxRecommended = 40;

    /// <summary>A pre-ticked term must be worth at least two uses of an unfamiliar word (log2(3) ≈ 1.58).</summary>
    private const double MinRecommendedScore = 1.0;

    /// <summary>Terms read from one dictation (ContextTerms ranks by frequency in the text).</summary>
    private const int TermsPerSample = 64;

    /// <summary>
    /// The prompt section drops anything shorter (<see cref="DictionaryStore.SectionsFor"/>):
    /// a two-letter term biases the decoder toward false hits and tells it nothing.
    /// </summary>
    private const int MinTermLength = 3;

    private const int MaxTermLength = 40;
    private const int MaxTermWords = 4;

    /// <summary>Score multiplier for a term whose every word is already one whisper token.</summary>
    private const double KnownFamiliarity = 0.25;

    /// <summary>…and for one whisper has to spell out of three or more pieces.</summary>
    private const double ForeignFamiliarity = 1.25;

    private sealed class Candidate
    {
        /// <summary>How the term is written where it was recognised as a term (ContextTerms, or the corpus for a misheard-only word).</summary>
        public readonly Dictionary<string, int> Spellings = new(StringComparer.Ordinal);

        /// <summary>Dictations ContextTerms picked it out of.</summary>
        public int Extracted;

        /// <summary>Dictations that use it at all, as whole words in any case.</summary>
        public int Uses;

        /// <summary>Came only from a mishearing: spelled the way the history writes it.</summary>
        public bool MisheardOnly;

        public int FirstSeen;
        public string? Example;

        public int Sightings => Math.Max(Extracted, Uses);
    }

    public static IReadOnlyList<Suggestion> Mine(
        IReadOnlyList<HistorySample> samples,
        IReadOnlyList<DictionaryEntry> dictionary,
        IReadOnlyDictionary<string, int>? misheardTerms,
        int promptBudgetChars,
        WhisperVocabulary? vocabulary)
    {
        var known = DictionaryCoverage.From(dictionary);
        var misheard = CaseInsensitive(misheardTerms);
        var texts = samples.Select(s => s.Best).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();

        var candidates = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in texts)
        {
            // Extract already merges case variants within one text, so each
            // term is noted once per dictation.
            foreach (var term in ContextTerms.Extract(text, TermsPerSample))
            {
                if (!IsUsable(term, known)) continue;
                Note(candidates, term, text);
            }
        }
        AddMisheardOnly(candidates, misheard, known, vocabulary);
        CountUses(candidates, texts);
        DropOrdinaryWords(candidates, texts, misheard, vocabulary);

        var scored = new List<(string Term, Candidate C, double Score, int Misheard, Familiarity F)>();
        foreach (var (key, c) in candidates)
        {
            var mishearings = misheard.GetValueOrDefault(key);
            if (c.Sightings < 2 && !(c.Sightings >= 1 && mishearings > 0)) continue;
            var term = PreferredSpelling(c);
            var familiarity = FamiliarityOf(term, vocabulary);
            var score = Math.Log2(1 + c.Sightings)
                        * Multiplier(familiarity)
                        * (1 + (mishearings > 0 ? 1.5 : 0))
                        * (term.Contains('_') ? 0.5 : 1.0);
            scored.Add((term, c, score, mishearings, familiarity));
        }

        var ranked = scored
            .OrderByDescending(s => s.Score)
            .ThenByDescending(s => s.C.Sightings)
            .ThenBy(s => s.C.FirstSeen)
            .Take(MaxSuggestions)
            .ToList();

        // One dictation is thin evidence for a word that will bias every later
        // decode: offered, but pre-ticked only with a second dictation or a
        // second mishearing.
        var thin = new HashSet<int>(Enumerable.Range(0, ranked.Count)
            .Where(i => ranked[i].C.Sightings < 2 && ranked[i].Misheard < 2));
        var (recommended, rewrites) = Choose(
            ranked.Select(r => (r.Term, r.Score)).ToList(), thin, texts, dictionary, promptBudgetChars);
        return ranked.Select((r, i) => new Suggestion(
                SuggestionKind.Vocabulary,
                Suggestion.KeyFor(SuggestionKind.Vocabulary, r.Term),
                r.Term,
                "",
                r.C.Sightings,
                r.Score,
                recommended.Contains(i),
                Why(r.Term, r.C.Sightings, r.Misheard, r.F) + (rewrites.Contains(i) ? RewritesNote : ""),
                Origin,
                r.C.Example))
            .ToList();
    }

    /// <summary>Added to the "why" line of a word the fuzzy matcher would turn other words into.</summary>
    internal const string RewritesNote = " · would also change a word you use";

    /// <summary>
    /// Which ranked terms to pre-tick (<see cref="PickRecommended"/>, minus
    /// <paramref name="thin"/> ones), and which would make the fuzzy matcher
    /// rewrite something the user actually wrote.
    ///
    /// An applied word is not only a prompt hint: it is also a target of
    /// <see cref="VocabularyMatcher"/> on every later dictation, so "Wispr"
    /// would turn every "whisper" into "Wispr", "Arma" every "army", "prefab"
    /// every "prefabs". Such a word is never pre-ticked (its budget goes to the
    /// next one) and says why; the user can still tick it. A run the matcher
    /// would hit exactly as another term — one already in the dictionary, or
    /// another word pre-ticked here — is safe, because an exact match beats
    /// any near one. That is why this repeats until the pre-ticked set is
    /// stable: dropping one word can expose a run another word relied on.
    /// </summary>
    private static (HashSet<int> Recommended, HashSet<int> Rewrites) Choose(
        IReadOnlyList<(string Term, double Score)> ranked, HashSet<int> thin, List<string> texts,
        IReadOnlyList<DictionaryEntry> dictionary, int promptBudgetChars)
    {
        if (ranked.Count == 0) return (new HashSet<int>(), new HashSet<int>());

        var index = VocabularyMatcher.RunIndex.Build(texts);
        var places = ranked.Select(r => index.PlacesRewrittenBy(r.Term)).ToList();
        var dictionaryKeys = DictionaryStore.SectionsFor(dictionary).Spellings
            .Select(VocabularyMatcher.MatchKey)
            .ToHashSet(StringComparer.Ordinal);

        bool Rewrites(int i, HashSet<string> safe) => places[i].Any(p => !index.IsProtected(p, safe));

        var blocked = new HashSet<int>();
        while (true)
        {
            var picked = PickRecommended(ranked, promptBudgetChars, i => thin.Contains(i) || blocked.Contains(i));
            var safe = new HashSet<string>(dictionaryKeys, StringComparer.Ordinal);
            foreach (var i in picked) safe.Add(VocabularyMatcher.MatchKey(ranked[i].Term));

            var clashes = picked.Where(i => Rewrites(i, safe)).ToList();
            if (clashes.Count > 0)
            {
                blocked.UnionWith(clashes);   // grows every round, so this ends
                continue;
            }

            var rewrites = new HashSet<int>(Enumerable.Range(0, ranked.Count)
                .Where(i => !picked.Contains(i) && Rewrites(i, safe)));
            return (picked, rewrites);
        }
    }

    // ----- Candidates -----

    private static void Note(Dictionary<string, Candidate> candidates, string term, string text)
    {
        if (!candidates.TryGetValue(term, out var c))
        {
            c = new Candidate { FirstSeen = candidates.Count, Example = CorrectionMiner.Excerpt(text, term) };
            candidates[term] = c;
        }
        c.Extracted++;
        c.Spellings[term] = c.Spellings.GetValueOrDefault(term) + 1;
    }

    /// <summary>
    /// Whether a term could become a useful vocabulary entry at all: not a
    /// handle, file, path, address or link (none of those is spoken the way it
    /// is written), short enough for the prompt, and not already in the
    /// dictionary as a word or as the target of a rule.
    /// </summary>
    internal static bool IsUsable(string term, DictionaryCoverage known)
    {
        var t = term.Trim();
        if (t.Length is < MinTermLength or > MaxTermLength) return false;
        if (!t.Any(char.IsLetter)) return false;
        if (t[0] is '@' or '#') return false;
        if (t.Contains('/') || t.Contains('\\') || t.Contains("://") || t.Contains('@')) return false;
        if (t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > MaxTermWords) return false;
        if (IsFileName(t)) return false;
        return !known.HasSpelling(t);
    }

    /// <summary>
    /// "App.xaml.cs", "index.ts", "Node.js": a dot followed by letters. The only
    /// dotted terms ContextTerms returns are file names and versioned names
    /// ("GPT-3.5"), whose last part has no letter.
    /// </summary>
    private static bool IsFileName(string t)
    {
        var dot = t.LastIndexOf('.');
        if (dot <= 0 || dot == t.Length - 1) return false;
        var ext = t[(dot + 1)..];
        return ext.Length <= 10 && ext.All(char.IsLetterOrDigit) && ext.Any(char.IsLetter);
    }

    /// <summary>
    /// Terms a recogniser misheard that ContextTerms never picked out — lower-case
    /// jargon, or a name only ever said at the start of a sentence.
    ///
    /// Shape says nothing about these, so the bar is the model's own
    /// vocabulary: a term whisper already writes in one piece is an ordinary
    /// word a formatter happened to touch, not jargon. Without a model file to
    /// ask, the term must have been misheard in two dictations — the live
    /// learner's bar for weak evidence.
    /// </summary>
    private static void AddMisheardOnly(
        Dictionary<string, Candidate> candidates, Dictionary<string, int> misheard,
        DictionaryCoverage known, WhisperVocabulary? vocabulary)
    {
        foreach (var (term, count) in misheard)
        {
            if (candidates.ContainsKey(term) || !IsUsable(term, known)) continue;
            if (vocabulary == null ? count < 2 : FamiliarityOf(term, vocabulary) == Familiarity.Known) continue;
            candidates[term] = new Candidate { FirstSeen = candidates.Count, MisheardOnly = true };
        }
    }

    /// <summary>
    /// How many dictations use each candidate at all: as whole words, in any
    /// case, wherever it stands. ContextTerms only picks a name out where its
    /// shape proves it is one — never at the start of a sentence, never in
    /// lower case — so its count understates use: on the user's history
    /// "Workbench" is picked out of 1 dictation and written in 23. "Used in N
    /// dictations" should mean what it says. One pass: every run of up to
    /// <see cref="MaxTermWords"/> words, punctuation trimmed from each word's
    /// ends, looked up case-insensitively; a dash or symbol on its own breaks
    /// a run, as it does a name.
    /// </summary>
    private static void CountUses(Dictionary<string, Candidate> candidates, List<string> texts)
    {
        if (candidates.Count == 0) return;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var words = new List<string>();
        foreach (var text in texts)
        {
            seen.Clear();
            words.Clear();
            foreach (var raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                words.Add(StripPossessive(TrimPunctuation(raw)));
            }

            for (var i = 0; i < words.Count; i++)
            {
                if (words[i].Length == 0) continue;
                var phrase = words[i];
                for (var n = 1; n <= MaxTermWords; n++)
                {
                    if (n > 1)
                    {
                        if (i + n - 1 >= words.Count || words[i + n - 1].Length == 0) break;
                        phrase = phrase + " " + words[i + n - 1];
                    }
                    if (!candidates.TryGetValue(phrase, out var c) || !seen.Add(phrase)) continue;
                    c.Uses++;
                    if (c.MisheardOnly)
                    {
                        c.Spellings[phrase] = c.Spellings.GetValueOrDefault(phrase) + 1;
                        c.Example ??= CorrectionMiner.Excerpt(text, phrase);
                    }
                }
            }
        }

        // A misheard word the history never actually uses has nothing to show.
        foreach (var key in candidates.Where(kv => kv.Value.Sightings == 0).Select(kv => kv.Key).ToList())
        {
            candidates.Remove(key);
        }
    }

    /// <summary>Letters, digits and inner punctuation of a word: "(Workbench)," → "Workbench".</summary>
    private static string TrimPunctuation(string raw)
    {
        var a = 0;
        while (a < raw.Length && !char.IsLetterOrDigit(raw[a])) a++;
        var b = raw.Length;
        while (b > a && !char.IsLetterOrDigit(raw[b - 1])) b--;
        return raw[a..b];
    }

    /// <summary>
    /// A capitalised word (no inner capital, digit, or all-caps) — or a
    /// Title-case run of such words ("Pull Request") — that the user also
    /// writes in lower case mid-sentence at least as often is ordinary
    /// language, not a name: "Flow" vs "flow". Kept anyway when whisper does
    /// not know the lower-case words as single tokens either ("workbench" —
    /// jargon whichever way it is cased, so worth the prompt) or a recogniser
    /// misheard it. A term never seen in lower case is left alone.
    ///
    /// A term kept that way is spelled as the user mostly writes it, in lower
    /// case. Spelled with the minority capital, the fuzzy matcher would
    /// re-case every lower-case use from then on ("my workbench in the
    /// garage" → "my Workbench…"); in lower case it changes nothing, because
    /// <see cref="VocabularyMatcher.PreserveCase"/> keeps a capital the user
    /// did write.
    /// </summary>
    private static void DropOrdinaryWords(
        Dictionary<string, Candidate> candidates, List<string> texts, Dictionary<string, int> misheard, WhisperVocabulary? vocabulary)
    {
        // A misheard-only phrase is already spelled as the corpus mostly
        // writes it (CountUses), so only extracted phrases need the check.
        var titled = candidates
            .Select(kv => (kv.Key, Term: PreferredSpelling(kv.Value), kv.Value.MisheardOnly))
            .Where(x => IsPlainCapitalised(x.Term) || (!x.MisheardOnly && IsTitlePhrase(x.Term)))
            .ToList();
        if (titled.Count == 0) return;

        var forms = new HashSet<string>(titled.Select(p => p.Term.ToLowerInvariant()), StringComparer.Ordinal);
        var (lower, capital) = MidSentenceCase(texts, forms);
        foreach (var (key, term, _) in titled)
        {
            var form = term.ToLowerInvariant();
            var lowerCount = lower.GetValueOrDefault(form);
            if (lowerCount == 0 || lowerCount < capital.GetValueOrDefault(form)) continue;

            var jargon = misheard.ContainsKey(key)
                         || (vocabulary != null
                             && !form.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(vocabulary.IsSingleToken));
            if (!jargon)
            {
                candidates.Remove(key);
                continue;
            }
            var c = candidates[key];
            c.Spellings.Clear();
            c.Spellings[form] = lowerCount;
        }
    }

    private static bool IsPlainCapitalised(string term) =>
        !term.Contains(' ')
        && term.Length >= 2
        && char.IsUpper(term[0])
        && !term.Skip(1).Any(char.IsUpper)
        && !term.Any(char.IsDigit);

    /// <summary>
    /// "Pull Request", "Arma reforger": two or more plain words (no inner
    /// capital, no digit), at least one capitalised. A strong shape anywhere
    /// ("GitHub Actions") marks a name and is left alone, as for single words.
    /// </summary>
    private static bool IsTitlePhrase(string term)
    {
        var words = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 2
               && words.Any(w => char.IsUpper(w[0]))
               && words.All(w => !w.Skip(1).Any(char.IsUpper) && !w.Any(char.IsDigit));
    }

    /// <summary>One word of a text as <see cref="MidSentenceCase"/> sees it.</summary>
    private readonly record struct CaseWord(string Core, bool AtStart, bool Leading, bool Trailing, bool NewlineBefore);

    /// <summary>
    /// For each lower-case form in <paramref name="forms"/> (a word, or words
    /// joined by single spaces): how often it is written in lower case, and how
    /// often capitalised ("Flow", "Pull Request"), away from the start of a
    /// sentence, a line or a quotation — where a capital is grammar. A phrase
    /// is only read where nothing but a space separates its words.
    /// </summary>
    internal static (Dictionary<string, int> Lower, Dictionary<string, int> Capital) MidSentenceCase(
        IEnumerable<string> texts, HashSet<string> forms)
    {
        var lower = new Dictionary<string, int>(StringComparer.Ordinal);
        var capital = new Dictionary<string, int>(StringComparer.Ordinal);
        var maxWords = forms.Count == 0 ? 1 : forms.Max(f => f.Count(ch => ch == ' ') + 1);
        var words = new List<CaseWord>();
        foreach (var text in texts)
        {
            CaseWords(text, words);
            for (var i = 0; i < words.Count; i++)
            {
                if (words[i].Core.Length == 0 || words[i].AtStart) continue;
                var phrase = words[i].Core;
                for (var n = 1; n <= maxWords; n++)
                {
                    if (n > 1)
                    {
                        if (i + n - 1 >= words.Count) break;
                        var before = words[i + n - 2];
                        var next = words[i + n - 1];
                        if (before.Trailing || next.Leading || next.NewlineBefore || next.Core.Length == 0) break;
                        phrase = phrase + " " + next.Core;
                    }
                    var form = phrase.ToLowerInvariant();
                    if (!forms.Contains(form)) continue;
                    if (phrase == form) lower[form] = lower.GetValueOrDefault(form) + 1;
                    else if (n > 1 || (char.IsUpper(phrase[0]) && string.CompareOrdinal(phrase, 1, form, 1, form.Length) == 0))
                        capital[form] = capital.GetValueOrDefault(form) + 1;
                }
            }
        }
        return (lower, capital);
    }

    /// <summary>
    /// The words of <paramref name="text"/> with punctuation peeled from both
    /// ends (and a possessive dropped), each marked when it starts a sentence,
    /// a line or a quotation.
    /// </summary>
    private static void CaseWords(string text, List<CaseWord> into)
    {
        into.Clear();
        var sentenceStart = true;
        var i = 0;
        while (i < text.Length)
        {
            var newline = false;
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                if (text[i] is '\n' or '\r') newline = true;
                i++;
            }
            if (i >= text.Length) break;
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;

            var raw = text.AsSpan(start, i - start);
            var a = 0;
            while (a < raw.Length && !char.IsLetterOrDigit(raw[a])) a++;
            var b = raw.Length;
            while (b > a && !char.IsLetterOrDigit(raw[b - 1])) b--;
            var core = StripPossessive(raw[a..b].ToString());
            var lead = raw[..a];
            var atStart = sentenceStart || newline || lead.IndexOfAny("\"“‘«'") >= 0;
            into.Add(new CaseWord(core, atStart, a > 0, b < raw.Length, newline));

            var endsSentence = raw[b..].IndexOfAny(".!?…") >= 0 && !(core.Length == 1 && char.IsUpper(core[0]));
            sentenceStart = core.Length == 0 ? atStart || endsSentence : endsSentence;
        }
    }

    private static string StripPossessive(string core)
    {
        if (core.Length > 2 && (core.EndsWith("'s", StringComparison.OrdinalIgnoreCase)
                                || core.EndsWith("’s", StringComparison.OrdinalIgnoreCase)))
        {
            return core[..^2];
        }
        return core;
    }

    /// <summary>
    /// The spelling used most; on a tie the one with more capitals ("GitHub"
    /// over "Github"), then the first seen.
    /// </summary>
    private static string PreferredSpelling(Candidate c) => c.Spellings
        .Select((kv, order) => (kv.Key, kv.Value, order))
        .OrderByDescending(x => x.Value)
        .ThenByDescending(x => x.Key.Count(char.IsUpper))
        .ThenBy(x => x.order)
        .First().Key;

    private static Dictionary<string, int> CaseInsensitive(IReadOnlyDictionary<string, int>? source)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (source == null) return map;
        foreach (var (k, v) in source)
        {
            if (string.IsNullOrWhiteSpace(k) || v <= 0) continue;
            map[k.Trim()] = map.GetValueOrDefault(k.Trim()) + v;
        }
        return map;
    }

    // ----- Ranking -----

    internal enum Familiarity
    {
        /// <summary>No model file to ask.</summary>
        Unknown,
        /// <summary>Every word is a single whisper token: the model already writes it.</summary>
        Known,
        /// <summary>Spelled out of pieces.</summary>
        Unfamiliar,
        /// <summary>Three or more pieces: the prompt helps most here.</summary>
        Foreign,
    }

    internal static Familiarity FamiliarityOf(string term, WhisperVocabulary? vocabulary)
    {
        if (vocabulary == null) return Familiarity.Unknown;
        var words = term.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0 && words.All(vocabulary.IsSingleToken)) return Familiarity.Known;
        return vocabulary.EstimateTokens(term) >= 3 ? Familiarity.Foreign : Familiarity.Unfamiliar;
    }

    private static double Multiplier(Familiarity f) => f switch
    {
        Familiarity.Known => KnownFamiliarity,
        Familiarity.Foreign => ForeignFamiliarity,
        _ => 1.0,
    };

    /// <summary>
    /// Indexes into <paramref name="ranked"/> (best first) to pre-tick: score at
    /// least <see cref="MinRecommendedScore"/>, at most <see cref="MaxRecommended"/>,
    /// and only while the total still fits the prompt budget left after the
    /// user's own dictionary — counted as the prompt counts it, the term plus
    /// ", ". A term too long for what is left is skipped and shorter ones after
    /// it still get their chance: only pre-ticked terms are applied, and they
    /// all fit, so the prompt never has to cut one. A term <paramref name="skip"/>
    /// rules out spends nothing, so its share goes to the next one.
    /// </summary>
    internal static HashSet<int> PickRecommended(
        IReadOnlyList<(string Term, double Score)> ranked, int promptBudgetChars, Func<int, bool>? skip = null)
    {
        var picked = new HashSet<int>();
        var used = 0;
        for (var i = 0; i < ranked.Count && picked.Count < MaxRecommended; i++)
        {
            var (term, score) = ranked[i];
            if (score < MinRecommendedScore) continue;
            if (skip != null && skip(i)) continue;
            var cost = Transcriber.SanitizeTerm(term).Length + 2;
            if (used + cost > promptBudgetChars) continue;
            used += cost;
            picked.Add(i);
        }
        return picked;
    }

    private static string Why(string term, int sightings, int misheard, Familiarity familiarity)
    {
        var parts = new List<string> { sightings == 1 ? "Used in 1 dictation" : $"Used in {sightings} dictations" };
        if (misheard > 0) parts.Add(misheard == 1 ? "misheard once" : $"misheard {misheard}×");
        switch (familiarity)
        {
            case Familiarity.Known:
                parts.Add("Whisper already knows it");
                break;
            case Familiarity.Unfamiliar:
            case Familiarity.Foreign:
                parts.Add(term.Contains(' ') ? "Whisper doesn't know this name" : "Whisper doesn't know this word");
                break;
        }
        return string.Join(" · ", parts);
    }
}
