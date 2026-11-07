using System.IO;
using System.Text;
using FlowType.Core;
using FlowType.Engine;
using FlowType.Session;

namespace FlowType.Learning;

/// <summary>
/// The --selftest vectors for the engine half of "Learn from your writing":
/// benchmark scoring, merging what several miners proposed and keeping one
/// rule per spoken phrase, the hashed dismissal list and its file round trip
/// (including entries the user removed), hand edits taking an entry out of the
/// undo, the order and de-duplication of learned dictionary entries, the
/// prompt budget and the report-wide trim to it, the daily run's "anything
/// new?" test, the benchmark giving way to a dictation, and — when a speech
/// model is loaded — benchmark runs over synthetic audio: plain, interrupted
/// by a dictation, and cancelled.
///
/// Synthetic data only. Never touches the user's dictionary, settings,
/// learning.json or any history; the state round trip uses a temp file that
/// is deleted again. Logged lines name the check and give counts, never the
/// strings involved, so the log has the same shape it would have with real
/// data.
/// </summary>
internal static class EngineSelfTest
{
    public static bool Run(StringBuilder log)
    {
        var pass = 0;
        var total = 0;
        void Check(string name, bool ok, string detail = "")
        {
            total++;
            if (ok) pass++;
            log.AppendLine($"learn engine {(ok ? "PASS" : "FAIL")}: {name}" + (detail.Length > 0 ? $" [{detail}]" : ""));
        }

        Scoring(Check);
        Merging(Check);
        State(Check);
        DictionaryPlan(Check);
        Budget(Check);
        Proposals(Check);
        Background(Check);
        GiveWay(Check);
        Orchestration(Check);
        Pipeline(Check);
        BenchSmoke(Check, log);

        log.AppendLine($"learn engine: {pass}/{total} passed");
        return pass == total;
    }

    private delegate void Checker(string name, bool ok, string detail = "");

    private static void Scoring(Checker check)
    {
        var terms = new[] { "Arma Reforger" };
        var miss = PersonalBench.Score("I play Arma Reforger daily.", "i play armor forger daily", terms);
        check("score: misheard term", miss == (2, 5, 1, 0), $"{miss}");

        var hit = PersonalBench.Score("I play Arma Reforger daily.", "I play arma reforger, daily!", terms);
        check("score: case and punctuation ignored", hit == (0, 5, 1, 1), $"{hit}");

        var repeated = PersonalBench.Score("GitHub and GitHub", "GitHub and git hub", new[] { "GitHub", "github" });
        check("score: each occurrence counted once, duplicate terms once", repeated.TermOcc == 2 && repeated.TermHits == 1,
            $"{repeated.TermOcc}/{repeated.TermHits}");

        var extra = PersonalBench.Score("Claude", "Claude Claude", new[] { "Claude" });
        check("score: no credit beyond the reference", extra.TermOcc == 1 && extra.TermHits == 1, $"{extra.TermOcc}/{extra.TermHits}");

        var inside = PersonalBench.Score("Armageddon is coming", "Armageddon is coming", new[] { "Arma", "C#", "" });
        check("score: no match inside a word, junk terms ignored", inside == (0, 3, 0, 0), $"{inside}");

        var absent = PersonalBench.Score("hello there", "hello there Reforger", new[] { "Reforger" });
        check("score: term only in the hypothesis", absent.TermOcc == 0 && absent.TermHits == 0 && absent.Errors == 1,
            $"{absent}");

        // Proposed words are counted in the final text, word agreement stays
        // against the raw transcript: where Wispr misheard the word, the raw
        // transcript doesn't have it, and that is the case learning is for.
        const string raw = "i play armor forger daily";
        const string final = "I play Arma Reforger daily.";
        var learnedRight = PersonalBench.Score(raw, "I play Arma Reforger daily", terms, final);
        check("score: term counted in the final text, agreement against the raw transcript",
            learnedRight == (2, 5, 1, 1), $"{learnedRight}");
        var stillWrong = PersonalBench.Score(raw, "i play armor forger daily", terms, final);
        check("score: a term still misheard is a miss", stillWrong == (0, 5, 1, 0), $"{stillWrong}");
        var rawOnly = PersonalBench.Score(raw, "I play Arma Reforger daily", terms);
        check("score: without a final text the raw transcript is the term reference",
            rawOnly.TermOcc == 0 && rawOnly.TermHits == 0, $"{rawOnly}");

        check("accuracy: 1 - WER, clamped",
            PersonalBench.Accuracy(2, 5) == 0.6 && PersonalBench.Accuracy(9, 5) == 0 && PersonalBench.Accuracy(0, 0) == 0
            && PersonalBench.Accuracy(0, 7) == 1);
    }

    private static Suggestion Make(SuggestionKind kind, string phrase, string replacement, double score, bool recommended,
        int sightings, string why, string origin = "your writing") =>
        new(kind, Suggestion.KeyFor(kind, phrase, replacement), phrase, replacement, sightings, score, recommended, why, origin);

