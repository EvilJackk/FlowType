using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json.Serialization;
using FlowType.Core;
using FlowType.Engine;

namespace FlowType.Session;

/// <summary>
/// One past dictation — a History row ("Notes" in Wispr Flow terms).
///
/// <see cref="Text"/> is always what was finally inserted (or copied): after
/// formatting, dictionary rewrites, style and any AI polish, without the
/// sentence-boundary space added on the way out. Everything else records how
/// it got there, so a bad result can be traced to the step that caused it and
/// undone from History rather than retyped.
///
/// Every field added in 2.0 has a default, so a notes.json written by 1.x
/// loads unchanged, and a 1.x build reading a 2.0 file simply ignores the
/// extra fields (System.Text.Json skips members it doesn't know).
/// </summary>
public class Note
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>The final text — what went into the other app.</summary>
    public string Text { get; set; } = "";

    public double DurationSeconds { get; set; }
    public int WordCount { get; set; }
    public string AppName { get; set; } = "";
    public string ModelId { get; set; } = "";

    /// <summary>
    /// What the recogniser produced, before formatting, dictionary rewrites,
    /// style or polish. Empty for notes from before 2.0. Kept because "the
    /// model misheard" and "the formatter mangled it" need different fixes, and
    /// only the raw text tells them apart.
    /// </summary>
    public string RawText { get; set; } = "";

    /// <summary>
    /// The other version of an AI-polished note. While <see cref="AiPolished"/>
    /// is true this is the rules-only text from before the model rewrote it
    /// (what "Undo AI edit" restores); after an undo it holds the model's
    /// version (what "Redo" restores). Null when polish never ran or changed
    /// nothing — then there is only one version, <see cref="Text"/>. Omitted
    /// from the file when null, so ordinary notes cost nothing extra.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FormattedText { get; set; }

    /// <summary>True when <see cref="Text"/> is the local model's rewrite.</summary>
    public bool AiPolished { get; set; }

    /// <summary>
    /// Writing style applied (a <c>WritingStyles</c> id: formal, casual,
    /// very-casual, excited), "command" for a Command Mode result, or empty.
    /// </summary>
    public string Style { get; set; } = "";

    /// <summary>Language the take was decoded as ("en"), or empty when unknown.</summary>
    public string Language { get; set; } = "";

    /// <summary>Wall-clock time whisper spent decoding, in milliseconds.</summary>
    public long DecodeMs { get; set; }

    /// <summary>Hotkey release to text inserted, in milliseconds — the delay the user actually felt.</summary>
    public long LatencyMs { get; set; }

    /// <summary>The text came from running the kept recording through a model again, from History.</summary>
    public bool Retranscribed { get; set; }

    /// <summary>History can offer "Undo AI edit".</summary>
    [JsonIgnore]
    public bool CanUndoAiEdit => AiPolished && FormattedText != null;

    /// <summary>History can offer "Redo AI edit" (an undone polish is still on file).</summary>
    [JsonIgnore]
    public bool CanRedoAiEdit => !AiPolished && FormattedText != null;

    /// <summary>
    /// A detached copy. Every member is a value or an immutable string, so a
    /// memberwise copy is a full one.
    /// </summary>
    public Note Clone() => (Note)MemberwiseClone();
}

/// <summary>
/// Dictation history, persisted to notes.json (newest first, capped at
/// <see cref="MaxNotes"/>), plus the bookkeeping that keeps each note's kept
/// recording in step with it: deleting a note deletes its audio, and a note
/// trimmed off the end of the history takes its audio with it.
///
/// Notes handed out by <see cref="All"/>, <see cref="Get"/> and friends are
/// never modified afterwards — <see cref="Update"/> replaces the stored note
/// with an edited copy — so a window can hold on to one while the session
/// updates it from another thread without ever seeing half an edit.
/// </summary>
public sealed class NotesStore
{
    /// <summary>
    /// The whole file is rewritten on every dictation, so the history is
    /// bounded: 1000 notes is a few hundred KB, which writes in milliseconds.
    /// </summary>
    private const int MaxNotes = 1000;

    public static NotesStore Instance { get; } = new();

    private readonly object _gate = new();
    private readonly string _file;
    private readonly RecordingArchive _archive;
    private List<Note> _notes;

