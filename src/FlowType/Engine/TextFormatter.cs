using System.Text.RegularExpressions;
using FlowType.Core;

namespace FlowType.Engine;

public record FormatterOptions(
    bool RemoveFillers,
    bool ScratchThat,
    bool LineCommands,
    bool PunctuationCommands,
    bool SmartTrailingPunctuation,
    string EnglishVariant = Engine.EnglishVariant.Off)
{
    public static FormatterOptions FromSettings()
    {
        var s = SettingsStore.Instance.Settings;
        return new FormatterOptions(
            s.RemoveFillers, s.ScratchThat, s.LineCommands,
            s.PunctuationCommands, s.SmartTrailingPunctuation, s.EnglishVariant);
    }
}

/// <summary>
/// FlowType's local stand-in for Wispr Flow's AI formatting layer — a
/// deterministic pipeline over the raw transcript:
///
///   1. "scratch that" — keep only what was said after the correction
///   2. filler removal — um / uh / erm and their attached commas
///   3. line commands — "new line" / "new paragraph" become breaks
///   4. spoken punctuation (opt-in) — "period", "comma", "question mark"…
///   5. English variant — British/American spelling conversion
///   6. dictionary rules (injected) — snippets + vocabulary fixes
///   7. smart trailing punctuation — no stray period after emails/URLs/numbers
///   8. whitespace + capitalization cleanup
/// </summary>
public static class TextFormatter
{
    public static string Apply(string raw, FormatterOptions options,
        Func<string, string>? applyDictionary = null)
    {
        var text = raw;
        if (string.IsNullOrWhiteSpace(text)) return "";

        if (options.ScratchThat) text = ApplyScratchThat(text);
        if (options.RemoveFillers) text = RemoveFillers(text);
        if (options.LineCommands) text = ApplyLineCommands(text);
        if (options.PunctuationCommands) text = ApplyPunctuationCommands(text);
        // Spelling convention runs before the dictionary so a user's explicit
        // rule always has the final word over the variant mapping.
        text = Engine.EnglishVariant.Convert(text, options.EnglishVariant);
        if (applyDictionary != null) text = applyDictionary(text);
        if (options.SmartTrailingPunctuation) text = StripSmartTrailingPeriod(text);

        return FinalCleanup(text);
    }

    public static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    // ----- 1. Scratch that -----

    /// <summary>"…wrong thing, scratch that, right thing" → "right thing".</summary>
    private static string ApplyScratchThat(string text)
    {
        var parts = Regex.Split(text,
            @"[,.!?]?\s*\b(?:scratch|strike)\s+that\b[,.!?]?\s*",
            RegexOptions.IgnoreCase);
        return parts.Length <= 1 ? text : parts[^1];
    }

    // ----- 2. Fillers -----

    private static string RemoveFillers(string text)
    {
        var result = Regex.Replace(text, @"\b(?:um+|uh+|erm+|uhm+)\b",
            "", RegexOptions.IgnoreCase);
        return TidyPunctuation(result);
    }

    // ----- 3. Line commands -----

    private static string ApplyLineCommands(string text)
    {
        // Strip only a *comma* before the command (whisper writes "…, new line,
        // …" mid-sentence) — a period/!/? there is real sentence punctuation
        // and must survive: "Send the report. New line. Thanks."
        var result = Regex.Replace(text,
            @",?\s*\bnew\s+paragraph\b[,.!?]?\s*", "\n\n", RegexOptions.IgnoreCase);
        result = Regex.Replace(result,
            @",?\s*\bnew\s?line\b[,.!?]?\s*", "\n", RegexOptions.IgnoreCase);
        return result;
    }

    // ----- 4. Spoken punctuation (opt-in) -----

    private static readonly (string Pattern, string Symbol)[] PunctuationPhrases =
    {
        (@"question\s+mark", "?"),
        (@"exclamation\s+(?:mark|point)", "!"),
        (@"full\s+stop", "."),
        (@"semicolon", ";"),
        (@"colon", ":"),
        (@"comma", ","),
        (@"period", "."),
        (@"open\s+quote", "“"),
        (@"close\s+quote", "”"),
    };

