using System.IO;
using System.Text;
using FlowType.Session;

namespace FlowType.Learning;

/// <summary>
/// --selftest vectors for the history readers. Everything runs on synthetic
/// data: scratch databases shaped like Wispr Flow's, built under %TEMP% and
/// deleted afterwards, and in-memory notes. The user's real Wispr database is
/// never opened here — <see cref="WisprHistory.DefaultDatabasePath"/> is not
/// referenced — so the log may quote the synthetic text when a vector fails.
///
/// Each reading rule has a "must keep" row and a "must skip" neighbour, the
/// same habit as the rest of the self-test: a reader that silently drops good
/// rows is as wrong as one that lets dismissed ones through.
/// </summary>
internal static class SourcesSelfTest
{
    public static bool Run(StringBuilder log)
    {
        var ok = true;
        ok &= Guarded(log, "wav", Wav);
        ok &= Guarded(log, "timestamps", TextRules);
        ok &= Guarded(log, "sqlite", Sqlite);
        ok &= Guarded(log, "wispr", Wispr);
        ok &= Guarded(log, "wispr list markup", WisprListMarkup);
        ok &= Guarded(log, "wispr old schema", WisprOldSchema);
        ok &= Guarded(log, "wispr problems", WisprProblems);
        ok &= Guarded(log, "flowtype", FlowTypeRules);
        log.AppendLine($"sources: {(ok ? "PASS" : "FAIL")}");
        return ok;
    }

    // ----- WAV decoding -----

    private static void Wav(Checks c)
    {
        var mono = WavFile(16000, 1, Pcm16(0f, 0.5f, -0.5f, 1f, -1f));
        var decoded = WisprHistory.DecodeWav(mono);
        c.Expect(decoded != null && Near(decoded, 0f, 0.5f, -0.5f, 32767 / 32768f, -1f), "PCM16 mono decodes in -1..1");

        // Stereo frames (0.5, -0.5) and (0.5, 0.25) average to 0 and 0.375.
        var stereo = WavFile(16000, 2, Pcm16(0.5f, -0.5f, 0.5f, 0.25f));
        c.Expect(WisprHistory.DecodeWav(stereo) is { } s && Near(s, 0f, 0.375f), "stereo is averaged to mono");

        // Float32 behind an odd-sized LIST chunk (pad byte): out-of-range clamps, NaN is silence.
        var floats = WavFile(16000, 1, Float32(0.25f, 2f, float.NaN), format: 3, bits: 32, oddChunk: true);
        c.Expect(WisprHistory.DecodeWav(floats) is { } f && Near(f, 0.25f, 1f, 0f), "float32 + extra chunk + padding");

        var extensible = WavFile(16000, 1, Pcm16(0.5f), extensible: true);
        c.Expect(WisprHistory.DecodeWav(extensible) is { } e && Near(e, 0.5f), "WAVE_FORMAT_EXTENSIBLE PCM16");

        // A streaming writer's "unknown" data size reads to the end of the file.
        var streaming = WavFile(16000, 1, Pcm16(0.5f, 0.5f), dataSize: uint.MaxValue);
        c.Expect(WisprHistory.DecodeWav(streaming) is { Length: 2 }, "oversized data chunk reads to end of file");

        c.Expect(WisprHistory.DecodeWav(WavFile(44100, 1, Pcm16(0.5f))) == null, "44.1 kHz is refused");
        c.Expect(WisprHistory.DecodeWav(WavFile(16000, 1, new byte[] { 128, 200 }, bits: 8)) == null, "8-bit PCM is refused");
        c.Expect(WisprHistory.DecodeWav(WavFile(16000, 1, Pcm16(0.5f), dataFirst: true)) == null, "data before fmt is refused");
        c.Expect(WisprHistory.DecodeWav(Encoding.ASCII.GetBytes("RIFF\0\0\0\0WAVE")) == null, "no data chunk → null");
        c.Expect(WisprHistory.DecodeWav(Encoding.ASCII.GetBytes("not a wav file at all")) == null, "garbage → null");
        c.Expect(WisprHistory.DecodeWav(Array.Empty<byte>()) == null, "empty → null");
    }

    // ----- Pure text rules -----

