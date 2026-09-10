using System.Text.RegularExpressions;
using FlowType.Core;

namespace FlowType.Engine;

public record FormatterOptions(
    bool RemoveFillers,
    bool ScratchThat,
    bool LineCommands,
    bool PunctuationCommands,
    bool SmartTrailingPunctuation,
    string EnglishVariant = Engine.EnglishVariant.Off,
    string Language = "",
    bool FuzzyVocabulary = true,
    IReadOnlyList<string>? Vocabulary = null,
    bool EnglishOnlyModel = false)
{
    /// <summary>
    /// Whether the English-only filler list may be applied.
    ///
    /// Fails closed. "um" is an article in Portuguese and a preposition in
    /// German, so it is dropped only on evidence that English was actually
    /// decoded — not because the *setting* says "auto", which is the shipped
    /// default and would have applied the English list to every language.
    /// "Unknown" counts only when the loaded model can produce nothing else.
    /// </summary>
    public bool EnglishFillersAllowed =>
        string.IsNullOrWhiteSpace(Language)
            ? EnglishOnlyModel
            : Language is "en" || Language.StartsWith("en-", StringComparison.OrdinalIgnoreCase);

    /// <param name="decodedLanguage">
    /// What the model actually produced, not what was requested — null when the
    /// decode gave no answer.
    /// </param>
    public static FormatterOptions FromSettings(IReadOnlyList<string>? vocabulary = null,
        string? decodedLanguage = null, bool englishOnlyModel = false)
    {
        var s = SettingsStore.Instance.Settings;
        return new FormatterOptions(
            s.RemoveFillers, s.ScratchThat, s.LineCommands,
            s.PunctuationCommands, s.SmartTrailingPunctuation, s.EnglishVariant,
            decodedLanguage ?? "", s.FuzzyVocabulary, vocabulary, englishOnlyModel);
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
///   6. dictionary rules (injected) — snippets + explicit rewrites
///   7. fuzzy vocabulary — near-misses of the user's own names and terms
///   8. smart trailing punctuation — no stray period after emails/URLs/numbers
///   9. whitespace + capitalization cleanup
/// </summary>
public static class TextFormatter
{
    public static string Apply(string raw, FormatterOptions options,
        Func<string, string>? applyDictionary = null)
    {
        var text = raw;
        if (string.IsNullOrWhiteSpace(text)) return "";

        // Decided once, on the raw transcript, and carried: the line commands
        // below insert newlines, and recomputing after that would flip the
        // guard on for ordinary prose.
        var structural = HasStructuralGuard(raw);

        if (options.ScratchThat) text = ApplyScratchThat(text);
        if (options.RemoveFillers) text = RemoveFillers(text, options, structural);
        if (options.LineCommands) text = ApplyLineCommands(text);
        if (options.PunctuationCommands) text = ApplyPunctuationCommands(text);
        // Spelling convention runs before the dictionary so a user's explicit
        // rule always has the final word over the variant mapping — and only on
        // English, because tire -> tyre on French text is the same trap the
        // filler lists were.
        if (options.EnglishFillersAllowed)
        {
            text = Engine.EnglishVariant.Convert(text, options.EnglishVariant);
        }
        if (applyDictionary != null) text = applyDictionary(text);
        // Explicit rules first — they are the user's stated intent and may
        // expand into whole snippets. The fuzzy pass then catches the
        // mis-hearings no rule was ever written for.
        if (options.FuzzyVocabulary && options.Vocabulary is { Count: > 0 })
        {
            text = VocabularyMatcher.Apply(text, options.Vocabulary);
        }
        if (options.SmartTrailingPunctuation) text = StripSmartTrailingPeriod(text);

        return FinalCleanup(text, structural);
    }

    public static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    // ----- Sentence-boundary space (applied at insertion, not to stored text) -----

    /// <summary>Closers that sit outside the sentence-ending punctuation.</summary>
    private const string Closers = "\"'”’)]}»";

    private static readonly string[] DomainEndings =
        { ".com", ".org", ".net", ".io", ".dev", ".app", ".co", ".uk" };

    /// <summary>
    /// Dictating three sentences as three separate takes used to produce
    /// "One.Two.Three." — nothing separates one insertion from the next. A
    /// single space after a finished sentence is what makes back-to-back
    /// dictation feel continuous.
    ///
    /// Deliberately narrow: only after a real sentence end, never after an
    /// address, a link or anything that looks like code, and never on
    /// multi-line text where the caller is clearly not mid-paragraph. Applied
    /// to the text on its way into the target app only — what goes into Notes
    /// stays clean, so re-copying from history never accumulates spaces.
    /// </summary>
    public static string WithSentenceSpace(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // Only spaces: a trailing tab or newline is deliberate structure.
        var trimmed = text.TrimEnd(' ');
        if (trimmed.Length == 0) return text;
        if (trimmed.Contains('\n') || trimmed.Contains('\r') || trimmed.EndsWith('\t')) return text;

        // Step past closing quotes/brackets to the character that actually ends
        // the sentence: He said "yes." is finished, and so is (that's all).
        var i = trimmed.Length - 1;
        while (i >= 0 && Closers.Contains(trimmed[i])) i--;
        if (i < 0 || trimmed[i] is not ('.' or '!' or '?')) return text;

        // Code markers anywhere in the line disqualify it — "x = y." is an
        // assignment, not a sentence. Address shapes are checked on the last
        // token only, since a sentence may legitimately mention one earlier.
        if (LooksLikeCode(trimmed)) return text;
        if (LooksLikeAddress(LastToken(trimmed))) return text;

        return trimmed + " ";
    }

    private static string LastToken(string text)
    {
        var start = text.LastIndexOfAny(new[] { ' ', '\t', '\n' });
        return start < 0 ? text : text[(start + 1)..];
    }

    private static bool LooksLikeCode(string text) =>
        text.Contains('=') || text.Contains("->") || text.Contains("::")
        || text.Contains('{') || text.Contains('}') || text.Contains(';')
        || text.Contains('`');

    /// <summary>A tail that is really a URL or an email gets no space.</summary>
    private static bool LooksLikeAddress(string token)
    {
        if (token.Contains("://")) return true;
        var at = token.IndexOf('@');
        if (at > 0 && at + 1 < token.Length) return true;

        var body = token.TrimEnd('.', '!', '?').TrimEnd(Closers.ToCharArray());
        return DomainEndings.Any(d => body.EndsWith(d, StringComparison.OrdinalIgnoreCase));
    }

    // ----- Structural guard -----

    /// <summary>
    /// True when the transcript carries structure the tidy-up passes would
    /// destroy: dictating "let x  =  1" or "foo :: bar" into an editor must not
    /// come back as "let x = 1" and "foo:: bar". Computed once on the *raw*
    /// transcript — recomputing after the line commands have inserted newlines
    /// would switch the guard on for ordinary prose.
    /// </summary>
    internal static bool HasStructuralGuard(string text) =>
        text.Contains('\n') || text.Contains('\r') || text.Contains('\t')
        || text.Contains("  ") || text.Contains('`') || text.Contains(" = ")
        || text.Contains("->") || text.Contains("=>") || text.Contains("::")
        || text.Contains('{') || text.Contains('}');

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

    /// <summary>
    /// Hesitation sounds that are not a real word in any language whisper can
    /// output, so dropping them is safe whatever was spoken.
    /// </summary>
    private static readonly string[] UniversalFillers =
        { "uh+", "uhh+", "uhm+", "umm+", "ehm+", "ahm+", "hmm+", "mmm+" };

    /// <summary>
    /// Fillers that are a real word somewhere else — "um" is Portuguese for
    /// "a", German for "around"; "mm" is millimetres; "er" is a German pronoun
    /// — so they are only removed when the transcript is English.
    /// </summary>
    private static readonly string[] EnglishFillers =
        { "um+", "erm+", "er+", "ah", "eh", "hm", "mm" };

    private static readonly Regex UniversalFillerPattern = BuildFillerPattern(UniversalFillers);
    private static readonly Regex EnglishFillerPattern = BuildFillerPattern(EnglishFillers);

    /// <summary>
    /// Word-bounded, hyphen-aware, and never a unit: "5 mm" and "ah-ha" survive.
    /// A trailing comma left dangling by the removal goes with it.
    /// </summary>
    private static Regex BuildFillerPattern(string[] words) => new(
        $@"(?<![\w\-])(?<!\d\s{{0,4}})(?:{string.Join("|", words)})(?![\w\-])[,]?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string RemoveFillers(string text, FormatterOptions options, bool structural)
    {
        var result = UniversalFillerPattern.Replace(text, "");
        if (options.EnglishFillersAllowed) result = EnglishFillerPattern.Replace(result, "");
        result = TidyPunctuation(result, structural);

        // "Eh?" or "Hmm." is the whole utterance: removing the filler would
        // leave nothing to type. Keep what was actually said instead.
        return result.Any(char.IsLetterOrDigit) ? result : text;
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
        result = TidyPunctuation(result, structural: false);
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
    private static string TidyPunctuation(string text, bool structural)
    {
        var result = text;
        if (!structural)
        {
            // "foo :: bar" and column-aligned code must keep their spacing.
            result = Regex.Replace(result, @"[ \t]+([,.!?;:])", "$1");
        }
        if (!structural)
        {
            // Doubled punctuation is filler-removal debris in prose — but "::"
            // is an operator, so this must not run on structural text either.
            result = Regex.Replace(result, @"([,;:])(?:\s*[,;:])+", "$1");
            result = Regex.Replace(result, @"([.!?])\s*,", "$1");
            result = Regex.Replace(result, @"[ \t]{2,}", " ");
        }
        return result;
    }

    private static string FinalCleanup(string text, bool structural)
    {
        var result = text.Replace("\r\n", "\n").Replace('\r', '\n');
        result = Regex.Replace(result, @"\n{3,}", "\n\n");
        if (!structural)
        {
            // Indentation and column alignment are content when the transcript
            // is code; everywhere else they are transcription noise.
            result = Regex.Replace(result, @"[ \t]+\n", "\n");
            result = Regex.Replace(result, @"\n[ \t]+", "\n");
            result = Regex.Replace(result, @"[ \t]{2,}", " ");
        }
        result = result.Trim();
        if (result.Length == 0) return "";

        // Leading punctuation orphaned by edits ("," hello → hello).
        result = Regex.Replace(result, @"^[\s,.;:!?]+", "");
        if (result.Length == 0) return "";

        if (structural) return result;

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