    private static void Merging(Checker check)
    {
        var fromWriting = Make(SuggestionKind.Vocabulary, "Reforger", "", 2.0, false, 6, "Used in 6 dictations");
        var fromWispr = Make(SuggestionKind.Vocabulary, "reforger", "", 1.0, true, 1, "In your Wispr Flow dictionary",
            "Wispr Flow dictionary");
        var snippet = Make(SuggestionKind.Snippet, "reforger", "Arma Reforger", 1.0, true, 1, "In your Wispr Flow dictionary");
        var top = Make(SuggestionKind.Vocabulary, "Claude", "", 3.0, true, 9, "Used in 9 dictations");
        var fix = Make(SuggestionKind.Correction, "armor forger", "Arma Reforger", 1.5, false, 2, "You fixed it 2 times");
        var benchFix = Make(SuggestionKind.Correction, "Armor Forger", "arma reforger", 1.2, true, 2, "FlowType misheard it 2 times",
            "your recordings");
        var blankKey = new Suggestion(SuggestionKind.Vocabulary, "", "Enfusion", "", 2, 0.5, false, "Used in 2 dictations", "your writing");

        var merged = HistoryLearner.Merge(new[] { fromWriting, fix, snippet, fromWispr, top, benchFix, blankKey });
        check("merge: one row per key", merged.Count == 5, $"{merged.Count} rows");
        check("merge: kinds in order, best first",
            merged.Select(m => m.Kind).SequenceEqual(new[]
            {
                SuggestionKind.Vocabulary, SuggestionKind.Vocabulary, SuggestionKind.Vocabulary,
                SuggestionKind.Correction, SuggestionKind.Snippet,
            })
            && merged[0].Phrase == "Claude" && merged[1].Phrase == "Reforger" && merged[2].Phrase == "Enfusion");

        var word = merged.FirstOrDefault(m => m.Key == "v|reforger");
        check("merge: higher score kept, recommended if either, sightings not summed",
            word != null && word.Score == 2.0 && word.Recommended && word.Sightings == 6 && word.Phrase == "Reforger"
            && word.Origin == "your writing");
        check("merge: reasons joined once", word?.Why == "Used in 6 dictations · In your Wispr Flow dictionary");

        var rule = merged.FirstOrDefault(m => m.Kind == SuggestionKind.Correction);
        check("merge: same fix from two miners", rule != null && rule.Recommended && rule.Score == 1.5
            && rule.Why == "You fixed it 2 times · FlowType misheard it 2 times");
        check("merge: blank key rebuilt", merged.Any(m => m.Key == "v|enfusion"));
        check("merge: reason de-duplication", HistoryLearner.JoinWhy("A · B", "b · C") == "A · B · C"
            && HistoryLearner.JoinWhy("", "") == "");
        check("pending: recommended rows", HistoryLearner.CountPending(merged) == 4, $"{HistoryLearner.CountPending(merged)}");

        var items = HistoryLearner.DictionaryItems(new[]
        {
            fix, fromWriting, snippet, top, Make(SuggestionKind.Style, "personal", "casual", 1, true, 10, "x"),
        });
        check("apply order: words, then fixes, then snippets; styles not in the dictionary",
            items.Count == 4
            && items[0] == ("Reforger", "", false) && items[1] == ("Claude", "", false)
            && items[2] == ("armor forger", "Arma Reforger", false) && items[3] == ("reforger", "Arma Reforger", true));
        var wisprWord = Make(SuggestionKind.Vocabulary, "Enfusion", "", 1.0, true, 1, "In your Wispr Flow dictionary",
            WisprDictionaryImport.Origin);
        var withWispr = HistoryLearner.DictionaryItems(new[] { top, fromWriting, wisprWord });
        check("apply order: the user's Wispr dictionary words land before mined words",
            withWispr.Select(i => i.Phrase).SequenceEqual(new[] { "Enfusion", "Claude", "Reforger" }));

        OneRulePerPhrase(check);
    }

    private static void OneRulePerPhrase(Checker check)
    {
        var rows = new[]
        {
            Make(SuggestionKind.Vocabulary, "x y", "", 2.0, true, 3, "w"),
            Make(SuggestionKind.Correction, "x y", "Zed", 1.5, true, 3, "a", CorrectionMiner.RecognizerOrigin),
            Make(SuggestionKind.Correction, " X Y ", "Why", 1.0, true, 1, "b", WisprDictionaryImport.Origin),
            Make(SuggestionKind.Correction, "abc", "ABC", 1.0, true, 4, "c", CorrectionMiner.RecognizerOrigin),
            Make(SuggestionKind.Correction, "abc", "Abc", 1.0, true, 1, "d", CorrectionMiner.EditsOrigin),
            Make(SuggestionKind.Snippet, "x y", "a whole paragraph", 3.0, true, 1, "e"),
            Make(SuggestionKind.Correction, "other", "Other", 1.0, true, 1, "f"),
            Make(SuggestionKind.Style, "personal", "casual", 1, true, 12, "g"),
        };
        var kept = HistoryLearner.OnePerTrigger(rows);
        check("one rule per phrase: best kept, a fix over a snippet, a deliberate source on a tie",
            kept.Count == 5
            && kept.Single(k => k.Kind == SuggestionKind.Correction && k.Phrase == "x y").Replacement == "Zed"
            && kept.Single(k => k.Phrase == "abc").Replacement == "Abc"
            && !kept.Any(k => k.Kind == SuggestionKind.Snippet),
            $"{kept.Count} kept");
        check("one rule per phrase: words, styles and other phrases untouched, order kept",
            kept.Select(k => k.Why).SequenceEqual(new[] { "w", "a", "d", "f", "g" }));

        var distinct = new[]
        {
            Make(SuggestionKind.Correction, "clawed", "Claude", 1, true, 2, "x"),
            Make(SuggestionKind.Snippet, "sign off", "Thanks, Alex", 1, true, 2, "x"),
        };
        check("one rule per phrase: different phrases all kept", HistoryLearner.OnePerTrigger(distinct).Count == 2);

        // The whole filter: a dismissed best rule does not take the
        // alternative for its phrase with it; a phrase the dictionary already
        // has a rule for loses every proposal; one row per key.
        var existing = new List<DictionaryEntry> { Entry("figjam", "FigJam") };
        var filtered = HistoryLearner.Filter(existing, new[]
        {
            Make(SuggestionKind.Correction, "clawed", "Claude", 2.0, true, 3, "x"),
            Make(SuggestionKind.Correction, "clawed", "Clyde", 1.0, true, 1, "x", "your recordings"),
            Make(SuggestionKind.Correction, "Clawed", "clyde", 1.2, true, 2, "x", "your recordings"),
            Make(SuggestionKind.Correction, "figjam", "Fig Jam", 1.0, true, 2, "x"),
            Make(SuggestionKind.Correction, "FIGJAM", "FIG JAM", 1.0, true, 2, "x"),
        }, s => s.Replacement == "Claude");
        check("report filter: dismissed and covered rules gone, the alternative offered once",
            filtered.Count == 1 && filtered[0].Phrase.Equals("clawed", StringComparison.OrdinalIgnoreCase)
            && filtered[0].Replacement.Equals("clyde", StringComparison.OrdinalIgnoreCase) && filtered[0].Score == 1.2,
            $"{filtered.Count} left");
    }

