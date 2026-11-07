using FlowType.Core;
using FlowType.Engine;
using FlowType.Learning;

namespace FlowType.UI.Controls;

/// <summary>
/// The plain-language wording "Learn from your writing" shares between the
/// Insights card and the review window, kept in one place so the two never
/// describe the same thing differently ("32 min of audio" on one, "0.5 h" on
/// the other). Pure formatting: no I/O, nothing here reads history.
/// </summary>
internal static class LearnFormat
{
    /// <summary>"45 s", "32 min", "2.5 h" — a length a person reads at a glance.</summary>
    public static string Duration(double seconds)
    {
        if (seconds < 90) return $"{Math.Max(0, Math.Round(seconds)):0} s";
        var minutes = seconds / 60.0;
        return minutes < 90 ? $"{Math.Round(minutes):0} min" : $"{minutes / 60.0:0.#} h";
    }

    /// <summary>"today at 14:02", "yesterday at 09:15", "on Monday", "on 3 Oct" — in the user's own clock and culture.</summary>
    public static string When(DateTime utc)
    {
        var local = (utc.Kind == DateTimeKind.Local ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime());
        var days = (DateTime.Today - local.Date).TotalDays;
        if (days < 1) return $"today at {local:t}";
        if (days < 2) return $"yesterday at {local:t}";
        if (days < 7) return $"on {local:dddd}";
        return local.Year == DateTime.Today.Year ? $"on {local:d MMM}" : $"on {local:d MMM yyyy}";
    }

    /// <summary>How a source is named in the UI.</summary>
    public static string SourceName(HistorySource source) => source switch
    {
        HistorySource.WisprFlow => "Wispr Flow history",
        _ => "FlowType history",
    };

    /// <summary>
    /// A source that can actually be learned from: it opened and holds at
    /// least one dictation. An empty FlowType History is "available" to the
    /// reader but useless to tick.
    /// </summary>
    public static bool IsUsable(SourceSummary s) => s.Available && s.Dictations > 0;

    /// <summary>
    /// The style bucket ids the miners use ("personal", "work", "email",
    /// "other") as the Style page labels them, so a suggestion reads the same
    /// as the setting it would change.
    /// </summary>
    public static string BucketLabel(string bucketId) => bucketId.Trim().ToLowerInvariant() switch
    {
        "personal" => WritingStyles.BucketLabel(WritingStyles.Bucket.Personal),
        "work" => WritingStyles.BucketLabel(WritingStyles.Bucket.Work),
        "email" => WritingStyles.BucketLabel(WritingStyles.Bucket.Email),
        "other" => WritingStyles.BucketLabel(WritingStyles.Bucket.Other),
        _ => Capitalise(bucketId.Trim()),
    };

    /// <summary>
    /// A style id's label. Deliberately an exact match rather than
    /// <see cref="WritingStyles.Normalize"/>, which maps anything unknown to
    /// "formal" — showing "Formal" for an id the miner got wrong would hide the
    /// mistake behind a plausible word.
    /// </summary>
    public static string StyleLabel(string styleId)
    {
        var id = styleId.Trim();
        foreach (var (known, label, _) in WritingStyles.All)
        {
            if (string.Equals(known, id, StringComparison.OrdinalIgnoreCase)) return label;
        }
        return Capitalise(id.Replace('_', ' ').Replace('-', ' '));
    }

    /// <summary>The style currently chosen for a bucket id, or "" for an id FlowType doesn't have.</summary>
    public static string CurrentStyle(string bucketId, AppSettings settings) => bucketId.Trim().ToLowerInvariant() switch
    {
        "personal" => settings.StylePersonal,
        "work" => settings.StyleWork,
        "email" => settings.StyleEmail,
        "other" => settings.StyleOther,
        _ => "",
    };

