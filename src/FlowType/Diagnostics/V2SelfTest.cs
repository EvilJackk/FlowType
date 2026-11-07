using System.Text;
using FlowType.Core;
using FlowType.Engine;
using FlowType.Session;

namespace FlowType.Diagnostics;

/// <summary>
/// The --selftest vectors for everything added in 2.0. Kept apart from the
/// 1.x block in App.xaml.cs so each generation's tests can be read on their
/// own. Same rule as before: every "must fire" vector has a "must not fire"
/// neighbour, because a wrong rewrite costs more than a missed one.
/// </summary>
internal static class V2SelfTest
{
    public static bool Run(StringBuilder log)
    {
        var ok = true;
        ok &= Guard(log);
        ok &= Completion(log);
        ok &= Triggers(log);
        ok &= Styles(log);
        ok &= Fit(log);
        ok &= Commands(log);
        ok &= Rendering(log);
        ok &= ContinuationEcho(log);
        // 2.1: learning from history (readers, miners, engine).
        ok &= Learning.LearningSelfTest.Run(log);
        // 2.2: no length limits.
        ok &= NoLimits(log);
        return ok;
    }

    /// <summary>
    /// 2.2: a take has no time limit unless one is switched on, and a long
    /// dictation's polish is split so that no single pass outgrows its budget.
    /// The split must reproduce the text exactly when joined back — the
    /// polish is allowed to change words, the splitter is not.
    /// </summary>
    private static bool NoLimits(StringBuilder log)
    {
        var ok = true;

        // Recording cap.
        var off = new AppSettings { LimitRecordingLength = false, MaxRecordingMinutes = 5 };
        var on = new AppSettings { LimitRecordingLength = true, MaxRecordingMinutes = 5 };
        var huge = new AppSettings { LimitRecordingLength = true, MaxRecordingMinutes = 999 };
        var capOk = Audio.AudioRecorder.CapFor(off) == null
            && Audio.AudioRecorder.CapFor(on) == TimeSpan.FromMinutes(5)
            && Audio.AudioRecorder.CapFor(huge) == TimeSpan.FromMinutes(60)
            && new AppSettings().LimitRecordingLength == false
            && new AppSettings().PolishLongDictations;
        log.AppendLine($"recording cap {(capOk ? "PASS" : "FAIL")}: off={Audio.AudioRecorder.CapFor(off)?.ToString() ?? "none"} " +
            $"on={Audio.AudioRecorder.CapFor(on)} clamp={Audio.AudioRecorder.CapFor(huge)}");
        ok &= capOk;

        // Settings written by 2.1 have no LimitRecordingLength key: they must
        // load as "no limit", whatever MaxRecordingMinutes says.
        var legacy = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            "{\"MaxRecordingMinutes\": 5, \"AiPolish\": \"smart\"}")!;
        var legacyOk = !legacy.LimitRecordingLength && legacy.MaxRecordingMinutes == 5
            && legacy.PolishLongDictations && Audio.AudioRecorder.CapFor(legacy) == null;
        log.AppendLine($"recording cap legacy settings {(legacyOk ? "PASS" : "FAIL")}");
        ok &= legacyOk;

        // Speaking pace counts a long dictation and skips a forgotten mic.
        static Note N(int words, double seconds) => new() { WordCount = words, DurationSeconds = seconds };
        var longTake = UI.Controls.Metrics.RecentWpm(new[] { N(974, 300.3), N(2392, 859) }, 1);   // ~160–195 wpm
        var openMic = UI.Controls.Metrics.RecentWpm(new[] { N(150, 60), N(12, 3600) }, 1);        // the hour is skipped
        var paceOk = longTake is > 150 and < 200 && openMic == 150;
        log.AppendLine($"speaking pace {(paceOk ? "PASS" : "FAIL")}: long takes {longTake} wpm, with an open mic {openMic} wpm");
        ok &= paceOk;