    private static void State(Checker check)
    {
        check("hash: SHA-256 prefix", LearningState.Hash("abc") == "ba7816bf8f01cfea");
        var a = LearningState.Hash("v|reforger");
        check("hash: 16 lowercase hex, distinct",
            a.Length == LearningState.HashChars && a.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            && a != LearningState.Hash("v|claude") && a == LearningState.Hash("v|reforger"));

        var state = new LearningState();
        var reforger = Make(SuggestionKind.Vocabulary, "Reforger", "", 1, true, 1, "x");
        var sameWord = Make(SuggestionKind.Vocabulary, "REFORGER", "", 1, true, 1, "y");
        var other = Make(SuggestionKind.Vocabulary, "Claude", "", 1, true, 1, "z");
        var asRule = Make(SuggestionKind.Correction, "Reforger", "Re-forger", 1, true, 1, "z");
        var added = state.Dismiss(new[] { reforger, asRule });
        check("dismiss: by key, any casing",
            added == 2 && state.IsDismissed(sameWord) && !state.IsDismissed(other) && state.Dismiss(new[] { sameWord }) == 0);

        // Undo / deleting a learned entry: the entry's key is the key the
        // miners will produce for it again, whatever its kind.
        var removed = new[]
        {
            Entry("Kubernetes", learned: true),
            Entry("armor forger", "Arma Reforger", learned: true),
            Entry("sign off", "Thanks, Alex", learned: true, snippet: true),
            Entry("hyphen-ated", " ", learned: true),
        };
        var forgotten = new LearningState();
        var forgottenCount = forgotten.DismissKeys(removed.Select(HistoryLearner.DismissalKey));
        check("removed entries: not suggested again, by kind",
            forgottenCount == 4
            && forgotten.IsDismissed(Make(SuggestionKind.Vocabulary, "kubernetes", "", 1, true, 1, "x"))
            && forgotten.IsDismissed(Make(SuggestionKind.Correction, "Armor Forger", "arma reforger", 1, true, 1, "x"))
            && forgotten.IsDismissed(Make(SuggestionKind.Snippet, "sign off", "Thanks, Alex", 1, true, 1, "x"))
            && forgotten.IsDismissed(Make(SuggestionKind.Correction, "hyphen-ated", " ", 1, true, 1, "x")));
        check("removed entries: other suggestions still offered",
            !forgotten.IsDismissed(Make(SuggestionKind.Vocabulary, "Claude", "", 1, true, 1, "x"))
            && !forgotten.IsDismissed(Make(SuggestionKind.Correction, "armor forger", "Armor Forge", 1, true, 1, "x"))
            && !forgotten.IsDismissed(Make(SuggestionKind.Vocabulary, "armor forger", "", 1, true, 1, "x"))
            && !forgotten.IsDismissed(Make(SuggestionKind.Correction, "Kubernetes", "K8s", 1, true, 1, "x")));

        // A hand edit makes a learned entry the user's own (Undo keeps it);
        // saving an editor unchanged does not.
        var edited = Entry("clawed", "Claude", learned: true);
        DictionaryStore.ApplyEdit(edited, "clawed", "Claude Code");
        var unchanged = Entry("clawed", "Claude", learned: true);
        DictionaryStore.ApplyEdit(unchanged, "clawed", "Claude");
        var own = Entry("Enfusion");
        DictionaryStore.ApplyEdit(own, "Enfusion", "");
        check("edit: changed text clears the learned tag, an unchanged save keeps it",
            !edited.LearnedFromHistory && edited.Replacement == "Claude Code"
            && unchanged.LearnedFromHistory && !own.LearnedFromHistory && own.Phrase == "Enfusion");

        // The dot only counts while a report is held: after a restart there
        // is nothing behind "Review N suggestions".
        var held = new LearningReport(DateTime.UtcNow, Array.Empty<SourceSummary>(), 0, Array.Empty<Suggestion>(), null,
            TimeSpan.Zero, Array.Empty<string>());
        check("pending: 0 without a report, the stored count with one",
            HistoryLearner.PendingFor(null, 7) == 0 && HistoryLearner.PendingFor(held, 7) == 7
            && HistoryLearner.PendingFor(held, 0) == 0);

        var path = Path.Combine(Path.GetTempPath(), $"FlowType-selftest-learning-{Guid.NewGuid():N}.json");
        try
        {
            var missing = LearningState.Load(path);
            check("state: missing file loads empty", missing.PendingCount == 0 && missing.Dismissed.Count == 0
                && missing.LastRunUtc == null && missing.LastBench == null && missing.LastFlowTypeNewestUtc == null);

            var when = new DateTime(2026, 10, 19, 6, 12, 40, DateTimeKind.Utc);
            state.LastRunUtc = when;
            state.LastManualRunUtc = when;
            state.PendingCount = 3;
            state.AppliedTotal = 5;
            state.LastFlowTypeDictations = 12;
            state.LastWisprDictations = 863;
            state.LastFlowTypeNewestUtc = when.AddTicks(1234567);
            state.LastBench = BenchSummary.From(new BenchResult(21, 1920.5, 5400, 0.912, 0.931, 40, 22, 35, 70, 3, 0.05,
                "large-v3-q5_0", when, new[] { reforger }));
            state.Save(path);
            state.Save(path);   // second write exercises the replace-with-backup path

            var text = File.ReadAllText(path);
            var loaded = LearningState.Load(path);
            check("state: round trip",
                loaded.LastRunUtc == when && loaded.LastManualRunUtc == when && loaded.PendingCount == 3
                && loaded.AppliedTotal == 5 && loaded.LastFlowTypeDictations == 12 && loaded.LastWisprDictations == 863
                && loaded.LastFlowTypeNewestUtc == state.LastFlowTypeNewestUtc
                && loaded.LastBench == state.LastBench && loaded.Dismissed.SetEquals(state.Dismissed));
            check("state: dismissals survive and still match", loaded.IsDismissed(sameWord) && !loaded.IsDismissed(other));
            check("state: no words in the file",
                !text.Contains("reforger", StringComparison.OrdinalIgnoreCase)
                && !text.Contains("RecognitionFixes", StringComparison.Ordinal), $"{text.Length} chars");
        }
        finally
        {
            foreach (var p in new[] { path, path + ".bak", path + ".tmp" })
            {
                try { File.Delete(p); } catch { }
            }
        }
        check("state: temp files removed", !File.Exists(path) && !File.Exists(path + ".bak"));
    }

    private static DictionaryEntry Entry(string phrase, string replacement = "", bool enabled = true,
        bool learned = false, bool snippet = false) =>
        new()
        {
            Phrase = phrase, Replacement = replacement, Enabled = enabled,
            LearnedFromHistory = learned, IsSnippet = snippet,
        };