    private static void TextRules(Checks c)
    {
        var expected = new DateTime(2026, 10, 19, 6, 12, 40, 965, DateTimeKind.Utc);
        var parsed = WisprHistory.ParseTimestamp("2026-10-19 06:12:40.965 +00:00");
        c.Expect(parsed == expected && parsed.Kind == DateTimeKind.Utc, "Wispr's format, UTC");
        c.Expect(WisprHistory.ParseTimestamp("2026-10-19 08:12:40.965 +02:00") == expected, "offset converted to UTC");
        c.Expect(WisprHistory.ParseTimestamp("2026-10-19 06:12:40 +00:00") == expected.AddMilliseconds(-965), "no fraction");
        c.Expect(WisprHistory.ParseTimestamp("2026-10-19T06:12:40.965Z") == expected, "ISO 8601");
        c.Expect(WisprHistory.ParseTimestamp("1760854360965") == DateTimeOffset.FromUnixTimeMilliseconds(1760854360965).UtcDateTime,
            "Unix milliseconds");
        c.Expect(WisprHistory.ParseTimestamp("not a date") == DateTime.MinValue, "garbage → MinValue");
        c.Expect(WisprHistory.ParseTimestamp(null) == DateTime.MinValue, "null → MinValue");

        c.Expect(WisprHistory.EditOf("Other.", "Same.") == "Other.", "a real edit is kept");
        c.Expect(WisprHistory.EditOf("Same.", "Same.") == null, "edit equal to final → null");
        c.Expect(WisprHistory.EditOf("  Same.\n", "Same. ") == null, "equal after trimming → null");
        c.Expect(WisprHistory.EditOf("same.", "Same.") == "same.", "a case-only edit is still an edit");
        c.Expect(WisprHistory.EditOf("  ", "x") == null && WisprHistory.EditOf(null, "x") == null, "blank edit → null");

        c.Expect(WisprHistory.StripListMarkup("<ol><li>One</li><li>Two</li></ol>") == "\n\nOne\n\nTwo\n\n"
            && WisprHistory.StripListMarkup("Plan:\n<ul>\n<li>Go</li>\n</ul>") == "Plan:\n\n\n\nGo\n\n\n",
            "all six list tags become line breaks");
        c.Expect(WisprHistory.StripListMarkup("a < b, c > d, <b>bold</b>, <li") == "a < b, c > d, <b>bold</b>, <li"
            && WisprHistory.StripListMarkup("plain") == "plain" && WisprHistory.StripListMarkup(null) == null,
            "other angle brackets, plain text and null are left alone");

        c.Expect(WisprHistory.TryParseStyle("{\"personalizationAppType\":\"other\",\"personalizationStyle\":\"formal\"}",
            out var app, out var style) && app == "other" && style == "formal", "style JSON parsed");
        c.Expect(!WisprHistory.TryParseStyle("{\"personalizationAppType\":\"work\"}", out _, out _), "style missing → skipped");
        c.Expect(!WisprHistory.TryParseStyle("{not json", out _, out _), "malformed JSON → skipped");
        c.Expect(!WisprHistory.TryParseStyle("[1,2]", out _, out _), "non-object JSON → skipped");
    }

    // ----- The SQLite wrapper itself -----

    private static void Sqlite(Checks c)
    {
        var path = ScratchPath();
        try
        {
            using (var db = WinSqlite.OpenReadWrite(path))
            {
                db.Execute("CREATE TABLE T (a TEXT, \"b c\" INTEGER); CREATE TABLE U (x);");
                c.Expect(db.Execute("INSERT INTO T VALUES (?, ?)", "one", 1) == 1, "insert reports one change");
                c.Expect(db.Execute("INSERT INTO T VALUES ('two', 2); INSERT INTO T VALUES ('three', 3)") == 2,
                    "multi-statement execute");
            }

            using var ro = WinSqlite.OpenReadOnly(path);
            c.Expect(ro.IsReadOnly, "opened read-only");
            c.Expect(ro.HasTable("t") && !ro.HasTable("Nope"), "HasTable ignores case, misses absent");
            c.Expect(ro.Columns("T").SequenceEqual(new[] { "a", "b c" }), "Columns in order, odd names quoted");
            c.Expect(ro.Columns("Nope").Count == 0, "Columns of a missing table is empty");

            var refused = false;
            try
            {
                ro.Execute("INSERT INTO T VALUES ('four', 4)");
            }
            catch (WinSqliteException ex) when (ex.IsReadOnly)
            {
                refused = true;
            }
            c.Expect(refused, "a read-only connection refuses writes");

            var blob = new byte[] { 1, 2, 3 };
            var seen = 0;
            foreach (var row in ro.Query("SELECT ? AS t, ? AS b, ? AS i, ? AS d, ? AS n, x'' AS e", "héllo ✓", blob, 42L, 2.5, null))
            {
                seen++;
                c.Expect(row.GetText("t") == "héllo ✓", "UTF-16 text round trip");
                c.Expect(row.GetBlob("b") is { } b && b.SequenceEqual(blob), "blob round trip");
                c.Expect(row.GetInt64("i") == 42 && Math.Abs(row.GetDouble("d") - 2.5) < 1e-9, "numbers round trip");
                c.Expect(row.IsNull("n") && row.GetText("n") == null && row.GetBlob("n") == null, "NULL reads as null");
                c.Expect(row.GetBlob("e") is { Length: 0 }, "empty blob is an empty array");
                c.Expect(row.Ordinal("missing") == -1 && row.IsNull("missing") && row.GetText("missing") == null,
                    "absent column reads as null");
            }
            c.Expect(seen == 1, "one row");

            var count = ro.Query("SELECT count(*) FROM T WHERE \"b c\" >= ?", 2).Select(r => r.GetInt64(0)).Single();
            c.Expect(count == 2, "bound parameter in WHERE");

            var badArgs = false;
            try
            {
                foreach (var _ in ro.Query("SELECT ?", 1, 2)) { }
            }
            catch (ArgumentException)
            {
                badArgs = true;
            }
            c.Expect(badArgs, "wrong parameter count is refused");
        }
        finally
        {
            DeleteScratch(path);
        }
    }

