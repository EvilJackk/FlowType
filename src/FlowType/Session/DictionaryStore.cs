using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlowType.Core;

namespace FlowType.Session;

/// <summary>
/// One dictionary entry. Two flavors, mirroring Wispr Flow:
/// - Replacement empty → a vocabulary word: fed to whisper's initial prompt so
///   the model recognizes the name/term in the first place.
/// - Replacement set → a rewrite rule / snippet: say the phrase, get the text
///   ("my email" → address; "figjam" → "FigJam").
/// </summary>
public class DictionaryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Phrase { get; set; } = "";
    public string Replacement { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool MatchWholeWord { get; set; } = true;

    public bool IsVocabularyOnly => string.IsNullOrWhiteSpace(Replacement);
}

public sealed class DictionaryStore
{
    public static DictionaryStore Instance { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private List<DictionaryEntry> _entries;

    public event Action? Changed;

    private DictionaryStore()
    {
        _entries = Load();
    }

    public IReadOnlyList<DictionaryEntry> Entries
    {
        get { lock (_gate) return _entries.ToList(); }
    }

    /// <summary>
    /// Words to bias recognition toward (whisper initial prompt). Vocabulary
    /// entries first, then the *targets* of short rewrite rules: a rule like
    /// "armor forger → Arma Reforger" tells us the user keeps getting that
    /// name mangled, and the cheapest fix is for the model to hear it right in
    /// the first place. Long snippets (addresses, boilerplate) are left out so
    /// they don't crowd the prompt.
    /// </summary>
    public IReadOnlyList<string> VocabularyWords
    {
        get
        {
            lock (_gate)
            {
                var words = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                void Add(string text)
                {
                    var trimmed = text.Trim();
                    if (trimmed.Length > 0 && seen.Add(trimmed)) words.Add(trimmed);
                }
                foreach (var e in _entries.Where(e => e.Enabled && e.IsVocabularyOnly)) Add(e.Phrase);
                foreach (var e in _entries.Where(e => e.Enabled && !e.IsVocabularyOnly))
                {
                    var target = e.Replacement.Trim();
                    var isShortName = target.Length <= 40
                        && target.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4
                        && target.Any(char.IsLetter)
                        && !target.Contains('@') && !target.Contains("://");
                    if (isShortName) Add(target);
                }
                return words;
            }
        }
    }

    public void Add(string phrase, string replacement, bool matchWholeWord = true)
    {
        lock (_gate)
        {
            _entries.Insert(0, new DictionaryEntry
            {
                Phrase = phrase,
                Replacement = replacement,
                MatchWholeWord = matchWholeWord,
            });
            Save();
        }
        Changed?.Invoke();
    }

    public void SetEnabled(Guid id, bool enabled)
    {
        lock (_gate)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == id);
            if (entry == null) return;
            entry.Enabled = enabled;
            Save();
        }
        Changed?.Invoke();
    }

    public void Delete(Guid id)
    {
        lock (_gate)
        {
            _entries.RemoveAll(e => e.Id == id);
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Apply every enabled rewrite rule. Case-insensitive, word-boundary aware.</summary>
    public string Apply(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        List<DictionaryEntry> snapshot;
        lock (_gate) snapshot = _entries.ToList();

        var result = text;
        foreach (var entry in snapshot.Where(e => e.Enabled && !e.IsVocabularyOnly))
        {
            var phrase = entry.Phrase.Trim();
            if (phrase.Length == 0) continue;
            result = ReplacePhrase(phrase, entry.Replacement, result, entry.MatchWholeWord);
        }
        return result;
    }

    private static string ReplacePhrase(string phrase, string replacement, string text,
        bool matchWholeWord)
    {
        // Spaces in the phrase match any whitespace run so multi-word snippets
        // survive spacing differences.
        var escaped = Regex.Escape(phrase).Replace(@"\ ", @"\s+");
        var lead = matchWholeWord && char.IsLetterOrDigit(phrase[0]) ? @"\b" : "";
        var tail = matchWholeWord && char.IsLetterOrDigit(phrase[^1]) ? @"\b" : "";

        try
        {
            return Regex.Replace(text, $"{lead}{escaped}{tail}",
                replacement.Replace("$", "$$"), RegexOptions.IgnoreCase);
        }
        catch (ArgumentException)
        {
            return text;
        }
    }

    private void Save() => JsonFile.Write(AppPaths.DictionaryFile, _entries);

    private static List<DictionaryEntry> Load() =>
        JsonFile.Read<List<DictionaryEntry>>(AppPaths.DictionaryFile) ?? new();
}