    private static void DictionaryPlan(Checker check)
    {
        var existing = new List<DictionaryEntry>
        {
            Entry("Arma Reforger"),
            Entry("figjam", "FigJam"),
            Entry("Wispr", enabled: false),
        };
        var items = new (string, string, bool)[]
        {
            ("Reforger", "", false),
            ("arma reforger", "", false),           // already a word (other casing)
            ("Wispr", "", false),                   // switched off by the user: stays off
            ("Claude", "", false),
            ("claude", "", false),                  // repeated in the same batch
            ("FigJam", "", false),                  // already written by a rule
            ("figjam", "Figjam", false),            // a second rule for one phrase could never fire
            ("armor forger", "Arma Reforger", false),
            ("  my site  ", "https://example.test", true),
            ("", "x", false),                       // blank phrase
            ("same", "same", false),                // rewrites to itself
            ("hyphen-ated", " ", false),            // a whitespace replacement is a real rule
        };
        var planned = DictionaryStore.PlanFromHistory(existing, items);
        check("learned entries: de-duplicated, in the order given",
            planned.Select(p => p.Phrase).SequenceEqual(new[] { "Reforger", "Claude", "armor forger", "my site", "hyphen-ated" }),
            $"{planned.Count} planned");
        check("learned entries: tagged, kinds kept",
            planned.All(p => p.LearnedFromHistory && p.Enabled && p.MatchWholeWord)
            && planned.Count(p => p.IsSnippet) == 1 && planned.Single(p => p.IsSnippet).Phrase == "my site"
            && planned[0].IsVocabularyOnly && planned[1].IsVocabularyOnly
            && planned[2].Replacement == "Arma Reforger" && planned[4].Replacement == " ");

        // Appended after the user's entries, and spent from the prompt after them.
        var combined = existing.Concat(planned).ToList();
        var sections = DictionaryStore.SectionsFor(combined);
        check("learned entries: user's spellings first in the prompt",
            sections.Spellings.SequenceEqual(new[] { "Arma Reforger", "FigJam", "Reforger", "Claude" }),
            $"{sections.Spellings.Count} spellings");
        var interleaved = new List<DictionaryEntry> { Entry("Claude", learned: true), Entry("Enfusion"), Entry("x y", "Workbench") };
        check("learned entries: user's terms keep priority wherever they sit",
            DictionaryStore.SectionsFor(interleaved).Spellings.SequenceEqual(new[] { "Enfusion", "Workbench", "Claude" }));
        var plain = new List<DictionaryEntry> { Entry("Enfusion"), Entry("x y", "Workbench"), Entry("Claude") };
        check("no learned entries: order unchanged",
            DictionaryStore.SectionsFor(plain).Spellings.SequenceEqual(new[] { "Enfusion", "Claude", "Workbench" }));

        // The report only offers what Apply would really add.
        var offered = HistoryLearner.WithoutKnown(existing, new[]
        {
            Make(SuggestionKind.Vocabulary, "arma reforger", "", 1, true, 2, "x"),
            Make(SuggestionKind.Vocabulary, "Wispr", "", 1, true, 2, "x"),
            Make(SuggestionKind.Vocabulary, "Claude", "", 1, true, 2, "x"),
            Make(SuggestionKind.Correction, "FIGJAM", "Fig Jam", 1, true, 2, "x"),
            Make(SuggestionKind.Correction, "armor forger", "Arma Reforger", 1, true, 2, "x"),
            Make(SuggestionKind.Snippet, "sign off", "Thanks, Alex", 1, true, 2, "x"),
            Make(SuggestionKind.Style, "personal", "casual", 1, true, 12, "x"),
        });
        check("report: entries Apply would skip are not offered",
            offered.Select(o => o.Phrase).SequenceEqual(new[] { "Claude", "armor forger", "sign off", "personal" }),
            $"{offered.Count} offered");

        // A learned rule never competes with the user's rule for the same
        // phrase; one for a new phrase works alongside it.
        var rules = existing.Concat(DictionaryStore.PlanFromHistory(existing,
            new[] { ("FIGJAM", "Fig Jam", false), ("arma", "ARMA", false) })).ToList();
        check("learned rules: the user's rule for a phrase stays the only one",
            rules.Count == existing.Count + 1 && DictionaryStore.ApplyRules("figjam arma", rules) == "FigJam ARMA");
    }

    private static void Budget(Checker check)
    {
        // What every dictation still needs after the spellings: on-screen
        // names, the space after the section, the previous-text slot (offered
        // min(200, left − 1) and costing its length plus a space).
        var perDictation = HistoryLearner.ScreenTermsReserve + Transcriber.MaxPreviousTextChars + 2;
        var dictionary = new List<DictionaryEntry> { Entry("Arma Reforger"), Entry("Claude") };
        var off = HistoryLearner.PromptBudget(dictionary, EnglishVariant.Off);
        check("budget: no variant, room kept for each dictation",
            off == Transcriber.MaxPromptChars - "Preferred spellings: .".Length - "Arma Reforger, Claude".Length - perDictation,
            $"{off}");

        var variant = EnglishVariant.RecognitionPrompt(EnglishVariant.Us);
        var us = HistoryLearner.PromptBudget(dictionary, EnglishVariant.Us);
        check("budget: variant sentence reserved", us == off - ((variant?.Trim().Length ?? -1) + 1), $"{us}");

        // The user's rules: their targets are spellings, their phrases the
        // spoken forms, which are budgeted last and must keep their room.
        var withRules = dictionary.Concat(new[] { Entry("armor forger", "Arma Reforger"), Entry("cloud code", "Claude Code") }).ToList();
        var sections = DictionaryStore.SectionsFor(withRules);
        var ruled = HistoryLearner.PromptBudget(withRules, EnglishVariant.Us);
        check("budget: the user's spoken forms reserved",
            sections.SpokenForms.Count == 2
            && ruled == us - ", Claude Code".Length - "Possible spoken forms: .".Length - "armor forger, cloud code".Length,
            $"{ruled}");

        // The budget must agree with the prompt builder. Learned words
        // costing exactly the budget, plus on-screen names filling their
        // reserve, still leave a full previous-text context and every spoken
        // form; one word more costs the spoken forms (the reserve is exact).
        var spellings = sections.Spellings;
        var fit = Enumerable.Range(0, ruled / 9).Select(i => $"Word{i:000}").ToList();   // 7 chars + ", " each
        var screen = Enumerable.Range(0, HistoryLearner.ScreenTermsReserve / 10).Select(i => $"Screen{i:00}").ToList();
        var previous = "x" + string.Join(" ", Enumerable.Repeat("word", 40));   // exactly 200 characters
        string Prompt(IEnumerable<string> learned) =>
            Transcriber.BuildPrompt(variant, spellings.Concat(learned).Concat(screen).ToList(), sections.SpokenForms, previous) ?? "";
        var prompt = Prompt(fit);
        var overPrompt = Prompt(fit.Append("Wordxyz"));
        check("budget: a full dictionary leaves the context, the screen names and the spoken forms",
            fit.Count > 0 && previous.Length == Transcriber.MaxPreviousTextChars
            && fit.All(w => prompt.Contains(w, StringComparison.Ordinal))
            && screen.All(w => prompt.Contains(w, StringComparison.Ordinal))
            && prompt.Contains(previous, StringComparison.Ordinal)
            && prompt.Contains("Possible spoken forms: armor forger, cloud code.", StringComparison.Ordinal)
            && prompt.Length <= Transcriber.MaxPromptChars,
            $"{fit.Count} words, {prompt.Length} chars");
        check("budget: exact — one word more than the budget costs a spoken form",
            overPrompt.Contains("Wordxyz", StringComparison.Ordinal)
            && overPrompt.Contains(previous, StringComparison.Ordinal)
            && !overPrompt.Contains("Possible spoken forms: armor forger, cloud code.", StringComparison.Ordinal));
        check("budget: never negative",
            HistoryLearner.PromptBudget(Enumerable.Range(0, 200).Select(i => Entry($"Term{i:000}")).ToList(), EnglishVariant.Us) == 0);

        FitToBudget(check);
    }

