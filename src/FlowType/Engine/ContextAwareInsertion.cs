using System.Globalization;

namespace FlowType.Engine;

/// <summary>What surrounds the caret in the field text is about to go into.</summary>
public sealed record CaretContext(string Before, string After, string Selection, bool Known)
{
    public static CaretContext Unknown { get; } = new("", "", "", false);

    /// <summary>Lengths only: a record's generated ToString would print the user's field text into any log.</summary>
    public override string ToString() =>
        $"CaretContext(known={Known}, before={Before.Length}, selection={Selection.Length}, after={After.Length})";
}

/// <summary>
/// Make a dictation fit where it lands: no capital letter in the middle of a
/// sentence, no full stop before the rest of a sentence, exactly one space on
/// each side, and a selection replaced like-for-like.
///
/// Wispr Flow does this on the client, deterministically, from the text box's
/// before/after/selection that its native helper reads through accessibility
/// (Windows: UI Automation TextPattern). This is FlowType's own implementation
/// of the same behaviour, written from the observed rules, not from their code.
///
/// When the field could not be read (<see cref="CaretContext.Known"/> false)
/// nothing is changed here and the caller falls back to the old heuristic of
/// one space after a finished sentence.
/// </summary>
public static class ContextAwareInsertion
{
    /// <summary>
    /// Characters after which a sentence is not over. Straight quotes are not
    /// here: " and ' open and close alike, so they are classified on their own
    /// (<see cref="StraightQuoteCloses"/>).
    /// </summary>
    private const string MidSentenceTail = ",-–—/:;([{“‘`";

    /// <summary>Characters after which inserted text needs a separating space (straight quotes: see above).</summary>
    private const string SpaceAfter = "!%),.:;>?]}”’";

    /// <summary>Characters that, at the start of what follows, mean "same sentence continues".</summary>
    private const string ContinuationStart = ",.;:!?%)>]}";

    /// <summary>Characters that open something; text typed straight before one needs a space.</summary>
    private const string OpensAfter = "([{“‘";

    public readonly record struct Result(string Text, bool LoweredFirst, bool StrippedEnd, bool AddedLeadingSpace, bool AddedTrailingSpace);

    /// <param name="isProperNoun">
    /// Whether a word must keep its capital (a name, a dictionary term, "I",
    /// an acronym). Lowercasing "Sarah" because the caret is mid-sentence would
    /// be worse than leaving a stray capital.
    /// </param>
    /// <param name="appendTrailingSpace">
    /// The user's "add a space after each dictation" setting. Only the space
    /// left at the very end of the field depends on it; the space that keeps
    /// the dictation off a following word is part of fitting, not a habit.
    /// </param>
    public static Result Fit(string text, CaretContext context, Func<string, bool> isProperNoun,
        bool appendTrailingSpace = true)
    {
        if (string.IsNullOrEmpty(text) || !context.Known) return new Result(text, false, false, false, false);
        // Multi-line dictations are blocks: they are inserted as written.
        if (text.Contains('\n')) return new Result(text, false, false, false, false);

        var body = text.Trim();
        if (body.Length == 0) return new Result(text, false, false, false, false);
        var lowered = false;
        var stripped = false;
        var lead = false;
        var trail = false;

        if (context.Selection.Length > 0)
        {
            // Replacing a selection: mirror it. A lowercase selection gets a
            // lowercase replacement; one without terminal punctuation gets none.
            var sel = context.Selection;
            var selCore = sel.Trim();
            if (selCore.Length > 0 && char.IsLower(selCore[0]) && StartsWithLowerable(body, isProperNoun))
            {
                body = LowerFirst(body); lowered = true;
            }
            if (selCore.Length > 0 && !"!%),.:;>?]}".Contains(selCore[^1]))
            {
                var t = body.TrimEnd('.', '!', '?');
                if (t.Length > 0 && t.Length != body.Length) { body = t; stripped = true; }
            }
            var leading = sel.Length - sel.TrimStart().Length;
            var trailing = sel.Length - sel.TrimEnd().Length;
            if (leading > 0) { body = sel[..leading] + body; lead = true; }
            if (trailing > 0) { body += sel[^trailing..]; trail = true; }
            return new Result(body, lowered, stripped, lead, trail);
        }

        var before = LastLine(context.Before);
        var after = FirstLine(context.After);
        var beforeCore = before.TrimEnd();

        // 1. Mid-sentence: continue in lowercase.
        if (ContinuesSentence(beforeCore) && StartsWithLowerable(body, isProperNoun))
        {
            body = LowerFirst(body);
            lowered = true;
        }

        // 2. The sentence carries on after the caret: no full stop of our own.
        var afterCore = after.TrimStart();
        if (afterCore.Length > 0 && (char.IsLower(afterCore[0]) || ContinuationStart.Contains(afterCore[0])))
        {
            var t = body.TrimEnd('.', '!', '?');
            if (t.Length > 0 && t.Length != body.Length) { body = t; stripped = true; }
        }

        // 3. Spacing. Never between CJK characters, which do not use spaces.
        if (before.Length > 0 && !char.IsWhiteSpace(before[^1]) && NeedsSpaceAfter(before) && !IsCjk(before[^1])
            && !IsCjk(body[0]))
        {
            body = " " + body;
            lead = true;
        }
        if (after.Length == 0 && context.After.Length == 0)
        {
            // End of the field: leave one space so the next dictation does not
            // run into this one — but only after a finished sentence, the same
            // rule FlowType always used when it could not see the field, and
            // only if the user wants that space at all.
            if (appendTrailingSpace && EndsSentence(body) && !IsCjk(body.TrimEnd()[^1])) { body += " "; trail = true; }
        }
        else if (after.Length > 0 && !char.IsWhiteSpace(after[0]) && NeedsSpaceBefore(body, after))
        {
            body += " ";
            trail = true;
        }

        return new Result(body, lowered, stripped, lead, trail);
    }

