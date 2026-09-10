using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using FlowType.Core;
using FlowType.Engine;

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

    /// <summary>Added automatically from a correction the user typed, not by hand.</summary>
    public bool LearnedFromCorrection { get; set; }

    /// <summary>
    /// Empty means "just a word to recognise". Deliberately not
    /// IsNullOrWhiteSpace: a replacement of a single space is the most useful
    /// rule a dictation user writes ("-" → " " to undo hyphenation), and
    /// treating it as vocabulary made it impossible to express *and* pushed the
    /// hyphen into the decoder's prompt.
    /// </summary>
    public bool IsVocabularyOnly => Replacement.Length == 0;
}

public sealed class DictionaryStore
{
    /// <summary>Ceiling on one rule's match time (see <see cref="MatchPhrase"/>).</summary>
    private static readonly TimeSpan RuleTimeout = TimeSpan.FromMilliseconds(250);

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
    /// The two halves of what the dictionary tells the recogniser.
    ///
    /// <paramref name="Spellings"/> is how a term should come out; it biases
    /// the decoder and is what the fuzzy matcher corrects toward.
    /// <paramref name="SpokenForms"/> is how the model keeps getting it wrong —
    /// the *from* side of a rewrite rule — which is useful to the decoder as a
    /// hint and would be actively harmful to the fuzzy matcher, since matching
    /// toward it would rewrite correct text into the mis-hearing.
    /// </summary>
    public readonly record struct VocabularyLists(
        IReadOnlyList<string> Spellings, IReadOnlyList<string> SpokenForms);

    /// <summary>
    /// Vocabulary entries and the *targets* of short rewrite rules become
    /// spellings: a rule "armor forger → Arma Reforger" says the user keeps
    /// getting that name mangled, and the cheapest fix is for the model to hear
    /// it right in the first place. The rule's own phrase becomes a spoken
    /// form. Long snippets (addresses, boilerplate) are left out of both so
    /// they don't crowd the prompt.
    /// </summary>
    public VocabularyLists VocabularySections
    {
        get
        {
            lock (_gate) return SectionsFor(_entries);
        }
    }

    /// <summary>The split on an explicit set of entries (also used by --selftest).</summary>
    internal static VocabularyLists SectionsFor(IReadOnlyList<DictionaryEntry> entries)
    {
        var spellings = new List<string>();
        var spoken = new List<string>();
        var seenSpelling = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenSpoken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Two-character terms bias the decoder toward false positives
        // and cost prompt budget for almost no information.
        const int MinTermLength = 3;

        static bool IsShortName(string text) =>
            text.Length is >= MinTermLength and <= 40
            && text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4
            && text.Any(char.IsLetter)
            && !text.Contains('@') && !text.Contains("://");

        foreach (var e in entries.Where(e => e.Enabled && e.IsVocabularyOnly))
        {
            var phrase = Transcriber.SanitizeTerm(e.Phrase);
            if (IsShortName(phrase) && seenSpelling.Add(phrase)) spellings.Add(phrase);
        }
        foreach (var e in entries.Where(e => e.Enabled && !e.IsVocabularyOnly))
        {
            var target = Transcriber.SanitizeTerm(e.Replacement);
            var phrase = Transcriber.SanitizeTerm(e.Phrase);
            var targetIsTerm = IsShortName(target);
            if (targetIsTerm && seenSpelling.Add(target)) spellings.Add(target);

            // "How it gets heard" is only worth telling the decoder when
            // the thing being heard is a term we are biasing toward. A
            // snippet trigger ("my email" → an address) is a phrase the
            // user says on purpose, not a mis-hearing, and a casing-only
            // rule teaches the decoder nothing at all.
            if (targetIsTerm && IsShortName(phrase)
                && !phrase.Equals(target, StringComparison.OrdinalIgnoreCase)
                && seenSpoken.Add(phrase))
            {
                spoken.Add(phrase);
            }
        }
        return new VocabularyLists(spellings, spoken);
    }

    /// <summary>Spellings only — the list the fuzzy matcher may correct toward.</summary>
    public IReadOnlyList<string> VocabularyWords => VocabularySections.Spellings;

    /// <summary>
    /// Every rewrite rule as a (heard, write) pair for the fuzzy matcher.
    ///
    /// The rule the user wrote down is the closest thing FlowType has to a
    /// sample of how the model actually mishears their term — much closer than
    /// the correct spelling is. Matching near-misses of *it* as well is what
    /// turns one rule into a family instead of a single point fix.
    /// </summary>
    public IReadOnlyList<VocabularyMatcher.Alias> Aliases
    {
        get
        {
            lock (_gate) return AliasesFor(_entries);
        }
    }

