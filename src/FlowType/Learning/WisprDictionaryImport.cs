using FlowType.Engine;
using FlowType.Session;

namespace FlowType.Learning;

/// <summary>
/// Offers the entries of Wispr Flow's own dictionary to FlowType.
///
/// These are the most deliberate signal there is: the user (or Wispr, from
/// the user's edits) already decided each one is worth keeping, so all of them
/// are pre-ticked. The only judgement left is which FlowType entry each one
/// becomes — a word to recognise, a rewrite rule or a voice shortcut — and
/// whether FlowType already has it.
/// </summary>
public static class WisprDictionaryImport
{
    public const string Origin = "Wispr Flow dictionary";

    public static IReadOnlyList<Suggestion> Suggest(
        IReadOnlyList<WisprDictionaryEntry> wispr, IReadOnlyList<DictionaryEntry> dictionary)
    {
        var known = DictionaryCoverage.From(dictionary);
        var result = new List<Suggestion>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in wispr)
        {
            var suggestion = ToSuggestion(entry);
            if (suggestion == null) continue;
            if (known.Covers(suggestion)) continue;
            if (!seen.Add(suggestion.Key)) continue;     // Wispr listing the same entry twice
            result.Add(suggestion);
        }
        return result;
    }

    /// <summary>
    /// What one Wispr entry becomes. A replacement that is blank or only
    /// re-cases the phrase is a plain word, and the replacement's spelling is
    /// the one kept ("claude code" → "Claude Code" is the word "Claude Code").
    /// </summary>
    private static Suggestion? ToSuggestion(WisprDictionaryEntry entry)
    {
        var phrase = (entry.Phrase ?? "").Trim();
        var replacement = (entry.Replacement ?? "").Trim();
        if (phrase.Length == 0) return null;

        if (entry.IsSnippet && replacement.Length > 0)
        {
            return Make(SuggestionKind.Snippet, phrase, replacement, entry.Origin,
                example: replacement.Length <= 120 ? replacement : replacement[..119] + "…");
        }
        if (replacement.Length == 0 || replacement.Equals(phrase, StringComparison.OrdinalIgnoreCase))
        {
            var word = replacement.Length > 0 ? replacement : phrase;
            return Make(SuggestionKind.Vocabulary, word, "", entry.Origin, example: null);
        }
        return Make(SuggestionKind.Correction, phrase, replacement, entry.Origin, example: null);
    }

    private static Suggestion Make(SuggestionKind kind, string phrase, string replacement, string? wisprOrigin, string? example) =>
        new(kind,
            Suggestion.KeyFor(kind, phrase, replacement),
            phrase,
            replacement,
            Sightings: 1,
            Score: 1.0,
            Recommended: true,
            Why: WhyFor(kind, wisprOrigin),
            Origin: Origin,
            Example: example);

    /// <summary>Wispr's <c>source</c> column, in words: who put the entry there.</summary>
    private static string WhyFor(SuggestionKind kind, string? wisprOrigin)
    {
        var what = kind switch
        {
            SuggestionKind.Snippet => "A shortcut",
            SuggestionKind.Correction => "A fix",
            _ => "A word",
        };
        return (wisprOrigin ?? "").Trim().ToLowerInvariant() switch
        {
            "manual" => $"{what} you added to Wispr Flow",
            "user_edits" => $"{what} Wispr Flow learned from your edits",
            "default" => $"{what} Wispr Flow included by default",
            _ => $"{what} from your Wispr Flow dictionary",
        };
    }
}

/// <summary>
/// What the user's FlowType dictionary already holds, in the shapes the
/// miners ask about. Disabled entries count too: the user switched them off
/// on purpose, and offering them again would be nagging.
/// </summary>
internal sealed class DictionaryCoverage
{
    private readonly HashSet<string> _spellings = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pairs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ruleTriggers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _snippetKeys = new(StringComparer.Ordinal);

    public static DictionaryCoverage From(IReadOnlyList<DictionaryEntry> dictionary)
    {
        var known = new DictionaryCoverage();
        foreach (var e in dictionary)
        {
            var phrase = Clean(e.Phrase);
            if (phrase.Length == 0) continue;
            if (e.IsVocabularyOnly)
            {
                known._spellings.Add(phrase);
                continue;
            }
            var replacement = Clean(e.Replacement);
            if (replacement.Length > 0) known._spellings.Add(replacement);
            known._pairs.Add(phrase + "\u0001" + replacement);
            known._ruleTriggers.Add(phrase);
            if (e.IsSnippet) known._snippetKeys.Add(SnippetKey(phrase));
        }
        return known;
    }

    /// <summary>The term is already a vocabulary word or the target of a rule.</summary>
    public bool HasSpelling(string term) => _spellings.Contains(Clean(term));

    /// <summary>This exact heard → written pair is already a rule.</summary>
    public bool HasPair(string heard, string write) => _pairs.Contains(Clean(heard) + "\u0001" + Clean(write));

    /// <summary>
    /// Some rule already fires on this phrase. A second rule with the same
    /// trigger could never win (rules apply in list order), so it is never
    /// worth proposing.
    /// </summary>
    public bool HasTrigger(string heard) => _ruleTriggers.Contains(Clean(heard));

    public bool Covers(Suggestion s) => s.Kind switch
    {
        SuggestionKind.Vocabulary => HasSpelling(s.Phrase),
        SuggestionKind.Snippet => _snippetKeys.Contains(SnippetKey(s.Phrase)) || HasTrigger(s.Phrase),
        SuggestionKind.Correction => HasPair(s.Phrase, s.Replacement) || HasTrigger(s.Phrase),
        _ => false,
    };

    private static string Clean(string? s) => Transcriber.SanitizeTerm(s ?? "");

    /// <summary>Snippets match on letters and digits only, as <see cref="DictionaryStore"/> does.</summary>
    private static string SnippetKey(string s) =>
        new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
