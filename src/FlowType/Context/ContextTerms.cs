using System.Text;

namespace FlowType.Context;

/// <summary>
/// Pulls the names and terms out of the text around the caret and the window
/// title, so the recogniser can be told what is likely to be said next.
///
/// Wispr Flow does this in the cloud: the text its helper reads near the
/// focus is posted to an LLM endpoint that returns a comma list of proper
/// nouns, and that list biases recognition for the dictation. FlowType has no
/// server and must not send field text anywhere, so this is the local stand-in
/// — written from the observed behaviour, not from their code. It errs toward
/// precision: a term that is wrong costs prompt budget and can nudge the
/// decoder toward a word nobody said, while a term that is missing only means
/// the model has to get it right on its own, as it always did before.
///
/// What counts as a term:
/// - a capitalised word that is not at the start of a sentence, and runs of
///   them ("Arma Reforger", "Sarah Connor");
/// - CamelCase and mixedCase identifiers (GitHub, iPhone, LLamaSharp);
/// - ALL-CAPS acronyms of 2–6 letters (CUDA, NATO), unless the text is
///   shouting;
/// - words mixing letters and digits (gpt4o, RTX4070, GPT-3.5);
/// - snake_case identifiers;
/// - @handles and #channels, kept with their sigil;
/// - file names with a known extension (index.ts, App.xaml.cs), including the
///   last segment of a path.
///
/// Never: e-mail addresses or URLs (personal and useless as speech), plain
/// numbers, ordinals and units (2nd, 10am, 5x), and a built-in stoplist of
/// capitalised words that carry no information (I, The, Monday, January,
/// Hello, Thanks, OK…).
///
/// Pure and allocation-light: no I/O, no regex backtracking, safe on any thread.
/// </summary>
public static class ContextTerms
{
    /// <summary>Shortest term worth keeping.</summary>
    public const int MinLength = 2;

    /// <summary>Longest term worth keeping; past this it is a sentence or a blob.</summary>
    public const int MaxLength = 40;

    /// <summary>
    /// Input beyond this many characters is ignored (the tail is kept, since
    /// the text nearest the caret is the most relevant). The capture side only
    /// ever hands over ~1–5 K, so this is a guard, not a working limit.
    /// </summary>
    private const int MaxInput = 20_000;

    /// <summary>
    /// A run of capitalised words longer than this is a Title Case heading,
    /// not a name, and is dropped whole.
    /// </summary>
    private const int MaxRunWords = 4;

    /// <summary>
    /// Separators between the parts of a window title. Browsers, editors and
    /// chat apps all put the app's own name last, after one of these.
    /// </summary>
    private static readonly string[] TitleSeparators = { " - ", " | ", " — ", " – ", " · " };

    // ----- Public API -----

    /// <summary>
    /// Terms in <paramref name="text"/>, most frequent first, then in order of
    /// first appearance. Case-insensitive duplicates are merged; the first
    /// spelling seen is the one returned.
    /// </summary>
    public static IReadOnlyList<string> Extract(string text, int max = 24)
    {
        if (string.IsNullOrWhiteSpace(text) || max <= 0) return Array.Empty<string>();
        if (text.Length > MaxInput) text = text[^MaxInput..];
        var counter = new TermCounter();
        Collect(text, titleMode: false, counter);
        return counter.Ranked(max);
    }

    /// <summary>
    /// Terms in a window title. The last segment ("Google Chrome", "Visual
    /// Studio Code", "Discord") is the app's name and is dropped, as is any
    /// segment that is just the process name or window chrome ("Inbox", "New
    /// Tab"). The first word of a segment is not treated as a sentence start —
    /// in a title it is usually the most meaningful word there is (a document,
    /// channel or contact name).
    /// </summary>
    public static IReadOnlyList<string> ExtractFromTitle(string windowTitle, string processName = "", int max = 8)
    {
        if (string.IsNullOrWhiteSpace(windowTitle) || max <= 0) return Array.Empty<string>();
        var title = windowTitle.Length > 512 ? windowTitle[..512] : windowTitle;

        // Zero-width characters appear in real titles (Edge's own name is
        // "Microsoft\u200B Edge") and would otherwise glue words together.
        title = new string(title.Where(c => c is not ('\u200B' or '\u200C' or '\u200D' or '\uFEFF')).ToArray());

        var segments = SplitTitle(title);
        if (segments.Count > 1) segments.RemoveAt(segments.Count - 1);

        var counter = new TermCounter();
        foreach (var segment in segments)
        {
            var s = segment.Trim();
            if (s.Length == 0) continue;
            if (processName.Length > 0 && s.Equals(processName, StringComparison.OrdinalIgnoreCase)) continue;
            if (TitleNoise.Contains(s)) continue;
            Collect(s, titleMode: true, counter);
            // Token indexes restart per segment; keep earlier segments first.
            counter.Base += 10_000;
        }
        return counter.Ranked(max);
    }

