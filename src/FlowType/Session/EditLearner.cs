using System.Diagnostics;
using FlowType.Context;
using FlowType.Core;
using FlowType.Engine;

namespace FlowType.Session;

/// <summary>
/// Learns from the corrections you make to a dictation after it lands, by
/// reading the field again and comparing.
///
/// <see cref="CorrectionWatcher"/> already learns from one gesture: backspace
/// over the end of what was inserted and retype it. That is the only edit it
/// can see, because it works from keystrokes. Most real fixes are not like
/// that — you click on the misheard name in the middle of a sentence, or
/// double-click it and type over it. This class handles those by doing what
/// Wispr Flow's helper does: re-read the text box a few times after the paste,
/// find the dictation again by the text that was on either side of it, line
/// the words up against what was inserted, and keep the small substitutions.
///
/// Wispr sends those candidates to a cloud LLM that decides which are real
/// terms. FlowType has no cloud, so the decision is local and deliberately
/// conservative, because a learned entry is a rewrite rule applied to every
/// future dictation: the new phrase must look like a term, the old phrase
/// must not be ordinary English, the two must be close in spelling or sound,
/// and casing- or punctuation-only edits, contractions, inflections
/// (widget → widgets, deployed → deploying) and homophones (weather → whether)
/// never count — those are grammar, and every one of them would otherwise
/// rewrite a real English word forever. The evidence that the new phrase is a
/// term comes in two strengths. Strong — a capital where grammar does not
/// explain it, a digit, an inner capital — is learned at once. Weak — only
/// "this word is not among the ~400 most common" — is not enough on its own:
/// such a fix is learned only when the same pair has been made in two
/// separate dictations. A missed correction costs nothing; a wrong rule
/// mangles text until the user finds and deletes it.
///
/// Privacy: the field text read here lives in memory for one comparison and
/// is never logged or saved. A weak pair waiting for its second sighting
/// (a few words: the heard and written phrase) is kept in memory only, for at
/// most a week, and never written. The only thing written anywhere is the
/// dictionary entry your own correction produced, exactly as with
/// CorrectionWatcher.
/// </summary>
public sealed class EditLearner
{
    /// <summary>When the field is read again, measured from the paste.</summary>
    private static readonly TimeSpan[] Checkpoints =
    {
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(45),
    };

    /// <summary>After this the watch ends; an edit later than this is new writing, not a fix.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    /// <summary>How often the foreground window is sampled for "the user moved on".</summary>
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(1);

    /// <summary>One re-read may take this long before it is skipped.</summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>Characters of the text on each side of the paste used to find it again.</summary>
    internal const int AnchorChars = 40;

    /// <summary>A dictation longer than this is not watched: its edits are rewrites, and the re-read would be large.</summary>
    private const int MaxInsertedChars = 4000;

    /// <summary>Longest phrase on either side of a learned correction (Wispr uses the same 4).</summary>
    private const int MaxClusterWords = 4;

    /// <summary>At most this many corrections are taken from one comparison (Wispr's classifier cap).</summary>
    private const int MaxPerRead = 4;

    private const int MinTermLength = 2;
    private const int MaxTermLength = 40;

    /// <summary>Alignment is O(n·m); past this the texts are not a dictation and its edit.</summary>
    private const int MaxAlignWords = 600;

    /// <summary>A gap wider than this on either side is a rewrite, not a correction.</summary>
    private const int MaxGapWords = 12;

    /// <summary>
    /// A fix backed only by weak evidence is learned once it has been seen in
    /// this many separate dictations (one watch counts once, however many
    /// re-reads see it).
    /// </summary>
    private const int WeakSightingsNeeded = 2;

    /// <summary>Weak pairs remembered while they wait for another sighting; the oldest goes first.</summary>
    private const int MaxPending = 64;

    /// <summary>A weak pair not seen again within this long is forgotten.</summary>
    private static readonly TimeSpan PendingLifetime = TimeSpan.FromDays(7);

    public static EditLearner Instance { get; } = new();

    /// <summary>
    /// Raised on the WPF dispatcher (when there is one) after a correction has
    /// been added to the dictionary. Not raised for a pair that was already there.
    /// </summary>
    public event Action<LearnedCorrection>? Learned;

    private readonly object _gate = new();
    private CancellationTokenSource? _watch;

    /// <summary>
    /// Weak pairs seen once ("heard\u0001write", case-insensitive) → sightings
    /// and when last seen. Memory only. Guarded by its own lock: an old watch
    /// can still be finishing a read while a new one starts.
    /// </summary>
    private readonly Dictionary<string, (int Count, DateTime LastUtc)> _pending = new(StringComparer.OrdinalIgnoreCase);

