using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace FlowType.Learning;

/// <summary>
/// Reads Wispr Flow's local history (<c>%APPDATA%\Wispr Flow\flow.sqlite</c>)
/// for "Learn from your writing": past dictations with Wispr's recogniser
/// output, what it pasted and what the user changed it to afterwards; the
/// recordings Wispr still keeps; its dictionary; and the writing styles it
/// applied.
///
/// Rules this class keeps, because the database belongs to another app that
/// is usually running and writing to it while we read:
/// - <b>Read-only, always.</b> Every open is <see cref="WinSqlite.OpenReadOnly"/>,
///   so SQLite itself refuses writes. Nothing is copied, cached or modified.
/// - <b>Short-lived connections.</b> Each call opens, reads and closes. A read
///   left open on a WAL database keeps its owner's checkpoints from finishing,
///   so nothing here holds one between calls.
/// - <b>Schema drift is expected.</b> Wispr adds and renames columns between
///   releases (its History table has 60+). Every query selects only the
///   columns <see cref="WinSqlite.Columns"/> reports, and a missing column
///   reads as NULL, so an update on Wispr's side degrades a feature rather
///   than breaking the read.
/// - <b>Busy is normal.</b> A checkpoint in progress can lock the file for a
///   moment; reads are retried three times with a growing pause before the
///   problem is reported.
/// - <b>Privacy.</b> Text and audio go to the caller and nowhere else. Nothing
///   here logs, writes or sends them; diagnostics carry error codes only.
///
/// Which rows count as dictations (the same in <see cref="Summarize"/> and
/// <see cref="ReadSamples"/>, so the counts on the Insights card match what a
/// run reads): not <c>dismissed</c> or <c>no_audio</c>, not archived, and with
/// some text. Blank text counts as no text throughout, so a stored "" never
/// hides a real value in the next column. Wispr's list markup is not text:
/// it is turned into line breaks before anything is chosen or compared, in
/// C# (<see cref="StripListMarkup"/>) and in SQL alike.
/// </summary>
public static class WisprHistory
{
    private const string HistoryTable = "History";
    private const string DictionaryTable = "Dictionary";

    /// <summary>Retries after the first attempt when the database is busy.</summary>
    private const int BusyRetries = 3;

    /// <summary>The pause before retry n is n × this.</summary>
    private static readonly TimeSpan BusyBackoff = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The whitespace "blank" and "the same after trimming" ignore. An explicit
    /// set rather than <see cref="char.IsWhiteSpace"/>, because the same rule
    /// runs inside SQLite (<see cref="SqlWhitespace"/>) for the counts, and the
    /// two must agree exactly.
    /// </summary>
    private static readonly char[] Whitespace = { ' ', '\t', '\n', '\v', '\f', '\r', ' ' };

    /// <summary><see cref="Whitespace"/> as an SQL expression, for <c>trim(x, …)</c>.</summary>
    private const string SqlWhitespace = "char(32, 9, 10, 11, 12, 13, 160)";

    /// <summary>The only sample rate FlowType's recogniser takes; anything else is left out rather than resampled badly.</summary>
    private const int RequiredSampleRate = 16000;

    /// <summary>
    /// The markup Wispr Flow writes into dictation text when it formats a
    /// list: pastedText, formattedText and editedText then hold HTML, and its
    /// validator allows only these tags. On the user's PC (2026-10-19, counts
    /// only) 22 of 823 dictations carried them and nothing else appeared — no
    /// attributes, no other tags, no entities. Exact lower-case strings rather
    /// than a pattern, because the same replacement also runs inside SQLite
    /// (<see cref="HistoryShape"/>), whose replace() is ordinal too: the counts
    /// on the Insights card stay equal to what a run reads.
    /// </summary>
    private static readonly string[] ListTags = { "<ul>", "</ul>", "<ol>", "</ol>", "<li>", "</li>" };

    /// <summary>Where Wispr Flow keeps its history on this PC.</summary>
    public static string DefaultDatabasePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Wispr Flow", "flow.sqlite");

    // ----- Summary (Insights card) -----