    // ----- Wispr-shaped database, current layout -----

    private static readonly string FormalOther = "{\"personalizationAppType\":\"other\",\"personalizationStyle\":\"formal\"}";

    private static void Wispr(Checks c)
    {
        var path = ScratchPath();
        try
        {
            var tone = Sine(8000, 0.25f);
            BuildCurrentLayout(path, tone);

            // --- Samples ---
            var samples = WisprHistory.ReadSamples(path);
            var byId = samples.ToDictionary(s => s.Id);
            c.Expect(samples.Count == 11, $"11 usable dictations (got {samples.Count})");
            foreach (var skipped in new[] { "dismissed", "notext", "noaudio", "archived" })
            {
                c.Expect(!byId.ContainsKey(skipped), $"'{skipped}' row skipped");
            }
            c.Expect(samples.All(s => s.Source == HistorySource.WisprFlow), "source is Wispr Flow");
            c.Expect(samples.Zip(samples.Skip(1), (a, b) => a.TimestampUtc >= b.TimestampUtc).All(x => x), "newest first");
            c.Expect(samples[^1].Id == "badts" && samples[^1].TimestampUtc == DateTime.MinValue, "unparseable time sorts last");

            if (byId.TryGetValue("plain", out var plain))
            {
                c.Expect(plain.Final == "Hello world. " && plain.Recognized == "hello wrld", "Final = pastedText, Recognized = asrText");
                c.Expect(plain.Edited == null && plain.Best == plain.Final, "no edit → Best is Final");
                c.Expect(plain.App == "claude" && plain.Language == "en" && Math.Abs(plain.DurationSeconds - 2.5) < 1e-9,
                    "app, detectedLanguage, duration");
                c.Expect(plain.TimestampUtc == new DateTime(2026, 10, 19, 6, 12, 40, 965, DateTimeKind.Utc), "timestamp");
                c.Expect(!plain.HasAudio, "no audio");
            }
            else c.Expect(false, "'plain' row present");

            c.Expect(byId.TryGetValue("raw", out var raw) && raw.Final == "just raw text" && raw.Language == "en",
                "raw_transcript row: Final falls back to asrText, Language to language");
            c.Expect(byId.TryGetValue("edited", out var edited) && edited.Edited == "Ship it to Claude."
                && edited.Best == "Ship it to Claude." && edited.TimestampUtc == new DateTime(2026, 10, 19, 7, 0, 0, DateTimeKind.Utc),
                "edited row keeps the edit; +02:00 converted");
            c.Expect(byId.TryGetValue("same", out var same) && same.Edited == null, "edit equal to final after trim → no edit");
            c.Expect(byId.TryGetValue("blankedit", out var blankEdit) && blankEdit.Edited == null, "blank edit → no edit");
            c.Expect(byId.TryGetValue("pastedblank", out var pastedBlank) && pastedBlank.Final == "Fallback.",
                "blank pastedText falls back to formattedText");
            c.Expect(byId.ContainsKey("nullstatus"), "NULL status row kept");
            c.Expect(byId.TryGetValue("emptyaudio", out var emptyAudio) && !emptyAudio.HasAudio, "zero-length audio → no audio");

            // --- Audio ---
            c.Expect(byId.TryGetValue("wav16", out var wav16) && wav16.HasAudio, "16 kHz row has audio");
            var audio = wav16 == null ? null : HistorySources.LoadAudio(wav16, path);
            c.Expect(audio != null && audio.Length == tone.Length && MaxError(audio, tone) < 1.5f / 32768f,
                "16 kHz PCM16 blob decodes to the same samples");
            c.Expect(byId.TryGetValue("wav44", out var wav44) && wav44.HasAudio, "44.1 kHz row has audio");
            c.Expect(wav44 != null && HistorySources.LoadAudio(wav44, path) == null, "44.1 kHz blob → null");
            c.Expect(plain != null && HistorySources.LoadAudio(plain, path) == null, "row without audio → null");
            c.Expect(WisprHistory.ReadAudio("no-such-id", path) == null && WisprHistory.ReadAudio("", path) == null,
                "unknown id → null");

            // --- Summary agrees with what a run reads ---
            var summary = WisprHistory.Summarize(path);
            c.Expect(summary.Available && summary.Problem == null && summary.Source == HistorySource.WisprFlow
                && summary.Location == path, "summary available");
            c.Expect(summary.Dictations == samples.Count, $"summary dictations {summary.Dictations} = samples {samples.Count}");
            c.Expect(summary.Edited == samples.Count(s => s.Edited != null) && summary.Edited == 1,
                $"summary edited {summary.Edited} = 1");
            c.Expect(summary.WithAudio == samples.Count(s => s.HasAudio) && summary.WithAudio == 2,
                $"summary with audio {summary.WithAudio} = 2");
            c.Expect(Math.Abs(summary.AudioSeconds - samples.Where(s => s.HasAudio).Sum(s => s.DurationSeconds)) < 1e-9
                && Math.Abs(summary.AudioSeconds - 0.75) < 1e-9, $"summary audio seconds {summary.AudioSeconds}");

            // --- Dictionary ---
            var dictionary = WisprHistory.ReadDictionary(path);
            c.Expect(dictionary.Count == 4, $"4 live dictionary entries (got {dictionary.Count})");
            c.Expect(dictionary.Any(d => d.Phrase == "Reforger" && d.Replacement == "" && !d.IsSnippet && d.Origin == "manual"),
                "plain word");
            c.Expect(dictionary.Any(d => d.Phrase == "armor forger" && d.Replacement == "Arma Reforger" && d.Origin == "user_edits"),
                "rewrite");
            c.Expect(dictionary.Any(d => d.Phrase == "my sig" && d.IsSnippet), "snippet flag");
            c.Expect(dictionary.Any(d => d.Phrase == "Wispr" && d.Replacement == ""), "NULL replacement and NULL isDeleted kept");
            c.Expect(dictionary.All(d => d.Phrase != "old"), "deleted entry skipped");

            // --- Styles ---
            var styles = WisprHistory.ReadStyleChoices(path);
            c.Expect(styles.Count == 2, $"2 style groups (got {styles.Count})");
            c.Expect(styles.Count > 0 && styles[0] is { AppType: "other", Style: "formal", Count: 2 }
                && styles[0].LastUsedUtc == new DateTime(2026, 10, 19, 7, 0, 0, DateTimeKind.Utc),
                "other/formal ×2 (case variants merged, dismissed row not counted), newest time");
            c.Expect(styles.Any(s => s is { AppType: "personal", Style: "casual", Count: 1 }), "personal/casual ×1");
        }
        finally
        {
            DeleteScratch(path);
        }
    }

