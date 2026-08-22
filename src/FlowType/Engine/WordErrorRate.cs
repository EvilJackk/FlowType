using System.Text.RegularExpressions;

namespace FlowType.Engine;

/// <summary>
/// Word error rate for the --bench harness: word-level Levenshtein distance
/// between a reference and a hypothesis after light normalisation (case,
/// punctuation, apostrophes). Deliberately simple — it only has to rank
/// configurations against each other on the same clips.
/// </summary>
public static class WordErrorRate
{
    /// <summary>(substitutions + insertions + deletions, reference word count)</summary>
    public static (int Errors, int Words) Count(string reference, string hypothesis)
    {
        var r = Normalize(reference);
        var h = Normalize(hypothesis);
        var d = new int[r.Length + 1, h.Length + 1];
        for (var i = 0; i <= r.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= h.Length; j++) d[0, j] = j;
        for (var i = 1; i <= r.Length; i++)
        {
            for (var j = 1; j <= h.Length; j++)
            {
                var cost = r[i - 1] == h[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }
        return (d[r.Length, h.Length], r.Length);
    }

    public static string[] Normalize(string text)
    {
        var t = text.ToLowerInvariant().Replace("'", "").Replace("’", "");
        t = Regex.Replace(t, @"[^a-z0-9]+", " ");
        return t.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }
}
