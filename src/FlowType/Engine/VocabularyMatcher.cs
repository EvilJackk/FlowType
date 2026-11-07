using System.Text;

namespace FlowType.Engine;

/// <summary>
/// Second-chance correction for the user's own names and terms.
///
/// Whisper's initial prompt biases recognition toward dictionary words, but it
/// is a soft hint: "Arma Reforger" still comes back as "Armour Forger",
/// "Charge Bee" as "Charge B". An exact rewrite rule only fixes the exact
/// mis-hearing the user happened to write down.
///
/// This pass compares every 1–3 word run of the transcript against the
/// canonical terms and swaps in the correct spelling when the run is close
/// enough — close by edit distance, and much more readily when the two also
/// sound alike (Soundex). Ported from Handy's audio_toolkit/text.rs, whose
/// combination of Levenshtein + Soundex + n-grams is the part worth having.
///
/// Deliberately conservative: ASCII-only keys, a length-ratio guard, and a
/// threshold well under half an edit per character, so ordinary prose is left
/// alone.
/// </summary>
public static class VocabularyMatcher
{
    /// <summary>
    /// Maximum combined score that still counts as a match. Handy ships 0.18;
    /// with the Soundex weighting below that means "up to ~18 % of the letters
    /// differ, or up to ~60 % differ when it also sounds the same".
    /// </summary>
    public const double DefaultThreshold = 0.18;

    /// <summary>Longest run of transcript words considered as one term.</summary>
    private const int MaxNgram = 3;

    /// <summary>A phonetic match is treated as this fraction of its edit distance.</summary>
    private const double PhoneticWeight = 0.3;

    /// <summary>
    /// Soundex only keeps an initial and three consonant codes, so genuinely
    /// different words collide ("workshop" and "Workbench" are both W621).
    /// The phonetic discount is therefore only allowed to rescue a candidate
    /// that is already in the right neighbourhood — under this share of its
    /// letters differing. Without the cap, "we are going to the workshop"
    /// became "…to the Workbench".
    /// </summary>
    private const double MaxPhoneticEditRatio = 0.45;

    /// <summary>Ignore candidates longer than this — they are never single terms.</summary>
    private const int MaxCandidateLength = 50;

    /// <summary>
    /// Terms shorter than this are matched by the exact rules and the initial
    /// prompt only. A three-letter key fuzzy-matches half the language.
    /// </summary>
    private const int MinKeyLength = 4;

    private readonly record struct TermKey(int TermIndex, string Key);

    /// <summary>
    /// A form the model is known to produce instead of the right one — the
    /// "from" side of a rewrite rule the user wrote — and what it should be.
    /// </summary>
    public readonly record struct Alias(string Heard, string Write);

    /// <summary>
    /// Rewrite runs of <paramref name="text"/> that are near-misses of one of
    /// <paramref name="terms"/>. Punctuation around a run and the capitalisation
    /// of its first word are preserved.
    /// </summary>
    /// <param name="aliases">
    /// Known mis-hearings, matched fuzzily as well and replaced with their
    /// canonical spelling. This is what makes one rule cover a family: a user
    /// who wrote "Armor Forger → Arma Reforger" also gets "Armour Forger",
    /// "Arm of Forger" and "Armored Forger" for free, because the recorded
    /// mis-hearing is a much closer anchor to the next mis-hearing than the
    /// correct spelling is.
    /// </param>
    public static string Apply(string text, IReadOnlyList<string> terms,
        IReadOnlyList<Alias>? aliases = null, double threshold = DefaultThreshold)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (terms.Count == 0 && aliases is not { Count: > 0 }) return text;

        // Targets are indexed once: the terms themselves, then each alias
        // pointing at the spelling it should become.
        var targets = new List<string>(terms);
        var keys = new List<TermKey>(terms.Count + 2);
        for (var i = 0; i < terms.Count; i++) AddKeys(keys, terms[i], i);