    private static bool EndsSentence(string text)
    {
        var t = text.TrimEnd();
        return t.Length > 0 && t[^1] is '.' or '!' or '?';
    }

    /// <summary>
    /// Whether the text before the caret leaves a sentence open, so the
    /// dictation continues it in lowercase. An emoji or pictograph at the end
    /// decides nothing — in chat it as often ends a sentence ("Great job 🎉")
    /// as sits inside one — so it is neither looked through nor counted, and
    /// the dictation keeps the casing it was spoken with.
    /// </summary>
    private static bool ContinuesSentence(string beforeCore)
    {
        if (beforeCore.Length == 0) return false;
        var last = LastElement(beforeCore);
        if (IsEmojiOrSymbol(last)) return false;
        var c = beforeCore[^1];
        if (c is '"' or '\'')
        {
            // An opening quote keeps the casing that was spoken ("He said
            // "Hello"); after a closing quote the sentence carries on unless
            // the quotation itself ended one ("He said "stop." Then…").
            if (!StraightQuoteCloses(beforeCore)) return false;
            var inner = beforeCore[..^1].TrimEnd('"', '\'');
            return inner.Length > 0 && inner[^1] is not ('.' or '!' or '?');
        }
        return char.IsLetterOrDigit(c) || MidSentenceTail.Contains(c);
    }

    /// <summary>
    /// Whether inserted text needs a space after <paramref name="before"/>,
    /// whose last character is not whitespace: after a word, after closing
    /// punctuation, after a closing straight quote (not an opening one:
    /// <c>"</c> + Hello must stay <c>"Hello</c>), and after an emoji — the
    /// low surrogate it ends in is neither a letter nor punctuation, which is
    /// how "🎉" + "See you" used to come out as "🎉See you".
    /// </summary>
    private static bool NeedsSpaceAfter(string before)
    {
        var prev = before[^1];
        if (prev is '"' or '\'') return StraightQuoteCloses(before);
        if (char.IsLetterOrDigit(prev) || SpaceAfter.Contains(prev)) return true;
        return IsEmojiOrSymbol(LastElement(before));
    }

    /// <summary>
    /// Whether the dictation needs a space before <paramref name="after"/>
    /// (whose first character is not whitespace): always before a word, and
    /// before something that opens — a bracket, an opening quote, an emoji —
    /// when the dictation ends in a word or in sentence punctuation. "see"
    /// typed in front of "(below)" must not become "see(below)".
    /// </summary>
    private static bool NeedsSpaceBefore(string body, string after)
    {
        var next = after[0];
        if (char.IsLetterOrDigit(next)) return !IsCjk(next);

        var end = body.TrimEnd();
        if (end.Length == 0) return false;
        var tail = end[^1];
        var endsInWordOrStop = (char.IsLetterOrDigit(tail) || ".!?,;:".Contains(tail)) && !IsCjk(tail);
        if (!endsInWordOrStop) return false;
        if (OpensAfter.Contains(next)) return true;
        // A straight double quote opens when a word follows it; otherwise it
        // closes something the caret is inside ("hello|"), which wants no space.
        if (next == '"') return after.Length > 1 && char.IsLetterOrDigit(after[1]);
        return IsEmojiOrSymbol(FirstElement(after));
    }