        // Polish splitter.
        var sentence = "We went over the plan for the launch and everyone agreed that the date should move. ";
        var longPara = string.Concat(Enumerable.Repeat(sentence, 40)).TrimEnd();       // 640 words
        var list = "Things to bring:\n1. Tent\n2. Stove\n3. Water";
        var cases = new (string Name, string Text, int Max, int MinParts, int MaxParts)[]
        {
            ("short", "Send it to Mark on Tuesday.", 220, 1, 1),
            ("empty", "", 220, 0, 0),
            ("one long paragraph", longPara, 220, 3, 4),
            ("paragraphs", longPara[..300] + ".\n\n" + longPara + "\n\n" + longPara[..200] + ".", 220, 4, 7),
            ("list kept whole", longPara + "\n\n" + list + "\n\n" + longPara, 220, 6, 9),
            ("crlf + trailing space", longPara.Replace(". ", ".\r\n\r\n") + "  ", 220, 3, 8),
            ("no sentence ends", string.Concat(Enumerable.Repeat("and then we talked some more ", 120)), 220, 1, 1),
            // What a real long take looks like: TextFormatter writes every
            // pause-paragraph (and every list line) as a single "\n".
            ("single-newline paragraphs", string.Join("\n", Enumerable.Repeat(
                string.Concat(Enumerable.Repeat(sentence, 4)).TrimEnd(), 10)), 220, 3, 5),
            ("single-newline paragraphs, no sentence ends", string.Join("\n", Enumerable.Repeat(
                string.Concat(Enumerable.Repeat("and then we talked some more ", 12)).TrimEnd(), 10)), 220, 3, 5),
            ("list after a single newline", longPara + "\n" + list + "\n" + longPara, 220, 6, 9),
            ("leading and trailing newlines", "\n\n" + longPara.Replace(". ", ".\n") + "\n", 220, 3, 8),
        };
        foreach (var (name, text, max, minParts, maxParts) in cases)
        {
            var parts = AiWriter.SplitForPolish(text, max);
            var joined = string.Concat(parts.Select(p => p.Text + p.Separator));
            var roundTrip = joined == text;
            var sizesOk = parts.All(p => TextFormatter.CountWords(p.Text) <= max
                || !System.Text.RegularExpressions.Regex.IsMatch(p.Text.TrimEnd(), @"[.!?…]\s+\S"));
            var listWhole = !text.Contains(list) || parts.Any(p => p.Text.Contains(list));
            var countOk = parts.Count >= minParts && parts.Count <= maxParts;
            var good = roundTrip && sizesOk && listWhole && countOk;
            ok &= good;
            log.AppendLine($"polish split {(good ? "PASS" : "FAIL")}: {name} -> {parts.Count} parts " +
                $"[{string.Join(", ", parts.Select(p => TextFormatter.CountWords(p.Text)))} words]" +
                (roundTrip ? "" : " ROUND-TRIP MISMATCH") + (sizesOk ? "" : " OVERSIZE") +
                (listWhole ? "" : " LIST SPLIT"));
        }

        // The post-decode backstop grows with the text and always covers a
        // full polish of it (parts × the 8 s per-part budget).
        var budgetSmall = AiWriter.PostDecodeBudget(10);
        var budgetBig = AiWriter.PostDecodeBudget(5000);
        var postOk = budgetSmall >= TimeSpan.FromMinutes(2)
            && budgetBig >= AiWriter.PolishBudget * (5000 / AiWriter.PolishPartWords + 1)
            && budgetBig > budgetSmall;
        log.AppendLine($"post-decode budget {(postOk ? "PASS" : "FAIL")}: 10 words {budgetSmall.TotalSeconds:0} s, 5000 words {budgetBig.TotalSeconds:0} s");
        ok &= postOk;