    /// <summary>
    /// What Wispr's history holds, counted inside SQLite: no dictation text and
    /// no audio is read into FlowType for this, so it is cheap enough to call
    /// whenever the Insights page opens. Never throws; a problem is reported in
    /// <see cref="SourceSummary.Problem"/>.
    /// </summary>
    public static SourceSummary Summarize(string? path = null)
    {
        var file = path ?? DefaultDatabasePath;
        if (!File.Exists(file)) return Unavailable(file, NotFoundProblem);

        try
        {
            return WithRetry(() => SummarizeOnce(file), CancellationToken.None);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Wispr history summary failed: {Diagnose(ex)}");
            return Unavailable(file, Describe(ex));
        }
    }

    private static SourceSummary SummarizeOnce(string file)
    {
        using var db = WinSqlite.OpenReadOnly(file);
        var shape = HistoryShape.Of(db);
        if (shape == null) return Unavailable(file, UnknownFormatProblem);

        var sql =
            $"SELECT count(*) AS dictations, " +
            $"coalesce(sum(CASE WHEN {shape.HasAudio} THEN 1 ELSE 0 END), 0) AS withAudio, " +
            $"coalesce(sum(CASE WHEN {shape.HasAudio} THEN max(coalesce({shape.Col("duration")}, 0), 0) ELSE 0 END), 0) AS audioSeconds, " +
            $"coalesce(sum(CASE WHEN {shape.EditDiffers} THEN 1 ELSE 0 END), 0) AS edited " +
            $"FROM {WinSqlite.QuoteIdentifier(HistoryTable)} WHERE {shape.IsDictation}";

        foreach (var row in db.Query(sql))
        {
            return new SourceSummary(
                HistorySource.WisprFlow,
                Available: true,
                Dictations: (int)row.GetInt64("dictations"),
                WithAudio: (int)row.GetInt64("withAudio"),
                AudioSeconds: Math.Max(0, row.GetDouble("audioSeconds")),
                Edited: (int)row.GetInt64("edited"),
                Problem: null,
                Location: file);
        }
        return new SourceSummary(HistorySource.WisprFlow, true, 0, 0, 0, 0, null, file);
    }

    private static SourceSummary Unavailable(string file, string problem) =>
        new(HistorySource.WisprFlow, Available: false, 0, 0, 0, 0, problem, file);

    // ----- Dictations -----

    /// <summary>
    /// Every usable dictation, newest first. Returns an empty list when Wispr
    /// Flow's database does not exist; throws <see cref="IOException"/> with a
    /// plain-language message when it exists but cannot be read (busy after
    /// retries, damaged, unreadable layout, Windows' SQLite missing), and
    /// <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    public static IReadOnlyList<HistorySample> ReadSamples(string? path = null, CancellationToken ct = default)
    {
        var file = path ?? DefaultDatabasePath;
        if (!File.Exists(file)) return Array.Empty<HistorySample>();
        return Guarded(() => WithRetry(() => ReadSamplesOnce(file, ct), ct));
    }

    private static List<HistorySample> ReadSamplesOnce(string file, CancellationToken ct)
    {
        using var db = WinSqlite.OpenReadOnly(file);
        var shape = HistoryShape.Of(db) ?? throw new UnknownFormatException();

        var wanted = new[]
        {
            "transcriptEntityId", "asrText", "formattedText", "pastedText", "editedText",
            "timestamp", "app", "detectedLanguage", "language", "duration",
        };
        var select = wanted.Where(shape.Has).Select(WinSqlite.QuoteIdentifier).ToList();
        select.Add($"{shape.AudioBytes} AS audioBytes");
        var sql = $"SELECT {string.Join(", ", select)} FROM {WinSqlite.QuoteIdentifier(HistoryTable)} " +
                  $"WHERE {shape.StatusAndArchiveFilter}";

        var samples = new List<HistorySample>();
        foreach (var row in db.Query(sql))
        {
            ct.ThrowIfCancellationRequested();
            var sample = ToSample(row);
            if (sample != null) samples.Add(sample);
        }
        // Stable sort: rows with equal timestamps keep the table's order.
        return samples.OrderByDescending(s => s.TimestampUtc).ToList();
    }

