using System.Text;
using System.Text.RegularExpressions;

namespace FlowType.Engine;

/// <summary>What the formatter changed, for the Insights page.</summary>
public sealed class FormatterReport
{
    public int FillersRemoved { get; set; }
    public int DictionaryFixes { get; set; }
    public int VocabularyFixes { get; set; }
    public int ParagraphBreaks { get; set; }
    public int ListItems { get; set; }
    public int SymbolsFormatted { get; set; }
    public int TermsRecased { get; set; }

    /// <summary>Everything that was not a dictionary rule — "words corrected".</summary>
    public int WordsCorrected =>
        FillersRemoved + VocabularyFixes + ListItems + SymbolsFormatted + TermsRecased;

    public int Total => WordsCorrected + DictionaryFixes + ParagraphBreaks;
}

/// <summary>
/// The formatting that makes dictation read like writing rather than a
/// transcript — the layer Wispr Flow leans on hardest.
///
/// Everything here is deterministic and reversible in the user's head: no model
/// decides what you meant. Each rule is deliberately narrow, and every one of
/// them has a "must not fire" vector in --selftest beside the "must fire" one,
/// because the cost of a wrong rewrite is much higher than the cost of a missed
/// one — a missed one just leaves what you said.
/// </summary>
public static class SmartFormatter
{
    /// <summary>
    /// Marks a paragraph boundary detected from a pause in speech, carried
    /// through the whole formatter as a single private-use character.
    ///
    /// It has to survive filler removal, dictionary rules and whitespace tidy-up
    /// and only become a real line break at the very end, because inserting real
    /// newlines up front would trip the structural guard and switch off tidy-up
    /// for every long dictation.
    /// </summary>
    public const char ParagraphMark = '\uE000';

    // ----- 1. Paragraphs from pauses -----

    /// <summary>
    /// A silence at least this long between two spoken segments, where the first
    /// ended a sentence, is a paragraph break. People pause to think between
    /// ideas; that pause is the only paragraph signal a transcript ever gets,
    /// and it is thrown away by every tool that only looks at the text.
    /// </summary>
    public static readonly TimeSpan ParagraphPause = TimeSpan.FromMilliseconds(1200);

    /// <summary>Below this many words a break is just a stutter in a short note.</summary>
    private const int MinWordsForParagraphs = 25;

    /// <summary>
    /// Join whisper's segments, inserting a paragraph mark wherever the speaker
    /// clearly stopped and started again.
    /// </summary>
    public static string JoinSegments(IReadOnlyList<SpeechSegment> segments)
    {
        if (segments.Count == 0) return "";

        var totalWords = segments.Sum(s =>
            s.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);

        var sb = new StringBuilder();
        for (var i = 0; i < segments.Count; i++)
        {
            var text = segments[i].Text;
            if (i > 0 && totalWords >= MinWordsForParagraphs)
            {
                var gap = segments[i].Start - segments[i - 1].End;
                var previous = sb.ToString().TrimEnd();
                var endsSentence = previous.Length > 0 && previous[^1] is '.' or '!' or '?';
                if (gap >= ParagraphPause && endsSentence)
                {
                    sb.Append(' ').Append(ParagraphMark);
                }
            }
            sb.Append(text);
        }
        return sb.ToString();
    }

    // ----- 2. Spoken lists -----

    private static readonly string[] Ordinals =
    {
        "first", "second", "third", "fourth", "fifth",
        "sixth", "seventh", "eighth", "ninth", "tenth",
    };

    private static readonly string[] NumberWords =
    {
        "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
    };

    /// <summary>
    /// Turn "First, buy milk. Second, call Mum." into a numbered list.
    ///
    /// Requires at least two markers that actually count upwards and each start
    /// their own sentence, so "First of all, I think…" — one marker, mid-thought
    /// — is left exactly as spoken.
    /// </summary>
    public static string ApplyLists(string text, FormatterReport report)
    {
        var sentences = SplitSentences(text);
        if (sentences.Count < 3) return text;

        var markers = new int[sentences.Count];
        for (var i = 0; i < sentences.Count; i++) markers[i] = MarkerValue(sentences[i]);

        // The run has to climb: 1, 2, 3 — not "first… first…".
        var start = -1;
        var count = 0;
        var bestStart = -1;
        var bestCount = 0;
        var expected = 1;
        for (var i = 0; i < sentences.Count; i++)
        {
            if (markers[i] == expected)
            {
                if (count == 0) start = i;
                count++;
                expected++;
                if (count > bestCount) { bestCount = count; bestStart = start; }
            }
            else if (markers[i] == 1)
            {
                start = i; count = 1; expected = 2;
                if (count > bestCount) { bestCount = count; bestStart = start; }
            }
        }
        if (bestCount < 2) return text;

        var result = new List<string>();
        for (var i = 0; i < sentences.Count; i++)
        {
            var inList = i >= bestStart && i < bestStart + bestCount;
            if (!inList) { result.Add(sentences[i]); continue; }

            var item = StripMarker(sentences[i]);
            if (item.Length == 0) { result.Add(sentences[i]); continue; }
            item = char.ToUpperInvariant(item[0]) + item[1..];
            report.ListItems++;
            result.Add($"{ParagraphMark}{i - bestStart + 1}. {item}");
        }
        // Items are already separated by their own marks; join with spaces and
        // let the mark become the line break later.
        return string.Join(" ", result);
    }