    /// <summary>The columns the reader uses, with Wispr's declared types (affinities matter).</summary>
    private const string CurrentLayoutSchema =
        "CREATE TABLE History (transcriptEntityId VARCHAR(36) PRIMARY KEY, asrText TEXT, formattedText TEXT, " +
        "editedText TEXT, timestamp DATETIME, audio BLOB, status VARCHAR(255), app VARCHAR(255), duration FLOAT, " +
        "language TEXT, pastedText TEXT, detectedLanguage TEXT, personalizationStyleSettings JSON, isArchived TINYINT(1));" +
        "CREATE TABLE Dictionary (id VARCHAR(36), phrase VARCHAR(255), replacement VARCHAR(255), isDeleted TINYINT(1), " +
        "source VARCHAR(255), isSnippet TINYINT(1));";

    private static void BuildCurrentLayout(string path, float[] tone)
    {
        using var db = WinSqlite.OpenReadWrite(path);
        db.Execute(CurrentLayoutSchema);

        const string ts = "2026-10-19 06:12:40.965 +00:00";
        const string personal = "{\"personalizationAppType\":\"personal\",\"personalizationStyle\":\"casual\"}";
        var formalOtherCaps = "{\"personalizationAppType\":\"Other\",\"personalizationStyle\":\"Formal\"}";

        //            id             status            asr                formatted            pasted              edited                 timestamp                          audio                         app       dur    lang  detected style            archived
        Row(db, "plain",        "formatted",      "hello wrld",      "Hello world.",      "Hello world. ",    null,                  ts,                                null,                         "claude", 2.5,  null, "en",  FormalOther,      0);
        Row(db, "dismissed",    "dismissed",      "x",               "X.",                "X.",               "Y.",                  ts,                                null,                         "claude", 1.0,  null, "en",  FormalOther,      0);
        Row(db, "raw",          "raw_transcript", "just raw text",   null,                null,               null,                  "2026-10-18 10:00:00.000 +00:00",  null,                         "msedge", 1.5,  "en", null,  null,             0);
        Row(db, "edited",       "formatted",      "ship it to clod", "Ship it to Clod.",  "Ship it to Clod.", "Ship it to Claude.",  "2026-10-19 09:00:00.000 +02:00",  null,                         "claude", 1.0,  null, "en",  formalOtherCaps,  0);
        Row(db, "same",         "formatted",      "same text",       "Same text.",        "Same text.",       "  Same text.\n",      ts,                                null,                         "Discord", 1.0, null, "en",  personal,         0);
        Row(db, "blankedit",    "formatted",      "a",               "A.",                "A.",               "   ",                 ts,                                null,                         "claude", 1.0,  null, "en",  "{not json",      0);
        Row(db, "pastedblank",  "formatted",      "fallback",        "Fallback.",         "",                 null,                  ts,                                null,                         "claude", 1.0,  null, "en",  "{\"personalizationAppType\":\"work\"}", 0);
        Row(db, "notext",       "formatted",      null,              null,                "  ",               null,                  ts,                                null,                         "claude", 1.0,  null, "en",  FormalOther,      0);
        Row(db, "noaudio",      "no_audio",       "hi",              "Hi.",               "Hi.",              null,                  ts,                                null,                         "claude", 1.0,  null, "en",  FormalOther,      0);
        Row(db, "archived",     "formatted",      "arch",            "Arch.",             "Arch.",            "Arched.",             ts,                                null,                         "claude", 1.0,  null, "en",  FormalOther,      1);
        Row(db, "nullstatus",   null,             "null status",     "Null status.",      "Null status.",     null,                  ts,                                null,                         "claude", 1.0,  null, "en",  null,             0);
        Row(db, "wav16",        "formatted",      "audio row",       "Audio row.",        "Audio row.",       null,                  ts,                                WavFile(16000, 1, Pcm16(tone)), "claude", 0.5, null, "en", null,             0);
        Row(db, "wav44",        "formatted",      "loud row",        "Loud row.",         "Loud row.",        null,                  ts,                                WavFile(44100, 1, Pcm16(tone)), "claude", 0.25, null, "en", null,            0);
        Row(db, "emptyaudio",   "formatted",      "e",               "E.",                "E.",               null,                  ts,                                Array.Empty<byte>(),          "claude", 3.0,  null, "en",  null,             0);
        Row(db, "badts",        "formatted",      "bad ts",          "Bad ts.",           "Bad ts.",          null,                  "not a date",                      null,                         "claude", 1.0,  null, "en",  null,             0);

        const string insert = "INSERT INTO Dictionary (id, phrase, replacement, isDeleted, source, isSnippet) VALUES (?, ?, ?, ?, ?, ?)";
        db.Execute(insert, "1", "Reforger", "", 0, "manual", 0);
        db.Execute(insert, "2", "armor forger", "Arma Reforger", 0, "user_edits", 0);
        db.Execute(insert, "3", "my sig", "Best, Alex", 0, "manual", 1);
        db.Execute(insert, "4", "old", "gone", 1, "manual", 0);
        db.Execute(insert, "5", "Wispr", null, null, "default", 0);
        db.Execute(insert, "6", "  ", "x", 0, "manual", 0);
    }