    /// <summary>The report-wide trim: pre-ticked rows of every miner held to the prompt budget.</summary>
    private static void FitToBudget(Checker check)
    {
        var dictionary = new List<DictionaryEntry> { Entry("Arma Reforger") };
        var rows = new List<Suggestion>
        {
            Make(SuggestionKind.Vocabulary, "Kubernetes", "", 3.0, true, 9, "a"),
            Make(SuggestionKind.Vocabulary, "Terraform", "", 2.0, true, 5, "b"),
            Make(SuggestionKind.Vocabulary, "Wispr", "", 1.0, true, 1, "c", WisprDictionaryImport.Origin),
            Make(SuggestionKind.Correction, "cloud code", "Claude Code", 1.5, true, 3, "d"),
            Make(SuggestionKind.Correction, "my site", "https://example.test", 1.0, true, 2, "e"),
            Make(SuggestionKind.Correction, "armor forger", "Arma Reforger", 1.0, true, 2, "f"),
            Make(SuggestionKind.Vocabulary, "Enfusion", "", 0.5, false, 2, "g"),
            Make(SuggestionKind.Style, "personal", "casual", 1, true, 12, "h"),
        };
        bool Ticked(List<Suggestion> list, string why) => list.Single(s => s.Why.StartsWith(why, StringComparison.Ordinal)).Recommended;

        // 20 characters: ", Wispr" (7) and ", Kubernetes" (12) fit; the rest
        // would not. A fix whose target is already a spelling, or is no term
        // at all, costs nothing.
        var tight = HistoryLearner.FitToPromptBudget(dictionary, rows, 20);
        check("trim: what does not fit is unticked, the user's Wispr words first in line",
            Ticked(tight, "c") && Ticked(tight, "a") && !Ticked(tight, "b") && !Ticked(tight, "d")
            && tight.Single(s => s.Why.StartsWith("b", StringComparison.Ordinal)).Why.Contains(HistoryLearner.NoRoomWhy)
            && tight.Count == rows.Count);
        check("trim: free rows, unticked rows and styles untouched",
            Ticked(tight, "e") && Ticked(tight, "f") && !Ticked(tight, "g") && Ticked(tight, "h")
            && tight.Single(s => s.Why == "g").Why == "g");

        var roomy = HistoryLearner.FitToPromptBudget(dictionary, rows, 500);
        check("trim: nothing changes when everything fits",
            roomy.Select(s => (s.Recommended, s.Why)).SequenceEqual(rows.Select(s => (s.Recommended, s.Why))));

        var many = Enumerable.Range(0, HistoryLearner.MaxRecommendedWords + 5)
            .Select(i => Make(SuggestionKind.Vocabulary, $"Term{i:00}", "", 2.0, true, 2, "x")).ToList();
        var capped = HistoryLearner.FitToPromptBudget(dictionary, many, 5000);
        check("trim: at most the word cap pre-ticked",
            capped.Count(s => s.Recommended) == HistoryLearner.MaxRecommendedWords && capped[^1].Recommended == false
            && capped[0].Recommended);

        check("trim: the cost counts only new spellings",
            HistoryLearner.SpellingsCost(dictionary, new[] { rows[0] }) == ", Kubernetes".Length
            && HistoryLearner.SpellingsCost(dictionary, new[] { rows[4], rows[5] }) == 0);
    }

    private static void Proposals(Checker check)
    {
        var current = new List<DictionaryEntry> { Entry("Arma Reforger") };
        var variant = EnglishVariant.RecognitionPrompt(EnglishVariant.Us);
        bool Changes(params Suggestion[] proposed) =>
            PersonalBench.PromptChanges(current, PersonalBench.WithProposals(current, proposed), variant);

        check("second decode: a new word changes the prompt",
            Changes(Make(SuggestionKind.Vocabulary, "Claude", "", 1, true, 2, "x")));
        check("second decode: a fix to a term changes the prompt",
            Changes(Make(SuggestionKind.Correction, "cloud code", "Claude Code", 1, true, 2, "x")));
        check("second decode: skipped for a word already known, a non-term fix, a snippet or a style",
            !Changes(Make(SuggestionKind.Vocabulary, "arma reforger", "", 1, true, 2, "x"))
            && !Changes(Make(SuggestionKind.Correction, "my site", "https://example.test", 1, true, 2, "x"))
            && !Changes(Make(SuggestionKind.Snippet, "sign off", "Thanks, Alex", 1, true, 2, "x"))
            && !Changes(Make(SuggestionKind.Style, "personal", "casual", 1, true, 2, "x")));

        var samples = new[]
        {
            Sample(HistorySource.WisprFlow, "a", 3, true, "hello"),
            Sample(HistorySource.WisprFlow, "b", 1, true, "hello"),
            Sample(HistorySource.WisprFlow, "c", 2, true, " "),         // no reference
            Sample(HistorySource.WisprFlow, "d", 4, false, "hello"),    // no audio
            Sample(HistorySource.FlowType, "e", 5, true, "hello"),      // not an independent reference
        };
        check("bench picks: Wispr takes with audio and a transcript, newest first",
            PersonalBench.Select(samples).Select(s => s.Id).SequenceEqual(new[] { "a", "b" }));
    }