    /// <summary>1-based position if the sentence opens with a list marker, else 0.</summary>
    private static int MarkerValue(string sentence)
    {
        var m = Regex.Match(sentence.TrimStart(),
            @"^(?:(?:and\s+)?(?:number\s+)?)(\w+)(?:ly)?\s*[,:]\s+",
            RegexOptions.IgnoreCase);
        if (!m.Success) return 0;
        var word = m.Groups[1].Value.ToLowerInvariant();
        var ordinal = Array.IndexOf(Ordinals, word);
        if (ordinal >= 0) return ordinal + 1;
        // "Number one," / "Number two," — the bare number word only counts when
        // "number" preceded it, or every sentence starting "One thing…" would.
        if (Regex.IsMatch(sentence.TrimStart(), @"^(?:and\s+)?number\s+", RegexOptions.IgnoreCase))
        {
            var n = Array.IndexOf(NumberWords, word);
            if (n >= 0) return n + 1;
            if (int.TryParse(word, out var digits) && digits is >= 1 and <= 20) return digits;
        }
        return 0;
    }

    private static string StripMarker(string sentence) => Regex.Replace(
        sentence.TrimStart(),
        @"^(?:(?:and\s+)?(?:number\s+)?)\w+(?:ly)?\s*[,:]\s+",
        "", RegexOptions.IgnoreCase).Trim();

    // ----- 3. Spoken addresses and links -----

    private static readonly string[] TopLevelDomains =
    {
        "com", "net", "org", "io", "co", "dev", "app", "ai", "me", "edu", "gov",
        "uk", "de", "fr", "ca", "au", "nl", "eu", "info", "gg", "tv", "xyz",
    };

    /// <summary>
    /// Words that are not the local part of an email address, however much they
    /// are followed by "at". Without this, "look at example dot com" becomes
    /// "look@example.com".
    /// </summary>
    private static readonly HashSet<string> NotAnAddress = new(StringComparer.OrdinalIgnoreCase)
    {
        "look", "looking", "arrive", "arrived", "stay", "stayed", "meet", "met",
        "be", "is", "are", "was", "were", "get", "got", "come", "came", "go", "went",
        "work", "works", "live", "lives", "start", "starts", "end", "ends", "aim",
        "point", "stare", "glance", "back", "home", "here", "there", "now", "then",
        "once", "all", "not", "and", "but", "or", "so", "we", "they", "you", "i",
        "he", "she", "it", "available", "based", "located", "held", "posted",
    };