    private static void Row(WinSqlite db, string id, string? status, string? asr, string? formatted, string? pasted,
        string? edited, string timestamp, byte[]? audio, string app, double duration, string? language,
        string? detected, string? style, int archived)
    {
        db.Execute(
            "INSERT INTO History (transcriptEntityId, status, asrText, formattedText, pastedText, editedText, timestamp, " +
            "audio, app, duration, language, detectedLanguage, personalizationStyleSettings, isArchived) " +
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
            id, status, asr, formatted, pasted, edited, timestamp, audio, app, duration, language, detected, style, archived);
    }

    // ----- Wispr's list markup -----

    /// <summary>
    /// When Wispr formats a list, pastedText, formattedText and editedText hold
    /// HTML (<c>&lt;ul&gt;</c>, <c>&lt;ol&gt;</c>, <c>&lt;li&gt;</c> and their
    /// closing tags; nothing else on the user's PC, 2026-10-19). Left in, the
    /// tags glue onto the first and last word of every item, so the miners saw
    /// "li&gt;Kubernetes" as a misheard term and lost real fixes inside items
    /// ("Clod&lt;/li" is address-like, so "Clod → Claude" was rejected). The
    /// reader turns the six tags into line breaks; the counts on the Insights
    /// card must still match what a run reads.
    /// </summary>
    private static void WisprListMarkup(Checks c)
    {
        var path = ScratchPath();
        try
        {
            const string plan = "We need two things:\n<ul>\n<li>Deploy the new app to the servers</li>\n" +
                                "<li>Kubernetes upgrade for the whole cluster</li>\n</ul>";
            const string steps = "Next steps:\n<ol>\n<li>Ship it to Clod</li>\n<li>Tell the team</li>\n</ol>";
            const string stepsFixed = "Next steps:\n<ol>\n<li>Ship it to Claude</li>\n<li>Tell the team</li>\n</ol>";
            const string ts = "2026-10-19 06:12:40.965 +00:00";
            using (var db = WinSqlite.OpenReadWrite(path))
            {
                db.Execute(CurrentLayoutSchema);
                //            id             status       asr                                                                                         formatted                 pasted                     edited
                Row(db, "list",        "formatted", "We need two things. Deploy the new app to the servers. Kubernetes upgrade for the whole cluster.", plan, plan, null,
                    ts, null, "claude", 1.0, null, "en", null, 0);
                Row(db, "listedit",    "formatted", "next steps ship it to clod tell the team",                                    steps,                   steps,                     stepsFixed,
                    ts, null, "claude", 1.0, null, "en", null, 0);
                // Markup with nothing inside is blank: the next column is used, in the count as in the read.
                Row(db, "markuponly",  "formatted", null,                                                                          "Fallback list.",        "<ul>\n<li></li>\n</ul>",  null,
                    ts, null, "claude", 1.0, null, "en", null, 0);
                Row(db, "allmarkup",   "formatted", null,                                                                          "<ul></ul>",             "<ol>\n<li> </li>\n</ol>", null,
                    ts, null, "claude", 1.0, null, "en", null, 0);
                // The same words with and without list markup are not an edit, whichever side has the tags.
                Row(db, "markupedit",  "formatted", "same item",                                                                   "<ul>\n<li>Same item</li>\n</ul>", "<ul>\n<li>Same item</li>\n</ul>", "Same item",
                    ts, null, "claude", 1.0, null, "en", null, 0);
                Row(db, "markupedit2", "formatted", "buy milk and eggs",                                                           "Buy milk and eggs",     "Buy milk and eggs",       "<ul><li>Buy milk and eggs</li></ul>",
                    ts, null, "claude", 1.0, null, "en", null, 0);
                // Angle brackets that are not Wispr's list tags are the user's text.
                Row(db, "angle",       "formatted", "keep a less than b",                                                          "Keep a < b and <b>bold</b> as typed.", "Keep a < b and <b>bold</b> as typed.", null,
                    ts, null, "claude", 1.0, null, "en", null, 0);
            }

            var samples = WisprHistory.ReadSamples(path);
            var byId = samples.ToDictionary(s => s.Id);
            c.Expect(samples.Count == 6 && !byId.ContainsKey("allmarkup"), $"6 dictations, markup-only row skipped (got {samples.Count})");
            c.Expect(samples.Where(s => s.Id != "angle").All(s => !s.Final.Contains('<') && !(s.Edited?.Contains('<') ?? false)),
                "no list tag left in Final or Edited");

            c.Expect(byId.TryGetValue("list", out var list)
                && list.Final == "We need two things:\n\n\n\nDeploy the new app to the servers\n\n\nKubernetes upgrade for the whole cluster\n\n\n"
                && list.Recognized.StartsWith("We need two things. Deploy", StringComparison.Ordinal),
                "list tags become line breaks; asrText untouched");
            c.Expect(byId.TryGetValue("listedit", out var listEdit) && listEdit.Edited != null
                && listEdit.Edited.Contains("\nShip it to Claude\n", StringComparison.Ordinal),
                "an edit inside a list item is kept, without its tags");
            c.Expect(byId.TryGetValue("markuponly", out var markupOnly) && markupOnly.Final == "Fallback list.",
                "pastedText that is only markup counts as blank");
            c.Expect(byId.TryGetValue("markupedit", out var markupEdit) && markupEdit.Edited == null
                && byId.TryGetValue("markupedit2", out var markupEdit2) && markupEdit2.Edited == null,
                "same words with and without list markup → no edit");
            c.Expect(byId.TryGetValue("angle", out var angle) && angle.Final == "Keep a < b and <b>bold</b> as typed.",
                "other angle brackets are left alone");

            // --- What the miners see ---
            var listMined = CorrectionMiner.Mine(list == null ? Array.Empty<HistorySample>() : new[] { list },
                Array.Empty<DictionaryEntry>(), vocabulary: null);
            c.Expect(!listMined.MisheardTerms.Keys.Any(k => k.Contains('<') || k.Contains('>'))
                && !listMined.FromRecognizer.Concat(listMined.FromEdits)
                    .Any(s => (s.Phrase + s.Replacement).IndexOfAny(new[] { '<', '>' }) >= 0),
                "a formatted list yields no tag-glued term or rule");
            var editMined = CorrectionMiner.Mine(listEdit == null ? Array.Empty<HistorySample>() : new[] { listEdit },
                Array.Empty<DictionaryEntry>(), vocabulary: null);
            c.Expect(editMined.FromEdits.Any(s => s.Phrase == "Clod" && s.Replacement == "Claude")
                && editMined.MisheardTerms.ContainsKey("Claude"),
                "a fix typed inside a list item is learned (Clod → Claude)");

            // --- Summary agrees with what a run reads ---
            var summary = WisprHistory.Summarize(path);
            c.Expect(summary.Available && summary.Dictations == samples.Count,
                $"summary dictations {summary.Dictations} = samples {samples.Count}");
            c.Expect(summary.Edited == samples.Count(s => s.Edited != null) && summary.Edited == 1,
                $"summary edited {summary.Edited} = 1");
        }
        finally
        {
            DeleteScratch(path);
        }
    }