    /// <summary>The daily run's "anything new?" test, and the --learncheck origin labels.</summary>
    private static void Background(Checker check)
    {
        var t1 = new DateTime(2026, 10, 19, 6, 0, 0, DateTimeKind.Utc);
        var t2 = t1.AddMinutes(3);
        check("daily run: a new note at the 1000-note cap is new writing",
            BackgroundLearner.FlowTypeChanged(1000, t2, 1000, t1));
        check("daily run: a changed count is new writing", BackgroundLearner.FlowTypeChanged(999, t1, 1000, t1));
        check("daily run: nothing new, no run",
            !BackgroundLearner.FlowTypeChanged(1000, t1, 1000, t1) && !BackgroundLearner.FlowTypeChanged(0, null, 0, null));

        var samples = new[]
        {
            Sample(HistorySource.FlowType, "a", 3, false, "x"),
            Sample(HistorySource.FlowType, "b", 9, false, "x"),
            Sample(HistorySource.WisprFlow, "c", 30, false, "x"),
        };
        check("daily run: newest FlowType note, Wispr's ignored",
            HistoryLearner.NewestOf(samples, HistorySource.FlowType) == samples[1].TimestampUtc
            && HistoryLearner.NewestOf(samples.Take(0), HistorySource.FlowType) == null);

        check("learncheck: every miner's origin has its own line (none counted as 'other')",
            new[]
            {
                VocabularyMiner.Origin, CorrectionMiner.EditsOrigin, CorrectionMiner.RecognizerOrigin,
                CorrectionMiner.FlowTypeRecognizerOrigin, CorrectionMiner.RecordingsOrigin, StyleMiner.EditsOrigin,
                StyleMiner.WisprOrigin, WisprDictionaryImport.Origin,
            }.All(App.KnownOrigins.Contains)
            && !App.KnownOrigins.Contains("other"));
    }

