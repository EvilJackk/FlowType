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
    /// Rewrite runs of <paramref name="text"/> that are near-misses of one of
    /// <paramref name="terms"/>. Punctuation around a run and the capitalisation
    /// of its first word are preserved.
    /// </summary>
    public static string Apply(string text, IReadOnlyList<string> terms, double threshold = DefaultThreshold)
    {
        if (string.IsNullOrEmpty(text) || terms.Count == 0) return text;

        var keys = new List<TermKey>(terms.Count + 2);
        for (var i = 0; i < terms.Count; i++) AddKeys(keys, terms[i], i);
        if (keys.Count == 0) return text;

        // Work line by line so "new paragraph" breaks are not swallowed.
        var lines = text.Split('\n');
        for (var l = 0; l < lines.Length; l++)
        {
            lines[l] = ApplyToLine(lines[l], terms, keys, threshold);
        }
        return string.Join("\n", lines);
    }

    private static string ApplyToLine(string line, IReadOnlyList<string> terms,
        List<TermKey> keys, double threshold)
    {
        var words = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return line;

        var result = new List<string>(words.Length);
        var i = 0;
        while (i < words.Length)
        {
            string? bestTerm = null;
            var bestScore = double.MaxValue;
            var bestSpan = 0;

            // Longest first would let "Charge B, che" swallow the following
            // word when both share a Soundex code, so every width is scored and
            // the closest wins.
            for (var n = MaxNgram; n >= 1; n--)
            {
                if (i + n > words.Length) continue;
                // A comma or full stop closes the candidate: "Charge B, che" is
                // two things, not one.
                if (SpansPunctuation(words, i, n)) continue;
                // An address or URL is a single opaque thing; rewriting a piece
                // of it is always wrong.
                if (SpansProtectedToken(words, i, n)) continue;

                var candidate = BuildKey(words, i, n);
                var match = FindBest(candidate, terms, keys, threshold);
                if (match != null && match.Value.Score < bestScore)
                {
                    bestTerm = match.Value.Term;
                    bestScore = match.Value.Score;
                    bestSpan = n;
                }
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
