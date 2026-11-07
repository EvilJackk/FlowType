using System.Diagnostics;
using System.IO;
using FlowType.Core;
using FlowType.Engine;
using FlowType.Session;

namespace FlowType.Learning;

/// <summary>
/// FlowType's own History as a learning source: the notes in notes.json and
/// the recordings kept beside them.
///
/// It is the smaller source by far — the history is capped at 1000 notes,
/// recordings are kept only when the user turned that on, and FlowType does
/// not observe edits made after a paste (<see cref="EditLearner"/> learns from
/// those live and keeps nothing) — but it is the one that reflects FlowType's
/// own model, so it is always offered.
///
/// Read-only: notes come from <see cref="NotesStore.All"/> (a snapshot), audio
/// from the archive folder. Nothing is written, and no text is logged.
/// </summary>
public static class FlowTypeHistory
{
    /// <summary>
    /// What <see cref="Note.Style"/> holds for a Command Mode result:
    /// an answer the local model wrote, not something the user said.
    /// </summary>
    private const string CommandStyle = "command";

    /// <summary>
    /// Counts for the Insights card. Cheap: notes are already in memory and
    /// "has a recording" is one file-existence check per note. Never throws.
    /// </summary>
    public static SourceSummary Summarize()
    {
        var location = AppPaths.NotesFile;
        try
        {
            var samples = ReadSamples();
            var exists = samples.Count > 0 || File.Exists(location);
            return SummaryOf(samples, exists, location);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"FlowType history summary failed: {ex.GetType().Name}");
            return new SourceSummary(HistorySource.FlowType, false, 0, 0, 0, 0,
                "FlowType couldn't read its own history.", location);
        }
    }

    /// <summary>Every usable dictation in FlowType's History, newest first.</summary>
    public static IReadOnlyList<HistorySample> ReadSamples() =>
        SamplesFrom(NotesStore.Instance.All, RecordingArchive.Instance.Has);

    /// <summary>
    /// A note's kept recording as 16 kHz mono samples, or null when it has
    /// none or it cannot be read (it may have been pruned a moment ago).
    /// </summary>
    public static float[]? ReadAudio(string id)
    {
        if (!Guid.TryParse(id, out var noteId)) return null;
        var path = RecordingArchive.Instance.PathFor(noteId);
        if (path == null) return null;
        try
        {
            return Transcriber.ReadSamples(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"FlowType recording read failed: {ex.GetType().Name}");
            return null;
        }
    }

    // ----- Pure rules (also used by the self-test) -----

    /// <summary>
    /// Notes → samples. Skips Command Mode results and notes with no text.
    /// <paramref name="hasAudio"/> answers "is this note's recording still on
    /// disk"; the self-test passes its own.
    /// </summary>
    internal static IReadOnlyList<HistorySample> SamplesFrom(IEnumerable<Note> notes, Func<Guid, bool> hasAudio)
    {
        return notes
            .Where(n => n != null
                && !string.Equals(n.Style, CommandStyle, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(n.Text))
            .Select(n => new HistorySample(
                HistorySource.FlowType,
                Id: n.Id.ToString("D"),
                TimestampUtc: n.Timestamp.ToUniversalTime(),
                App: n.AppName ?? "",
                Recognized: n.RawText ?? "",
                Final: n.Text,
                Edited: null,
                Language: n.Language ?? "",
                DurationSeconds: Math.Max(0, n.DurationSeconds),
                HasAudio: hasAudio(n.Id)))
            .OrderByDescending(s => s.TimestampUtc)
            .ToList();
    }

    /// <summary>The summary of a set of samples (FlowType keeps no edits, so Edited is always 0).</summary>
    internal static SourceSummary SummaryOf(IReadOnlyList<HistorySample> samples, bool exists, string location)
    {
        var withAudio = samples.Where(s => s.HasAudio).ToList();
        return new SourceSummary(
            HistorySource.FlowType,
            Available: exists,
            Dictations: samples.Count,
            WithAudio: withAudio.Count,
            AudioSeconds: withAudio.Sum(s => s.DurationSeconds),
            Edited: 0,
            Problem: exists ? null : "No FlowType dictations yet.",
            Location: location);
    }
}