    /// <summary>
    /// The benchmark gives way to the user mid-decode (<see cref="PersonalBench.GiveWayAsync"/>),
    /// with a fake dictation session: an idle user never interrupts it, a
    /// dictation cancels the decode and it is redone afterwards, a user who
    /// keeps interrupting costs the take (null), and cancelling the run still
    /// ends it.
    /// </summary>
    private static void GiveWay(Checker check)
    {
        var busy = 0;
        Action? onBusy = null;
        var subscriptions = 0;
        var activity = new PersonalBench.UserActivity(
            () => Volatile.Read(ref busy) != 0,
            cb =>
            {
                Interlocked.Increment(ref subscriptions);
                Volatile.Write(ref onBusy, cb);
                return new PersonalBench.Unsubscriber(() => Volatile.Write(ref onBusy, null));
            },
            TimeSpan.FromMilliseconds(20));

        // The user starts dictating: mark busy, tell the subscriber, finish a moment later.
        void UserDictates()
        {
            Volatile.Write(ref busy, 1);
            Volatile.Read(ref onBusy)?.Invoke();
            _ = Task.Run(async () =>
            {
                await Task.Delay(80).ConfigureAwait(false);
                Volatile.Write(ref busy, 0);
            });
        }

        T Sync<T>(Func<Task<T>> run) => Task.Run(run).GetAwaiter().GetResult();

        try
        {
            var attempts = 0;
            var idle = Sync(() => PersonalBench.GiveWayAsync(_ =>
            {
                attempts++;
                return Task.FromResult("done");
            }, activity, null, CancellationToken.None));
            check("give way: an idle user never interrupts", idle == "done" && attempts == 1, $"{attempts} attempts");

            attempts = 0;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var resumed = Sync(() => PersonalBench.GiveWayAsync(async token =>
            {
                if (++attempts == 1)
                {
                    UserDictates();
                    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                }
                return "done";
            }, activity, null, CancellationToken.None));
            check("give way: a dictation stops the decode, which is redone after it",
                resumed == "done" && attempts == 2 && watch.ElapsedMilliseconds < 3000,
                $"{attempts} attempts, {watch.ElapsedMilliseconds} ms");

            attempts = 0;
            var givenUp = Sync(() => PersonalBench.GiveWayAsync(async token =>
            {
                attempts++;
                UserDictates();
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                return "never";
            }, activity, null, CancellationToken.None));
            check("give way: a pass interrupted every time skips the take",
                givenUp == null && attempts == PersonalBench.MaxInterruptions, $"{attempts} attempts");

            // Let the last fake dictation finish before timing a cancel.
            var settle = System.Diagnostics.Stopwatch.StartNew();
            while (Volatile.Read(ref busy) != 0 && settle.ElapsedMilliseconds < 2000) Thread.Sleep(10);
            attempts = 0;
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(60));
            var cancelled = false;
            try
            {
                Sync(() => PersonalBench.GiveWayAsync(async token =>
                {
                    attempts++;
                    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
                    return "never";
                }, activity, null, cts.Token));
            }
            catch (OperationCanceledException) { cancelled = true; }
            check("give way: cancelling the run still ends it", cancelled && attempts == 1, $"{attempts} attempts");
            check("give way: every subscription released", Volatile.Read(ref onBusy) == null && subscriptions >= 7,
                $"{subscriptions} subscriptions");
        }
        catch (Exception ex)
        {
            check("give way: ran", false, ex.GetType().Name);
        }
    }

    /// <summary>
    /// One real run of the orchestrator, as a dry run against a Wispr
    /// database path that does not exist: one run at a time, the missing
    /// source becomes a note instead of an error, and nothing is persisted.
    /// Reads the user's dictionary and settings; writes nothing.
    /// </summary>
    private static void Orchestration(Checker check)
    {
        var learner = HistoryLearner.Instance;
        var wasDry = learner.DryRun;
        learner.DryRun = true;
        var stateFile = AppPaths.LearningFile;
        var before = File.Exists(stateFile) ? File.GetLastWriteTimeUtc(stateFile) : (DateTime?)null;
        var missing = Path.Combine(Path.GetTempPath(), $"FlowType-selftest-nowispr-{Guid.NewGuid():N}.sqlite");
        try
        {
            var changes = 0;
            void OnChanged() => Interlocked.Increment(ref changes);
            learner.Changed += OnChanged;
            try
            {
                var request = new LearningRequest(UseFlowType: false, UseWispr: true, RunBench: false, WisprPath: missing);
                var first = learner.RunAsync(request, null, CancellationToken.None);
                var rejected = false;
                try { _ = learner.RunAsync(request, null, CancellationToken.None); }
                catch (InvalidOperationException) { rejected = true; }
                var report = Task.Run(() => first).GetAwaiter().GetResult();

                check("run: one at a time", rejected && !learner.IsRunning && changes >= 2, $"{changes} change events");
                check("run: an unreadable source is a note, not an error",
                    report.Sources.Count == 1 && !report.Sources[0].Available && report.SamplesRead == 0
                    && report.Notes.Count >= 1 && report.Bench == null && learner.LastReport == report,
                    $"{report.Notes.Count} notes, {report.Suggestions.Count} suggestions");
            }
            finally
            {
                learner.Changed -= OnChanged;
            }
            var after = File.Exists(stateFile) ? File.GetLastWriteTimeUtc(stateFile) : (DateTime?)null;
            check("run: a dry run persists nothing", before == after && !File.Exists(missing));
        }
        finally
        {
            learner.DryRun = wasDry;
        }
    }

    /// <summary>
    /// The whole text-only pipeline, as a dry run over a scratch Wispr-shaped
    /// database in %TEMP%: sixty made-up names (more than the prompt budget
    /// holds), a recogniser fix and a Wispr dictionary rule that disagree about
    /// one spoken phrase, and two Wispr dictionary words. Checks invariants
    /// that hold whatever the user's own dictionary and dismissals are (both
    /// are read, neither is written): one rule per phrase, every pre-ticked
    /// row together within the prompt budget and the word cap, and the user's
    /// Wispr words never left out while a mined word is ticked.
    /// </summary>
    private static void Pipeline(Checker check)
    {
        var learner = HistoryLearner.Instance;
        var wasDry = learner.DryRun;
        learner.DryRun = true;
        var stateFile = AppPaths.LearningFile;
        var before = File.Exists(stateFile) ? File.GetLastWriteTimeUtc(stateFile) : (DateTime?)null;
        var path = Path.Combine(Path.GetTempPath(), $"FlowType-selftest-pipeline-{Guid.NewGuid():N}.sqlite");
        try
        {
            using (var db = WinSqlite.OpenReadWrite(path))
            {
                db.Execute(
                    "CREATE TABLE History (transcriptEntityId VARCHAR(36) PRIMARY KEY, asrText TEXT, formattedText TEXT, " +
                    "editedText TEXT, timestamp DATETIME, audio BLOB, status VARCHAR(255), app VARCHAR(255), duration FLOAT, " +
                    "language TEXT, pastedText TEXT, detectedLanguage TEXT, personalizationStyleSettings JSON, isArchived TINYINT(1));" +
                    "CREATE TABLE Dictionary (id VARCHAR(36), phrase VARCHAR(255), replacement VARCHAR(255), isDeleted TINYINT(1), " +
                    "source VARCHAR(255), isSnippet TINYINT(1));");
                const string row =
                    "INSERT INTO History (transcriptEntityId, status, asrText, formattedText, pastedText, timestamp, app, duration, " +
                    "language, isArchived) VALUES (?, 'formatted', ?, ?, ?, ?, 'selftest', 2.0, 'en', 0)";
                var syllables = new[] { "zor", "quen", "vit", "drak", "teph", "yun", "kras", "obel", "nix", "pruv" };
                var n = 0;
                string Stamp() => $"2026-10-{1 + n % 18:00} 06:{n % 60:00}:00.000 +00:00";
                for (var i = 0; i < 60; i++)
                {
                    var word = char.ToUpperInvariant(syllables[i % 10][0]) + syllables[i % 10][1..] + syllables[(i / 10 + 3) % 10]
                        + syllables[(i * 7 + 1) % 10];
                    for (var k = 0; k < 3; k++, n++)
                    {
                        var text = $"Today we shipped {word} again and it worked.";
                        db.Execute(row, $"w{n}", text, text, text, Stamp());
                    }
                }
                for (var k = 0; k < 3; k++, n++)
                {
                    db.Execute(row, $"c{n}", "please call zorb lax now", "Please call Zorblax now.", "Please call Zorblax now.", Stamp());
                }
                const string entry = "INSERT INTO Dictionary (id, phrase, replacement, isDeleted, source, isSnippet) VALUES (?, ?, ?, 0, 'manual', 0)";
                db.Execute(entry, "1", "zorb lax", "Zorblacks");
                db.Execute(entry, "2", "Quexil", "");
                db.Execute(entry, "3", "Brontavia", "");
            }

            var report = Task.Run(() => learner.RunAsync(
                new LearningRequest(UseFlowType: false, UseWispr: true, RunBench: false, WisprPath: path),
                null, CancellationToken.None)).GetAwaiter().GetResult();
            var dictionary = DictionaryStore.Instance.Entries;
            var budget = HistoryLearner.PromptBudget(dictionary, SettingsStore.Instance.Settings.EnglishVariant);
            var ticked = report.Suggestions.Where(s => s.Recommended).ToList();
            var cost = HistoryLearner.SpellingsCost(dictionary, ticked);
            var words = ticked.Count(s => s.Kind == SuggestionKind.Vocabulary);

            check("pipeline: the synthetic history was mined",
                report.SamplesRead == 183 && report.OfKind(SuggestionKind.Vocabulary).Count() >= 20,
                $"{report.SamplesRead} read, {report.Suggestions.Count} suggestions");
            var rules = report.Suggestions.Where(s => s.Kind is SuggestionKind.Correction or SuggestionKind.Snippet).ToList();
            check("pipeline: one rule per spoken phrase across miners",
                rules.Select(s => s.Phrase.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == rules.Count,
                $"{rules.Count} rules");
            check("pipeline: everything pre-ticked fits the prompt budget together, within the word cap",
                cost <= budget && words <= HistoryLearner.MaxRecommendedWords,
                $"{cost} of {budget} chars, {words} words");
            var wisprWords = report.OfKind(SuggestionKind.Vocabulary)
                .Where(s => s.Origin == WisprDictionaryImport.Origin).ToList();
            var minedTicked = ticked.Any(s => s.Kind == SuggestionKind.Vocabulary && s.Origin != WisprDictionaryImport.Origin);
            check("pipeline: the user's Wispr words get the room before mined words",
                !minedTicked || wisprWords.All(s => s.Recommended), $"{wisprWords.Count} Wispr words");

            var after = File.Exists(stateFile) ? File.GetLastWriteTimeUtc(stateFile) : (DateTime?)null;
            check("pipeline: a dry run persists nothing", before == after);
        }
        catch (Exception ex)
        {
            check("pipeline: ran", false, ex.GetType().Name);
        }
        finally
        {
            learner.DryRun = wasDry;
            foreach (var file in new[] { path, path + "-journal", path + "-wal", path + "-shm" })
            {
                try { if (File.Exists(file)) File.Delete(file); } catch { }
            }
        }
    }

    private static HistorySample Sample(HistorySource source, string id, int minute, bool audio, string recognized,
        double seconds = 24) =>
        new(source, id, new DateTime(2026, 10, 19, 6, minute, 0, DateTimeKind.Utc), "selftest", recognized,
            recognized, null, "en", seconds, audio);

    /// <summary>
    /// One real benchmark over 24 s of synthetic audio with the loaded model:
    /// the plumbing (audio loaded once for both passes, the live chunker's
    /// cuts reported, numbers in range) and that cancelling stops it promptly
    /// and frees the decoder. Skipped without a model. Sync over async on a
    /// worker: every await below is ConfigureAwait(false) or already on the
    /// pool, so the blocked UI thread is never needed.
    /// </summary>
    private static void BenchSmoke(Checker check, StringBuilder log)
    {
        if (!Transcriber.Instance.IsReady)
        {
            log.AppendLine("learn engine: benchmark smoke skipped (no speech model loaded)");
            return;
        }

        var audio = SyntheticTake(24, gapSeconds: 0.7);
        var loads = 0;
        float[]? Loader(HistorySample s)
        {
            Interlocked.Increment(ref loads);
            return s.Id == "take" ? audio : null;
        }
        var samples = new[]
        {
            Sample(HistorySource.WisprFlow, "take", 1, true, "the quick brown fox jumps over the lazy dog"),
            Sample(HistorySource.FlowType, "own", 2, true, "not independent"),
            Sample(HistorySource.WisprFlow, "silent", 0, true, "gone"),   // loader returns null: skipped
        };
        var proposed = new[] { Make(SuggestionKind.Vocabulary, "Reforger", "", 2, true, 3, "x") };

        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = Task.Run(() => PersonalBench.RunWithAudioAsync(samples, Array.Empty<DictionaryEntry>(), proposed,
                null, CancellationToken.None, Loader)).GetAwaiter().GetResult();
            var ms = watch.ElapsedMilliseconds;

            var settings = SettingsStore.Instance.Settings;
            var plan = LiveTranscriber.PlanChunks(audio);
            var expectedChunks = settings.LiveTranscription ? plan.Count : 0;
            var expectedForced = settings.LiveTranscription ? plan.Count(c => c.Forced) : 0;
            check("bench smoke: one recording decoded, others skipped",
                result != null && result.Recordings == 1 && loads == 2 && result.ReferenceWords == 9
                && Math.Abs(result.AudioSeconds - 24.0) < 0.05,
                $"{result?.Recordings} recordings, {loads} loads, {ms} ms");
            check("bench smoke: numbers in range",
                result != null && result.BaselineAccuracy is >= 0 and <= 1 && result.LearnedAccuracy is >= 0 and <= 1
                && result.TermOccurrences == 0 && result.RealTimeFactor > 0
                && result.ModelId == Transcriber.Instance.CurrentModelId && result.RecognitionFixes != null,
                $"rtf {result?.RealTimeFactor:0.000}");
            check("bench smoke: live cuts match the cut policy",
                result != null && result.LiveChunks == expectedChunks && result.ForcedCuts == expectedForced,
                $"{result?.LiveChunks}/{expectedChunks} chunks, {result?.ForcedCuts}/{expectedForced} forced");

            // A dictation starting 100 ms into the first decode of a whole take
            // (live transcription off — the path that used to hold the decoder
            // for the entire recording): the decoder is freed at once, the pass
            // is redone afterwards, and the take counts.
            var userBusy = 0;
            var decodeStarts = 0;
            var interruptions = 0;
            var interrupting = new PersonalBench.UserActivity(
                () => Volatile.Read(ref userBusy) != 0,
                onBusy =>
                {
                    if (Interlocked.Increment(ref decodeStarts) == 1)
                    {
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(100).ConfigureAwait(false);
                            Volatile.Write(ref userBusy, 1);
                            Interlocked.Increment(ref interruptions);
                            onBusy();
                            await Task.Delay(300).ConfigureAwait(false);
                            Volatile.Write(ref userBusy, 0);
                        });
                    }
                    return new PersonalBench.Unsubscriber(() => { });
                },
                TimeSpan.FromMilliseconds(50));
            var interruptWatch = System.Diagnostics.Stopwatch.StartNew();
            var interrupted = Task.Run(() => PersonalBench.RunWithAudioAsync(samples, Array.Empty<DictionaryEntry>(),
                proposed, null, CancellationToken.None, Loader, interrupting, live: false)).GetAwaiter().GetResult();
            check("bench smoke: a dictation mid-decode (whole take) is given way to, and the take still counts",
                interrupted != null && interrupted.Recordings == 1 && interrupted.LiveChunks == 0
                && interruptions == 1 && decodeStarts >= 3 && !Transcriber.Instance.IsTranscribing,
                $"{decodeStarts} decode starts, {interruptWatch.ElapsedMilliseconds} ms");

            // Cancel mid-run: must throw promptly and leave the decoder free.
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            var cancelWatch = System.Diagnostics.Stopwatch.StartNew();
            var threw = false;
            var finished = false;
            try
            {
                Task.Run(() => PersonalBench.RunWithAudioAsync(samples, Array.Empty<DictionaryEntry>(), proposed,
                    null, cts.Token, Loader)).GetAwaiter().GetResult();
                finished = true;
            }
            catch (OperationCanceledException) { threw = true; }
            var cancelMs = cancelWatch.ElapsedMilliseconds;
            var freed = System.Diagnostics.Stopwatch.StartNew();
            while (Transcriber.Instance.IsTranscribing && freed.ElapsedMilliseconds < 5000) Thread.Sleep(20);
            check("bench smoke: cancel stops promptly and frees the decoder",
                (threw || finished) && cancelMs < 4000 && !Transcriber.Instance.IsTranscribing,
                threw ? $"stopped after {cancelMs} ms" : $"finished before the cancel ({cancelMs} ms)");
        }
        catch (Exception ex)
        {
            check("bench smoke: ran", false, ex.GetType().Name);
        }
    }

    /// <summary>
    /// A 200 Hz tone whose loudness swings like syllables, silent for
    /// <paramref name="gapSeconds"/> every 6 s — long enough (≥ 20 s, with
    /// pauses) for the live chunker to cut it. Deterministic.
    /// </summary>
    private static float[] SyntheticTake(double seconds, double gapSeconds)
    {
        var n = (int)(seconds * AudioConditioner.SampleRate);
        var x = new float[n];
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)AudioConditioner.SampleRate;
            var inGap = gapSeconds > 0 && t % 6.0 < gapSeconds && t > 1;
            x[i] = inGap ? 0f : (float)((0.2 + 0.1 * Math.Sin(2 * Math.PI * 4 * t)) * Math.Sin(2 * Math.PI * 200 * t));
        }
        return x;
    }
}