    // ----- Wispr-shaped database, an older layout -----

    /// <summary>A History table without editedText, pastedText, status, detectedLanguage, styles, archive flag or audio; no Dictionary table.</summary>
    private static void WisprOldSchema(Checks c)
    {
        var path = ScratchPath();
        try
        {
            using (var db = WinSqlite.OpenReadWrite(path))
            {
                db.Execute("CREATE TABLE History (transcriptEntityId VARCHAR(36), asrText TEXT, formattedText TEXT, " +
                           "timestamp DATETIME, app VARCHAR(255), duration FLOAT, language TEXT)");
                const string insert = "INSERT INTO History VALUES (?, ?, ?, ?, ?, ?, ?)";
                db.Execute(insert, "old1", "hello there", "Hello there.", "2026-05-02 10:00:00.000 +00:00", "notepad", 1.0, "en");
                db.Execute(insert, "old2", "only asr", null, "2026-05-01 10:00:00.000 +00:00", "notepad", 2.0, null);
                db.Execute(insert, "old3", null, null, "2026-05-03 10:00:00.000 +00:00", "notepad", 1.0, "en");
            }

            var samples = WisprHistory.ReadSamples(path);
            c.Expect(samples.Count == 2, $"2 dictations (got {samples.Count})");
            c.Expect(samples.Count == 2 && samples[0].Id == "old1" && samples[0].Final == "Hello there."
                && samples[0].Edited == null && samples[0].Language == "en", "Final = formattedText without pastedText");
            c.Expect(samples.Count == 2 && samples[1].Final == "only asr" && samples[1].Language == "", "Final = asrText last");
            c.Expect(samples.All(s => !s.HasAudio), "no audio column → no audio");

            var summary = WisprHistory.Summarize(path);
            c.Expect(summary.Available && summary.Dictations == 2 && summary.Edited == 0 && summary.WithAudio == 0
                && summary.AudioSeconds == 0, "summary on the old layout");
            c.Expect(WisprHistory.ReadDictionary(path).Count == 0, "no Dictionary table → empty");
            c.Expect(WisprHistory.ReadStyleChoices(path).Count == 0, "no style column → empty");
            c.Expect(WisprHistory.ReadAudio("old1", path) == null, "no audio column → null");
        }
        finally
        {
            DeleteScratch(path);
        }
    }

