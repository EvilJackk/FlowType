using System.Text.RegularExpressions;
using FlowType.Core;

namespace FlowType.Engine;

/// <summary>
/// How finished text should read, chosen per kind of app — the same idea as
/// Wispr Flow's Styles (formal / casual / very casual / excited, set separately
/// for personal messages, work messages, email and everything else). Wispr
/// applies them in its cloud formatter; FlowType applies the mechanical part
/// here, deterministically, and hands the rest to the local AI polish as an
/// instruction when that is switched on.
/// </summary>
public static class WritingStyles
{
    public const string Polished = "formal";
    public const string Casual = "casual";
    public const string VeryCasual = "very-casual";
    public const string Excited = "excited";

    public static readonly (string Id, string Label, string Example)[] All =
    {
        (Polished, "Formal", "Hey Sam, are you free for lunch tomorrow? Let's do 12 if that works for you."),
        (Casual, "Casual", "Hey Sam, are you free for lunch tomorrow? Let's do 12 if that works for you"),
        (VeryCasual, "Very casual", "hey Sam, are you free for lunch tomorrow? let's do 12 if that works for you"),
        (Excited, "Excited", "Hey Sam, are you free for lunch tomorrow? Let's do 12 if that works for you!"),
    };

    /// <summary>The four buckets styles are chosen for.</summary>
    public enum Bucket { Personal, Work, Email, Other }

    public static Bucket BucketFor(AppKind kind) => kind switch
    {
        AppKind.PersonalMessages => Bucket.Personal,
        AppKind.WorkMessages => Bucket.Work,
        AppKind.Email => Bucket.Email,
        _ => Bucket.Other,
    };

    public static string BucketLabel(Bucket bucket) => bucket switch
    {
        Bucket.Personal => "Personal messages",
        Bucket.Work => "Work messages",
        Bucket.Email => "Email",
        _ => "Everything else",
    };

    /// <summary>The style the user picked for this kind of app.</summary>
    public static string For(AppKind kind)
    {
        var s = SettingsStore.Instance.Settings;
        var id = BucketFor(kind) switch
        {
            Bucket.Personal => s.StylePersonal,
            Bucket.Work => s.StyleWork,
            Bucket.Email => s.StyleEmail,
            _ => s.StyleOther,
        };
        return Normalize(id);
    }

    public static string Normalize(string? id) =>
        All.Any(a => a.Id == id) ? id! : Polished;

    /// <summary>Instruction text for the AI polish prompt.</summary>
    public static string Describe(string style) => Normalize(style) switch
    {
        Casual => "casual — conversational, keep the speaker's informal words, no period after the final sentence",
        VeryCasual => "very casual — all lowercase except names and acronyms, minimal punctuation, no final period",
        Excited => "excited — warm and upbeat, end the final sentence with an exclamation mark",
        _ => "formal — clear, complete sentences with standard capitalization and punctuation",
    };

    /// <summary>
    /// The whole first word of each sentence. Matching only the capital and
    /// the lowercase run after it used to see "NASA" as "N" and "GitHub" as
    /// "Git", lowercase their first letter, and compare that fragment against
    /// the protected words — so no dictionary term was ever protected.
    /// </summary>
    private static readonly Regex SentenceStart = new(@"(^|[.!?]\s+|\n+)(\p{Lu})([\p{L}\p{N}'’-]*)", RegexOptions.Compiled);

    /// <summary>
    /// The deterministic half of a style, applied after formatting whether or
    /// not AI polish ran. Structured text (lists, multiple lines, code-ish
    /// content) is left alone — a style is about tone, not layout.
    /// </summary>
    /// <param name="protectedWords">
    /// Words that keep their capital in "very casual": the user's dictionary
    /// terms and names seen on screen. "I" and acronyms are always kept.
    /// </param>
    public static string Apply(string text, string style, IReadOnlyCollection<string>? protectedWords = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        style = Normalize(style);
        if (style == Polished) return text;
        if (text.Contains('\n') || TextFormatter.HasStructuralGuard(text)) return text;

        var result = text;
        if (style == VeryCasual)
        {
            var keep = protectedWords is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(protectedWords.SelectMany(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
                    StringComparer.Ordinal);
            result = SentenceStart.Replace(result, m =>
            {
                var rest = m.Groups[3].Value;
                var word = m.Groups[2].Value + rest;
                if (KeepsCapital(word, rest, keep)) return m.Value;
                return m.Groups[1].Value + m.Groups[2].Value.ToLowerInvariant() + rest;
            });
        }

        if (style is Casual or VeryCasual)
        {
            result = DropFinalPeriod(result);
        }
        else if (style == Excited)
        {
            // Same guard as DropFinalPeriod: the dot of "etc." or "U.S." is
            // part of the word, and "U.S!" is a misspelling, not excitement.
            var trimmed = result.TrimEnd();
            if (EndsWithSentencePeriod(trimmed)) result = trimmed[..^1] + "!";
        }
        return result;
    }

    /// <summary>
    /// Whether a sentence-initial word keeps its capital in "very casual":
    /// "I" and its contractions ("I'm", "I’ll"), acronyms and mixed case
    /// ("NASA", "GitHub", "Wi-Fi" — any capital after the first letter), and
    /// the user's protected words, also in the possessive ("Sam's").
    /// </summary>
    private static bool KeepsCapital(string word, string rest, HashSet<string> keep)
    {
        if (word == "I" || word.StartsWith("I'", StringComparison.Ordinal) || word.StartsWith("I’", StringComparison.Ordinal)) return true;
        if (rest.Any(char.IsUpper)) return true;
        if (keep.Contains(word)) return true;
        foreach (var possessive in new[] { "'s", "’s" })
        {
            if (word.EndsWith(possessive, StringComparison.Ordinal) && keep.Contains(word[..^possessive.Length])) return true;
        }
        return false;
    }

    /// <summary>"Sounds good." → "Sounds good". Never touches "..." or abbreviations like "etc." and "U.S."</summary>
    internal static string DropFinalPeriod(string text)
    {
        var trimmed = text.TrimEnd();
        return EndsWithSentencePeriod(trimmed) ? trimmed[..^1] : text;
    }

    /// <summary>
    /// The text ends in a full stop that belongs to the sentence: not an
    /// ellipsis, and not the last dot of an abbreviation or a dotted
    /// initialism. <paramref name="trimmed"/> has no trailing whitespace.
    /// </summary>
    private static bool EndsWithSentencePeriod(string trimmed)
    {
        if (!trimmed.EndsWith('.') || trimmed.EndsWith("..")) return false;
        var lastToken = trimmed[(trimmed.LastIndexOfAny(new[] { ' ', '\t', '(', '"', '“' }) + 1)..];
        return !Abbreviation.IsMatch(lastToken);
    }

    /// <summary>
    /// Common abbreviations (with "Ph.D.", which is not a plain initialism),
    /// plus any dotted initialism of two or more letters: "U.S.", "U.K.",
    /// "e.g.", "a.m.", "D.C.".
    /// </summary>
    private static readonly Regex Abbreviation = new(
        @"^(?:(?:etc|e\.g|i\.e|vs|approx|dept|inc|ltd|co|jr|sr|st|mr|mrs|ms|dr|a\.m|p\.m|ph\.d)\.|(?:\p{L}\.){2,})$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