    /// <summary>
    /// notes.json existed and had content but nothing could be read from it
    /// (and from its .bak). Orphan-recording cleanup is skipped in that state:
    /// "no notes" might be a transient read failure, and deleting every kept
    /// recording on the strength of it can't be undone.
    /// </summary>
    private readonly bool _loadSuspect;

    /// <summary>Raised after any change, on the thread that made it.</summary>
    public event Action? Changed;

    private NotesStore() : this(AppPaths.NotesFile, RecordingArchive.Instance)
    {
    }

    /// <summary>
    /// Test seam: a store over any notes file and archive, so history rules can
    /// be exercised on scratch files without touching the user's real notes.
    /// </summary>
    internal NotesStore(string notesFile, RecordingArchive archive)
    {
        _file = notesFile;
        _archive = archive;
        _notes = Load(notesFile, out _loadSuspect);
    }

    // ----- Reading -----

    public IReadOnlyList<Note> All
    {
        get { lock (_gate) return _notes.ToList(); }
    }

    /// <summary>The most recent dictation, or null (Paste last / Copy last).</summary>
    public Note? Last
    {
        get { lock (_gate) return _notes.Count > 0 ? _notes[0] : null; }
    }

    public Note? Get(Guid id)
    {
        lock (_gate) return _notes.Find(n => n.Id == id);
    }

