using FlowType.Core;
using FlowType.Engine;

namespace FlowType.Learning;

/// <summary>
/// Suggests a writing style per kind of app from what the user actually
/// did, not from what they might like.
///
/// Two kinds of evidence. What Wispr Flow is set to for that kind of app —
/// the user chose it there, over many dictations (the style it used most, and
/// only while that is still the one it uses now). And what the user's own
/// edits show: a style is mostly about the ends of a sentence (a final full
/// stop, a capital at the start, an exclamation mark), and those are exactly
/// what someone fixes by hand when the style is wrong for them. The edits win
/// when the two disagree: a setting can be left at its default, but deleting
/// the full stop in seven of nine edits is a preference stated nine times.
///
/// An edit only counts toward a signal when it left the word it touches in
/// place — the same last word for a full stop or "!", the same first word for
/// a lowered capital — so rewriting the end of a sentence is never mistaken
/// for a punctuation preference.
/// </summary>
public static class StyleMiner
{
    public const string EditsOrigin = "your edits";
    public const string WisprOrigin = "Wispr Flow settings";

    /// <summary>Bucket ids, as <see cref="SuggestionKind.Style"/> suggestions carry them.</summary>
    public const string Personal = "personal";
    public const string Work = "work";
    public const string Email = "email";
    public const string Other = "other";

    /// <summary>Wispr's choice for a kind of app counts once it covers this many dictations.</summary>
    private const int MinWisprDictations = 10;

    /// <summary>An edit signal needs this many edits where it could have shown…</summary>
    private const int MinEdits = 5;

    /// <summary>…and to show in at least this share of them.</summary>
    private const double MinAgreement = 0.6;

    public static IReadOnlyList<Suggestion> Mine(
        IReadOnlyList<HistorySample> samples, IReadOnlyList<WisprStyleChoice> wisprChoices, AppSettings settings)
    {
        var wispr = WisprEvidence(wisprChoices);
        var edits = EditEvidence(samples);
        var result = new List<Suggestion>();

        foreach (var bucket in new[] { Personal, Work, Email, Other })
        {
            wispr.TryGetValue(bucket, out var w);
            edits.TryGetValue(bucket, out var e);
            var chosen = e ?? w;
            if (chosen == null) continue;
            if (chosen.Style == Current(settings, bucket)) continue;

            var fromEdits = e != null;
            var why = fromEdits
                ? (w != null && w.Style == e!.Style ? $"{e.Why} · {w.Why}" : e!.Why)
                : w!.Why;
            result.Add(new Suggestion(
                SuggestionKind.Style,
                Suggestion.KeyFor(SuggestionKind.Style, bucket, chosen.Style),
                bucket,
                chosen.Style,
                chosen.Sightings,
                fromEdits ? 1.0 + chosen.Share : chosen.Share,
                Recommended: true,
                why,
                fromEdits ? EditsOrigin : WisprOrigin,
                WritingStyles.All.FirstOrDefault(a => a.Id == chosen.Style).Example));
        }
        return result.OrderByDescending(s => s.Score).ToList();
    }

    /// <summary>A style one source of evidence points to for one bucket.</summary>
    private sealed record Finding(string Style, int Sightings, double Share, string Why);

    /// <summary>The style FlowType uses for <paramref name="bucket"/> today.</summary>
    internal static string Current(AppSettings settings, string bucket) => WritingStyles.Normalize(bucket switch
    {
        Personal => settings.StylePersonal,
        Work => settings.StyleWork,
        Email => settings.StyleEmail,
        _ => settings.StyleOther,
    });

    /// <summary>The bucket FlowType files an app under ("claude" → other, "slack" → work).</summary>
    internal static string BucketOf(string app)
    {
        var name = (app ?? "").Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return WritingStyles.BucketFor(AppCategory.Classify(name)) switch
        {
            WritingStyles.Bucket.Personal => Personal,
            WritingStyles.Bucket.Work => Work,
            WritingStyles.Bucket.Email => Email,
            _ => Other,
        };
    }

    // ----- Wispr's settings -----