    private static string ApplyPunctuationCommands(string text)
    {
        var result = text;
        foreach (var (pattern, symbol) in PunctuationPhrases)
        {
            result = Regex.Replace(result,
                $@"(?:,\s*)?\b{pattern}\b[,.]?", symbol, RegexOptions.IgnoreCase);
        }
        result = TidyPunctuation(result);
        // Users dictating explicit punctuation expect sentence casing after it.
        result = Regex.Replace(result, @"([.!?]\s+)(\p{Ll})",
            m => m.Groups[1].Value + char.ToUpper(m.Groups[2].Value[0]));
        return result;
    }

    // ----- 6. Smart trailing punctuation (port of the conservative heuristic) -----

    /// <summary>
    /// Drops the sentence-final period only when the whole transcript is an
    /// email, URL, number, or single bare token — so dictating an address into
    /// a login field doesn't end with a stray dot. Prose is never touched.
    /// </summary>
    internal static string StripSmartTrailingPeriod(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.EndsWith('.') || trimmed.EndsWith("..")) return text;

        var candidate = trimmed[..^1];
        if (candidate.Length == 0) return text;

        var isNumber = Regex.IsMatch(candidate, @"^\+?\(?\d(?:[\d\s.,\-()/:]*\d)?$");
        var isEmail = Regex.IsMatch(candidate, @"^[^\s@]+@[^\s@]+\.[^\s@]+$");
        var isUrl = Regex.IsMatch(candidate,
            @"^(?:[a-z][a-z0-9+.\-]*://\S+|www\.\S+\.\S+|[a-z0-9\-]+(?:\.[a-z0-9\-]+)*\.[a-z]{2,}(?:[/:?#]\S*)?)$",
            RegexOptions.IgnoreCase);
        var isPlainToken = !candidate.Any(char.IsWhiteSpace) && !candidate.Contains('.');

        return isNumber || isEmail || isUrl || isPlainToken ? candidate : text;
    }

    // ----- 7. Cleanup -----

    /// <summary>
    /// Don't sentence-case starts that aren't prose: a lone token (email, URL,
    /// identifier) or anything whose first word contains @ or a scheme.
    /// </summary>
    private static bool ShouldCapitalizeStart(string text)
    {
        var firstBreak = text.IndexOfAny(new[] { ' ', '\n', '\t' });
        var firstToken = firstBreak < 0 ? text : text[..firstBreak];
        if (firstBreak < 0) return false;  // single-token transcript — leave as-is
        return !firstToken.Contains('@') && !firstToken.Contains("://")
            && !firstToken.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Fix artifacts left by removals: doubled commas, space-before-punct…</summary>
    private static string TidyPunctuation(string text)
    {
        var result = Regex.Replace(text, @"[ \t]+([,.!?;:])", "$1");
        result = Regex.Replace(result, @"([,;:])(?:\s*[,;:])+", "$1");
        result = Regex.Replace(result, @"([.!?])\s*,", "$1");
        result = Regex.Replace(result, @"[ \t]{2,}", " ");
        return result;
    }

    private static string FinalCleanup(string text)
    {
        var result = text.Replace("\r\n", "\n").Replace('\r', '\n');
        result = Regex.Replace(result, @"[ \t]+\n", "\n");
        result = Regex.Replace(result, @"\n[ \t]+", "\n");
        result = Regex.Replace(result, @"\n{3,}", "\n\n");
        result = Regex.Replace(result, @"[ \t]{2,}", " ");
        result = result.Trim();
        if (result.Length == 0) return "";

        // Leading punctuation orphaned by edits ("," hello → hello).
        result = Regex.Replace(result, @"^[\s,.;:!?]+", "");
        if (result.Length == 0) return "";

        // Sentence-case the very start and the start of every forced line break —
        // but never re-case an email/URL/bare token ("roy@example.com" must not
        // become "Roy@…"). Whisper capitalizes real prose on its own anyway.
        if (ShouldCapitalizeStart(result) && char.IsLower(result[0]))
        {
            result = char.ToUpper(result[0]) + result[1..];
        }
        result = Regex.Replace(result, @"(\n+)(\p{Ll})",
            m => m.Groups[1].Value + char.ToUpper(m.Groups[2].Value[0]));
        return result;
    }
}