    public static string Capitalise(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // ----- The review window's decisions, kept pure so --selftest can check them -----

    /// <summary>
    /// What the review shows: the report's suggestions, each key once, minus
    /// whatever <paramref name="alreadyKnown"/> says is in the dictionary or
    /// settings by now. Deliberately <see cref="LearningReport.Suggestions"/>
    /// only, never <see cref="BenchResult.RecognitionFixes"/> as well:
    /// <see cref="HistoryLearner"/> merges the benchmark's fixes into
    /// Suggestions after its "dismissed" and "already known" filters, so
    /// re-reading the raw benchmark list would bring back, pre-ticked, a fix
    /// the user had asked never to see again.
    /// </summary>
    public static List<Suggestion> ReviewList(LearningReport report, Func<Suggestion, bool> alreadyKnown) =>
        report.Suggestions.DistinctBy(s => s.Key).Where(s => !alreadyKnown(s)).ToList();

    /// <summary>
    /// The review's main button. It is never greyed out: with nothing ticked
    /// it still has a job — turning the unticked ones down for good, or just
    /// saying "I've looked" — and either way the "suggestions waiting" dot has
    /// to go. A disabled button left Close as the only way out, which kept the
    /// dot (and the same suggestions) coming back.
    /// </summary>
    /// <param name="ticked">Rows ticked.</param>
    /// <param name="dismissing">Unticked rows that would be turned down for good.</param>
    public static string ReviewButton(int ticked, int dismissing) =>
        ticked > 0 ? $"Apply {ticked:N0} selected"
            : dismissing > 0 ? "Don't suggest these"
            : "Done";

    /// <summary>
    /// How much of a row must have been inside the scroll viewport to count as
    /// seen: its first line (or all of it, if shorter). A sliver at the edge
    /// is not a row the user could have read.
    /// </summary>
    public const double SeenLine = 24;

    /// <summary>
    /// A row whose top is <paramref name="top"/> px below the top of a
    /// viewport <paramref name="viewportHeight"/> px tall has been seen when at
    /// least <see cref="SeenLine"/> px of it are inside. "Seen" decides what
    /// "Don't suggest the ones I left unticked" turns down, and that cannot be
    /// undone, so a row the user never scrolled to must not count.
    /// </summary>
    public static bool WasSeen(double top, double height, double viewportHeight)
    {
        if (height <= 0 || viewportHeight <= 0) return false;
        var inside = Math.Min(top + height, viewportHeight) - Math.Max(top, 0);
        return inside >= Math.Min(height, SeenLine) - 0.5;
    }

    /// <summary>
    /// What the personal benchmark would play: Wispr Flow's kept recordings,
    /// when its history is ticked, capped the way PersonalBench caps them.
    /// Only Wispr's takes count because PersonalBench decodes nothing else —
    /// they are the ones with a second, independent transcript (Wispr's own
    /// recogniser output) to compare FlowType's text with. Counting FlowType's
    /// recordings too offered a test that always ended "nothing was tested".
    /// </summary>
    public static (int Recordings, double Seconds) BenchAudio(IEnumerable<SourceSummary>? sources, bool useWispr, int cap)
    {
        if (!useWispr || sources == null) return (0, 0);
        var recordings = 0;
        double seconds = 0;
        foreach (var s in sources)
        {
            if (s.Source != HistorySource.WisprFlow || !IsUsable(s)) continue;
            recordings += s.WithAudio;
            seconds += s.AudioSeconds;
        }
        if (recordings > cap)
        {
            seconds *= cap / (double)recordings;
            recordings = cap;
        }
        return (recordings, seconds);
    }

    /// <summary>The line under "Also test on my recordings": what it does, or why it can't.</summary>
    /// <param name="modelReady">The speech model is loaded.</param>
    /// <param name="sources">What each history holds; null while still being counted.</param>
    /// <param name="useWispr">Wispr Flow's history is ticked.</param>
    /// <param name="recordings">From <see cref="BenchAudio"/>.</param>
    public static string BenchHint(bool modelReady, IReadOnlyList<SourceSummary>? sources, bool useWispr, int recordings)
    {
        if (!modelReady) return "Needs the speech model loaded. It loads when FlowType starts — try again in a moment.";
        if (sources == null) return "Checking which recordings are kept…";
        const string why = "Only its recordings come with a second transcript to compare FlowType's text with.";
        if (!sources.Any(s => s.Source == HistorySource.WisprFlow && IsUsable(s))) return "Needs Wispr Flow's history. " + why;
        if (!useWispr) return "Tick Wispr Flow history above to use this. " + why;
        if (recordings == 0) return "Wispr Flow hasn't kept any recordings.";
        return $"Plays up to {recordings:N0} of your Wispr Flow {Ui.Plural(recordings, "recording", "recordings")} "
            + "through the speech model twice — with today's dictionary and with the suggestions — and compares "
            + "each with Wispr Flow's raw transcript of it. Slower than the rest; it pauses whenever you dictate.";
    }

    /// <summary>
    /// The benchmark card's opening line. The agreement figure is measured
    /// against Wispr Flow's raw recogniser transcript (<c>asrText</c>), from
    /// before Wispr's own fixes — the very transcript many suggestions were
    /// mined from the mistakes of. So it has to say what it is compared with,
    /// that it is a comparison rather than correctness, and that a learned
    /// spelling can lower it exactly where Wispr's recogniser was wrong;
    /// otherwise 91.2% → 90.8% reads as "the suggestions make it worse".
    /// </summary>
    public static string BenchIntro(int recordings, double audioSeconds) =>
        $"Tested on {recordings:N0} of your Wispr Flow {Ui.Plural(recordings, "recording", "recordings")} "
        + $"({Duration(audioSeconds)}). The first figure compares FlowType's text with Wispr Flow's raw recogniser "
        + "transcript of each one, from before Wispr's own fixes — a comparison, not a measure of correctness. "
        + "Learned spellings can lower it wherever Wispr's recogniser got a word wrong.";

    /// <summary>The agreement tile's eyebrow: names the reference rather than claiming accuracy.</summary>
    public const string AgreementTitle = "MATCHES WISPR'S RAW TRANSCRIPT";

    /// <summary>
    /// The term tile's eyebrow. Occurrences are counted in what the user
    /// finally wrote, so "said a suggested word" is literal and "written
    /// right" is FlowType's output agreeing with it.
    /// </summary>
    public const string TermsTitle = "SUGGESTED WORDS WRITTEN RIGHT";

    /// <summary>The agreement tile's caption.</summary>
    public static string AgreementCaption(bool changed) => changed
        ? "Today → with the suggestions. Not a measure of correctness."
        : "With today's dictionary. Not a measure of correctness.";

    /// <summary>The term tile's caption.</summary>
    public const string TermsCaption =
        "Of the times you said a suggested word, how many were written right: today → with the suggestions.";

    /// <summary>
    /// The live-transcription check under the benchmark. It names the feature
    /// by the switch the user actually sees in Settings ("Transcribe while you
    /// talk"); the earlier "Live typing" matched nothing anywhere in FlowType.
    /// </summary>
    public static string LiveChunksLine(int chunks, int forcedCuts) =>
        $"With “Transcribe while you talk”, these were split into {chunks:N0} "
        + $"{Ui.Plural(chunks, "piece", "pieces")}; {forcedCuts:N0} of the cuts found no pause to cut at.";
}
