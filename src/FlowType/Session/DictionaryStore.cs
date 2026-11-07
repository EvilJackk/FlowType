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
    /// Added by "Learn from your writing" (2.1) after the user approved it, or
    /// automatically when they allowed words to be added. Tagged so that
    /// everything the feature ever added can be removed in one step
    /// (<see cref="DictionaryStore.RemoveLearnedFromHistory"/>) without
    /// touching anything the user wrote themselves. Editing the entry's text by
    /// hand clears it: from then on the entry is the user's.
    /// </summary>
    public bool LearnedFromHistory { get; set; }

    /// <summary>
    /// A voice shortcut ("my calendar link" → the URL; "sign off" → a whole
    /// signature). Listed on its own tab, and when it is the entire dictation
    /// the expansion replaces everything, including the full stop whisper adds.
    /// </summary>
    public bool IsSnippet { get; set; }

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

        // The user's own entries are spent from the prompt budget before
        // anything "Learn from your writing" added (2.1), so a batch of
        // learned words can never push out a term the user typed in — the
        // prompt keeps a prefix of each list. With no learned entries this is
        // the original single pass, in the original order.
        foreach (var group in new[] { entries.Where(e => !e.LearnedFromHistory), entries.Where(e => e.LearnedFromHistory) })
        {
            foreach (var e in group.Where(e => e.Enabled && e.IsVocabularyOnly))
            {
                var phrase = Transcriber.SanitizeTerm(e.Phrase);
                if (IsShortName(phrase) && seenSpelling.Add(phrase)) spellings.Add(phrase);
            }
            foreach (var e in group.Where(e => e.Enabled && !e.IsVocabularyOnly))
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

    public void Add(string phrase, string replacement, bool matchWholeWord = true, bool isSnippet = false)
    {
        lock (_gate)
        {
            _entries.Insert(0, new DictionaryEntry
            {
                Phrase = phrase,
                Replacement = replacement,
                MatchWholeWord = matchWholeWord,
                IsSnippet = isSnippet,
            });
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Edit an entry in place (the dictionary and snippet editors).</summary>
    public void Update(Guid id, string phrase, string replacement)
    {
        lock (_gate)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == id);
            if (entry == null) return;
            ApplyEdit(entry, phrase, replacement);
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// The field changes behind <see cref="Update"/>. Pure, so --selftest can
    /// check it without touching the user's dictionary.
    ///
    /// A hand edit makes a learned entry the user's own: the
    /// <see cref="DictionaryEntry.LearnedFromHistory"/> tag is cleared when the
    /// text actually changes, so "Undo everything learned" keeps the edit (the
    /// undo dialog promises that what the user wrote is kept) and the entry
    /// moves into the user's half of the prompt budget. Saving an editor with
    /// nothing changed keeps the tag, so opening one by accident does not take
    /// an entry out of the undo.
    /// </summary>
    internal static void ApplyEdit(DictionaryEntry entry, string phrase, string replacement)
    {
        if (!string.Equals(entry.Phrase, phrase, StringComparison.Ordinal)
            || !string.Equals(entry.Replacement, replacement, StringComparison.Ordinal))
        {
            entry.LearnedFromHistory = false;
        }
        entry.Phrase = phrase;
        entry.Replacement = replacement;
    }

    /// <summary>
    /// A snippet spoken on its own: "My calendar link." → the link, with no
    /// stray full stop and no capital-letter fixups around it. Compared with
    /// punctuation and spacing stripped, so whisper's punctuation never decides
    /// whether a snippet fires.
    /// </summary>
    public string? WholeUtteranceSnippet(string transcript)
    {
        var key = SnippetKey(transcript);
        if (key.Length == 0) return null;
        lock (_gate)
        {
            foreach (var e in _entries)
            {
                if (!e.Enabled || !e.IsSnippet || e.IsVocabularyOnly) continue;
                if (SnippetKey(e.Phrase) == key) return e.Replacement;
            }
        }
        return null;
    }

    private static string SnippetKey(string s) =>
        new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

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

    // ----- Learned from history (2.1) -----

    /// <summary>
    /// Add entries the user approved in "Learn from your writing" (or that a
    /// background run added with their permission). Returns how many were new.
    ///
    /// Appended, not inserted at the top like <see cref="Add"/>: the order of
    /// the list is the order spellings are spent from the decoder's
    /// 900-character prompt budget, and a batch of forty learned words must
    /// never push out a term the user typed in by hand. One save and one
    /// <see cref="Changed"/> for the whole batch, because the caller is the UI
    /// thread and every Changed redraws the Dictionary page.
    /// </summary>
    public int AddFromHistory(IEnumerable<(string Phrase, string Replacement, bool IsSnippet)> items) =>
        AddFromHistoryEntries(items).Count;

    /// <summary><see cref="AddFromHistory"/>, returning the entries actually added (for per-kind counts).</summary>
    internal IReadOnlyList<DictionaryEntry> AddFromHistoryEntries(
        IEnumerable<(string Phrase, string Replacement, bool IsSnippet)> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var batch = items.ToList();
        List<DictionaryEntry> added;
        lock (_gate)
        {
            added = PlanFromHistory(_entries, batch);
            if (added.Count == 0) return added;
            _entries.AddRange(added);
            Save();
        }
        Changed?.Invoke();
        return added;
    }

    /// <summary>
    /// Which of <paramref name="items"/> are new, in the order given, as the
    /// entries <see cref="AddFromHistory"/> would append. Pure, so --selftest
    /// can check the rules without touching the user's dictionary.
    ///
    /// Already present means:
    /// <list type="bullet">
    /// <item>a word (empty replacement) that the dictionary already gives the
    /// decoder as a spelling — a vocabulary entry, or the target of a rewrite
    /// rule, compared case-insensitively. A second copy would only spend
    /// prompt budget twice; the user's own casing stays.</item>
    /// <item>a rule or snippet whose spoken phrase already has a rule. Two
    /// rules for one phrase is not a choice: <see cref="ApplyRules"/> resolves
    /// the tie by list order, so an appended one could never fire.</item>
    /// </list>
    /// Disabled entries count as present: the user switched that one off, and
    /// learning it again would quietly overrule them. Blank phrases and rules
    /// that rewrite a phrase to exactly itself are skipped.
    /// </summary>
    internal static List<DictionaryEntry> PlanFromHistory(
        IReadOnlyList<DictionaryEntry> existing,
        IEnumerable<(string Phrase, string Replacement, bool IsSnippet)> items)
    {
        var spellings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rulePhrases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in existing)
        {
            if (e.IsVocabularyOnly) spellings.Add(e.Phrase.Trim());
            else
            {
                rulePhrases.Add(e.Phrase.Trim());
                if (e.Replacement.Trim().Length > 0) spellings.Add(e.Replacement.Trim());
            }
        }

        var planned = new List<DictionaryEntry>();
        foreach (var (rawPhrase, rawReplacement, isSnippet) in items)
        {
            var phrase = (rawPhrase ?? "").Trim();
            if (phrase.Length == 0) continue;
            // A replacement of whitespace is a real rule ("-" → " "), so only
            // a replacement with text in it is trimmed.
            var replacement = rawReplacement ?? "";
            if (replacement.Trim().Length > 0) replacement = replacement.Trim();

            if (replacement.Length == 0)
            {
                if (!spellings.Add(phrase)) continue;
            }
            else
            {
                if (string.Equals(phrase, replacement, StringComparison.Ordinal)) continue;
                if (!rulePhrases.Add(phrase)) continue;
                if (replacement.Trim().Length > 0) spellings.Add(replacement);
            }

            planned.Add(new DictionaryEntry
            {
                Phrase = phrase,
                Replacement = replacement,
                MatchWholeWord = true,
                IsSnippet = isSnippet && replacement.Length > 0,
                LearnedFromHistory = true,
            });
        }
        return planned;
    }

    /// <summary>
    /// Remove every entry "Learn from your writing" added — the one-click undo.
    /// Entries the user added or edited by hand are not tagged and stay
    /// (<see cref="ApplyEdit"/> clears the tag on an edit). Returns how many
    /// were removed.
    /// </summary>
    public int RemoveLearnedFromHistory() => RemoveLearnedFromHistoryEntries().Count;

    /// <summary>
    /// <see cref="RemoveLearnedFromHistory"/>, returning the entries removed, so
    /// the learner can remember them as "don't suggest again" — otherwise the
    /// next daily run would find the same words in the same history and put
    /// them straight back.
    /// </summary>
    internal IReadOnlyList<DictionaryEntry> RemoveLearnedFromHistoryEntries()
    {
        List<DictionaryEntry> removed;
        lock (_gate)
        {
            removed = _entries.Where(e => e.LearnedFromHistory).ToList();
            if (removed.Count == 0) return removed;
            _entries.RemoveAll(e => e.LearnedFromHistory);
            Save();
        }
        Changed?.Invoke();
        return removed;
    }

    /// <summary>How many entries came from "Learn from your writing" (the undo link's count).</summary>
    public int LearnedFromHistoryCount
    {
        get { lock (_gate) return _entries.Count(e => e.LearnedFromHistory); }
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