    // ----- Missing, damaged and unrecognised databases -----

    private static void WisprProblems(Checks c)
    {
        var missing = ScratchPath();
        var missingSummary = WisprHistory.Summarize(missing);
        c.Expect(!missingSummary.Available && !string.IsNullOrEmpty(missingSummary.Problem), "missing file → unavailable");
        c.Expect(WisprHistory.ReadSamples(missing).Count == 0 && WisprHistory.ReadDictionary(missing).Count == 0
            && WisprHistory.ReadStyleChoices(missing).Count == 0 && WisprHistory.ReadAudio("x", missing) == null,
            "missing file → empty reads");
        c.Expect(!File.Exists(missing), "reading a missing file does not create it");

        var garbage = ScratchPath();
        var noHistory = ScratchPath();
        try
        {
            File.WriteAllText(garbage, "This is not a database, just text long enough to have a header. " + new string('x', 200));
            var damaged = WisprHistory.Summarize(garbage);
            c.Expect(!damaged.Available && damaged.Problem != null && damaged.Problem.Contains("damaged"),
                $"not a database → damaged ({damaged.Problem})");
            c.Expect(Throws<IOException>(() => WisprHistory.ReadSamples(garbage)), "not a database → ReadSamples throws IOException");

            using (var db = WinSqlite.OpenReadWrite(noHistory))
            {
                db.Execute("CREATE TABLE Other (x TEXT)");
            }
            var unknown = WisprHistory.Summarize(noHistory);
            c.Expect(!unknown.Available && unknown.Problem != null && unknown.Problem.Contains("format"),
                $"no History table → unrecognised format ({unknown.Problem})");
            c.Expect(Throws<IOException>(() => WisprHistory.ReadSamples(noHistory)), "no History table → ReadSamples throws");
            c.Expect(WisprHistory.ReadDictionary(noHistory).Count == 0, "no Dictionary table → empty dictionary");

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            c.Expect(Throws<OperationCanceledException>(() => WisprHistory.ReadSamples(noHistory, cts.Token)),
                "cancelled read throws OperationCanceledException");
        }
        finally
        {
            DeleteScratch(garbage);
            DeleteScratch(noHistory);
        }
    }

    // ----- FlowType's own History -----

    private static void FlowTypeRules(Checks c)
    {
        var now = DateTime.Now;
        var recent = new Note { Text = "Hello.", RawText = "hello", AppName = "notepad", Language = "en",
            DurationSeconds = 1.2, Timestamp = now.AddHours(-1) };
        var command = new Note { Text = "An answer.", Style = "command", Timestamp = now.AddMinutes(-5) };
        var blank = new Note { Text = "   ", Timestamp = now.AddMinutes(-4) };
        var older = new Note { Text = "Older.", RawText = "", DurationSeconds = 3.5, Timestamp = now.AddDays(-2) };
        var withAudio = new HashSet<Guid> { older.Id };

        // Stored oldest-first on purpose: the reader orders by time, not storage.
        var samples = FlowTypeHistory.SamplesFrom(new[] { older, blank, command, recent }, withAudio.Contains);
        c.Expect(samples.Count == 2, $"command and blank notes skipped (got {samples.Count})");
        c.Expect(samples.Count == 2 && samples[0].Id == recent.Id.ToString("D") && samples[1].Id == older.Id.ToString("D"),
            "newest first, id in \"D\" format");
        if (samples.Count == 2)
        {
            var s = samples[0];
            c.Expect(s.Source == HistorySource.FlowType && s.Recognized == "hello" && s.Final == "Hello."
                && s.Edited == null && s.App == "notepad" && s.Language == "en", "fields mapped");
            c.Expect(s.TimestampUtc == recent.Timestamp.ToUniversalTime() && s.TimestampUtc.Kind == DateTimeKind.Utc, "UTC time");
            c.Expect(!s.HasAudio && samples[1].HasAudio && samples[1].Recognized == "", "audio flag; 1.x note has no raw text");
        }

        var summary = FlowTypeHistory.SummaryOf(samples, exists: true, "notes.json");
        c.Expect(summary.Available && summary.Dictations == 2 && summary.WithAudio == 1
            && Math.Abs(summary.AudioSeconds - 3.5) < 1e-9 && summary.Edited == 0 && summary.Problem == null, "summary");
        var none = FlowTypeHistory.SummaryOf(Array.Empty<HistorySample>(), exists: false, "notes.json");
        c.Expect(!none.Available && none.Problem != null, "no history → unavailable with a reason");

        c.Expect(FlowTypeHistory.ReadAudio("not-a-guid") == null, "bad id → null audio");
        var noAudio = samples.Count > 0 ? samples[0] : null;
        c.Expect(noAudio != null && HistorySources.LoadAudio(noAudio) == null, "sample without audio → null");
    }

