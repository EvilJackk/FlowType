using System.Security.Cryptography;
using System.Text;
using FlowType.Core;

namespace FlowType.Learning;

/// <summary>
/// What "Learn from your writing" remembers between runs, in
/// <c>%APPDATA%\FlowType\learning.json</c>.
///
/// Deliberately bookkeeping only: timestamps, counts, the benchmark's numbers,
/// and the "don't suggest this again" list as hashes. The suggestions
/// themselves are not kept — they are recomputed from the history on every
/// run — so this file never holds a word the user dictated. A dismissed key
/// such as <c>v|reforger</c> is stored as the first 16 hex characters of its
/// SHA-256, which is enough to recognise it when it comes up again and not
/// enough to read it back.
///
/// Not thread-safe on its own; <see cref="HistoryLearner"/> owns the one
/// instance and guards it.
/// </summary>
public sealed class LearningState
{
    /// <summary>Hex characters kept from the SHA-256 of a dismissal key (64 bits).</summary>
    internal const int HashChars = 16;

    /// <summary>When the last run of either kind finished (UTC). The daily run waits 24 h from this.</summary>
    public DateTime? LastRunUtc { get; set; }

    /// <summary>When the user last pressed "Learn from my history" (UTC).</summary>
    public DateTime? LastManualRunUtc { get; set; }

    /// <summary>The last benchmark's numbers; the recognition fixes it found are not kept.</summary>
    public BenchSummary? LastBench { get; set; }

    /// <summary>
    /// Recommended suggestions from the last run that the user has neither
    /// applied nor dismissed. Stored with the rest of the bookkeeping, but it
    /// only means something beside the in-memory report it counts:
    /// <see cref="HistoryLearner.PendingCount"/> reports 0 while no report is
    /// held (after a restart), because a dot or a "Review N suggestions"
    /// button with nothing behind it would lead the user nowhere. The next run
    /// overwrites it.
    /// </summary>
    public int PendingCount { get; set; }

    /// <summary>Everything ever applied (words, fixes, snippets, styles), for the Insights line.</summary>
    public int AppliedTotal { get; set; }

    /// <summary>
    /// Usable dictations each source held at the last run that read it. The
    /// daily run only starts when one of these has changed — no new writing,
    /// nothing new to learn.
    /// </summary>
    public int LastFlowTypeDictations { get; set; } = -1;

    /// <inheritdoc cref="LastFlowTypeDictations"/>
    public int LastWisprDictations { get; set; } = -1;

    /// <summary>
    /// When the newest FlowType dictation the last run read was made (UTC);
    /// null before the first run or when there was none. The count alone stops
    /// moving once History reaches its 1000-note cap — each new note pushes
    /// the oldest out — so the daily run also treats a newer note as new
    /// writing. Wispr Flow's rows are never pruned, so its count is enough.
    /// </summary>
    public DateTime? LastFlowTypeNewestUtc { get; set; }

    /// <summary>Hashes (<see cref="Hash"/>) of suggestion keys the user said not to suggest again.</summary>
    public HashSet<string> Dismissed { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Schema marker so a later version can migrate the file.</summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// The stored form of a suggestion key: the first <see cref="HashChars"/>
    /// lowercase hex characters of SHA-256 over its UTF-8 bytes.
    /// </summary>
    public static string Hash(string key)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(key ?? ""));
        return Convert.ToHexString(digest, 0, HashChars / 2).ToLowerInvariant();
    }

    /// <summary>True when the user dismissed this suggestion (by kind, phrase and replacement) in an earlier review.</summary>
    public bool IsDismissed(Suggestion suggestion) => Dismissed.Contains(Hash(KeyOf(suggestion)));

    /// <summary>Remember these as "don't suggest again". Returns how many were new.</summary>
    public int Dismiss(IEnumerable<Suggestion> suggestions) => DismissKeys(suggestions.Select(KeyOf));

    /// <summary>
    /// Remember these suggestion keys (<see cref="Suggestion.KeyFor"/>) as
    /// "don't suggest again" — for things the user removed from the dictionary
    /// rather than unticked in a review. Returns how many were new.
    /// </summary>
    public int DismissKeys(IEnumerable<string> keys)
    {
        var added = 0;
        foreach (var key in keys)
        {
            if (Dismissed.Add(Hash(key))) added++;
        }
        return added;
    }

    /// <summary>
    /// The suggestion's key, rebuilt from its parts when a producer left it
    /// blank — so a dismissal can never silently fail to match.
    /// </summary>
    internal static string KeyOf(Suggestion s) =>
        string.IsNullOrEmpty(s.Key) ? Suggestion.KeyFor(s.Kind, s.Phrase, s.Replacement) : s.Key;

    /// <summary>Read the state, or a fresh one when the file is missing or unreadable.</summary>
    public static LearningState Load(string? path = null)
    {
        var state = JsonFile.Read<LearningState>(path ?? AppPaths.LearningFile) ?? new LearningState();
        // An older or hand-edited file may carry a null set or one built with
        // a different comparer; hashes are lowercase hex, so ordinal is exact.
        state.Dismissed = new HashSet<string>(state.Dismissed ?? new HashSet<string>(), StringComparer.Ordinal);
        if (state.PendingCount < 0) state.PendingCount = 0;
        return state;
    }

    /// <summary>Write the state (crash-safe, via <see cref="JsonFile"/>).</summary>
    public void Save(string? path = null) => JsonFile.Write(path ?? AppPaths.LearningFile, this);
}

/// <summary>
/// <see cref="BenchResult"/> as stored: every number, none of the recognition
/// fixes (those are suggestions, which carry the user's words).
/// </summary>
public sealed record BenchSummary(
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
    DateTime RunUtc)
{
    public static BenchSummary From(BenchResult r) => new(
        r.Recordings, r.AudioSeconds, r.ReferenceWords, r.BaselineAccuracy, r.LearnedAccuracy,
        r.TermOccurrences, r.TermHitsBaseline, r.TermHitsLearned, r.LiveChunks, r.ForcedCuts,
        r.RealTimeFactor, r.ModelId, r.RunUtc);
}