    /// <summary>Notes whose text or app name contains the query (all notes when it is blank), newest first.</summary>
    public IReadOnlyList<Note> Search(string? query)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(query)) return _notes.ToList();
            var q = query.Trim();
            return _notes
                .Where(n => n.Text.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || n.AppName.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    /// <summary>
    /// Matching notes grouped by local calendar day for the History page —
    /// newest day first, newest note first within a day. Ordered by timestamp,
    /// not storage order, so an imported or re-dated note still lands under
    /// the right day.
    /// </summary>
    public IReadOnlyList<(DateTime Day, IReadOnlyList<Note> Notes)> GroupedByDay(string? query = null)
    {
        return Search(query)
            .OrderByDescending(n => n.Timestamp)
            .GroupBy(n => n.Timestamp.Date)
            .Select(g => (g.Key, (IReadOnlyList<Note>)g.ToList()))
            .ToList();
    }

    /// <summary>
    /// Header text for a History day group: "Today", "Yesterday", the weekday
    /// within the last week, otherwise the date (with the year only when it is
    /// not this year).
    /// </summary>
    public static string DayLabel(DateTime day, DateTime? today = null)
    {
        var now = (today ?? DateTime.Now).Date;
        var date = day.Date;
        var days = (now - date).Days;
        var culture = CultureInfo.CurrentCulture;
        return days switch
        {
            0 => "Today",
            1 => "Yesterday",
            > 1 and < 7 => date.ToString("dddd", culture),
            _ when date.Year == now.Year => date.ToString("dddd, MMMM d", culture),
            _ => date.ToString("MMMM d, yyyy", culture),
        };
    }

    // ----- Writing -----

    public void Add(Note note)
    {
        if (note == null) return;
        List<Guid> trimmed;
        lock (_gate)
        {
            // Re-adding an id replaces the old note rather than duplicating it.
            _notes.RemoveAll(n => n.Id == note.Id);
            _notes.Insert(0, note);
            trimmed = new List<Guid>();
            if (_notes.Count > MaxNotes)
            {
                trimmed.AddRange(_notes.Skip(MaxNotes).Select(n => n.Id));
                _notes.RemoveRange(MaxNotes, _notes.Count - MaxNotes);
            }
            Save();
        }
        // A note that fell off the end of history takes its audio with it.
        foreach (var id in trimmed) _archive.Delete(id);
        RaiseChanged();
    }

    /// <summary>
    /// Edit one note: <paramref name="mutate"/> receives a copy, and the copy
    /// replaces the stored note (its <see cref="Note.Id"/> is kept). If the
    /// callback throws, nothing changes. Keep the callback short — it runs
    /// under the store's lock. Returns false when the note no longer exists.
    /// </summary>
    /// <remarks>
    /// Derived fields are the caller's job: set <see cref="Note.WordCount"/>
    /// when changing <see cref="Note.Text"/> (see <see cref="TextFormatter.CountWords"/>).
    /// </remarks>
    public bool Update(Guid id, Action<Note> mutate)
    {
        lock (_gate)
        {
            var index = _notes.FindIndex(n => n.Id == id);
            if (index < 0) return false;
            var copy = _notes[index].Clone();
            mutate(copy);
            copy.Id = id;
            _notes[index] = copy;
            Save();
        }
        RaiseChanged();
        return true;
    }

    /// <summary>
    /// Swap an AI-polished note between the model's version and the rules-only
    /// version (History → Undo / Redo AI edit). Returns false when the note has
    /// only one version.
    /// </summary>
    public bool ToggleAiEdit(Guid id)
    {
        // Cheap pre-check so a note with one version doesn't cost a file write.
        if (Get(id)?.FormattedText == null) return false;
        var swapped = false;
        var found = Update(id, note =>
        {
            if (note.FormattedText == null) return;
            (note.Text, note.FormattedText) = (note.FormattedText, note.Text);
            note.AiPolished = !note.AiPolished;
            note.WordCount = TextFormatter.CountWords(note.Text);
            swapped = true;
        });
        return found && swapped;
    }

    /// <summary>Delete one note and its kept recording.</summary>
    public void Delete(Guid id)
    {
        lock (_gate)
        {
            if (_notes.RemoveAll(n => n.Id == id) > 0) Save();
        }
        _archive.Delete(id);
        RaiseChanged();
    }

    /// <summary>Delete every note and every kept recording.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _notes.Clear();
            Save();
        }
        _archive.DeleteAll();
        RaiseChanged();
    }

    /// <summary>
    /// Start-up: delete kept recordings whose note is gone and trim the
    /// archive to the configured count. Touches only the archive folder, so it
    /// is safe on a background thread. Returns how many recordings were deleted.
    /// </summary>
    public int ReconcileRecordings()
    {
        if (_loadSuspect) return _archive.Prune();
        List<Guid> ids;
        lock (_gate) ids = _notes.Select(n => n.Id).ToList();
        return _archive.PruneOrphans(ids);
    }

    // ----- Persistence -----

    /// <summary>
    /// Write the list (call with the lock held). A failed write — disk full, the
    /// file held open by a sync tool — must not turn a successful dictation
    /// into an error after the text was already inserted, so it is logged and
    /// the in-memory history stands; the next successful save persists it.
    /// </summary>
    private void Save()
    {
        try
        {
            JsonFile.Write(_file, _notes);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Saving notes failed: {ex.Message}");
        }
    }

    private static List<Note> Load(string file, out bool suspect)
    {
        var loaded = JsonFile.Read<List<Note>>(file);
        var notes = new List<Note>();
        if (loaded != null)
        {
            foreach (var note in loaded)
            {
                // Hand-edited or partially written files can hold nulls; every
                // string is non-null afterwards so search and display never throw.
                if (note == null) continue;
                note.Text ??= "";
                note.AppName ??= "";
                note.ModelId ??= "";
                note.RawText ??= "";
                note.Style ??= "";
                note.Language ??= "";
                notes.Add(note);
            }
        }

        suspect = false;
        if (notes.Count == 0)
        {
            try
            {
                suspect = File.Exists(file) && new FileInfo(file).Length > 16 && loaded == null;
            }
            catch
            {
                suspect = true;
            }
        }
        return notes;
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Debug.WriteLine($"Notes Changed handler threw: {ex.Message}"); }
    }

    /// <summary>Write every note to a plain-text file (Notes → Export).</summary>
    public void ExportTo(string path)
    {
        List<Note> snapshot;
        lock (_gate) snapshot = _notes.ToList();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"FlowType notes — exported {DateTime.Now:f}");
        sb.AppendLine(new string('=', 52));
        foreach (var note in snapshot)
        {
            sb.AppendLine();
            var app = string.IsNullOrEmpty(note.AppName) ? "" : $" · {note.AppName}";
            sb.AppendLine($"[{note.Timestamp:g}{app} · {note.WordCount} words]");
            sb.AppendLine(note.Text);
        }
        File.WriteAllText(path, sb.ToString());
    }
}