    /// <summary>Wispr's app type → FlowType bucket. Its "other" covers what FlowType's does.</summary>
    internal static string BucketOfWisprAppType(string appType) => (appType ?? "").Trim().ToLowerInvariant() switch
    {
        "personal" => Personal,
        "work" => Work,
        "email" => Email,
        _ => Other,
    };

    /// <summary>Wispr's style name → a <see cref="WritingStyles"/> id, or null when unknown.</summary>
    internal static string? StyleOfWispr(string style) => (style ?? "").Trim().ToLowerInvariant() switch
    {
        "formal" => WritingStyles.Polished,
        "casual" => WritingStyles.Casual,
        "very_casual" or "verycasual" or "very-casual" or "very casual" => WritingStyles.VeryCasual,
        "excited" => WritingStyles.Excited,
        _ => null,
    };

    /// <summary>
    /// Per bucket, the style Wispr applied most, once the bucket holds at least
    /// <see cref="MinWisprDictations"/> dictations with a style FlowType knows.
    /// A tie goes to the style used most recently.
    ///
    /// The most-used style only counts while it is also the one Wispr applied
    /// last, i.e. Wispr's current setting. A user who ran Wispr on Formal for
    /// 500 dictations and then switched to Casual has told us Casual; offering
    /// Formal back, pre-ticked, would undo that choice. A bucket like that gives
    /// no Wispr-based suggestion (the user's own edits still can).
    /// </summary>
    private static Dictionary<string, Finding> WisprEvidence(IReadOnlyList<WisprStyleChoice> choices)
    {
        var result = new Dictionary<string, Finding>(StringComparer.Ordinal);
        var perBucket = choices
            .Select(c => (Bucket: BucketOfWisprAppType(c.AppType), Style: StyleOfWispr(c.Style), c.Count, c.LastUsedUtc))
            .Where(c => c.Style != null && c.Count > 0)
            .GroupBy(c => c.Bucket);

        foreach (var bucket in perBucket)
        {
            var byStyle = bucket
                .GroupBy(c => c.Style!)
                .Select(g => (Style: g.Key, Count: g.Sum(c => c.Count), Last: g.Max(c => c.LastUsedUtc)))
                .OrderByDescending(s => s.Count)
                .ThenByDescending(s => s.Last)
                .ToList();
            var total = byStyle.Sum(s => s.Count);
            if (total < MinWisprDictations) continue;
            var top = byStyle[0];
            if (byStyle.Any(s => s.Last > top.Last)) continue;   // switched away from it since
            var label = WritingStyles.All.First(a => a.Id == top.Style).Label;
            var why = top.Count == total
                ? $"Wispr Flow was set to {label} for all {total} dictations in these apps"
                : $"Wispr Flow was set to {label} for {top.Count} of {total} dictations in these apps";
            result[bucket.Key] = new Finding(top.Style, top.Count, (double)top.Count / total, why);
        }
        return result;
    }

    // ----- The user's own edits -----

    private sealed class EditCounts
    {
        public int Edited;
        public int StopApplicable, StopRemoved;
        public int NoStopApplicable, StopAdded;
        public int CapitalApplicable, Lowered;
        public int ExclaimApplicable, Exclaimed;
    }

    /// <summary>
    /// Per bucket, the style the user's edits point to, if one signal is
    /// clear: an added "!" → excited; a lowered first letter → very casual;
    /// a removed final full stop → casual; an added one → formal. Each needs
    /// <see cref="MinEdits"/> edits where it could have shown and
    /// <see cref="MinAgreement"/> of them showing it.
    /// </summary>
    private static Dictionary<string, Finding> EditEvidence(IReadOnlyList<HistorySample> samples)
    {
        var counts = new Dictionary<string, EditCounts>(StringComparer.Ordinal);
        foreach (var s in samples)
        {
            if (string.IsNullOrWhiteSpace(s.Edited) || string.IsNullOrWhiteSpace(s.Final)) continue;
            var bucket = BucketOf(s.App);
            if (!counts.TryGetValue(bucket, out var c)) counts[bucket] = c = new EditCounts();
            Count(c, s.Final.Trim(), s.Edited!.Trim());
        }

        var result = new Dictionary<string, Finding>(StringComparer.Ordinal);
        foreach (var (bucket, c) in counts)
        {
            if (c.Edited < MinEdits) continue;
            var finding =
                Signal(WritingStyles.Excited, c.Exclaimed, c.ExclaimApplicable, "ended with “!”")
                ?? Signal(WritingStyles.VeryCasual, c.Lowered, c.CapitalApplicable, "started with a small letter")
                ?? Signal(WritingStyles.Casual, c.StopRemoved, c.StopApplicable, "removed the final full stop")
                ?? Signal(WritingStyles.Polished, c.StopAdded, c.NoStopApplicable, "added a final full stop");
            if (finding != null) result[bucket] = finding;
        }
        return result;
    }