    /// <summary>
    /// A straight quote at the end of <paramref name="text"/> closes when
    /// something other than whitespace precedes it ("said "hi"" ← closing);
    /// at the start of a line or after a space it opens ("said "" ← opening).
    /// </summary>
    private static bool StraightQuoteCloses(string text) =>
        text.Length >= 2 && !char.IsWhiteSpace(text[^2]);

    private static string LastLine(string text)
    {
        var i = text.LastIndexOfAny(new[] { '\n', '\r' });
        return i < 0 ? text : text[(i + 1)..];
    }

    private static string FirstLine(string text)
    {
        var i = text.IndexOfAny(new[] { '\n', '\r' });
        return i < 0 ? text : text[..i];
    }

    /// <summary>
    /// The last user-perceived character (grapheme cluster): a whole emoji
    /// with its modifiers, joiners and variation selector, where the last
    /// UTF-16 unit would only be a low surrogate or a selector. Only the tail
    /// is examined; no cluster is anywhere near 64 units long.
    /// </summary>
    internal static string LastElement(string text)
    {
        if (text.Length == 0) return "";
        var start = Math.Max(0, text.Length - 64);
        if (start > 0 && char.IsLowSurrogate(text[start])) start--;
        var e = StringInfo.GetTextElementEnumerator(text, start);
        var last = "";
        while (e.MoveNext()) last = (string)e.Current;
        return last;
    }

    /// <summary>The first grapheme cluster of <paramref name="text"/>.</summary>
    internal static string FirstElement(string text) =>
        text.Length == 0 ? "" : StringInfo.GetNextTextElement(text, 0);

    /// <summary>
    /// An emoji or a pictographic symbol (★, ©, °), judged on the whole
    /// cluster: a variation selector, a zero-width joiner or a keycap mark
    /// makes any base an emoji ("❤️", "1️⃣"), and otherwise the first code
    /// point's category is OtherSymbol — which also covers the supplementary-
    /// plane emoji whose UTF-16 form is a surrogate pair. CJK symbols are
    /// excluded: CJK text takes no spaces.
    /// </summary>
    internal static bool IsEmojiOrSymbol(string element)
    {
        if (string.IsNullOrEmpty(element)) return false;
        if (element.IndexOfAny(new[] { '️', '‍', '⃣' }) >= 0) return true;
        if (IsCjk(element[0])) return false;
        try
        {
            return CharUnicodeInfo.GetUnicodeCategory(element, 0) == UnicodeCategory.OtherSymbol;
        }
        catch (ArgumentException)
        {
            return false;   // a lone surrogate from a field read mid-edit
        }
    }

    private static bool StartsWithLowerable(string body, Func<string, bool> isProperNoun)
    {
        if (body.Length == 0 || !char.IsUpper(body[0])) return false;
        var end = 0;
        while (end < body.Length && (char.IsLetterOrDigit(body[end]) || body[end] is '\'' or '’' or '-')) end++;
        var word = body[..end];
        if (word.Length == 0) return false;
        if (word == "I" || word.StartsWith("I'") || word.StartsWith("I’")) return false;
        if (word.Length > 1 && word.All(ch => !char.IsLetter(ch) || char.IsUpper(ch))) return false;   // acronym
        if (word.Skip(1).Any(char.IsUpper)) return false;                                                // iPhone, GitHub
        return !isProperNoun(word);
    }

    private static string LowerFirst(string s) =>
        s.Length == 0 ? s : char.ToLower(s[0], CultureInfo.CurrentCulture) + s[1..];

    private static bool IsCjk(char c) =>
        c is >= '぀' and <= 'ヿ'      // kana
          or >= '㐀' and <= '鿿'      // CJK ideographs
          or >= '가' and <= '힯'      // Hangul
          or >= '　' and <= '〿'      // CJK punctuation
          or >= '＀' and <= '￯';     // full-width forms
}