    /// <summary>One History row → a sample, or null when it has no text (see the class notes for the rules).</summary>
    private static HistorySample? ToSample(WinSqliteRow row)
    {
        // asrText is the recogniser's own output and never holds markup.
        var recognized = row.GetText("asrText");
        var final = FirstNonBlank(
            StripListMarkup(row.GetText("pastedText")), StripListMarkup(row.GetText("formattedText")), recognized);
        if (final == null) return null;

        var id = row.GetText("transcriptEntityId") ?? "";
        return new HistorySample(
            HistorySource.WisprFlow,
            Id: id,
            TimestampUtc: ParseTimestamp(row.GetText("timestamp")),
            App: row.GetText("app") ?? "",
            Recognized: recognized ?? "",
            Final: final,
            Edited: EditOf(StripListMarkup(row.GetText("editedText")), final),
            Language: FirstNonBlank(row.GetText("detectedLanguage"), row.GetText("language")) ?? "",
            DurationSeconds: row.IsNull("duration") ? 0 : Math.Max(0, row.GetDouble("duration")),
            // Without an id the audio could not be fetched again, so it does not count as kept.
            HasAudio: !IsBlank(id) && row.GetInt64("audioBytes") > 0);
    }

    /// <summary>The user's edit, when there is one worth learning from: not blank, and not just the pasted text again.</summary>
    internal static string? EditOf(string? edited, string final)
    {
        if (IsBlank(edited)) return null;
        return string.Equals(edited!.Trim(Whitespace), final.Trim(Whitespace), StringComparison.Ordinal) ? null : edited;
    }

    /// <summary>
    /// Wispr's list markup (<see cref="ListTags"/>) turned into line breaks.
    /// Left in, a tag glues onto the first and last word of every item, and
    /// the miners split on whitespace: "&lt;li&gt;Kubernetes" was offered as a
    /// misheard term ("li&gt;Kubernetes", pre-ticked), and a fix typed inside
    /// an item ("Clod&lt;/li&gt;" → "Claude&lt;/li&gt;") looked like an
    /// address and was thrown away. A line break keeps items apart as words
    /// without inventing punctuation the user never wrote. Any other angle
    /// bracket is the user's text and is left alone. Null stays null.
    /// </summary>
    internal static string? StripListMarkup(string? text)
    {
        if (text == null || text.IndexOf('<') < 0) return text;
        // Same tags, same order as HistoryShape.Stripped. Replacing with a line
        // break can never form a new tag, so the order does not change the result.
        foreach (var tag in ListTags) text = text.Replace(tag, "\n", StringComparison.Ordinal);
        return text;
    }

    private static string? FirstNonBlank(params string?[] values) => values.FirstOrDefault(v => !IsBlank(v));

    private static bool IsBlank(string? value) => value == null || value.Trim(Whitespace).Length == 0;