    private static Finding? Signal(string style, int shown, int applicable, string what)
    {
        if (applicable < MinEdits) return null;
        var share = (double)shown / applicable;
        if (share < MinAgreement) return null;
        return new Finding(style, applicable, share, $"You {what} in {shown} of {applicable} edits");
    }

    /// <summary>
    /// One edited dictation's votes. Only the ends are compared, and only when
    /// the word at that end survived the edit.
    /// </summary>
    private static void Count(EditCounts c, string final, string edited)
    {
        c.Edited++;

        if (SameKey(LastWord(final), LastWord(edited)))
        {
            var finalStop = EndsWithFullStop(final);
            var finalOpen = !finalStop && char.IsLetterOrDigit(final[^1]);
            var editedStop = EndsWithFullStop(edited);
            var editedOpen = !editedStop && char.IsLetterOrDigit(edited[^1]);

            if (finalStop)
            {
                c.StopApplicable++;
                if (editedOpen) c.StopRemoved++;
            }
            else if (finalOpen)
            {
                c.NoStopApplicable++;
                if (editedStop) c.StopAdded++;
            }

            if (!final.EndsWith('!'))
            {
                c.ExclaimApplicable++;
                if (edited.EndsWith('!')) c.Exclaimed++;
            }
        }

        var firstFinal = FirstWord(final);
        var firstEdited = FirstWord(edited);
        if (firstFinal.Length > 0 && SameKey(firstFinal, firstEdited)
            && char.IsUpper(firstFinal[0]) && !IsAlwaysCapital(firstFinal))
        {
            c.CapitalApplicable++;
            if (char.IsLower(firstEdited[0])) c.Lowered++;
        }
    }

    /// <summary>
    /// "I", acronyms and mixed case keep their capital in every style, so their
    /// first letter says nothing about one.
    /// </summary>
    private static bool IsAlwaysCapital(string word) =>
        word == "I" || word.StartsWith("I'", StringComparison.Ordinal) || word.StartsWith("I’", StringComparison.Ordinal)
        || word.Skip(1).Any(char.IsUpper);

    /// <summary>The text ends in a sentence's full stop (not "…", "etc." or "U.S."), as styles judge it.</summary>
    private static bool EndsWithFullStop(string trimmed) =>
        trimmed.Length > 0 && WritingStyles.DropFinalPeriod(trimmed).Length == trimmed.Length - 1;

    /// <summary>First word, punctuation peeled from its start and end.</summary>
    private static string FirstWord(string text)
    {
        var i = 0;
        while (i < text.Length && !char.IsLetterOrDigit(text[i])) i++;
        var start = i;
        while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
        return TrimWord(text[start..i]);
    }

    private static string LastWord(string text)
    {
        var end = text.Length;
        while (end > 0 && !char.IsLetterOrDigit(text[end - 1])) end--;
        var start = end;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        return TrimWord(text[start..end]);
    }

    private static string TrimWord(string w)
    {
        var a = 0;
        while (a < w.Length && !char.IsLetterOrDigit(w[a])) a++;
        var b = w.Length;
        while (b > a && !char.IsLetterOrDigit(w[b - 1])) b--;
        return w[a..b];
    }

    private static bool SameKey(string a, string b) =>
        a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