    /// <summary>
    /// Concatenate term lists in priority order, dropping case-insensitive
    /// duplicates, and stop at <paramref name="max"/>.
    /// </summary>
    public static IReadOnlyList<string> Merge(int max, params IReadOnlyList<string>?[] lists)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var list in lists)
        {
            if (list == null) continue;
            foreach (var term in list)
            {
                if (result.Count >= max) return result;
                if (!string.IsNullOrWhiteSpace(term) && seen.Add(term)) result.Add(term);
            }
        }
        return result;
    }

    /// <summary>
    /// Whether <paramref name="word"/> is one of the few hundred most common
    /// English words. Shared with <see cref="Session.EditLearner"/>, which
    /// uses it to refuse to turn an ordinary word into a rewrite rule.
    /// </summary>
    internal static bool IsCommonWord(string word)
    {
        if (string.IsNullOrEmpty(word)) return false;
        return CommonWords.Contains(word);
    }

    /// <summary>
    /// Whether two different words sound alike (or are the classic spelling
    /// confusions: affect/effect, then/than) by the built-in homophone list.
    /// Both are compact keys: lower case, letters and digits only, so "who's"
    /// is "whos". Used by <see cref="Session.EditLearner"/>: fixing a
    /// homophone is grammar, and a rule built from one would rewrite a real
    /// English word in every later dictation.
    /// </summary>
    internal static bool AreHomophones(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return false;
        return HomophoneGroup.TryGetValue(a, out var ga)
               && HomophoneGroup.TryGetValue(b, out var gb)
               && ga == gb;
    }

    /// <summary>
    /// Whether two different words are forms of one another by a regular
    /// English inflection — plural or third person -s/-es/-ies, past -ed/-d/-ied,
    /// -ing — including the silent-e and doubled-consonant spellings
    /// (compile/compiling, plan/planned) and two inflections of one stem
    /// (deployed/deploying). Compact lower-case keys, as for
    /// <see cref="AreHomophones"/>. Deliberately generous: a false match only
    /// means EditLearner passes up one correction, while a miss lets a grammar
    /// fix become a permanent rewrite rule.
    /// </summary>
    internal static bool AreInflections(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        if (a == b) return false;
        var stems = new HashSet<string>(Stems(a), StringComparer.Ordinal);
        return Stems(b).Any(stems.Contains);
    }

    /// <summary>
    /// The word and every stem a regular suffix could have come off. Over-
    /// generates on purpose ("bed" yields "be"); see <see cref="AreInflections"/>.
    /// </summary>
    private static IEnumerable<string> Stems(string w)
    {
        yield return w;
        if (w.Length < 3) yield break;
        if (w.EndsWith("ies", StringComparison.Ordinal) || w.EndsWith("ied", StringComparison.Ordinal))
        {
            if (w.Length > 4) yield return w[..^3] + "y";                // copies, tried
        }
        if (w.EndsWith("es", StringComparison.Ordinal)) yield return w[..^2];   // boxes
        if (w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal)) yield return w[..^1];
        if (w.EndsWith('d')) yield return w[..^1];                          // baked → bake
        if (w.EndsWith("ed", StringComparison.Ordinal))
        {
            foreach (var s in Undoubled(w[..^2])) yield return s;           // deployed, planned
        }
        if (w.EndsWith("ing", StringComparison.Ordinal) && w.Length > 4)
        {
            var s = w[..^3];
            foreach (var u in Undoubled(s)) yield return u;                 // deploying, planning
            yield return s + "e";                                           // compiling → compile
        }
    }

    /// <summary>The stem as cut, and without a doubled final consonant (plann → plan).</summary>
    private static IEnumerable<string> Undoubled(string s)
    {
        if (s.Length == 0) yield break;
        yield return s;
        if (s.Length >= 3 && s[^1] == s[^2] && !"aeiou".Contains(s[^1])) yield return s[..^1];
    }

    // ----- Tokenising -----

    private enum Kind
    {
        None,
        /// <summary>Stands on its own (identifier, acronym, handle, file…).</summary>
        Strong,
        /// <summary>A Title-case word: a name only by position and company.</summary>
        Capitalized,
    }

    private readonly record struct Token(
        string Core, int Index, bool SentenceStart, bool LeadPunct, bool EndsRun, Kind Kind, string Term);

    private static void Collect(string text, bool titleMode, TermCounter counter)
    {
        var tokens = Tokenize(text, titleMode);
        if (tokens.Count == 0) return;

        var shouting = MarkShouting(tokens);
        var run = new List<Token>();

        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];

            if (t.Kind == Kind.Capitalized)
            {
                // A run continues only across plain spaces: "Arma Reforger"
                // is one name, "Arma, Reforger" is two words.
                var joins = run.Count > 0 && !run[^1].EndsRun && !t.LeadPunct && !t.SentenceStart;
                if (!joins) FlushRun(run, counter);
                run.Add(t);
                if (t.EndsRun) FlushRun(run, counter);
                continue;
            }

            FlushRun(run, counter);
            if (t.Kind != Kind.Strong) continue;
            // ALL CAPS inside a shouted sentence is emphasis, not an acronym.
            if (shouting[i]) continue;
            counter.Add(t.Term, t.Index);
        }
        FlushRun(run, counter);
    }

    private static List<Token> Tokenize(string text, bool titleMode)
    {
        var tokens = new List<Token>();
        var i = 0;
        var nextIsSentenceStart = true;
        while (i < text.Length)
        {
            var newline = false;
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                if (text[i] is '\n' or '\r' or '\u2028' or '\u2029') newline = true;
                i++;
            }
            if (i >= text.Length) break;
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
            var raw = text[start..i];

            // Leading punctuation is peeled off, except a sigil directly in
            // front of a name (@devon, #general).
            var a = 0;
            while (a < raw.Length && !char.IsLetterOrDigit(raw[a])
                   && !(raw[a] is '@' or '#' && a + 1 < raw.Length && char.IsLetterOrDigit(raw[a + 1])))
            {
                a++;
            }
            var b = raw.Length;
            while (b > a && !char.IsLetterOrDigit(raw[b - 1])) b--;

            var lead = raw[..a];
            var trail = raw[b..];
            var core = raw[a..b];

            var sentenceStart = !titleMode && (nextIsSentenceStart || newline || OpensQuote(lead));

            if (core.Length == 0)
            {
                // A bullet or a dash: whatever follows a bullet at the start of
                // a line starts a sentence too; a dash mid-sentence does not.
                nextIsSentenceStart = sentenceStart || EndsSentence(trail);
                continue;
            }

            var stripped = StripClitic(core, out var hadClitic);
            var (kind, term) = Classify(stripped);
            tokens.Add(new Token(stripped, tokens.Count, sentenceStart, lead.Length > 0,
                trail.Length > 0 || hadClitic, kind, term));

            nextIsSentenceStart = EndsSentence(trail) && !IsAbbreviation(core);
        }
        return tokens;
    }

    private static bool OpensQuote(string lead) =>
        lead.IndexOfAny(new[] { '"', '“', '‘', '«', '\'' }) >= 0;

    private static bool EndsSentence(string trail) =>
        trail.IndexOfAny(new[] { '.', '!', '?', '…' }) >= 0;

    /// <summary>"Dr. Patel" — the full stop after a title does not end the sentence.</summary>
    private static bool IsAbbreviation(string core)
    {
        if (core.Length == 1 && char.IsUpper(core[0])) return true;   // an initial: "J. R. R. Tolkien"
        return Abbreviations.Contains(core);
    }

    /// <summary>Sarah's → Sarah, I'm → I, don't → do.</summary>
    private static string StripClitic(string core, out bool stripped)
    {
        foreach (var suffix in Clitics)
        {
            if (core.Length > suffix.Length && core.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                stripped = true;
                return core[..^suffix.Length];
            }
        }
        stripped = false;
        return core;
    }

    private static readonly string[] Clitics =
    {
        "n't", "n’t", "'s", "’s", "'m", "’m", "'ll", "’ll", "'re", "’re", "'ve", "’ve", "'d", "’d",
    };

    // ----- Classification -----

    private static (Kind Kind, string Term) Classify(string core)
    {
        if (core.Length == 0) return (Kind.None, "");

        // @handle — kept with the sigil so a consumer can tell a handle from a word.
        if (core[0] == '@')
        {
            var handle = core[1..];
            if (handle.Length is >= 1 and <= 39 && handle.Any(char.IsLetter)
                && handle.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '-'))
            {
                return (Kind.Strong, core);
            }
            return (Kind.None, "");
        }

        // #channel — but not an issue number (#123) or a colour (#FF00AA).
        if (core[0] == '#')
        {
            var name = core[1..];
            if (name.Length is >= 2 and <= 39 && char.IsLetter(name[0])
                && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-')
                && !IsHexColour(name))
            {
                return (Kind.Strong, core);
            }
            return (Kind.None, "");
        }

        // E-mail addresses and links are personal data and are never spoken
        // the way they are written, so they are no use to the recogniser.
        if (core.Contains('@') || core.Contains("://")
            || core.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            return (Kind.None, "");
        }

        // A path: the file at the end of it is the part anyone would say.
        var slash = core.LastIndexOfAny(new[] { '/', '\\' });
        if (slash >= 0)
        {
            var last = core[(slash + 1)..];
            return IsFileName(last) ? (Kind.Strong, last) : (Kind.None, "");
        }

        if (IsFileName(core)) return (Kind.Strong, core);

        var hasLetter = false;
        var hasDigit = false;
        var hasUnderscore = false;
        var allowed = true;
        foreach (var c in core)
        {
            if (char.IsLetter(c)) hasLetter = true;
            else if (char.IsDigit(c)) hasDigit = true;
            else if (c == '_') hasUnderscore = true;
            else if (c is not ('-' or '.' or '\'' or '’')) allowed = false;
        }
        if (!hasLetter || !allowed) return (Kind.None, "");

        // gpt4o, RTX4070, GPT-3.5 — but not 2nd, 10am, 1990s, v2.
        if (hasDigit)
        {
            if (core.Contains('\'') || core.Contains('’')) return (Kind.None, "");
            if (IsNumberWithUnit(core) || IsVersion(core)) return (Kind.None, "");
            return (Kind.Strong, core);
        }

        // snake_case identifiers.
        if (hasUnderscore)
        {
            var inner = core.Trim('_');
            return inner.Contains('_') && core.All(c => char.IsLetterOrDigit(c) || c == '_')
                ? (Kind.Strong, core)
                : (Kind.None, "");
        }

        // A stray dot that is not a file name: "e.g", "U.S" — no use.
        if (core.Contains('.')) return (Kind.None, "");

        var letters = core.Where(char.IsLetter).ToArray();

        // ACRONYM (2–6 letters, all capitals) unless it is a common word in
        // capitals ("IT", "US") or chat shorthand ("OK", "BTW").
        if (letters.Length >= 2 && letters.All(char.IsUpper))
        {
            if (letters.Length > 6 || core.Contains('-')) return (Kind.None, "");
            if (Stop.Contains(core) || IsCommonWord(core.ToLowerInvariant())) return (Kind.None, "");
            return (Kind.Strong, core);
        }

        // mixedCase / CamelCase: a capital after the first letter of a part,
        // with lowercase present (GitHub, iPhone, McDonald, O'Brien).
        var parts = core.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (letters.Any(char.IsLower) && parts.Any(p => p.Skip(1).Any(char.IsUpper)))
        {
            return Stop.Contains(core) ? (Kind.None, "") : (Kind.Strong, core);
        }

        // Title case: a candidate name, decided by position and neighbours.
        if (char.IsUpper(core[0])) return (Kind.Capitalized, core);

        return (Kind.None, "");
    }

    private static bool IsFileName(string s)
    {
        if (s.Length is < 3 or > MaxLength) return false;
        var dot = s.LastIndexOf('.');
        if (dot <= 0 || dot == s.Length - 1) return false;
        var ext = s[(dot + 1)..];
        if (!KnownExtensions.Contains(ext)) return false;
        var stem = s[..dot];
        if (!char.IsLetterOrDigit(stem[0]) && stem[0] != '_') return false;
        if (!stem.Any(char.IsLetter)) return false;
        return stem.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '-');
    }

    private static bool IsHexColour(string s) =>
        s.Length is 3 or 6 or 8 && s.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));

    /// <summary>2nd, 10am, 5x, 1990s, 100ms, 4K, 16GB — digits followed by a unit or suffix.</summary>
    private static bool IsNumberWithUnit(string s)
    {
        var i = 0;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] is '.' or ',')) i++;
        if (i == 0 || i == s.Length) return false;
        var unit = s[i..].ToLowerInvariant();
        return Units.Contains(unit);
    }

    /// <summary>v2, v1.8.0, V3 — a version tag rather than a name.</summary>
    private static bool IsVersion(string s) =>
        s.Length >= 2 && s[0] is 'v' or 'V' && s[1..].All(c => char.IsDigit(c) || c == '.');

    /// <summary>
    /// Index flags for all-caps words that sit in a run of three or more:
    /// "THIS IS SO COOL" is emphasis, and none of it is an acronym.
    /// </summary>
    private static bool[] MarkShouting(List<Token> tokens)
    {
        var flags = new bool[tokens.Count];
        var runStart = -1;
        for (var i = 0; i <= tokens.Count; i++)
        {
            var caps = i < tokens.Count && IsAllCapsWord(tokens[i].Core);
            if (caps)
            {
                if (runStart < 0) runStart = i;
                continue;
            }
            if (runStart >= 0 && i - runStart >= 3)
            {
                for (var k = runStart; k < i; k++) flags[k] = true;
            }
            runStart = -1;
        }
        return flags;
    }

    private static bool IsAllCapsWord(string core)
    {
        var letters = 0;
        foreach (var c in core)
        {
            if (!char.IsLetter(c)) return false;
            if (!char.IsUpper(c)) return false;
            letters++;
        }
        return letters >= 1 && core.Length >= 1 && (letters >= 2 || core is "A" or "I");
    }

    /// <summary>
    /// Turn a run of Title-case words into at most one term. Greetings, days
    /// and other stopwords are trimmed from both ends; a lone capital at the
    /// start of a sentence says nothing and is dropped; so is a lone common
    /// word ("Home"), and a run that is all common words ("New Tab").
    /// </summary>
    private static void FlushRun(List<Token> run, TermCounter counter)
    {
        if (run.Count == 0) return;
        var words = new List<Token>(run);
        run.Clear();

        while (words.Count > 0 && IsStopToken(words[0])) words.RemoveAt(0);
        while (words.Count > 0 && IsStopToken(words[^1])) words.RemoveAt(words.Count - 1);
        if (words.Count == 0) return;

        if (words[0].SentenceStart)
        {
            if (words.Count == 1) return;
            if (IsCommonWord(words[0].Core.ToLowerInvariant())) words.RemoveAt(0);
        }

        if (words.Count > MaxRunWords) return;
        if (words.All(w => IsCommonWord(w.Core.ToLowerInvariant()))) return;

        var term = string.Join(" ", words.Select(w => w.Core));
        counter.Add(term, words[0].Index);
    }

    private static bool IsStopToken(Token t) => Stop.Contains(t.Core) || TitleNoise.Contains(t.Core);

    private static List<string> SplitTitle(string title)
    {
        var parts = new List<string> { title };
        foreach (var sep in TitleSeparators)
        {
            var next = new List<string>();
            foreach (var p in parts) next.AddRange(p.Split(sep));
            parts = next;
        }
        // Unsaved-file and unread markers ("● file.cs", "(3) Inbox").
        for (var i = 0; i < parts.Count; i++)
        {
            var p = parts[i].Trim().TrimStart('●', '•', '*', '○', '◦');
            if (p.StartsWith('(') && p.IndexOf(')') is var close and > 0
                && p[1..close].All(char.IsDigit))
            {
                p = p[(close + 1)..];
            }
            parts[i] = p.Trim();
        }
        parts.RemoveAll(p => p.Length == 0);
        return parts;
    }

    // ----- Counting -----

    private sealed class TermCounter
    {
        private readonly Dictionary<string, (string Display, int Count, int First)> _terms =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Added to every position, so separately tokenised pieces keep their order.</summary>
        public int Base;

        public void Add(string term, int position)
        {
            term = term.Trim();
            if (term.Length is < MinLength or > MaxLength) return;
            if (Stop.Contains(term)) return;
            _terms[term] = _terms.TryGetValue(term, out var e)
                ? (e.Display, e.Count + 1, e.First)
                : (term, 1, Base + position);
        }

        public IReadOnlyList<string> Ranked(int max) => _terms.Values
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.First)
            .Take(max)
            .Select(t => t.Display)
            .ToList();
    }

    // ----- Word lists -----

    /// <summary>
    /// Capitalised words that are capitalised for grammar, not because they
    /// name anything worth recognising.
    /// </summary>
    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "I", "A", "An", "The", "This", "That", "These", "Those",
        "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday",
        "Mon", "Tue", "Tues", "Wed", "Thu", "Thur", "Thurs", "Fri", "Sat", "Sun",
        "January", "February", "March", "April", "May", "June", "July", "August",
        "September", "October", "November", "December",
        "Jan", "Feb", "Mar", "Apr", "Jun", "Jul", "Aug", "Sep", "Sept", "Oct", "Nov", "Dec",
        "Hi", "Hello", "Hey", "Dear", "Greetings", "Thanks", "Thank", "Cheers", "Regards",
        "Best", "Sincerely", "Yours", "Kind", "Warm",
        "OK", "Okay", "Yes", "No", "Yeah", "Yep", "Nope", "Sure", "Please", "Sorry",
        "Mr", "Mrs", "Ms", "Mx", "Dr", "Prof", "Sir", "Madam",
        "AM", "PM", "TV", "LOL", "LMAO", "OMG", "BTW", "FYI", "ASAP", "TBH", "IMO", "IMHO",
        "IDK", "TLDR", "TL", "DR", "RE", "FW", "FWD", "CC", "BCC", "PS", "NB", "AKA", "ETA", "EOD",
        "Re", "Fwd", "Fw", "Today", "Tomorrow", "Yesterday", "Tonight",
        "English", "Ok",
    };

    /// <summary>Window chrome that shows up as a title segment but names nothing.</summary>
    private static readonly HashSet<string> TitleNoise = new(StringComparer.OrdinalIgnoreCase)
    {
        "Untitled", "New Tab", "Inbox", "Home", "Settings", "Search", "Start", "Welcome",
        "Draft", "Drafts", "Sent", "Outbox", "Archive", "Notifications", "Messages", "Message",
        "Chat", "Chats", "Calendar", "Mail", "Document", "Documents", "Downloads", "Desktop",
        "File Explorer", "Explorer", "Task Manager", "Personal", "Work", "Profile", "Private",
        "InPrivate", "Incognito", "Tab", "Page", "Channel", "Thread", "Threads", "DM", "DMs",
        "Direct Messages", "Compose", "Reply", "Forward", "Editor", "Preview", "Loading",
    };

    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "mx", "dr", "prof", "st", "jr", "sr", "vs", "etc", "eg", "ie",
        "inc", "ltd", "co", "no", "approx", "dept", "est", "fig", "gen", "gov", "lt", "mt",
        "rev", "sgt", "capt", "col", "cpl", "pvt", "ave", "blvd", "rd",
    };

    private static readonly HashSet<string> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        "st", "nd", "rd", "th", "s", "am", "pm", "a", "p", "k", "m", "b", "x", "h", "hr", "hrs",
        "min", "mins", "sec", "secs", "ms", "s", "d", "w", "y", "yr", "yrs", "kb", "mb", "gb", "tb",
        "kbps", "mbps", "gbps", "px", "pt", "em", "rem", "hz", "khz", "mhz", "ghz", "mm", "cm",
        "km", "kg", "g", "lb", "lbs", "oz", "ft", "in", "mi", "mph", "kph", "fps", "p",
        "c", "f", "v", "mah", "wh", "kw", "kwh", "l", "ml", "gal", "pc", "pcs", "x",
    };

    private static readonly HashSet<string> KnownExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "cs", "csproj", "sln", "slnx", "xaml", "razor", "fs", "vb", "ts", "tsx", "js", "jsx", "mjs",
        "cjs", "json", "jsonc", "py", "pyi", "ipynb", "md", "mdx", "txt", "rst", "c", "h", "cc",
        "cpp", "hpp", "cxx", "rs", "go", "java", "kt", "kts", "scala", "rb", "php", "swift", "m",
        "mm", "dart", "lua", "r", "jl", "pl", "sh", "bash", "zsh", "ps1", "psm1", "bat", "cmd",
        "html", "htm", "css", "scss", "sass", "less", "vue", "svelte", "astro", "xml", "yml",
        "yaml", "toml", "ini", "cfg", "conf", "env", "lock", "gradle", "cmake", "mk", "sql",
        "graphql", "proto", "dockerfile", "tf", "hcl", "png", "jpg", "jpeg", "gif", "svg", "webp",
        "ico", "bmp", "tiff", "psd", "blend", "fbx", "obj", "gltf", "glb", "pdf", "doc", "docx",
        "xls", "xlsx", "ppt", "pptx", "csv", "tsv", "log", "wav", "mp3", "flac", "ogg", "mp4",
        "mov", "mkv", "webm", "zip", "7z", "rar", "tar", "gz", "exe", "dll", "msi", "pak", "et",
        "ent", "layout", "gguf", "bin", "onnx", "pt", "safetensors", "vsix", "nupkg", "props",
        "targets", "resx", "manifest", "otf", "ttf", "woff", "woff2",
    };

    /// <summary>
    /// About three hundred of the most common English words, lower case.
    /// Built in so nothing is fetched (Wispr downloads its stopword list from
    /// a CDN at run time; FlowType makes no network calls for this).
    /// </summary>
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "able", "about", "above", "across", "actually", "after", "again", "against", "ago",
        "all", "almost", "already", "also", "although", "always", "am", "among", "an", "and",
        "another", "any", "anyone", "anything", "are", "around", "as", "ask", "asked", "at",
        "away", "back", "bad", "be", "became", "because", "become", "been", "before", "began",
        "being", "believe", "below", "best", "better", "between", "big", "bit", "both", "bring",
        "but", "buy", "by", "call", "called", "came", "can", "cannot", "case", "change", "come",
        "could", "course", "day", "days", "did", "different", "do", "does", "doing", "done",
        "down", "during", "each", "early", "end", "enough", "even", "ever", "every", "everyone",
        "everything", "fact", "far", "feel", "few", "find", "first", "for", "found", "free",
        "from", "full", "gave", "get", "gets", "getting", "give", "go", "goes", "going", "gone",
        "good", "got", "great", "had", "hand", "happen", "hard", "has", "have", "having", "he",
        "head", "hear", "heard", "help", "her", "here", "hey", "hi", "high", "him", "his",
        "hold", "home", "hope", "house", "how", "however", "i", "idea", "if", "important", "in",
        "instead", "into", "is", "it", "its", "just", "keep", "kept", "kind", "knew", "know",
        "known", "large", "last", "late", "later", "least", "leave", "left", "less", "let",
        "life", "like", "line", "little", "long", "look", "looking", "lot", "lots", "love",
        "made", "make", "making", "man", "many", "may", "maybe", "me", "mean", "meet", "meeting",
        "might", "mind", "more", "morning", "most", "move", "much", "must", "my", "name", "need",
        "never", "new", "next", "nice", "night", "no", "not", "nothing", "now", "number", "of",
        "off", "often", "oh", "ok", "okay", "old", "on", "once", "one", "only", "open", "or",
        "other", "others", "our", "out", "over", "own", "part", "people", "perhaps", "person",
        "place", "play", "please", "point", "pretty", "probably", "problem", "put", "question",
        "quite", "rather", "read", "real", "really", "reason", "right", "room", "run", "said",
        "same", "saw", "say", "says", "see", "seem", "seen", "send", "sent", "set", "several",
        "she", "short", "should", "show", "side", "since", "small", "so", "some", "someone",
        "something", "sometimes", "soon", "sorry", "start", "still", "stop", "such", "sure",
        "take", "talk", "team", "tell", "than", "thank", "thanks", "that", "the", "their",
        "them", "then", "there", "these", "they", "thing", "things", "think", "this", "those",
        "though", "thought", "three", "through", "time", "times", "to", "today", "together",
        "told", "tomorrow", "tonight", "too", "took", "top", "try", "trying", "turn", "two",
        "under", "understand", "until", "up", "upon", "us", "use", "used", "using", "very",
        "want", "wanted", "was", "watch", "water", "way", "we", "week", "well", "went", "were",
        "what", "when", "where", "which", "while", "who", "whole", "why", "will", "with",
        "without", "word", "words", "work", "working", "world", "would", "write", "yeah", "year",
        "years", "yes", "yesterday", "yet", "you", "your",
    };

    /// <summary>
    /// English homophones and near-homophone spelling confusions, one group per
    /// entry, as compact keys (lower case, no apostrophes). Not exhaustive: the
    /// pairs that matter are the ones a dictation engine mixes up and a user
    /// then fixes by hand — and above all the ones outside
    /// <see cref="CommonWords"/>, which nothing else stops EditLearner from
    /// turning into a rule (weather → whether, principal → principle).
    /// </summary>
    private static readonly string[] Homophones =
    {
        "accept|except", "affect|effect", "aisle|isle", "allowed|aloud", "altar|alter", "ate|eight",
        "bare|bear", "base|bass", "be|bee", "beat|beet", "berry|bury", "berth|birth", "billed|build",
        "blew|blue", "board|bored", "born|borne", "brake|break", "bridal|bridle", "buy|by|bye",
        "canvas|canvass", "capital|capitol", "ceiling|sealing", "cell|sell", "cent|scent|sent",
        "cereal|serial", "cheap|cheep", "chews|choose", "chord|cord", "cite|site|sight",
        "coarse|course", "colonel|kernel", "complement|compliment", "council|counsel",
        "creak|creek", "currant|current", "dear|deer", "desert|dessert", "dew|due", "die|dye",
        "discreet|discrete", "dual|duel", "elicit|illicit", "ensure|insure", "eye|i",
        "fair|fare", "faze|phase", "feat|feet", "find|fined", "flair|flare", "flea|flee",
        "flew|flu|flue", "flour|flower", "for|four|fore", "foreword|forward", "forth|fourth",
        "gait|gate", "genes|jeans", "grate|great", "groan|grown", "guessed|guest", "hair|hare",
        "hall|haul", "heal|heel", "hear|here", "heard|herd", "hi|high", "higher|hire", "him|hymn",
        "hoard|horde", "hoarse|horse", "hole|whole", "hour|our", "idle|idol", "in|inn",
        "knead|need", "knew|new", "knight|night", "knot|not", "know|no", "knows|nose",
        "lead|led", "lessen|lesson", "lightening|lightning", "loan|lone", "loose|lose",
        "made|maid", "mail|male", "main|mane", "manner|manor", "mantel|mantle", "meat|meet|mete",
        "medal|meddle", "metal|mettle", "might|mite", "mind|mined", "miner|minor", "missed|mist",
        "moose|mousse", "morning|mourning", "muscle|mussel", "naval|navel", "none|nun",
        "oar|or|ore", "one|won", "pail|pale", "pain|pane", "pair|pare|pear", "palate|palette|pallet",
        "passed|past", "patience|patients", "pause|paws", "peace|piece", "peak|peek|pique",
        "peal|peel", "pedal|peddle", "peer|pier", "plain|plane", "pole|poll", "poor|pore|pour",
        "pray|prey", "presence|presents", "principal|principle", "profit|prophet", "rain|reign|rein",
        "raise|rays", "rap|wrap", "read|red|reed", "real|reel", "right|rite|wright|write",
        "ring|wring", "road|rode|rowed", "role|roll", "root|route", "rose|rows", "sail|sale",
        "scene|seen", "sea|see", "seam|seem", "sew|so|sow", "shone|shown", "side|sighed",
        "sighs|size", "sole|soul", "some|sum", "son|sun", "stair|stare", "stake|steak",
        "stationary|stationery", "steal|steel", "storey|story", "straight|strait", "suite|sweet",
        "tail|tale", "taught|taut", "tea|tee", "team|teem", "tear|tier", "than|then",
        "their|there|theyre", "threw|through", "tide|tied", "to|too|two", "toe|tow", "vain|vane|vein",
        "vary|very", "wail|whale", "waist|waste", "wait|weight", "war|wore", "ware|wear|where",
        "warn|worn", "way|weigh", "weak|week", "weather|whether", "which|witch", "whine|wine",
        "whos|whose", "wood|would", "yoke|yolk", "yore|your|youre",
    };

    /// <summary>Word → index of its group in <see cref="Homophones"/>.</summary>
    private static readonly Dictionary<string, int> HomophoneGroup = BuildHomophoneGroups();

    private static Dictionary<string, int> BuildHomophoneGroups()
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var g = 0; g < Homophones.Length; g++)
        {
            foreach (var word in Homophones[g].Split('|'))
            {
                // A word in two groups would silently join them; first wins.
                map.TryAdd(word, g);
            }
        }
        return map;
    }

    // ----- Self-test -----

    /// <summary>Test vectors for --selftest. Pure; touches nothing on disk.</summary>
    public static (int Passed, int Total, IReadOnlyList<string> Lines) SelfTest()
    {
        var lines = new List<string>();
        var passed = 0;
        var total = 0;

        void Check(string name, IReadOnlyList<string> got, params string[] expected)
        {
            total++;
            var ok = got.Count == expected.Length
                     && got.Zip(expected).All(p => p.First == p.Second);
            if (ok) passed++;
            lines.Add($"{(ok ? "PASS" : "FAIL")} terms/{name}: got [{string.Join(", ", got)}]"
                + (ok ? "" : $" expected [{string.Join(", ", expected)}]"));
        }

        Check("names", Extract("We met Sarah and Tom at the Hilton yesterday."), "Sarah", "Tom", "Hilton");
        Check("identifiers", Extract("Push the fix to GitHub and ping @devon in #flowtype-dev."),
            "GitHub", "@devon", "#flowtype-dev");
        Check("alnum+acronym", Extract("Running gpt4o on an RTX4070 with CUDA."), "gpt4o", "RTX4070", "CUDA");
        Check("files", Extract("Open App.xaml.cs and index.ts please"), "App.xaml.cs", "index.ts");
        Check("stoplist", Extract("I think The plan is OK. Thanks!"));
        Check("abbreviation", Extract("Meeting on Monday in January with Dr. Patel"), "Patel");
        Check("shouting", Extract("THIS IS SO COOL"));
        Check("run", Extract("I've been playing Arma Reforger all week"), "Arma Reforger");
        Check("frequency", Extract("ask Zed and Kai, then Zed again, then Zed"), "Zed", "Kai");
        Check("length", Extract("a B and Supercalifragilisticexpialidociousandmoreletters1234567 x"));
        Check("path", Extract("edit src/FlowType/Engine/VocabularyMatcher.cs now"), "VocabularyMatcher.cs");
        Check("no-pii", Extract("mail roy@example.com or visit https://example.com/Docs"));
        Check("units", Extract("on the 2nd at 10am for 5x speed in the 1990s with v2"));
        Check("possessive", Extract("it's Sarah's laptop"), "Sarah");
        Check("max", Extract("ask Zed, Kai, Bo and Lu", max: 2), "Zed", "Kai");
        Check("greeting", Extract("Dear Sarah,\nThanks for the update.\nBest,\nTom"), "Sarah");
        Check("title-editor", ExtractFromTitle("● App.xaml.cs - FlowType - Visual Studio Code", "Code"),
            "App.xaml.cs", "FlowType");
        Check("title-mail", ExtractFromTitle("Sarah Connor - Inbox - Outlook", "olk"), "Sarah Connor");
        Check("title-discord", ExtractFromTitle("#modding | Arma Reforger - Discord", "Discord"),
            "#modding", "Arma Reforger");
        Check("title-edge", ExtractFromTitle("New Tab - Personal - Microsoft\u200B Edge", "msedge"));
        Check("merge", Merge(3, new[] { "Alpha", "Bravo" }, new[] { "bravo", "Charlie", "Delta" }),
            "Alpha", "Bravo", "Charlie");

        void Is(string name, bool ok)
        {
            total++;
            if (ok) passed++;
            lines.Add($"{(ok ? "PASS" : "FAIL")} terms/{name}");
        }

        // Grammar, not terms (EditLearner refuses these as rules).
        Is("homophones", AreHomophones("weather", "whether") && AreHomophones("principle", "principal")
                         && AreHomophones("theyre", "there") && AreHomophones("Rose", "rows")
                         && !AreHomophones("kara", "cara") && !AreHomophones("weather", "weather"));
        Is("inflections", AreInflections("widgets", "widget") && AreInflections("deployed", "deploying")
                          && AreInflections("compile", "compiling") && AreInflections("plan", "planned")
                          && AreInflections("dependency", "dependencies") && AreInflections("tried", "try")
                          && AreInflections("boxes", "box")
                          && !AreInflections("shawn", "sean") && !AreInflections("jason", "json")
                          && !AreInflections("kara", "kara") && !AreInflections("class", "clas"));

        return (passed, total, lines);
    }
}