    private static readonly Regex DottedName = new(
        @"\b([A-Za-z0-9][A-Za-z0-9\-]*)((?:\s+dot\s+[A-Za-z0-9\-]+)+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// "roy at example dot com" → "roy@example.com", and "example dot com" →
    /// "example.com" on its own. Only when the last piece is a real top-level
    /// domain, so "the dot matrix printer" is safe.
    /// </summary>
    public static string ApplyAddresses(string text, FormatterReport report)
    {
        var result = DottedName.Replace(text, m =>
        {
            var parts = new List<string> { m.Groups[1].Value };
            foreach (Match dot in Regex.Matches(m.Groups[2].Value,
                @"dot\s+([A-Za-z0-9\-]+)", RegexOptions.IgnoreCase))
            {
                parts.Add(dot.Groups[1].Value);
            }
            if (parts.Count < 2) return m.Value;
            if (!TopLevelDomains.Contains(parts[^1].ToLowerInvariant())) return m.Value;
            report.SymbolsFormatted++;
            return string.Join(".", parts);
        });

        // Now that the domain is one token, an "at" in front of it is an address.
        result = Regex.Replace(result,
            @"\b([A-Za-z0-9][A-Za-z0-9._\-]*)\s+at\s+([A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)+)\b",
            m =>
            {
                if (NotAnAddress.Contains(m.Groups[1].Value)) return m.Value;
                report.SymbolsFormatted++;
                return $"{m.Groups[1].Value}@{m.Groups[2].Value}";
            },
            RegexOptions.IgnoreCase);

        return result;
    }

    // ----- 4. Symbols, money, percentages and times -----

    /// <summary>
    /// The handful of spoken symbols that are unambiguous next to a number, plus
    /// clock times. Deliberately excludes weights and measures, where the word
    /// is what people actually want written.
    /// </summary>
    public static string ApplySymbols(string text, FormatterReport report)
    {
        var before = text;

        // "25 percent" → "25%"
        text = Regex.Replace(text, @"\b(\d+(?:\.\d+)?)\s+percent\b", "$1%", RegexOptions.IgnoreCase);
        // "20 dollars" → "$20"; "20 euros" → "€20"
        text = Regex.Replace(text, @"\b(\d+(?:[.,]\d+)?)\s+dollars?\b", "$$$1", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\b(\d+(?:[.,]\d+)?)\s+euros?\b", "€$1", RegexOptions.IgnoreCase);
        // "3 30 p.m." / "3:30 pm" / "3 pm" → "3:30 PM" / "3 PM"
        text = Regex.Replace(text,
            @"\b(\d{1,2})(?:[:\s](\d{2}))?\s*([ap])\.?\s*m\.?(?![\w])",
            m =>
            {
                var hour = m.Groups[1].Value;
                var minute = m.Groups[2].Success ? $":{m.Groups[2].Value}" : "";
                var half = m.Groups[3].Value.ToUpperInvariant();
                return $"{hour}{minute} {half}M";
            },
            RegexOptions.IgnoreCase);

        // "quote ... unquote" → "..."
        text = Regex.Replace(text,
            @"\bquote\b[,:]?\s+(.+?)\s*[,]?\s*\b(?:unquote|end\s+quote)\b[.]?",
            "“$1”", RegexOptions.IgnoreCase);

        if (!ReferenceEquals(text, before) && text != before) report.SymbolsFormatted++;
        return text;
    }

    // ----- 5. Terms that are always written a particular way -----

    /// <summary>
    /// Words a recogniser reliably lower-cases. Ambiguous ones are deliberately
    /// absent: "us", "it", "ram" and "ml" are ordinary English far more often
    /// than they are acronyms, and getting those wrong is worse than leaving
    /// every acronym alone.
    /// </summary>
    private static readonly Dictionary<string, string> KnownTerms =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["api"] = "API", ["apis"] = "APIs", ["url"] = "URL", ["urls"] = "URLs",
            ["html"] = "HTML", ["css"] = "CSS", ["json"] = "JSON", ["xml"] = "XML",
            ["sql"] = "SQL", ["http"] = "HTTP", ["https"] = "HTTPS", ["ssh"] = "SSH",
            ["cpu"] = "CPU", ["gpu"] = "GPU", ["ssd"] = "SSD", ["usb"] = "USB",
            ["pdf"] = "PDF", ["png"] = "PNG", ["jpeg"] = "JPEG", ["gif"] = "GIF",
            ["ui"] = "UI", ["ux"] = "UX", ["faq"] = "FAQ", ["ceo"] = "CEO",
            ["ok"] = "OK",   // casing only; "okay" is left alone, it is a different word
            ["iphone"] = "iPhone", ["ipad"] = "iPad", ["ios"] = "iOS",
            ["macos"] = "macOS", ["macbook"] = "MacBook", ["airpods"] = "AirPods",
            ["github"] = "GitHub", ["gitlab"] = "GitLab", ["youtube"] = "YouTube",
            ["javascript"] = "JavaScript", ["typescript"] = "TypeScript",
            ["powershell"] = "PowerShell", ["paypal"] = "PayPal",
            ["whatsapp"] = "WhatsApp", ["chatgpt"] = "ChatGPT", ["openai"] = "OpenAI",
            ["wifi"] = "Wi-Fi", ["wi-fi"] = "Wi-Fi", ["npm"] = "npm",
        };

    private static readonly Regex WordPattern = new(@"[A-Za-z][A-Za-z'\-]*", RegexOptions.Compiled);

    public static string ApplyKnownTerms(string text, FormatterReport report)
    {
        return WordPattern.Replace(text, m =>
        {
            if (!KnownTerms.TryGetValue(m.Value, out var canonical)) return m.Value;
            if (m.Value == canonical) return m.Value;
            // "OK" is the one that also appears as a name; leave it alone when
            // the speaker clearly capitalised something else around it.
            report.TermsRecased++;
            return canonical;
        });
    }

    // ----- shared -----

    internal static List<string> SplitSentences(string text) =>
        Regex.Split(text.Trim(), @"(?<=[.!?])\s+")
            .Where(s => s.Length > 0)
            .ToList();
}