    // ----- Helpers -----

    /// <summary>Counts one area's vectors; logs each failure and one summary line.</summary>
    private sealed class Checks
    {
        private readonly StringBuilder _log;
        private readonly string _area;
        private int _passed;
        private int _total;

        public Checks(StringBuilder log, string area)
        {
            _log = log;
            _area = area;
        }

        public void Expect(bool condition, string what)
        {
            _total++;
            if (condition) _passed++;
            else _log.AppendLine($"sources {_area} FAIL: {what}");
        }

        public bool Finish()
        {
            var ok = _passed == _total && _total > 0;
            _log.AppendLine($"sources {_area} {(ok ? "PASS" : "FAIL")}: {_passed}/{_total}");
            return ok;
        }
    }

    private static bool Guarded(StringBuilder log, string area, Action<Checks> body)
    {
        var checks = new Checks(log, area);
        try
        {
            body(checks);
        }
        catch (Exception ex)
        {
            checks.Expect(false, $"threw {ex.GetType().Name}: {ex.Message}");
        }
        return checks.Finish();
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (T)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ScratchPath() =>
        Path.Combine(Path.GetTempPath(), $"FlowType-selftest-{Guid.NewGuid():N}.sqlite");

    private static void DeleteScratch(string path)
    {
        foreach (var file in new[] { path, path + "-journal", path + "-wal", path + "-shm" })
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch (IOException)
            {
                // A leftover scratch file in %TEMP% is harmless; never fail the test for it.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static float[] Sine(int count, float amplitude) =>
        Enumerable.Range(0, count).Select(i => amplitude * MathF.Sin(2 * MathF.PI * 440 * i / 16000f)).ToArray();

    private static float MaxError(float[] a, float[] b) =>
        a.Length != b.Length ? float.MaxValue : a.Zip(b, (x, y) => Math.Abs(x - y)).DefaultIfEmpty(0).Max();

    private static bool Near(float[] actual, params float[] expected) =>
        actual.Length == expected.Length && actual.Zip(expected, (a, e) => Math.Abs(a - e) < 1e-4f).All(x => x);

    private static byte[] Pcm16(params float[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var value = (short)Math.Clamp((int)MathF.Round(samples[i] * 32768f), short.MinValue, short.MaxValue);
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 2), value);
        }
        return bytes;
    }

    private static byte[] Float32(params float[] samples)
    {
        var bytes = new byte[samples.Length * 4];
        for (var i = 0; i < samples.Length; i++) BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), samples[i]);
        return bytes;
    }

    /// <summary>A RIFF/WAVE file with the given payload, optionally with the quirks real writers produce.</summary>
    private static byte[] WavFile(int rate, int channels, byte[] data, ushort format = 1, int bits = 16,
        bool extensible = false, bool oddChunk = false, bool dataFirst = false, uint? dataSize = null)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(0u); // patched below
        w.Write(Encoding.ASCII.GetBytes("WAVE"));

        void Fmt()
        {
            w.Write(Encoding.ASCII.GetBytes("fmt "));
            w.Write(extensible ? 40u : 16u);
            w.Write(extensible ? (ushort)0xFFFE : format);
            w.Write((ushort)channels);
            w.Write(rate);
            w.Write(rate * channels * bits / 8);
            w.Write((ushort)(channels * bits / 8));
            w.Write((ushort)bits);
            if (extensible)
            {
                w.Write((ushort)22);          // cbSize
                w.Write((ushort)bits);        // valid bits
                w.Write(0u);                  // channel mask
                w.Write(format);              // sub-format GUID, first two bytes = format code
                w.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71 });
            }
        }

        void Data()
        {
            w.Write(Encoding.ASCII.GetBytes("data"));
            w.Write(dataSize ?? (uint)data.Length);
            w.Write(data);
        }

        if (dataFirst)
        {
            Data();
            Fmt();
        }
        else
        {
            Fmt();
            if (oddChunk)
            {
                w.Write(Encoding.ASCII.GetBytes("LIST"));
                w.Write(3u);
                w.Write(new byte[] { 1, 2, 3, 0 }); // three bytes + the pad byte
            }
            Data();
        }

        w.Flush();
        var bytes = stream.ToArray();
        BitConverter.TryWriteBytes(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        return bytes;
    }
}