    private EditLearner() { }

    /// <summary>
    /// Start watching the field in <paramref name="target"/> after FlowType
    /// inserted <paramref name="insertedText"/> into it. Replaces any earlier
    /// watch. Does nothing unless corrections are being learned, screen
    /// context is allowed, and the field's caret context was read at capture
    /// time (that is where the anchors come from) and it is not a password field.
    /// </summary>
    public void Arm(string insertedText, FocusSnapshot target)
    {
        Disarm();
        try
        {
            var settings = SettingsStore.Instance.Settings;
            if (!settings.LearnCorrections || !settings.UseScreenContext) return;
        }
        catch
        {
            return;
        }
        if (target is null || target.Element == null || target.IsPassword || !target.Caret.Known) return;
        if (string.IsNullOrWhiteSpace(insertedText)) return;
        var inserted = insertedText.Trim();
        if (inserted.Length > MaxInsertedChars) return;

        var anchors = Anchors.From(target.Caret);
        var cts = new CancellationTokenSource();
        lock (_gate) _watch = cts;
        _ = WatchAsync(inserted, target, anchors, cts.Token);
    }

    /// <summary>Stop watching. Safe to call at any time, from any thread.</summary>
    public void Disarm()
    {
        CancellationTokenSource? old;
        lock (_gate)
        {
            old = _watch;
            _watch = null;
        }
        try { old?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    // ----- The watch -----

    private async Task WatchAsync(string inserted, FocusSnapshot target, Anchors anchors, CancellationToken ct)
    {
        try
        {
            var clock = Stopwatch.StartNew();
            var next = 0;
            var lastForeground = NativeMethods.GetForegroundWindow();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            while (!ct.IsCancellationRequested && clock.Elapsed < Lifetime)
            {
                await Task.Delay(Poll, ct).ConfigureAwait(false);

                var foreground = NativeMethods.GetForegroundWindow();
                var focusMoved = foreground != lastForeground;
                lastForeground = foreground;

                var due = false;
                while (next < Checkpoints.Length && clock.Elapsed >= Checkpoints[next])
                {
                    due = true;
                    next++;
                }
                if (!due && !focusMoved) continue;

                var read = await ReadEditsAsync(target, anchors, inserted).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;
                if (read.Status == EditReadStatus.RegionLost) return;   // sent, cleared, or rewritten
                if (read.Status == EditReadStatus.Failed) continue;     // busy app: try at the next checkpoint

                foreach (var candidate in read.Corrections)
                {
                    var correction = candidate.Correction;
                    // Each re-read sees the same edit again; it counts once per watch.
                    if (!seen.Add(correction.Heard + "\u0001" + correction.Write)) continue;
                    if (candidate.Strong || NoteWeakSighting(correction)) Commit(correction);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disarmed.
        }
        catch
        {
            // Learning is a nicety; a failure here must never surface.
        }
    }

    /// <summary>
    /// Add the rule and announce it, on the UI thread when there is one —
    /// the same thread CorrectionWatcher learns on, so dictionary listeners
    /// see one consistent caller.
    /// </summary>
    private void Commit(LearnedCorrection correction)
    {
        void Apply()
        {
            try
            {
                if (DictionaryStore.Instance.AddLearned(correction.Heard, correction.Write))
                {
                    Learned?.Invoke(correction);
                }
            }
            catch
            {
                // A dictionary write failure is not worth a crash.
            }
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(new Action(Apply));
        else Apply();
    }

    /// <summary>
    /// Count one sighting of a weakly evidenced fix and say whether it has
    /// now been seen often enough (<see cref="WeakSightingsNeeded"/>) to
    /// learn. The caller counts a pair at most once per watch, so two
    /// sightings means two separate dictations got the same fix by hand —
    /// the user's own repetition standing in for the cloud classifier's
    /// judgement. Memory only; never logged or written.
    /// </summary>
    internal bool NoteWeakSighting(LearnedCorrection correction)
    {
        var key = correction.Heard + "\u0001" + correction.Write;
        var now = DateTime.UtcNow;
        lock (_pending)
        {
            foreach (var stale in _pending.Where(kv => now - kv.Value.LastUtc > PendingLifetime)
                         .Select(kv => kv.Key).ToList())
            {
                _pending.Remove(stale);
            }

            var count = (_pending.TryGetValue(key, out var entry) ? entry.Count : 0) + 1;
            if (count >= WeakSightingsNeeded)
            {
                _pending.Remove(key);
                return true;
            }
            if (!_pending.ContainsKey(key) && _pending.Count >= MaxPending)
            {
                var oldest = _pending.MinBy(kv => kv.Value.LastUtc).Key;
                _pending.Remove(oldest);
            }
            _pending[key] = (count, now);
            return false;
        }
    }

    internal enum EditReadStatus
    {
        /// <summary>The field did not answer. Try again later.</summary>
        Failed,
        /// <summary>The dictation can no longer be found between its anchors. Give up.</summary>
        RegionLost,
        Read,
    }

    internal readonly record struct EditRead(EditReadStatus Status, IReadOnlyList<EditCandidate> Corrections);

    /// <summary>
    /// A correction found in one comparison. <paramref name="Strong"/>: the new
    /// phrase carries strong evidence of being a term and may be learned at
    /// once; otherwise it waits for a second sighting (<see cref="NoteWeakSighting"/>).
    /// </summary>
    internal readonly record struct EditCandidate(LearnedCorrection Correction, bool Strong);

    /// <summary>What <see cref="Judge"/> made of a phrase pair.</summary>
    internal enum Verdict
    {
        /// <summary>Not a correction worth a rule.</summary>
        Reject,
        /// <summary>Plausible, but the only evidence is "not a common word": learn on a second sighting.</summary>
        Weak,
        /// <summary>A capital grammar does not explain, a digit or an inner capital: learn now.</summary>
        Strong,
    }

    /// <summary>
    /// The text that sat on either side of the caret when the dictation went
    /// in. The dictation is whatever is between them now.
    /// </summary>
    internal readonly record struct Anchors(string Before, string After)
    {
        public static Anchors From(CaretContext caret)
        {
            var before = caret.Before ?? "";
            var after = caret.After ?? "";
            return new Anchors(
                before.Length > AnchorChars ? before[^AnchorChars..] : before,
                after.Length > AnchorChars ? after[..AnchorChars] : after);
        }
    }

    /// <summary>One re-read of the field and comparison. Writes nothing.</summary>
    internal static async Task<EditRead> ReadEditsAsync(FocusSnapshot target, Anchors anchors, string inserted)
    {
        // Enough on each side of the caret to hold the whole dictation and its
        // anchors wherever in it the user's caret ended up.
        var span = Math.Min(8000, inserted.Length + 1000);
        var window = await FocusContext.ReadWindowAsync(target, span, ReadTimeout).ConfigureAwait(false);
        switch (window.Status)
        {
            case FocusContext.WindowReadStatus.Gone:
                return new EditRead(EditReadStatus.RegionLost, Array.Empty<EditCandidate>());
            case FocusContext.WindowReadStatus.Failed:
                return new EditRead(EditReadStatus.Failed, Array.Empty<EditCandidate>());
        }

        var region = LocateRegion(window.Text, window.AtStart, window.AtEnd, anchors, inserted.Length);
        if (region == null) return new EditRead(EditReadStatus.RegionLost, Array.Empty<EditCandidate>());
        return new EditRead(EditReadStatus.Read, DiffEdits(inserted, region));
    }

    /// <summary>
    /// Find the dictation in <paramref name="text"/>: after the before-anchor
    /// and before the after-anchor. An empty anchor means the dictation went
    /// in at the very start/end of the field, which only counts if this read
    /// reached that edge. When an anchor occurs more than once, the pairing
    /// whose span is closest to the inserted length wins. Null when it cannot
    /// be found or the span is implausibly large.
    /// </summary>
    internal static string? LocateRegion(string text, bool atStart, bool atEnd, Anchors anchors, int insertedLength)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var haystack = Normalize(text);
        var before = Normalize(anchors.Before);
        var after = Normalize(anchors.After);

        var starts = new List<int>();
        if (before.Length == 0)
        {
            if (!atStart) return null;
            starts.Add(0);
        }
        else
        {
            for (var i = haystack.IndexOf(before, StringComparison.Ordinal);
                 i >= 0 && starts.Count < 64;
                 i = haystack.IndexOf(before, i + 1, StringComparison.Ordinal))
            {
                starts.Add(i + before.Length);
            }
        }

        string? best = null;
        var bestCost = int.MaxValue;
        foreach (var start in starts)
        {
            int end;
            if (after.Length == 0)
            {
                if (!atEnd) continue;
                end = haystack.Length;
            }
            else
            {
                end = haystack.IndexOf(after, start, StringComparison.Ordinal);
                if (end < 0) continue;
            }
            var cost = Math.Abs(end - start - insertedLength);
            if (cost < bestCost)
            {
                bestCost = cost;
                best = haystack[start..end];
            }
        }

        if (best == null) return null;
        // Far longer than the dictation: the anchors matched something else,
        // or the user wrote a lot more here. Either way it is not our text.
        if (best.Length > insertedLength * 3 + 200) return null;
        return best;
    }

    /// <summary>Line endings and no-break spaces as apps rewrite them; nothing else.</summary>
    private static string Normalize(string s) =>
        s.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\u00A0', ' ').Replace('\u202F', ' ');

    // ----- Word alignment -----

    private readonly record struct Word(string Raw, string Key);

    /// <summary>
    /// The corrections in <paramref name="edited"/> relative to
    /// <paramref name="inserted"/>, best first, at most four.
    ///
    /// Words are aligned by longest common subsequence on a key that ignores
    /// case, punctuation and a trailing possessive, so "github" → "GitHub",
    /// "Sam" → "Sam!" and "Kara" → "Kara's" are matches, not edits. Each gap
    /// between matches that has words on both sides is a substitution; gaps
    /// on one side only are the user writing more or deleting, never a
    /// correction. Within a gap the whole substitution is tried first, then —
    /// for a fix with extra typing next to it ("Shawn." → "Sean. Also call
    /// Mike") — the closest-sounding slice anchored at either edge.
    /// </summary>
    internal static IReadOnlyList<EditCandidate> DiffEdits(string inserted, string edited)
    {
        var a = Words(inserted);
        var b = Words(edited);
        if (a.Count == 0 || b.Count == 0) return Array.Empty<EditCandidate>();
        if (a.Count > MaxAlignWords || b.Count > MaxAlignWords) return Array.Empty<EditCandidate>();

        var n = a.Count;
        var m = b.Count;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i].Key == b[j].Key
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        // Most of the dictation must still be there; otherwise this is a
        // rewrite and none of its differences are corrections of mishearings.
        var changed = n - lcs[0, 0];
        if (changed > Math.Max(2, n / 2)) return Array.Empty<EditCandidate>();

        var result = new List<EditCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Gap(int a0, int a1, int b0, int b1)
        {
            if (a1 <= a0 || b1 <= b0) return;                                   // pure insert/delete
            if (a1 - a0 > MaxGapWords || b1 - b0 > MaxGapWords) return;
            if (result.Count >= MaxPerRead) return;
            var pick = PickCandidate(a, a0, a1, b, b0, b1);
            if (pick is { } c && seen.Add(c.Correction.Heard + "\u0001" + c.Correction.Write)) result.Add(c);
        }

        int x = 0, y = 0, gapA = 0, gapB = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && a[x].Key == b[y].Key)
            {
                Gap(gapA, x, gapB, y);
                x++;
                y++;
                gapA = x;
                gapB = y;
            }
            else if (y >= m || (x < n && lcs[x + 1, y] >= lcs[x, y + 1]))
            {
                x++;
            }
            else
            {
                y++;
            }
        }
        Gap(gapA, n, gapB, m);
        return result;
    }

    private static List<Word> Words(string text)
    {
        var list = new List<Word>();
        if (string.IsNullOrWhiteSpace(text)) return list;
        foreach (var raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var key = AlignKeyOf(raw);
            // A token with no letters or digits (a dash, an emoji, "…") is
            // neither a word nor an edit.
            if (key.Length > 0) list.Add(new Word(raw, key));
        }
        return list;
    }

    /// <summary>
    /// The key a word is aligned on: <see cref="KeyOf"/> without a trailing
    /// possessive. "Kara" → "Kara's" is the user adding a word, not replacing
    /// one; keyed apart, the two fell into one gap, and the edge-slice search
    /// then paired "Kara" with whatever was typed after it ("Kara's colleague
    /// Cara" taught Kara → Cara). Only the alignment key changes — Raw, which
    /// the learned pair is built from, keeps the apostrophe.
    /// </summary>
    private static string AlignKeyOf(string raw)
    {
        var end = raw.Length;
        while (end > 0 && !char.IsLetterOrDigit(raw[end - 1])) end--;
        var core = raw[..end];
        if (EndsWithPossessive(core))
        {
            var stripped = KeyOf(core[..^2]);
            if (stripped.Length > 0) return stripped;
        }
        return KeyOf(raw);
    }

    private static string KeyOf(string raw)
    {
        var chars = new char[raw.Length];
        var k = 0;
        foreach (var c in raw)
        {
            if (char.IsLetterOrDigit(c)) chars[k++] = char.ToLowerInvariant(c);
        }
        return new string(chars, 0, k);
    }

    private readonly record struct Option(int A0, int A1, int B0, int B1, double Similarity);

    /// <summary>
    /// The best acceptable correction inside one gap, or null. The whole gap
    /// wins if it passes; otherwise slices anchored at a gap edge are tried,
    /// closest spelling first.
    /// </summary>
    private static EditCandidate? PickCandidate(List<Word> a, int a0, int a1, List<Word> b, int b0, int b1)
    {
        var options = new List<Option>();
        if (a1 - a0 <= MaxClusterWords && b1 - b0 <= MaxClusterWords)
        {
            var whole = Build(a, a0, a1, b, b0, b1);
            if (whole is { } w && Judge(w.Heard, w.Write, AtSentenceStart(b, b0)) is var verdict
                && verdict != Verdict.Reject)
            {
                return new EditCandidate(w, verdict == Verdict.Strong);
            }
        }

        // Slices of the edited side (extra words typed next to the fix)…
        if (a1 - a0 <= MaxClusterWords)
        {
            for (var k = 1; k < b1 - b0 && k <= MaxClusterWords; k++)
            {
                options.Add(Score(a, a0, a1, b, b0, b0 + k));
                options.Add(Score(a, a0, a1, b, b1 - k, b1));
            }
        }
        // …and of the inserted side (neighbouring words deleted with it).
        if (b1 - b0 <= MaxClusterWords)
        {
            for (var k = 1; k < a1 - a0 && k <= MaxClusterWords; k++)
            {
                options.Add(Score(a, a0, a0 + k, b, b0, b1));
                options.Add(Score(a, a1 - k, a1, b, b0, b1));
            }
        }

        foreach (var o in options.OrderByDescending(o => o.Similarity))
        {
            // The heard words still stand in the edited gap beside the slice:
            // nothing was replaced, the user only typed next to them.
            if (StillPresent(a, o.A0, o.A1, b, b0, b1, o.B0, o.B1)) continue;
            var c = Build(a, o.A0, o.A1, b, o.B0, o.B1);
            if (c is { } candidate && Judge(candidate.Heard, candidate.Write, AtSentenceStart(b, o.B0)) is var verdict
                && verdict != Verdict.Reject)
            {
                return new EditCandidate(candidate, verdict == Verdict.Strong);
            }
        }
        return null;
    }

    /// <summary>
    /// Whether any heard word of a[<paramref name="a0"/>..<paramref name="a1"/>)
    /// (by alignment key) also appears in the edited gap
    /// b[<paramref name="b0"/>..<paramref name="b1"/>) outside the slice
    /// b[<paramref name="s0"/>..<paramref name="s1"/>) proposed as its
    /// replacement. LCS normally aligns such a word; when word order keeps it
    /// from doing so, it is still not a word that was corrected.
    /// </summary>
    private static bool StillPresent(List<Word> a, int a0, int a1, List<Word> b, int b0, int b1, int s0, int s1)
    {
        for (var j = b0; j < b1; j++)
        {
            if (j >= s0 && j < s1) continue;
            for (var i = a0; i < a1; i++)
            {
                if (a[i].Key == b[j].Key) return true;
            }
        }
        return false;
    }

    private static Option Score(List<Word> a, int a0, int a1, List<Word> b, int b0, int b1)
    {
        var h = string.Concat(a.Skip(a0).Take(a1 - a0).Select(w => w.Key));
        var w = string.Concat(b.Skip(b0).Take(b1 - b0).Select(x => x.Key));
        return new Option(a0, a1, b0, b1, Similarity(h, w));
    }

    /// <summary>
    /// The phrase pair as the user would read it: words joined by single
    /// spaces, punctuation trimmed from the ends, and a possessive on both
    /// sides dropped ("Shawn's" → "Sean's" teaches Shawn → Sean).
    /// </summary>
    private static LearnedCorrection? Build(List<Word> a, int a0, int a1, List<Word> b, int b0, int b1)
    {
        var heard = TrimEnds(string.Join(" ", a.Skip(a0).Take(a1 - a0).Select(w => w.Raw)));
        var write = TrimEnds(string.Join(" ", b.Skip(b0).Take(b1 - b0).Select(w => w.Raw)));
        if (EndsWithPossessive(heard) && EndsWithPossessive(write))
        {
            heard = heard[..^2];
            write = write[..^2];
        }
        if (heard.Length == 0 || write.Length == 0) return null;
        return new LearnedCorrection(heard, write);
    }

    private static string TrimEnds(string s)
    {
        var start = 0;
        while (start < s.Length && !char.IsLetterOrDigit(s[start])) start++;
        var end = s.Length;
        while (end > start && !char.IsLetterOrDigit(s[end - 1])) end--;
        return s[start..end];
    }

    private static bool EndsWithPossessive(string s) =>
        s.Length > 2 && (s.EndsWith("'s", StringComparison.OrdinalIgnoreCase)
                         || s.EndsWith("’s", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether the edited word at <paramref name="index"/> starts a sentence,
    /// where a capital letter is grammar rather than evidence of a name. The
    /// first word of the region counts as a sentence start: what came before
    /// it is not part of the comparison, so assume the cautious case.
    /// </summary>
    private static bool AtSentenceStart(List<Word> words, int index)
    {
        if (index <= 0) return true;
        var previous = words[index - 1].Raw.TrimEnd('"', '\'', '”', '’', ')', ']', '}', '»');
        return previous.Length > 0 && previous[^1] is '.' or '!' or '?' or '…';
    }

    // ----- Is this a correction worth a rule? -----

    /// <summary>
    /// The local stand-in for Wispr's cloud classifier. Every test here is
    /// there because the rule it would otherwise create is applied to all
    /// future dictations, whole-word and case-insensitively.
    ///
    /// Returns <see cref="Verdict.Weak"/> when the pair passes but the only
    /// sign that the new phrase is a term is that it is not among the ~400
    /// common words — an ordinary English word ("deploying", "dependencies",
    /// "whether") is outside that list too, so that alone must never create
    /// a rule; the caller waits for a second sighting.
    /// </summary>
    internal static Verdict Judge(string heard, string write, bool writeAtSentenceStart)
    {
        if (heard.Length is < MinTermLength or > MaxTermLength) return Verdict.Reject;
        if (write.Length is < MinTermLength or > MaxTermLength) return Verdict.Reject;
        if (!heard.Any(char.IsLetter) || !write.Any(char.IsLetter)) return Verdict.Reject;
        if (IsAddressLike(heard) || IsAddressLike(write)) return Verdict.Reject;

        var heardWords = heard.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var writeWords = write.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (heardWords.Length > MaxClusterWords || writeWords.Length > MaxClusterWords) return Verdict.Reject;

        // Casing and punctuation only ("github" → "GitHub", "e-mail" → "email"):
        // never learned. A change in spacing ("Git Hub" → "GitHub") is kept.
        if (WordKey(heardWords) == WordKey(writeWords)) return Verdict.Reject;

        // "their" → "they're" is grammar, not a term.
        if (writeWords.Any(IsContraction)) return Verdict.Reject;

        // So are "widgets" → "widget", "deployed" → "deploying" and
        // "weather" → "whether" — whatever the casing says. A rule from any of
        // them rewrites a real English word in every later dictation; the
        // phonetic and spelling tests below would wave all of them through.
        if (DiffersOnlyByGrammar(heardWords, writeWords)) return Verdict.Reject;

        // A rule whose trigger is ordinary English ("meet", "their", "for good")
        // would rewrite ordinary speech forever.
        if (heardWords.All(w => ContextTerms.IsCommonWord(KeyOf(w)))) return Verdict.Reject;

        var (isTerm, strongEvidence) = TermEvidence(writeWords, writeAtSentenceStart);
        if (!isTerm) return Verdict.Reject;

        var h = KeyOf(heard);
        var w = KeyOf(write);
        if (h.Length == 0 || w.Length == 0) return Verdict.Reject;

        // Adding to a word ("Flow" → "FlowType") is adding meaning, not fixing
        // a mishearing — and a rule would rewrite every future "flow".
        if (heardWords.Length == 1 && h.Length >= 3 && w.Length > h.Length
            && (w.StartsWith(h, StringComparison.Ordinal) || w.EndsWith(h, StringComparison.Ordinal)))
        {
            return Verdict.Reject;
        }

        var phonetic = IsAsciiLetters(h) && IsAsciiLetters(w)
                       && VocabularyMatcher.PhoneticKey(h) == VocabularyMatcher.PhoneticKey(w);
        // Without a capital or digit to say "this is a name", ask for closer
        // spelling: "probably" → "possibly" is a word choice, not a mishearing.
        var needed = strongEvidence ? 0.5 : 0.6;
        if (!phonetic && Similarity(h, w) < needed) return Verdict.Reject;
        return strongEvidence ? Verdict.Strong : Verdict.Weak;
    }

    /// <summary>
    /// Same number of words, and every word that changed is an inflection of
    /// its partner or a homophone of it (<see cref="ContextTerms.AreInflections"/>,
    /// <see cref="ContextTerms.AreHomophones"/>). A change in word count
    /// ("Git Hub" → "GitHub") is never just grammar.
    /// </summary>
    private static bool DiffersOnlyByGrammar(string[] heardWords, string[] writeWords)
    {
        if (heardWords.Length != writeWords.Length) return false;
        var differs = false;
        for (var k = 0; k < heardWords.Length; k++)
        {
            var x = KeyOf(heardWords[k]);
            var y = KeyOf(writeWords[k]);
            if (x == y) continue;
            differs = true;
            if (!ContextTerms.AreInflections(x, y) && !ContextTerms.AreHomophones(x, y)) return false;
        }
        return differs;
    }

    /// <summary>
    /// Whether the new phrase looks like a term, and whether the evidence is
    /// strong (a digit, an inner capital, or a capital where the sentence
    /// does not start) rather than only "not a common word".
    /// </summary>
    private static (bool IsTerm, bool Strong) TermEvidence(string[] words, bool atSentenceStart)
    {
        var strong = false;
        for (var k = 0; k < words.Length; k++)
        {
            var word = words[k];
            if (word.Any(char.IsDigit)) strong = true;
            if (word.Skip(1).Any(char.IsUpper)) strong = true;
            if (word.Length > 0 && char.IsUpper(word[0]) && word != "I" && (k > 0 || !atSentenceStart)) strong = true;
        }
        var uncommon = words.Any(word => !ContextTerms.IsCommonWord(KeyOf(word)));
        return (strong || uncommon, strong);
    }

    private static string WordKey(string[] words) => string.Join(" ", words.Select(KeyOf).Where(k => k.Length > 0));

    private static bool IsContraction(string word)
    {
        var w = word.Replace('’', '\'');
        // O'Brien, D'Angelo: an apostrophe after a leading capital is a name.
        if (w.Length > 2 && char.IsUpper(w[0]) && w[1] == '\'' && char.IsUpper(w[2])) return false;
        return w.EndsWith("n't", StringComparison.OrdinalIgnoreCase)
               || w.EndsWith("'s", StringComparison.OrdinalIgnoreCase)
               || w.EndsWith("'ll", StringComparison.OrdinalIgnoreCase)
               || w.EndsWith("'re", StringComparison.OrdinalIgnoreCase)
               || w.EndsWith("'ve", StringComparison.OrdinalIgnoreCase)
               || w.EndsWith("'d", StringComparison.OrdinalIgnoreCase)
               || w.EndsWith("'m", StringComparison.OrdinalIgnoreCase)
               || w.EndsWith('\'');
    }

    private static bool IsAddressLike(string s) =>
        s.Contains('@') || s.Contains("://") || s.Contains('/') || s.Contains('\\');

    private static bool IsAsciiLetters(string s) => s.Length > 0 && s.All(c => c is >= 'a' and <= 'z');

    /// <summary>1 − normalised edit distance on the compact keys.</summary>
    private static double Similarity(string a, string b)
    {
        var max = Math.Max(a.Length, b.Length);
        if (max == 0) return 1.0;
        return 1.0 - (double)VocabularyMatcher.Levenshtein(a, b) / max;
    }

    // ----- Self-test -----

    /// <summary>Test vectors for --selftest. Pure: no UI Automation, no dictionary writes.</summary>
    public static (int Passed, int Total, IReadOnlyList<string> Lines) SelfTest()
    {
        var lines = new List<string>();
        var passed = 0;
        var total = 0;

        // "heard→write" is learned at once; "heard→write?" waits for a second sighting.
        void Diff(string name, string inserted, string edited, params string[] expected)
        {
            total++;
            var got = DiffEdits(inserted, edited)
                .Select(c => $"{c.Correction.Heard}→{c.Correction.Write}{(c.Strong ? "" : "?")}").ToList();
            var ok = got.SequenceEqual(expected);
            if (ok) passed++;
            lines.Add($"{(ok ? "PASS" : "FAIL")} edits/{name}: got [{string.Join(", ", got)}]"
                + (ok ? "" : $" expected [{string.Join(", ", expected)}]"));
        }

        void Locate(string name, string text, bool atStart, bool atEnd, string before, string after,
            int length, string? expected)
        {
            total++;
            var got = LocateRegion(text, atStart, atEnd, new Anchors(before, after), length);
            var ok = got == expected;
            if (ok) passed++;
            lines.Add($"{(ok ? "PASS" : "FAIL")} region/{name}: got {(got == null ? "null" : $"'{got}'")}"
                + (ok ? "" : $" expected {(expected == null ? "null" : $"'{expected}'")}"));
        }

        Diff("name", "Let's meet Shawn tomorrow.", "Let's meet Sean tomorrow.", "Shawn→Sean");
        Diff("two-word name", "I play Armour Forger every day.", "I play Arma Reforger every day.",
            "Armour Forger→Arma Reforger");
        Diff("joined", "we use Charge Bee for billing", "we use ChargeBee for billing", "Charge Bee→ChargeBee");
        Diff("merged words", "set up Kuber Netties today", "set up Kubernetes today", "Kuber Netties→Kubernetes");
        Diff("casing only", "push it to github now", "push it to GitHub now");
        Diff("punctuation only", "hello there, Sam", "hello there Sam!");
        Diff("insertion", "call Sean", "call Sean tomorrow at noon");
        Diff("deletion", "call Sean right now", "call Sean");
        Diff("contraction", "I think their right", "I think they're right");
        Diff("word choice", "it will probably work", "it will possibly work");
        Diff("fix plus typing", "send it to Shawn.", "send it to Sean. Also call Mike", "Shawn→Sean");
        Diff("rewrite", "the quick brown fox jumps", "a slow red dog sleeps");
        Diff("extension", "open Flow now", "open FlowType now");
        Diff("common trigger", "we can meet later", "we can Meetup later");
        Diff("numbers", "ordered an RTX forty seventy", "ordered an RTX 4070");
        Diff("possessive", "ask Shawn's team", "ask Sean's team", "Shawn→Sean");
        // "Sean" opens the sentence, so its capital is grammar: only "not a
        // common word" says it is a name, which is weak evidence.
        Diff("two fixes", "Shawn and Kara met at Starbucks", "Sean and Cara met at Starbucks",
            "Shawn→Sean?", "Kara→Cara");
        Diff("weak term", "open the jason file", "open the json file", "jason→json?");

        // Grammar is never a term, whatever the evidence (each of these passed before).
        Diff("plural", "ship the widgets today", "ship the widget today");
        Diff("tense", "they deployed it", "they are deploying it");
        Diff("y plural", "update the dependency now", "update the dependencies now");
        Diff("homophone", "I wonder weather it works", "I wonder whether it works");
        Diff("homophone name", "say hi to rows", "say hi to Rose");

        // The heard word is still there: "Shawn's" aligns with "Shawn", so
        // everything after it is typing, not a fix of Shawn → Sean.
        Diff("possessive insertion", "send it to Shawn", "send it to Shawn's colleague Sean");
        Diff("possessive insertion 2", "Please invite Kara to the review.",
            "Please invite Karl and Kara's team to the review.");

        void Is(string name, bool ok)
        {
            total++;
            if (ok) passed++;
            lines.Add($"{(ok ? "PASS" : "FAIL")} edits/{name}");
        }

        // A weak pair is learned on its second sighting, then starts over.
        var probe = new EditLearner();
        var weak = new LearnedCorrection("jason", "json");
        Is("weak needs two sightings", !probe.NoteWeakSighting(weak) && probe.NoteWeakSighting(weak)
                                       && !probe.NoteWeakSighting(weak)
                                       && !probe.NoteWeakSighting(new LearnedCorrection("jason", "JSON5")));

        Locate("between anchors", "Hello Bob. Let's meet Sean tomorrow. Bye", false, false,
            "Hello Bob. ", " Bye", 25, "Let's meet Sean tomorrow.");
        Locate("anchors gone", "Something else entirely", false, false, "Hello Bob. ", " Bye", 25, null);
        Locate("field start", "Let's meet Sean. Bye", true, false, "", " Bye", 16, "Let's meet Sean.");
        Locate("start not reached", "Let's meet Sean. Bye", false, false, "", " Bye", 16, null);
        Locate("field end", "Hi. Let's meet Sean.", false, true, "Hi. ", "", 16, "Let's meet Sean.");
        Locate("closest pair", "x. A. x. Let's go now. y", false, false, "x. ", ". y", 12, "Let's go now");

        return (passed, total, lines);
    }
}