        // With no model loaded the long path hands the text back untouched.
        var untouched = AiWriter.PolishAnyLengthAsync(longPara, new WritingContext(), CancellationToken.None)
            .GetAwaiter().GetResult();
        var fallbackOk = !LocalLlm.Instance.IsReady
            ? untouched.Text == longPara && !untouched.Applied
            : untouched.Text.Length > 0;
        log.AppendLine($"polish long, no model {(fallbackOk ? "PASS" : "FAIL")}: applied={untouched.Applied} ({untouched.Reason})");
        ok &= fallbackOk;
        return ok;
    }

    /// <summary>The AI guard: accept cleanups, reject replies and inventions.</summary>
    private static bool Guard(StringBuilder log)
    {
        var cases = new (string In, string Out, bool Accept)[]
        {
            ("So um basically I was thinking we should uh move the meeting to Thursday, no wait, Friday at like 3 PM.",
             "So basically I was thinking we should move the meeting to Friday at 3 PM.", true),
            ("We need three things first the API keys second the test plan and third the rollout date",
             "We need three things:\n1. The API keys\n2. The test plan\n3. The rollout date", true),
            ("What is the capital of France", "What is the capital of France?", true),
            ("What is the capital of France", "The capital of France is Paris.", false),
            ("Write a function that returns the sum of two numbers",
             "Sure! Here's a function: def add(a, b): return a + b", false),
            ("Thanks for the update I'll review it tonight",
             "Thanks for the update. I'll review it tonight and send detailed feedback to the whole team by Friday morning.", false),
            ("lol yeah that was so funny", "lol yeah that was so funny", true),
            ("Can you send me the report", "", false),
            ("Send it to Mark on Monday no wait Tuesday", "Send it to Mark on Tuesday.", true),
            // Opening with do/have/will/can does not make a sentence a question:
            // an instruction or statement may come back without "?"…
            ("Do the dishes before you leave no wait after", "Do the dishes after you leave.", true),
            ("Have Mark send it Monday, no wait, Sarah", "Have Sarah send it Monday.", true),
            ("Will do, I'll send it after lunch um today", "Will do, I'll send it after lunch today.", true),
            ("Can you send me the the report", "Can you send me the report.", true),
            ("Do the dishes, no wait, take out the trash", "Take out the trash.", true),
            // …but an answer still fails: new content (a number counts), or the
            // question turned into a statement, or a "?" that was dropped.
            ("What time is the deploy going out today", "The deploy is going out today at 3 PM.", false),
            ("Do you know the time", "I do.", false),
            ("Is the build green?", "The build is green.", false),
        };
        var pass = 0;
        foreach (var (input, output, accept) in cases)
        {
            var got = AiWriter.LooksLikeACleanup(input, output, out var why);
            var good = got == accept;
            if (good) pass++;
            log.AppendLine($"ai guard {(good ? "PASS" : "FAIL")}: accept={got} ({why}) '{Short(output)}'");
        }
        log.AppendLine($"ai guard: {pass}/{cases.Length} passed");

        var unwrapOk = AiWriter.Unwrap("\"Hello there.\"", "Hello there") == "Hello there."
            && AiWriter.Unwrap("\"Quoted\"", "\"Quoted\"") == "\"Quoted\""
            && LocalLlm.Clean("<think>\nhmm\n</think>\n\nDone.<|im_end|>") == "Done."
            && LocalLlm.Clean("Partial <think> never closed") == "Partial";
        log.AppendLine($"ai unwrap {(unwrapOk ? "PASS" : "FAIL")}");

        // Command Mode keeps quotes the instruction asked for, quotes that
        // wrapped the selection, and quotes inside the answer.
        var commandUnwrapOk =
            AiWriter.UnwrapCommand("\"Hello there.\"", "make this friendlier", "hi there") == "Hello there."
            && AiWriter.UnwrapCommand("\"Hello there.\"", "put this in quotes", "Hello there.") == "\"Hello there.\""
            && AiWriter.UnwrapCommand("“Ship it.”", "wrap it in quotation marks", "Ship it.") == "“Ship it.”"
            && AiWriter.UnwrapCommand("\"Quoted\"", "fix the typo", "\"Quotd\"") == "\"Quoted\""
            && AiWriter.UnwrapCommand("\"a\" and \"b\"", "make it shorter", "a and also b") == "\"a\" and \"b\"";
        log.AppendLine($"ai command unwrap {(commandUnwrapOk ? "PASS" : "FAIL")}");

        var rendered = LocalLlm.Render(new[] { new ChatTurn("system", "S"), new ChatTurn("user", "U") }, qwen3: true);
        var renderOk = rendered.Contains("U /no_think<|im_end|>")
            && rendered.EndsWith("<|im_start|>assistant\n<think>\n\n</think>\n\n");
        log.AppendLine($"ai template {(renderOk ? "PASS" : "FAIL")}");
        return pass == cases.Length && unwrapOk && commandUnwrapOk && renderOk;
    }

    /// <summary>
    /// How a generation ended. LLamaSharp's executor stops quietly on a
    /// cancelled token and at the token cap; only a stop string or an
    /// end-of-generation token (under the cap) is a finished answer.
    /// </summary>
    private static bool Completion(StringBuilder log)
    {
        var cases = new (string Raw, int Produced, int Max, bool Cancelled, LocalLlm.CompletionEnd Expected)[]
        {
            ("Done.", 3, 64, false, LocalLlm.CompletionEnd.Finished),             // end-of-generation token
            ("Done.<|im_end|>", 64, 64, false, LocalLlm.CompletionEnd.Finished),  // stop string on the last token
            ("Done.<|im_end|>", 4, 64, true, LocalLlm.CompletionEnd.Finished),    // finished, then the budget fired
            ("The first half of a rewr", 64, 64, false, LocalLlm.CompletionEnd.Truncated),
            ("The first half", 3, 64, true, LocalLlm.CompletionEnd.Cancelled),
            ("The first half of a rewr", 64, 64, true, LocalLlm.CompletionEnd.Cancelled),
        };
        var pass = 0;
        foreach (var (raw, produced, max, cancelled, expected) in cases)
        {
            var got = LocalLlm.ClassifyEnd(raw, produced, max, cancelled);
            if (got == expected) pass++;
            log.AppendLine($"llm end {(got == expected ? "PASS" : "FAIL")}: {produced}/{max} tokens, cancelled={cancelled} -> {got}"
                + (got == expected ? "" : $" (expected {expected})"));
        }

        // A cut-off Command Mode answer carries no text to insert.
        var cut = new CommandResult(CommandStatus.TimedOut, null, 45000);
        var resultOk = !cut.Succeeded && cut.Failure != null && cut.Note == "command: timed out"
            && new CommandResult(CommandStatus.Ok, "x", 1).Succeeded
            && !new CommandResult(CommandStatus.TooLong, null, 1).ToString().Contains("rewr");
        log.AppendLine($"command result {(resultOk ? "PASS" : "FAIL")}");
        log.AppendLine($"llm end: {pass}/{cases.Length} passed");
        return pass == cases.Length && resultOk;
    }

    /// <summary>Smart polish only spends time when the transcript needs it.</summary>
    private static bool Triggers(StringBuilder log)
    {
        var cases = new (string Text, bool Expect)[]
        {
            ("Let's meet Thursday, no wait, Friday.", true),
            ("I think the the issue is here.", true),
            ("First the keys, second the plan, third the date.", true),
            ("Sounds good, see you then.", false),
            ("Thanks!", false),
            ("Had had a long day.", true),   // a real repeated word still triggers; polish's guard keeps it safe
        };
        var pass = 0;
        foreach (var (text, expect) in cases)
        {
            var got = AiWriter.NeedsPolish(text, out var reason);
            if (got == expect) pass++;
            log.AppendLine($"ai trigger {(got == expect ? "PASS" : "FAIL")}: {got} ({reason}) '{text}'");
        }
        log.AppendLine($"ai triggers: {pass}/{cases.Length} passed");
        return pass == cases.Length;
    }

    private static bool Styles(StringBuilder log)
    {
        var protectedWords = new[] { "Sam", "Arma Reforger" };
        var cases = new (string In, string Style, string Expected)[]
        {
            ("Are you free for lunch tomorrow? Let's do 12.", WritingStyles.Polished, "Are you free for lunch tomorrow? Let's do 12."),
            ("Are you free for lunch tomorrow? Let's do 12.", WritingStyles.Casual, "Are you free for lunch tomorrow? Let's do 12"),
            ("Hey Sam, are you free? Let's play Arma Reforger.", WritingStyles.VeryCasual, "hey Sam, are you free? let's play Arma Reforger"),
            ("Sounds good. I will be there.", WritingStyles.VeryCasual, "sounds good. I will be there"),
            ("That works for me.", WritingStyles.Excited, "That works for me!"),
            ("Bring the cables, adapters, etc.", WritingStyles.Casual, "Bring the cables, adapters, etc."),
            ("Wait for it...", WritingStyles.Casual, "Wait for it..."),
            ("Line one.\nLine two.", WritingStyles.VeryCasual, "Line one.\nLine two."),
            // Very casual looks at the whole first word: acronyms, CamelCase,
            // hyphenated caps, "I" contractions and protected possessives keep
            // their capital; ordinary words still drop it.
            ("NASA launched it. GitHub is down.", WritingStyles.VeryCasual, "NASA launched it. GitHub is down"),
            ("I'm on my way. I’ll call you.", WritingStyles.VeryCasual, "I'm on my way. I’ll call you"),
            ("Wi-Fi is down. Okay then.", WritingStyles.VeryCasual, "Wi-Fi is down. okay then"),
            ("Sam's here. Arma Reforger is up.", WritingStyles.VeryCasual, "Sam's here. Arma Reforger is up"),
            // Dotted initialisms keep their last dot in every style.
            ("We met in the U.S.", WritingStyles.Casual, "We met in the U.S."),
            ("Meet me at 5 p.m.", WritingStyles.VeryCasual, "meet me at 5 p.m."),
            ("We met in the U.S.", WritingStyles.Excited, "We met in the U.S."),
            ("Bring the cables, adapters, etc.", WritingStyles.Excited, "Bring the cables, adapters, etc."),
            ("It ships Friday.", WritingStyles.Excited, "It ships Friday!"),
        };
        var pass = 0;
        foreach (var (input, style, expected) in cases)
        {
            var actual = WritingStyles.Apply(input, style, protectedWords);
            var good = actual == expected;
            if (good) pass++;
            log.AppendLine($"style {(good ? "PASS" : "FAIL")} [{style}]: '{actual}'" + (good ? "" : $" (expected '{expected}')"));
        }
        log.AppendLine($"styles: {pass}/{cases.Length} passed");
        return pass == cases.Length;
    }

    /// <summary>Fitting a dictation to the caret it lands at.</summary>
    private static bool Fit(StringBuilder log)
    {
        Func<string, bool> names = w => w is "Sarah" or "FlowType";
        var cases = new List<(string Text, CaretContext Ctx, string Expected, bool TrailingSpace)>();
        foreach (var (text, ctx, expected) in new (string, CaretContext, string)[]
        {
            // Mid-sentence continuation: lowercase, leading space.
            ("Because it was raining.", new CaretContext("I stayed home", "", "", true), " because it was raining. "),
            // Names keep their capital.
            ("Sarah said yes.", new CaretContext("and then", "", "", true), " Sarah said yes. "),
            // "I" stays "I".
            ("I agree.", new CaretContext("Honestly,", "", "", true), " I agree. "),
            // Start of a new sentence: keep the capital, add the space.
            ("Next item.", new CaretContext("Done.", "", "", true), " Next item. "),
            // The sentence carries on after the caret: no full stop of our own, space before the next word.
            ("The blue one.", new CaretContext("Pick ", "and send it", "", true), "the blue one "),
            // Empty field: as dictated, one trailing space after a finished sentence.
            ("Hello there.", new CaretContext("", "", "", true), "Hello there. "),
            // Selection replaced like-for-like: lowercase, no full stop, keep surrounding spaces.
            ("Quick fox.", new CaretContext("the ", " jumps", " lazy dog ", true), " quick fox "),
            // Unknown context: untouched (the caller falls back to the old heuristic).
            ("Hello.", CaretContext.Unknown, "Hello."),
            // Multi-line blocks are inserted as written.
            ("One.\nTwo.", new CaretContext("x", "", "", true), "One.\nTwo."),
            // Acronyms and mixed-case brands keep their case mid-sentence.
            ("NASA launched it.", new CaretContext("I read that", "", "", true), " NASA launched it. "),
            ("FlowType does it.", new CaretContext("and", "", "", true), " FlowType does it. "),
            // Straight quotes: after an opening one, no space and the spoken
            // casing; after a closing one, a space, and lowercase unless the
            // quotation ended a sentence.
            ("Hello there.", new CaretContext("He said \"", "", "", true), "Hello there. "),
            ("Then he left.", new CaretContext("He said \"hi\"", "", "", true), " then he left. "),
            ("Then he left.", new CaretContext("He said \"Stop.\"", "", "", true), " Then he left. "),
            ("hello", new CaretContext("He said \"", "\"", "", true), "hello"),
            // Emoji: a space after one, no lowering through it, a space before one.
            ("See you tomorrow.", new CaretContext("Great job 🎉", "", "", true), " See you tomorrow. "),
            ("See you tomorrow.", new CaretContext("Love it ❤️", "", "", true), " See you tomorrow. "),
            ("Great job", new CaretContext("", "🎉", "", true), "Great job "),
            // Nothing glued to an opening bracket or quote that follows.
            ("See the notes", new CaretContext("", "(below)", "", true), "See the notes "),
            ("He said", new CaretContext("", "\"ok\"", "", true), "He said "),
            // CJK text still takes no spaces.
            ("你好。", new CaretContext("我说", "", "", true), "你好。"),
        })
        {
            cases.Add((text, ctx, expected, true));
        }
        // The "add a space after each dictation" setting off: no space left at
        // the end of the field, but the one before a following word stays.
        cases.Add(("Hello there.", new CaretContext("", "", "", true), "Hello there.", false));
        cases.Add(("Next item.", new CaretContext("Done.", "", "", true), " Next item.", false));
        cases.Add(("The blue one.", new CaretContext("Pick ", "and send it", "", true), "the blue one ", false));

        var pass = 0;
        foreach (var (text, ctx, expected, trailingSpace) in cases)
        {
            var actual = ContextAwareInsertion.Fit(text, ctx, names, trailingSpace).Text;
            var good = actual == expected;
            if (good) pass++;
            log.AppendLine($"fit {(good ? "PASS" : "FAIL")}: '{text}' + [{Short(ctx.Before)}|{Short(ctx.After)}]"
                + (trailingSpace ? "" : " (no trailing space)") + $" -> '{actual}'"
                + (good ? "" : $" (expected '{expected}')"));
        }
        log.AppendLine($"context fit: {pass}/{cases.Count} passed");
        return pass == cases.Count;
    }

    /// <summary>Voice commands and snippets.</summary>
    private static bool Commands(StringBuilder log)
    {
        var enter = new (string In, bool Match, string Remainder)[]
        {
            ("Sounds good, see you there. Press enter.", true, "Sounds good, see you there."),
            ("Ship it press enter", true, "Ship it"),
            ("Press enter to continue the installer.", false, ""),
            ("I told him to press enter twice.", false, ""),
        };
        var pass = 0;
        foreach (var (input, match, remainder) in enter)
        {
            var got = DictationSession.PressEnterTail.IsMatch(input);
            var after = got ? DictationSession.PressEnterTail.Replace(input, "").TrimEnd() : "";
            var good = got == match && (!match || after == remainder);
            if (good) pass++;
            log.AppendLine($"press-enter {(good ? "PASS" : "FAIL")}: '{input}' -> {got} '{after}'");
        }

        var snippetOk = SnippetKeyEquals("My calendar link.", "my calendar link")
            && SnippetKeyEquals("  my CALENDAR, link!  ", "My calendar link")
            && !SnippetKeyEquals("my calendar links please", "my calendar link");
        log.AppendLine($"snippet key {(snippetOk ? "PASS" : "FAIL")}");
        log.AppendLine($"commands: {pass}/{enter.Length} passed");
        return pass == enter.Length && snippetOk;
    }

    private static bool SnippetKeyEquals(string a, string b)
    {
        static string Key(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return Key(a) == Key(b);
    }

    /// <summary>The polish prompt carries style, app and vocabulary, and stays bounded.</summary>
    private static bool Rendering(StringBuilder log)
    {
        var ctx = new WritingContext("Discord", AppKind.PersonalMessages, WritingStyles.Casual, "en",
            Enumerable.Range(0, 100).Select(i => $"Term{i}").ToList(), new string('x', 2000));
        var turns = AiWriter.BuildPolishConversation("hello there", ctx);
        var system = turns[0].Content;
        var good = system.Contains("casual")
            && system.Contains("Discord")
            && system.Contains("Term0") && !system.Contains("Term45")
            && system.Length < 4000
            && turns[^1].Content == "hello there";
        log.AppendLine($"polish prompt {(good ? "PASS" : "FAIL")}: system={system.Length} chars, turns={turns.Count}");
        return good;
    }

    /// <summary>
    /// A live chunk's real previous text is not prompt text: only a leading
    /// verbatim repeat of its end is an echo. Sentences that merely reuse
    /// four or more of its words — which the four-word prompt-echo rule used
    /// to delete — are speech.
    /// </summary>
    private static bool ContinuationEcho(StringBuilder log)
    {
        const string previous = "We moved the billing service over on Tuesday, and so far the error rate has stayed flat in every region.";
        var cases = new (string Text, string Expected)[]
        {
            ("So far the error rate has stayed flat in every region. Next we test the workers.", "Next we test the workers."),
            ("The error rate has stayed flat since Monday. Next we test the workers.",
             "The error rate has stayed flat since Monday. Next we test the workers."),
            ("Next we test the workers. So far the error rate has stayed flat in every region.",
             "Next we test the workers. So far the error rate has stayed flat in every region."),
            ("In every region. Then the workers.", "In every region. Then the workers."),   // under five words
            ("So far the error rate has stayed flat in every region.", ""),
        };
        var pass = 0;
        foreach (var (text, expected) in cases)
        {
            var context = Transcriber.PreviousContext(previous, Transcriber.MaxPreviousTextChars);
            var actual = Transcriber.StripContinuationEcho(text, context);
            var good = actual == expected;
            if (good) pass++;
            log.AppendLine($"continuation echo {(good ? "PASS" : "FAIL")}: '{Short(text)}' -> '{Short(actual)}'"
                + (good ? "" : $" (expected '{Short(expected)}')"));
        }

        // The variant sentence alone (what the four-word rule now sees on a
        // live chunk) leaves that reused wording alone.
        var variant = EnglishVariant.RecognitionPrompt(EnglishVariant.Us) ?? "";
        const string reuse = "The error rate has stayed flat since Monday. Next we test the workers.";
        var variantOnlyOk = HallucinationFilter.Apply(reuse, variant) == reuse;
        log.AppendLine($"echo guard on variant only {(variantOnlyOk ? "PASS" : "FAIL")}");
        log.AppendLine($"continuation echo: {pass}/{cases.Length} passed");
        return pass == cases.Length && variantOnlyOk;
    }

    private static string Short(string s) =>
        s.Replace("\n", "⏎").Length <= 48 ? s.Replace("\n", "⏎") : s.Replace("\n", "⏎")[..45] + "...";

    /// <summary>
    /// End-to-end check of the on-device model, when one is installed: load,
    /// polish three realistic transcripts, and confirm the guard accepted
    /// them. Reports timing. Skipped (not failed) without a model.
    /// </summary>
    public static async Task<bool> LlmSmokeAsync(StringBuilder log)
    {
        var model = LlmCatalog.ById(SettingsStore.Instance.Settings.AiModel) ?? LlmCatalog.BestDownloaded();
        if (model == null)
        {
            log.AppendLine("llm: SKIPPED (no on-device model installed)");
            return true;
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await LocalLlm.Instance.LoadAsync(model.Id);
        log.AppendLine($"llm: loaded {model.Id} in {watch.ElapsedMilliseconds} ms on {LocalLlm.Instance.RuntimeLabel}");
        foreach (var line in LocalLlm.Instance.NativeLog.Where(l => l.Contains("offloaded") || l.Contains("Vulkan0")).Take(4))
        {
            log.AppendLine($"  native: {line}");
        }

        var inputs = new[]
        {
            ("So um I was thinking we should move the meeting to Thursday, no wait, Friday at 3 PM.", "Friday"),
            ("Can you check whether the build passed? I mean the release build.", "release build"),
            ("What time is the deploy going out today", "deploy"),
        };
        var pass = 0;
        foreach (var (input, mustContain) in inputs)
        {
            var result = await AiWriter.PolishAsync(input, new WritingContext(), CancellationToken.None);
            var good = result.Applied && result.Text.Contains(mustContain, StringComparison.OrdinalIgnoreCase)
                       && !(input.Contains("Thursday") && result.Text.Contains("Thursday"));
            if (input.StartsWith("What time")) good = result.Text.Contains('?') || !result.Applied;
            if (good) pass++;
            log.AppendLine($"llm polish {(good ? "PASS" : "FAIL")} [{result.Milliseconds} ms, {result.Reason}]: '{result.Text}'");
        }
        log.AppendLine($"llm: {pass}/{inputs.Length} passed");
        return pass == inputs.Length;
    }
}