    /// <summary>
    /// Wispr stores <c>"2026-10-19 06:12:40.965 +00:00"</c> (Sequelize's
    /// format). Parsed exactly first, then leniently; numbers are taken as Unix
    /// time (milliseconds when large). Unparseable → <see cref="DateTime.MinValue"/>
    /// (UTC), which sorts it last rather than dropping a usable dictation.
    /// </summary>
    internal static DateTime ParseTimestamp(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
        var value = text.Trim();

        string[] formats =
        {
            "yyyy-MM-dd HH:mm:ss.FFFFFFF zzz", "yyyy-MM-dd HH:mm:ss zzz",
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        };
        var styles = DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal;
        if (DateTimeOffset.TryParseExact(value, formats, CultureInfo.InvariantCulture, styles, out var exact))
        {
            return exact.UtcDateTime;
        }
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, styles, out var loose))
        {
            return loose.UtcDateTime;
        }
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
        {
            try
            {
                var moment = unix > 100_000_000_000L
                    ? DateTimeOffset.FromUnixTimeMilliseconds(unix)
                    : DateTimeOffset.FromUnixTimeSeconds(unix);
                return moment.UtcDateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                // Falls through to "unknown".
            }
        }
        return DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
    }

    // ----- Audio -----

    /// <summary>
    /// One dictation's recording as 16 kHz mono samples in −1..1, or null
    /// when there is none, it is not 16 kHz PCM16/float32 WAV, or anything at
    /// all goes wrong (the benchmark simply skips that recording). Reads one
    /// blob; nothing is written anywhere.
    /// </summary>
    public static float[]? ReadAudio(string id, string? path = null)
    {
        if (string.IsNullOrEmpty(id)) return null;
        var file = path ?? DefaultDatabasePath;
        if (!File.Exists(file)) return null;

        try
        {
            var wav = WithRetry(() => ReadAudioBlob(file, id), CancellationToken.None);
            return wav == null ? null : DecodeWav(wav);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Wispr audio read failed: {Diagnose(ex)}");
            return null;
        }
    }

    private static byte[]? ReadAudioBlob(string file, string id)
    {
        using var db = WinSqlite.OpenReadOnly(file);
        var columns = db.Columns(HistoryTable);
        if (!Contains(columns, "audio") || !Contains(columns, "transcriptEntityId")) return null;

        foreach (var row in db.Query(
            $"SELECT audio FROM {WinSqlite.QuoteIdentifier(HistoryTable)} WHERE transcriptEntityId = ? LIMIT 1", id))
        {
            return row.GetBlob(0);
        }
        return null;
    }

    /// <summary>
    /// A WAV file → 16 kHz mono floats in −1..1. Walks the RIFF chunks rather
    /// than assuming a 44-byte header, because writers put LIST/fact/JUNK
    /// chunks before the data. Accepts PCM16 and float32 (plain or
    /// WAVE_FORMAT_EXTENSIBLE); averages channels; returns null for any other
    /// rate or encoding, a truncated header, or a file with no data chunk. A
    /// data chunk that claims more than the file holds (streaming writers
    /// leave 0 or 0xFFFFFFFF) is read to the end of the file.
    /// </summary>
    internal static float[]? DecodeWav(byte[] wav)
    {
        var bytes = wav.AsSpan();
        if (bytes.Length < 12 || !IsTag(bytes, 0, "RIFF") || !IsTag(bytes, 8, "WAVE")) return null;

        WavFormat? format = null;
        long pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            var at = (int)pos;
            var declared = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(at + 4, 4));
            var body = at + 8;
            var length = (int)Math.Min(declared, (uint)(bytes.Length - body));

            if (IsTag(bytes, at, "fmt "))
            {
                format = WavFormat.Parse(bytes.Slice(body, length));
                if (format == null) return null;
            }
            else if (IsTag(bytes, at, "data"))
            {
                return format == null ? null : ToMono16k(bytes.Slice(body, length), format.Value);
            }

            // Chunks are word-aligned: an odd-sized chunk is followed by a pad byte.
            pos = (long)body + declared + (declared & 1);
        }
        return null;
    }

    private static bool IsTag(ReadOnlySpan<byte> bytes, int at, string tag) =>
        at + 4 <= bytes.Length
        && bytes[at] == tag[0] && bytes[at + 1] == tag[1] && bytes[at + 2] == tag[2] && bytes[at + 3] == tag[3];

    private readonly record struct WavFormat(bool IsFloat, int Channels, int SampleRate, int BitsPerSample)
    {
        private const ushort Pcm = 1;
        private const ushort IeeeFloat = 3;
        private const ushort Extensible = 0xFFFE;

        public int BytesPerSample => BitsPerSample / 8;

        public static WavFormat? Parse(ReadOnlySpan<byte> fmt)
        {
            if (fmt.Length < 16) return null;
            var tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
            var channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
            var rate = BinaryPrimitives.ReadInt32LittleEndian(fmt[4..]);
            var bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);

            // WAVE_FORMAT_EXTENSIBLE: the real format code is the first two
            // bytes of the sub-format GUID at offset 24.
            if (tag == Extensible)
            {
                if (fmt.Length < 26) return null;
                tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]);
            }

            if (channels < 1) return null;
            return tag switch
            {
                Pcm when bits == 16 => new WavFormat(false, channels, rate, bits),
                IeeeFloat when bits == 32 => new WavFormat(true, channels, rate, bits),
                _ => null,
            };
        }
    }

    private static float[]? ToMono16k(ReadOnlySpan<byte> data, WavFormat format)
    {
        if (format.SampleRate != RequiredSampleRate) return null;

        var frameBytes = format.Channels * format.BytesPerSample;
        var frames = data.Length / frameBytes;
        var samples = new float[frames];
        for (var f = 0; f < frames; f++)
        {
            var frame = data.Slice(f * frameBytes, frameBytes);
            var sum = 0f;
            for (var c = 0; c < format.Channels; c++)
            {
                var at = c * format.BytesPerSample;
                sum += format.IsFloat
                    ? Clean(BinaryPrimitives.ReadSingleLittleEndian(frame[at..]))
                    : BinaryPrimitives.ReadInt16LittleEndian(frame[at..]) / 32768f;
            }
            samples[f] = sum / format.Channels;
        }
        return samples;
    }

    /// <summary>A float sample forced into −1..1; NaN and infinities become silence.</summary>
    private static float Clean(float value) => float.IsFinite(value) ? Math.Clamp(value, -1f, 1f) : 0f;

    // ----- Dictionary and styles -----

    /// <summary>
    /// Wispr's own dictionary (deleted entries left out), in table order.
    /// Empty when the database or the table does not exist; throws
    /// <see cref="IOException"/> with a plain message when it cannot be read.
    /// </summary>
    public static IReadOnlyList<WisprDictionaryEntry> ReadDictionary(string? path = null)
    {
        var file = path ?? DefaultDatabasePath;
        if (!File.Exists(file)) return Array.Empty<WisprDictionaryEntry>();
        return Guarded(() => WithRetry(() => ReadDictionaryOnce(file), CancellationToken.None));
    }

    private static List<WisprDictionaryEntry> ReadDictionaryOnce(string file)
    {
        using var db = WinSqlite.OpenReadOnly(file);
        var columns = db.Columns(DictionaryTable);
        var entries = new List<WisprDictionaryEntry>();
        if (!Contains(columns, "phrase")) return entries;

        string Col(string name) => Contains(columns, name) ? WinSqlite.QuoteIdentifier(name) : "NULL";
        var notDeleted = Contains(columns, "isDeleted") ? "WHERE coalesce(isDeleted, 0) = 0" : "";
        var sql = $"SELECT phrase, {Col("replacement")} AS replacement, {Col("isSnippet")} AS isSnippet, " +
                  $"{Col("source")} AS source FROM {WinSqlite.QuoteIdentifier(DictionaryTable)} {notDeleted}";

        foreach (var row in db.Query(sql))
        {
            var phrase = row.GetText("phrase");
            if (IsBlank(phrase)) continue;
            entries.Add(new WisprDictionaryEntry(
                phrase!.Trim(),
                (row.GetText("replacement") ?? "").Trim(),
                IsSnippet: row.GetInt64("isSnippet") != 0,
                Origin: row.GetText("source") ?? ""));
        }
        return entries;
    }

    /// <summary>
    /// The writing styles Wispr applied, one row per (app type, style) as
    /// stored, most used first — over the same dictations
    /// <see cref="ReadSamples"/> returns. Only Wispr's settings labels are
    /// read (<c>personalizationStyleSettings</c> and the time), never text.
    /// Empty when there is nothing to read; throws <see cref="IOException"/>
    /// with a plain message when the database cannot be read.
    /// </summary>
    public static IReadOnlyList<WisprStyleChoice> ReadStyleChoices(string? path = null)
    {
        var file = path ?? DefaultDatabasePath;
        if (!File.Exists(file)) return Array.Empty<WisprStyleChoice>();
        return Guarded(() => WithRetry(() => ReadStyleChoicesOnce(file), CancellationToken.None));
    }

    private static List<WisprStyleChoice> ReadStyleChoicesOnce(string file)
    {
        using var db = WinSqlite.OpenReadOnly(file);
        var shape = HistoryShape.Of(db);
        if (shape == null || !shape.Has("personalizationStyleSettings")) return new List<WisprStyleChoice>();

        var sql = $"SELECT personalizationStyleSettings AS style, {shape.Col("timestamp")} AS ts " +
                  $"FROM {WinSqlite.QuoteIdentifier(HistoryTable)} " +
                  $"WHERE personalizationStyleSettings IS NOT NULL AND {shape.IsDictation}";

        // Grouped ignoring case; the first spelling seen is the one reported.
        var groups = new Dictionary<(string, string), (string AppType, string Style, int Count, DateTime Last)>();
        foreach (var row in db.Query(sql))
        {
            if (!TryParseStyle(row.GetText("style"), out var appType, out var style)) continue;
            var key = (appType.ToLowerInvariant(), style.ToLowerInvariant());
            var when = ParseTimestamp(row.GetText("ts"));
            groups[key] = groups.TryGetValue(key, out var g)
                ? (g.AppType, g.Style, g.Count + 1, when > g.Last ? when : g.Last)
                : (appType, style, 1, when);
        }

        return groups.Values
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.AppType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Style, StringComparer.OrdinalIgnoreCase)
            .Select(g => new WisprStyleChoice(g.AppType, g.Style, g.Count, g.Last))
            .ToList();
    }

    /// <summary><c>{"personalizationAppType":"other","personalizationStyle":"formal"}</c> → ("other", "formal"). Anything malformed is skipped.</summary>
    internal static bool TryParseStyle(string? json, out string appType, out string style)
    {
        appType = style = "";
        if (IsBlank(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json!);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            appType = StringProperty(doc.RootElement, "personalizationAppType");
            style = StringProperty(doc.RootElement, "personalizationStyle");
            return appType.Length > 0 && style.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string StringProperty(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? "").Trim()
            : "";

    // ----- Schema -----

    private static bool Contains(IReadOnlyList<string> columns, string name) =>
        columns.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which History columns this version of Wispr has, and the SQL fragments
    /// built from them. An absent column becomes the literal NULL, so every
    /// expression below stays valid on any version of the table.
    /// </summary>
    private sealed class HistoryShape
    {
        private readonly HashSet<string> _columns;

        private HistoryShape(IEnumerable<string> columns)
        {
            _columns = new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Null when there is no History table (or it has no text column at all).</summary>
        public static HistoryShape? Of(WinSqlite db)
        {
            var shape = new HistoryShape(db.Columns(HistoryTable));
            var hasText = shape.Has("asrText") || shape.Has("formattedText") || shape.Has("pastedText");
            return hasText ? shape : null;
        }

        public bool Has(string column) => _columns.Contains(column);

        public string Col(string column) => Has(column) ? WinSqlite.QuoteIdentifier(column) : "NULL";

        /// <summary>Byte length of the recording. length() of a blob column is answered without reading the blob.</summary>
        public string AudioBytes => Has("audio") ? "coalesce(length(\"audio\"), 0)" : "0";

        /// <summary>
        /// A recording that can be fetched again: some audio bytes and an id to
        /// fetch them by — the rule <see cref="ToSample"/> applies.
        /// </summary>
        public string HasAudio => $"{AudioBytes} > 0 AND {NonBlank("transcriptEntityId")}";

        /// <summary>Not dismissed / no_audio, not archived (an entry the user put away is not offered back to them).</summary>
        public string StatusAndArchiveFilter
        {
            get
            {
                var status = Has("status")
                    ? "lower(coalesce(\"status\", '')) NOT IN ('dismissed', 'no_audio')"
                    : "1";
                var archived = Has("isArchived") ? "coalesce(\"isArchived\", 0) = 0" : "1";
                return $"({status}) AND ({archived})";
            }
        }

        /// <summary>A dictation: passes the status filter and has some non-blank text.</summary>
        public string IsDictation => $"{StatusAndArchiveFilter} AND ({Final}) IS NOT NULL";

        /// <summary>
        /// The pasted text, else the formatted, else the recognised — the first
        /// that is not blank once list markup is gone (NULL when all are). The
        /// rule <see cref="ToSample"/> applies.
        /// </summary>
        private string Final =>
            $"CASE WHEN {IsNotBlank(Stripped("pastedText"))} THEN {Stripped("pastedText")} " +
            $"WHEN {IsNotBlank(Stripped("formattedText"))} THEN {Stripped("formattedText")} " +
            $"WHEN {NonBlank("asrText")} THEN {Col("asrText")} ELSE NULL END";

        /// <summary>
        /// An edit that is not blank and differs from the final text after
        /// trimming, both without list markup — the rule <see cref="EditOf"/>
        /// applies to <see cref="StripListMarkup"/>'s output. The same words
        /// with and without list tags are not an edit.
        /// </summary>
        public string EditDiffers =>
            $"{IsNotBlank(Stripped("editedText"))} AND {Trimmed(Stripped("editedText"))} <> {Trimmed($"({Final})")}";

        /// <summary>
        /// A text column with Wispr's list tags turned into line breaks, as
        /// <see cref="StripListMarkup"/> does: nested replace() over the same
        /// <see cref="ListTags"/>. SQLite's replace() matches exactly and case-
        /// sensitively, like the ordinal C# replace, and NULL stays NULL. The
        /// tags hold no quote, so they are safe as SQL literals.
        /// </summary>
        private string Stripped(string column)
        {
            var expression = Col(column);
            foreach (var tag in ListTags) expression = $"replace({expression}, '{tag}', char(10))";
            return expression;
        }

        private string NonBlank(string column) => IsNotBlank(Col(column));

        private static string IsNotBlank(string expression) => $"{Trimmed(expression)} <> ''";

        private static string Trimmed(string expression) => $"trim({expression}, {SqlWhitespace})";
    }

    // ----- Errors and retries -----

    private const string NotFoundProblem = "Wispr Flow's history wasn't found on this PC.";
    private const string UnknownFormatProblem = "Wispr Flow's history is in a format FlowType doesn't recognise.";

    /// <summary>
    /// Run one complete read (open → query → close), again from scratch while
    /// the database reports busy. Each attempt builds its own result, so a
    /// retry never returns half of one read and half of another.
    /// </summary>
    private static T WithRetry<T>(Func<T> read, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return read();
            }
            catch (WinSqliteException ex) when (ex.IsBusy && attempt <= BusyRetries)
            {
                Debug.WriteLine($"Wispr history busy (code {ex.Code}), retry {attempt} of {BusyRetries}");
                if (ct.WaitHandle.WaitOne(BusyBackoff * attempt)) ct.ThrowIfCancellationRequested();
            }
        }
    }

    /// <summary>Turn any read failure into an <see cref="IOException"/> whose message can be shown as is; cancellation passes through.</summary>
    private static T Guarded<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Wispr history read failed: {Diagnose(ex)}");
            throw new IOException(Describe(ex), ex);
        }
    }

    /// <summary>A failure in plain words, for the review window and the Insights card.</summary>
    internal static string Describe(Exception ex) => ex switch
    {
        WinSqliteException { IsUnavailable: true } =>
            "Windows' built-in database reader isn't available on this PC, so Wispr Flow's history can't be read.",
        WinSqliteException { IsBusy: true } =>
            "Wispr Flow is busy saving to its history — try again in a minute.",
        WinSqliteException { IsDamaged: true } =>
            "Wispr Flow's history file looks damaged, so FlowType can't read it.",
        UnknownFormatException => UnknownFormatProblem,
        WinSqliteException { IsCantOpen: true } or UnauthorizedAccessException or IOException =>
            "FlowType couldn't open Wispr Flow's history file.",
        _ => "FlowType couldn't read Wispr Flow's history.",
    };

    /// <summary>The database opened, but has no History table with text in it: Wispr changed its layout.</summary>
    private sealed class UnknownFormatException : Exception
    {
        public UnknownFormatException() : base("no usable History table") { }
    }

    /// <summary>For Debug output: the error's kind and code only — never a message that might quote data.</summary>
    private static string Diagnose(Exception ex) =>
        ex is WinSqliteException sql ? $"WinSqliteException code {sql.Code}" : ex.GetType().Name;
}
