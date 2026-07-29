using System.IO;
using System.Text.Json;
using FlowType.Core;

namespace FlowType.Session;

/// <summary>One past dictation ("Notes" in Wispr Flow terms).</summary>
public class Note
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Text { get; set; } = "";
    public double DurationSeconds { get; set; }
    public int WordCount { get; set; }
    public string AppName { get; set; } = "";
    public string ModelId { get; set; } = "";
}

public sealed class NotesStore
{
    private const int MaxNotes = 1000;

    public static NotesStore Instance { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private List<Note> _notes;

    public event Action? Changed;

    private NotesStore()
    {
        _notes = Load();
    }

    public IReadOnlyList<Note> All
    {
        get { lock (_gate) return _notes.ToList(); }
    }

    public IReadOnlyList<Note> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return All;
        lock (_gate)
        {
            return _notes
                .Where(n => n.Text.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || n.AppName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    public void Add(Note note)
    {
        lock (_gate)
        {
            _notes.Insert(0, note);
            if (_notes.Count > MaxNotes)
            {
                _notes.RemoveRange(MaxNotes, _notes.Count - MaxNotes);
            }
            Save();
        }
        Changed?.Invoke();
    }

    public void Delete(Guid id)
    {
        lock (_gate)
        {
            _notes.RemoveAll(n => n.Id == id);
            Save();
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _notes.Clear();
            Save();
        }
        Changed?.Invoke();
    }

    private void Save() => JsonFile.Write(AppPaths.NotesFile, _notes);

    private static List<Note> Load() => JsonFile.Read<List<Note>>(AppPaths.NotesFile) ?? new();

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
