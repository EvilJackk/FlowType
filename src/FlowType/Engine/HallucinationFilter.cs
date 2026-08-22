using System.Text.RegularExpressions;

namespace FlowType.Engine;

/// <summary>
/// Guards against the two ways whisper fills a gap it cannot hear, both
/// reproduced on unintelligible clips by the 1.3.0 benchmark:
///
///   * echoing the initial prompt back ("The following is American English,
///     and the following is American English, …" — or the vocabulary list);
///   * looping a phrase ("the quarter of the quarter of the quarter…", or the
///     same sentence twice).
///
/// Rare on real speech, but one occurrence types garbage into a document.
/// Runs on the raw transcript before the formatter.
/// </summary>
public static class HallucinationFilter
{
    private const int MinRepeatsForCollapse = 3;   // "very very very" → "very"
    private const int MaxNgram = 10;
    private const int MinDuplicateSentenceWords = 5;

    /// <summary>Consecutive prompt words, in prompt order, that count as an echo run.</summary>
    internal const int MinPromptRun = 4;

    /// <summary>
    /// Vocabulary is the user's own names; saying two of them back to back is
    /// legitimate, so reciting the list only counts from six words on.
    /// </summary>
    internal const int MinVocabularyRun = 6;

    /// <param name="promptSentence">The fixed conditioning sentence (English variant), if any.</param>
    /// <param name="vocabulary">Dictionary words that were appended to the prompt.</param>
    public static string Apply(string text, string? promptSentence, IReadOnlyList<string>? vocabulary = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var result = CollapseRepeats(text);
        result = CollapseDuplicateSentences(result);
        if (!string.IsNullOrWhiteSpace(promptSentence))
        {
            result = StripPromptEcho(result, promptSentence, MinPromptRun);
        }
        if (vocabulary is { Count: > 0 })
        {
            result = StripPromptEcho(result, string.Join(" ", vocabulary), MinVocabularyRun);
        }
        // Room noise sometimes decodes to a lone "." or "-": nothing to type.
        if (!result.Any(char.IsLetterOrDigit)) return "";
        return result.Trim();
    }

    /// <summary>Collapse any n-gram (n ≤ 10) repeated three or more times in a row.</summary>
    internal static string CollapseRepeats(string text)
    {
        var tokens = Tokenize(text);
        var changed = true;
        while (changed)
        {
            changed = false;
            for (var n = 1; n <= MaxNgram && !changed; n++)
            {
                for (var i = 0; i + n * MinRepeatsForCollapse <= tokens.Count; i++)
                {
                    var repeats = 1;
                    while (i + (repeats + 1) * n <= tokens.Count && SameRun(tokens, i, i + repeats * n, n))
                    {
                        repeats++;
                    }
                    if (repeats >= MinRepeatsForCollapse)
                    {
                        // Keep the first occurrence, but carry the last one's
                        // trailing punctuation so sentences still end properly.
                        var last = tokens[i + repeats * n - 1];
                        tokens.RemoveRange(i + n, (repeats - 1) * n);
                        tokens[i + n - 1] = MergePunctuation(tokens[i + n - 1], last);
                        changed = true;
                        break;
                    }
                }
            }
        }
        return string.Join(" ", tokens.Select(t => t.Raw));
    }

    /// <summary>Drop a sentence that repeats the previous one verbatim (5+ words).</summary>
    internal static string CollapseDuplicateSentences(string text)
    {
        var sentences = SplitSentences(text);
        if (sentences.Count < 2) return text;
        var kept = new List<string> { sentences[0] };
        for (var i = 1; i < sentences.Count; i++)
        {
            var a = WordErrorRate.Normalize(sentences[i]);
            var b = WordErrorRate.Normalize(kept[^1]);
            if (a.Length >= MinDuplicateSentenceWords && a.SequenceEqual(b)) continue;
            kept.Add(sentences[i]);
        }
        return kept.Count == sentences.Count ? text : string.Join(" ", kept);
    }

    /// <summary>
    /// Drop a sentence that is mostly made of runs of the prompt's own words in
    /// the prompt's order — the decoder continued our prompt instead of
    /// listening. "Mostly" = runs of at least <paramref name="minRun"/> words
    /// cover 60 % or more of the sentence, so a long genuine sentence that
    /// merely contains a few prompt words survives, while "X, and X, and X"
    /// (the echo pattern actually observed) does not.
    /// </summary>
    internal static string StripPromptEcho(string text, string prompt, int minRun)
    {
        var promptWords = WordErrorRate.Normalize(prompt);
        if (promptWords.Length < minRun) return text;

        var sentences = SplitSentences(text);
        var kept = new List<string>();
        foreach (var sentence in sentences)
        {
            var words = WordErrorRate.Normalize(sentence);
            var covered = EchoCoverage(words, promptWords, minRun);
            var isEcho = words.Length > 0 && covered * 10 >= words.Length * 6;
            if (!isEcho) kept.Add(sentence);
        }
        return kept.Count == sentences.Count ? text : string.Join(" ", kept);
    }

    /// <summary>Number of sentence words that sit inside an ordered prompt run of ≥ minRun words.</summary>
    internal static int EchoCoverage(string[] words, string[] promptWords, int minRun)
    {
        var covered = new bool[words.Length];
        for (var i = 0; i < words.Length; i++)
        {
            for (var j = 0; j < promptWords.Length; j++)
            {
                var k = 0;
                while (i + k < words.Length && j + k < promptWords.Length
                    && words[i + k] == promptWords[j + k])
                {
                    k++;
                }
                if (k >= minRun)
                {
                    for (var c = 0; c < k; c++) covered[i + c] = true;
                }
            }
        }
        return covered.Count(c => c);
    }

    // ----- helpers -----

    private readonly record struct Token(string Raw, string Key);

    private static List<Token> Tokenize(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(raw => new Token(raw, Regex.Replace(raw.ToLowerInvariant(), @"[^\p{L}\p{N}]", "")))
            .ToList();

    private static bool SameRun(List<Token> tokens, int a, int b, int n)
    {
        for (var k = 0; k < n; k++)
        {
            if (tokens[a + k].Key.Length == 0 || tokens[a + k].Key != tokens[b + k].Key) return false;
        }
        return true;
    }

    private static Token MergePunctuation(Token keep, Token last)
    {
        var trailing = Regex.Match(last.Raw, @"[.!?,;:]+$").Value;
        if (trailing.Length == 0 || Regex.IsMatch(keep.Raw, @"[.!?,;:]$")) return keep;
        return keep with { Raw = keep.Raw + trailing };
    }

    private static List<string> SplitSentences(string text) =>
        Regex.Split(text.Trim(), @"(?<=[.!?])\s+")
            .Where(s => s.Length > 0)
            .ToList();
}