    internal static IReadOnlyList<VocabularyMatcher.Alias> AliasesFor(
        IReadOnlyList<DictionaryEntry> entries)
    {
        var list = new List<VocabularyMatcher.Alias>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries.Where(e => e.Enabled && !e.IsVocabularyOnly))
        {
            var heard = Transcriber.SanitizeTerm(e.Phrase);
            var write = Transcriber.SanitizeTerm(e.Replacement);
            // Only for real terms: a snippet trigger whose target is an address
            // or a paragraph must stay an exact-match rule.
            if (heard.Length < 4 || write.Length is < 3 or > 40) continue;
            if (write.Contains('@') || write.Contains("://")) continue;
            if (heard.Equals(write, StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Add(heard)) list.Add(new VocabularyMatcher.Alias(heard, write));
        }
        return list;
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

    /// <summary>
    /// Add a rule that came from the user correcting FlowType by hand. Returns
    /// false when it would be a duplicate — the same correction made twice
    /// should be silent the second time, not add a second entry.
    /// </summary>
    public bool AddLearned(string heard, string write)
    {
        lock (_gate)
        {
            var already = _entries.Any(e =>
                e.Phrase.Equals(heard, StringComparison.OrdinalIgnoreCase)
                && e.Replacement.Equals(write, StringComparison.OrdinalIgnoreCase));
            if (already) return false;

            _entries.Insert(0, new DictionaryEntry
            {
                Phrase = heard,
                Replacement = write,
                MatchWholeWord = true,
                LearnedFromCorrection = true,
            });
            Save();
        }
        Changed?.Invoke();
        return true;
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
        List<DictionaryEntry> snapshot;
        lock (_gate) snapshot = _entries.ToList();
        return ApplyRules(text, snapshot);
    }

    private readonly record struct Candidate(int Start, int End, string Target, int Rule);

    /// <summary>
    /// Apply every enabled rewrite rule to the *original* text, once.
    ///
    /// This used to be a loop of full-text replacements, each running on the
    /// previous rule's output, so rules silently fed each other: with
    /// "react → React" and "React → React.js", "i use react daily" came out as
    /// "i use React.js daily", and adding an unrelated entry could change what
    /// an existing one produced. Every rule now matches against the transcript
    /// as spoken; overlaps are resolved longest-first, then by the order the
    /// rules appear, and the result is spliced together in one pass.
    /// </summary>
    internal static string ApplyRules(string text, IReadOnlyList<DictionaryEntry> entries)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var candidates = new List<Candidate>();
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (!entry.Enabled || entry.IsVocabularyOnly) continue;
            var phrase = entry.Phrase.Trim();
            if (phrase.Length == 0) continue;

            foreach (var match in MatchPhrase(phrase, text, entry.MatchWholeWord))
            {
                if (IsInsideProtectedToken(text, match.Index, match.Index + match.Length)) continue;
                candidates.Add(new Candidate(
                    match.Index, match.Index + match.Length, entry.Replacement, i));
            }
        }
        if (candidates.Count == 0) return text;

        // List.Sort is unstable, so the rule index is an explicit tiebreak
        // rather than something inherited from insertion order.
        candidates.Sort((a, b) =>
        {
            var byStart = a.Start.CompareTo(b.Start);
            if (byStart != 0) return byStart;
            var byLength = (b.End - b.Start).CompareTo(a.End - a.Start);
            return byLength != 0 ? byLength : a.Rule.CompareTo(b.Rule);
        });

        var sb = new System.Text.StringBuilder(text.Length);
        var cursor = 0;
        foreach (var c in candidates)
        {
            if (c.Start < cursor) continue;      // already covered by a longer match
            sb.Append(text, cursor, c.Start - cursor);
            // A splice, not a Regex substitution: "$" in a replacement is
            // literal text and needs no escaping.
            sb.Append(c.Target);
            cursor = c.End;
        }
        sb.Append(text, cursor, text.Length - cursor);
        return sb.ToString();
    }

    private static IEnumerable<Match> MatchPhrase(string phrase, string text, bool matchWholeWord)
    {
        // Spaces in the phrase match any whitespace run so multi-word snippets
        // survive spacing differences.
        var escaped = Regex.Escape(phrase).Replace(@"\ ", @"\s+");
        var lead = matchWholeWord && char.IsLetterOrDigit(phrase[0]) ? @"\b" : "";
        var tail = matchWholeWord && char.IsLetterOrDigit(phrase[^1]) ? @"\b" : "";

        try
        {
            // A user-authored phrase becomes a regex, and the space -> \s+
            // rewrite above is a catastrophic-backtracking shape. A dictionary
            // entry must never be able to hang a dictation.
            return Regex.Matches(text, $"{lead}{escaped}{tail}",
                RegexOptions.IgnoreCase, RuleTimeout).ToList();
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
        {
            return Array.Empty<Match>();
        }
    }

    /// <summary>
    /// An email address, URL or path is one opaque thing: rewriting a fragment
    /// of it always breaks it. A rule "type → Type" must not turn
    /// "roy@type.com" into "roy@Type.com".
    /// </summary>
    private static bool IsInsideProtectedToken(string text, int start, int end)
    {
        var tokenStart = start;
        while (tokenStart > 0 && !char.IsWhiteSpace(text[tokenStart - 1])) tokenStart--;
        var tokenEnd = end;
        while (tokenEnd < text.Length && !char.IsWhiteSpace(text[tokenEnd])) tokenEnd++;

        var token = text[tokenStart..tokenEnd];
        if (token.Contains("://")) return true;
        if (token.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) return true;
        var at = token.IndexOf('@');
        return at > 0 && at + 1 < token.Length;
    }

    private void Save() => JsonFile.Write(AppPaths.DictionaryFile, _entries);

    private static List<DictionaryEntry> Load() =>
        JsonFile.Read<List<DictionaryEntry>>(AppPaths.DictionaryFile) ?? new();
}