        if (aliases is { Count: > 0 })
        {
            foreach (var alias in aliases)
            {
                if (string.IsNullOrWhiteSpace(alias.Heard) || string.IsNullOrWhiteSpace(alias.Write))
                {
                    continue;
                }
                targets.Add(alias.Write);
                AddKeys(keys, alias.Heard, targets.Count - 1);
            }
        }
        if (keys.Count == 0) return text;
        var terms2 = (IReadOnlyList<string>)targets;

        // Work line by line so "new paragraph" breaks are not swallowed.
        var lines = text.Split('\n');
        for (var l = 0; l < lines.Length; l++)
        {
            lines[l] = ApplyToLine(lines[l], terms2, keys, threshold);
        }
        return string.Join("\n", lines);
    }

    private static string ApplyToLine(string line, IReadOnlyList<string> terms,
        List<TermKey> keys, double threshold)
    {
        var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return line;

        var result = new List<string>(words.Length);
        var runKeys = new string?[MaxNgram];
        var i = 0;
        while (i < words.Length)
        {
            var (bestTerm, bestScore, bestSpan) = BestAt(words, i, terms, keys, threshold, runKeys);

            // A multi-word run must not swallow the word in front of the term.
            // "an Arma Reforger" is within the threshold of "Arma Reforger"
            // (two extra letters in fourteen), and because runs are scored from
            // where they start, "an" used to vanish: "we need an Arma Reforger
            // mod" came out as "we need Arma Reforger mod", and "I bought a
            // workbench" as "I bought Workbench". When the run starting one word
            // later matches at least as well, the first word is ordinary text;
            // the next position takes the match. A genuinely split mishearing
            // ("get hub" → GitHub, "Arm a Reforger") has no such shorter run.
            if (bestTerm != null && bestSpan >= 2 && i + 1 < words.Length
                && BestAt(words, i + 1, terms, keys, threshold, runKeys).Score <= bestScore)
            {
                bestTerm = null;
            }

            if (bestTerm != null)
            {
                var (prefix, _) = SplitPunctuation(words[i]);
                var (_, suffix) = SplitPunctuation(words[i + bestSpan - 1]);
                result.Add(prefix + PreserveCase(words[i], bestTerm) + suffix);
                i += bestSpan;
            }
            else
            {
                result.Add(words[i]);
                i++;
            }
        }
        return string.Join(" ", result);
    }

    /// <summary>
    /// The closest match among the runs starting at word <paramref name="i"/>
    /// (term, score, width in words), or a null term. Every width is scored
    /// and the closest wins: longest-first would let "Charge B, che" swallow
    /// the following word when both share a Soundex code.
    /// <paramref name="runKeys"/> is scratch space.
    /// </summary>
    private static (string? Term, double Score, int Span) BestAt(string[] words, int i,
        IReadOnlyList<string> terms, List<TermKey> keys, double threshold, string?[] runKeys)
    {
        string? bestTerm = null;
        var bestScore = double.MaxValue;
        var bestSpan = 0;
        RunKeysAt(words, i, runKeys);
        for (var n = MaxNgram; n >= 1; n--)
        {
            var candidate = runKeys[n - 1];
            if (candidate == null) continue;
            var match = FindBest(candidate, terms, keys, threshold);
            if (match != null && match.Value.Score < bestScore)
            {
                bestTerm = match.Value.Term;
                bestScore = match.Value.Score;
                bestSpan = n;
            }
        }
        return (bestTerm, bestScore, bestSpan);
    }

    /// <summary>
    /// The keys of the 1…<see cref="MaxNgram"/>-word runs that start at word
    /// <paramref name="i"/>, as <see cref="ApplyToLine"/> scores them:
    /// <paramref name="into"/>[n − 1] is the n-word run, or null where that run
    /// is never considered. Shared with <see cref="RunIndex"/> so the
    /// "would this term rewrite anything?" check can never drift from what
    /// Apply really does.
    /// </summary>
    private static void RunKeysAt(string[] words, int i, string?[] into)
    {
        for (var n = 1; n <= MaxNgram; n++)
        {
            into[n - 1] = null;
            if (i + n > words.Length) continue;
            // A comma or full stop closes the candidate: "Charge B, che" is
            // two things, not one.
            if (SpansPunctuation(words, i, n)) continue;
            // An address or URL is a single opaque thing; rewriting a piece
            // of it is always wrong.
            if (SpansProtectedToken(words, i, n)) continue;
            into[n - 1] = BuildKey(words, i, n);
        }
    }

    // ----- collision check (for "Learn from your writing") -----

    /// <summary>
    /// Whether adding <paramref name="term"/> as a vocabulary word would make
    /// <see cref="Apply"/> rewrite something in <paramref name="text"/> — a run
    /// that is not simply the term itself in other capitals or spacing
    /// ("wispr", "Wis pr" for "Wispr" are fine; "whisper", "army" for "Arma",
    /// "prefabs" for "prefab" are not).
    ///
    /// A learned word is a fuzzy-match target on every later dictation, so a
    /// word the user really writes that sits within the threshold of it is
    /// silently rewritten from then on. "Learn from your writing" asks this
    /// before it pre-ticks a word. Pure; nothing is rewritten. Every place in
    /// the text is checked, including ones an earlier match would have
    /// consumed, so it can only err towards "yes".
    /// </summary>
    internal static bool WouldRewrite(string text, string term) =>
        RunIndex.Build(new[] { text }).PlacesRewrittenBy(term).Count > 0;

    /// <summary>
    /// Every place in a body of text where <see cref="Apply"/> would try a
    /// match, reduced to the keys it would score there. Built once over the
    /// user's history so many candidate terms can be checked against it
    /// cheaply: each distinct run key is scored once per term, and each
    /// distinct place (the keys of its 1-, 2- and 3-word runs) is decided once.
    /// The decision is <see cref="ApplyToLine"/>'s — same runs, same
    /// <see cref="FindBest"/>, same "closest width wins".
    /// </summary>
    internal sealed class RunIndex
    {
        private readonly List<string> _keys = new();
        private readonly Dictionary<string, int> _keyIds = new(StringComparer.Ordinal);

        /// <summary>Letter counts per key (same index as <see cref="_keys"/>), for the edit-distance bound.</summary>
        private readonly List<byte[]> _counts = new();

        /// <summary>Key ids by key length, so a term only scores keys its length guard could pass.</summary>
        private readonly Dictionary<int, List<int>> _idsByLength = new();

        /// <summary>
        /// Distinct places: index into <see cref="_keys"/> per run width (1, 2,
        /// 3 words) at this word, then the same for the next word (Apply's
        /// "don't swallow the word before the term" look-ahead); −1 where none.
        /// </summary>
        private readonly List<int[]> _places = new();

        private RunIndex() { }

        public static RunIndex Build(IEnumerable<string> texts)
        {
            var index = new RunIndex();
            var seen = new HashSet<(int, int, int, int, int, int)>();
            var runKeys = new string?[MaxNgram];
            var nextKeys = new string?[MaxNgram];
            foreach (var text in texts)
            {
                if (string.IsNullOrEmpty(text)) continue;
                foreach (var line in text.Split('\n'))
                {
                    var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    for (var i = 0; i < words.Length; i++)
                    {
                        RunKeysAt(words, i, runKeys);
                        var here = (index.IdOf(runKeys[0]), index.IdOf(runKeys[1]), index.IdOf(runKeys[2]));
                        if (here is (< 0, < 0, < 0)) continue;
                        if (i + 1 < words.Length) RunKeysAt(words, i + 1, nextKeys);
                        else Array.Fill(nextKeys, null);
                        var place = (here.Item1, here.Item2, here.Item3,
                            index.IdOf(nextKeys[0]), index.IdOf(nextKeys[1]), index.IdOf(nextKeys[2]));
                        if (!seen.Add(place)) continue;
                        index._places.Add(new[] { place.Item1, place.Item2, place.Item3, place.Item4, place.Item5, place.Item6 });
                    }
                }
            }
            return index;
        }

        /// <summary>A key FindBest could ever match gets an id; anything else is −1.</summary>
        private int IdOf(string? key)
        {
            if (key == null || !IsSupportedKey(key) || key.Length > MaxCandidateLength) return -1;
            if (_keyIds.TryGetValue(key, out var id)) return id;
            id = _keys.Count;
            _keys.Add(key);
            _counts.Add(LetterCounts(key));
            _keyIds[key] = id;
            if (!_idsByLength.TryGetValue(key.Length, out var sameLength)) _idsByLength[key.Length] = sameLength = new List<int>();
            sameLength.Add(id);
            return id;
        }

        /// <summary>
        /// The places where <c>Apply(text, [term])</c> would replace a run that
        /// is not the term itself (same <see cref="MatchKey"/>), as indices for
        /// <see cref="IsProtected"/>.
        /// </summary>
        public IReadOnlyList<int> PlacesRewrittenBy(string term)
        {
            var termKeys = new List<TermKey>(2);
            AddKeys(termKeys, term, 0);
            if (termKeys.Count == 0 || _places.Count == 0) return Array.Empty<int>();

            var own = MatchKey(term);
            var terms = new[] { term };
            // Two cheap tests skip most keys before FindBest's edit distance.
            // Both are loose on purpose: whatever passes, FindBest decides.
            // 1. A copy of its length guard (plus one).
            // 2. Letter counts: a substitution changes the count difference by
            //    at most 2 and an insert or delete by 1, so the edit distance is
            //    at least half of it. No match is possible once that bound is
            //    past the phonetic cap — the most any match may differ.
            var termCounts = termKeys.Select(t => (t.Key.Length, Counts: LetterCounts(t.Key))).ToList();
            var maxRatio = Math.Max(DefaultThreshold, MaxPhoneticEditRatio);
            var scores = new double[_keys.Count];
            Array.Fill(scores, double.MaxValue);
            foreach (var (length, ids) in _idsByLength)
            {
                if (!termCounts.Any(t => Math.Abs(length - t.Length) <= Math.Max(Math.Max(length, t.Length) * 0.25, 2.0) + 1)) continue;
                foreach (var id in ids)
                {
                    var key = _keys[id];
                    var keyCounts = _counts[id];
                    var possible = false;
                    foreach (var (termLength, counts) in termCounts)
                    {
                        var difference = 0;
                        for (var slot = 0; slot < CountSlots; slot++) difference += Math.Abs(counts[slot] - keyCounts[slot]);
                        if ((difference + 1) / 2 <= maxRatio * Math.Max(key.Length, termLength) + 1e-9)
                        {
                            possible = true;
                            break;
                        }
                    }
                    if (possible) scores[id] = FindBest(key, terms, termKeys, DefaultThreshold)?.Score ?? double.MaxValue;
                }
            }

            var hits = new List<int>();
            for (var p = 0; p < _places.Count; p++)
            {
                var ids = _places[p];
                var (bestId, bestScore, bestWidth) = Best(ids, 0, scores);
                if (bestId < 0) continue;
                // ApplyToLine's look-ahead: a multi-word run gives way when the
                // run one word later matches at least as well.
                if (bestWidth >= 2 && Best(ids, MaxNgram, scores).Score <= bestScore) continue;
                if (!string.Equals(_keys[bestId], own, StringComparison.Ordinal)) hits.Add(p);
            }
            return hits;
        }

        /// <summary>
        /// <see cref="BestAt"/> over precomputed scores: the closest of the three
        /// widths stored at <paramref name="offset"/> (0 = this word, 3 = the next).
        /// </summary>
        private static (int Id, double Score, int Width) Best(int[] ids, int offset, double[] scores)
        {
            var bestScore = double.MaxValue;
            var bestId = -1;
            var bestWidth = 0;
            for (var n = MaxNgram; n >= 1; n--)
            {
                var id = ids[offset + n - 1];
                if (id < 0 || scores[id] >= bestScore) continue;
                bestScore = scores[id];
                bestId = id;
                bestWidth = n;
            }
            return (bestId, bestScore, bestWidth);
        }

        /// <summary>Supported keys are a–z and 0–9 only (<see cref="IsSupportedKey"/>).</summary>
        private const int CountSlots = 36;

        private static int Slot(char c) => c <= '9' ? 26 + (c - '0') : c - 'a';

        /// <summary>How often each letter and digit occurs; keys are at most 50 characters, so a byte holds it.</summary>
        private static byte[] LetterCounts(string key)
        {
            var counts = new byte[CountSlots];
            foreach (var c in key) counts[Slot(c)]++;
            return counts;
        }

        /// <summary>
        /// Whether a run at <paramref name="place"/> is exactly one of
        /// <paramref name="exactKeys"/> (<see cref="MatchKey"/> form). Such a run
        /// matches its own term with a score of 0, which no other term can
        /// beat, so a term in the same list cannot rewrite it.
        /// </summary>
        public bool IsProtected(int place, IReadOnlySet<string> exactKeys)
        {
            var ids = _places[place];
            for (var n = 0; n < MaxNgram; n++)   // this word's runs only, not the look-ahead
            {
                var id = ids[n];
                if (id >= 0 && exactKeys.Contains(_keys[id])) return true;
            }
            return false;
        }
    }

    // ----- term keys -----

    private static void AddKeys(List<TermKey> keys, string term, int index)
    {
        var primary = MatchKey(term);
        if (IsSupportedKey(primary)) keys.Add(new TermKey(index, primary));

        // "R&D" is dictated as "R and D"; index both spellings.
        if (term.Contains('&'))
        {
            var expanded = MatchKey(term.Replace("&", " and "));
            if (IsSupportedKey(expanded) && expanded != primary)
            {
                keys.Add(new TermKey(index, expanded));
            }
        }
    }

    /// <summary>Letters and digits only, lower-cased, spaces dropped — so
    /// "Charge B" and "ChargeBee" compare as "chargeb" vs "chargebee".</summary>
    internal static string MatchKey(string word)
    {
        var sb = new StringBuilder(word.Length);
        foreach (var c in word)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>
    /// The fallback matcher is ASCII-only on purpose: whitespace tokenisation
    /// and Soundex are meaningless for CJK, and those terms are already handled
    /// by the initial prompt and exact rules.
    /// </summary>
    private static bool IsSupportedKey(string key) =>
        key.Length >= MinKeyLength && key.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9'));

    private static bool SupportsSoundex(string key) =>
        key.Length > 0 && key.All(c => c is >= 'a' and <= 'z');

    // ----- matching -----

    private readonly record struct Match(string Term, double Score);

    private static Match? FindBest(string candidate, IReadOnlyList<string> terms,
        List<TermKey> keys, double threshold)
    {
        if (!IsSupportedKey(candidate) || candidate.Length > MaxCandidateLength) return null;

        string? best = null;
        var bestScore = double.MaxValue;

        foreach (var key in keys)
        {
            // Percentage-based length guard: without it a long n-gram happily
            // matches a much shorter term ("openaigpt" → "openai").
            var maxLen = Math.Max(candidate.Length, key.Key.Length);
            var allowed = Math.Max(maxLen * 0.25, 2.0);
            if (Math.Abs(candidate.Length - key.Key.Length) > allowed) continue;

            var distance = Levenshtein(candidate, key.Key);
            var score = maxLen > 0 ? (double)distance / maxLen : 1.0;

            // Soundex is an English phonetic algorithm; a term with digits can
            // still match on edit distance but must not get the phonetic boost.
            if (score <= MaxPhoneticEditRatio
                && SupportsSoundex(candidate) && SupportsSoundex(key.Key)
                && PhoneticKey(candidate) == PhoneticKey(key.Key))
            {
                score *= PhoneticWeight;
            }

            if (score < threshold && score < bestScore)
            {
                best = terms[key.TermIndex];
                bestScore = score;
            }
        }

        return best == null ? null : new Match(best, bestScore);
    }

    /// <summary>
    /// Soundex's consonant coding, but kept at full length instead of the
    /// classic initial-plus-three. The truncation is what makes textbook
    /// Soundex collide on words that merely start alike — "workplace",
    /// "workshop" and "Workbench" are all W621 — and every one of those
    /// collisions showed up as a wrong correction in the prose corpus.
    /// Comparing the whole skeleton keeps the phonetic idea ("sounds the same
    /// even though it is spelled differently") without the false friends.
    /// </summary>
    internal static string PhoneticKey(string word)
    {
        if (word.Length == 0) return "";
        var sb = new StringBuilder(word.Length);
        sb.Append(char.ToUpperInvariant(word[0]));
        var previous = Code(word[0]);

        for (var i = 1; i < word.Length; i++)
        {
            var code = Code(word[i]);
            if (code == '0')
            {
                // A vowel separates two identical codes; h and w are transparent.
                var c = char.ToLowerInvariant(word[i]);
                if (c is not ('h' or 'w')) previous = '0';
                continue;
            }
            if (code != previous) sb.Append(code);
            previous = code;
        }
        return sb.ToString();
    }

    private static char Code(char c) => char.ToLowerInvariant(c) switch
    {
        'b' or 'f' or 'p' or 'v' => '1',
        'c' or 'g' or 'j' or 'k' or 'q' or 's' or 'x' or 'z' => '2',
        'd' or 't' => '3',
        'l' => '4',
        'm' or 'n' => '5',
        'r' => '6',
        _ => '0',
    };

    /// <summary>Edit distance, two-row variant (keys are short).</summary>
    internal static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }

    // ----- word plumbing -----

    private static string BuildKey(string[] words, int start, int count)
    {
        var sb = new StringBuilder();
        for (var k = 0; k < count; k++) sb.Append(MatchKey(words[start + k]));
        return sb.ToString();
    }

    /// <summary>True if any word except the last carries trailing punctuation.</summary>
    private static bool SpansPunctuation(string[] words, int start, int count)
    {
        for (var k = 0; k < count - 1; k++)
        {
            if (SplitPunctuation(words[start + k]).Suffix.Length > 0) return true;
        }
        return false;
    }

    private static bool SpansProtectedToken(string[] words, int start, int count)
    {
        for (var k = 0; k < count; k++)
        {
            if (IsProtectedToken(words[start + k])) return true;
        }
        return false;
    }

    /// <summary>Email addresses, URLs and paths must never be partially rewritten.</summary>
    internal static bool IsProtectedToken(string word)
    {
        if (word.Contains("://") || word.Contains('/') || word.Contains('\\')) return true;
        var at = word.IndexOf('@');
        if (at > 0 && at + 1 < word.Length) return true;
        return word.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
    }

    internal static (string Prefix, string Suffix) SplitPunctuation(string word)
    {
        var first = 0;
        while (first < word.Length && !char.IsLetterOrDigit(word[first])) first++;
        if (first == word.Length) return (word, "");   // all punctuation

        var last = word.Length - 1;
        while (last >= 0 && !char.IsLetterOrDigit(word[last])) last--;

        return (word[..first], word[(last + 1)..]);
    }

    /// <summary>ALL CAPS stays caps, Title stays title, anything else takes the term as written.</summary>
    internal static string PreserveCase(string original, string replacement)
    {
        var letters = original.Where(char.IsLetter).ToArray();
        if (letters.Length > 1 && letters.All(char.IsUpper)) return replacement.ToUpperInvariant();
        if (letters.Length > 0 && char.IsUpper(letters[0]) && replacement.Length > 0
            && char.IsLower(replacement[0]))
        {
            return char.ToUpperInvariant(replacement[0]) + replacement[1..];
        }
        return replacement;
    }
}
