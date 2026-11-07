using System.IO;
using System.Text;
using FlowType.Core;
using FlowType.Engine;
using FlowType.Session;

namespace FlowType.Learning;

/// <summary>
/// The --selftest vectors for the history miners. Synthetic samples only —
/// nothing here reads the user's history. Same rule as the rest of the
/// self-test: every "must fire" vector has a "must not fire" neighbour,
/// because a wrong suggestion that gets applied costs more than a missed one.
/// The whisper-vocabulary block also checks each installed model file (read
/// only, header and vocabulary) and says "skipped" when there is none.
/// </summary>
internal static class MinersSelfTest
{
    public static bool Run(StringBuilder log)
    {
        var t = new Tally(log);
        Corrections(t);
        RecordingFixes(t);
        Vocabulary(t);
        Styles(t);
        WisprDictionary(t);
        WhisperVocabularyFile(t);
        InstalledModels(t);
        log.AppendLine($"miners: {t.Passed}/{t.Total} passed");
        return t.Passed == t.Total;
    }

    private sealed class Tally
    {
        private readonly StringBuilder _log;
        public int Passed;
        public int Total;

        public Tally(StringBuilder log) => _log = log;

        public void Check(string name, bool ok, string detail = "")
        {
            Total++;
            if (ok) Passed++;
            _log.AppendLine($"miners {(ok ? "PASS" : "FAIL")} {name}{(ok || detail.Length == 0 ? "" : $" [{detail}]")}");
        }

        public void Note(string line) => _log.AppendLine("miners " + line);
    }

    private static HistorySample S(string final, string? edited = null, string recognized = "",
        string app = "claude", HistorySource source = HistorySource.WisprFlow) =>
        new(source, Guid.NewGuid().ToString("D"), new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc),
            app, recognized, final, edited, "en", 5, false);

    private static HistorySample[] Repeat(int n, Func<int, HistorySample> make) =>
        Enumerable.Range(0, n).Select(make).ToArray();

    private static DictionaryEntry Word(string phrase) => new() { Phrase = phrase };

    private static DictionaryEntry Rule(string heard, string write) => new() { Phrase = heard, Replacement = write };

    private static readonly IReadOnlyList<DictionaryEntry> NoDictionary = Array.Empty<DictionaryEntry>();

    /// <summary>The correction miners without a model vocabulary, so results never depend on what is installed.</summary>
    private static CorrectionFindings Mine(IReadOnlyList<HistorySample> samples, IReadOnlyList<DictionaryEntry> dictionary) =>
        CorrectionMiner.Mine(samples, dictionary, vocabulary: null);

    private static IReadOnlyList<Suggestion> FromRecordings(
        IReadOnlyList<(string Hypothesis, string Reference)> pairs, IReadOnlyList<DictionaryEntry> dictionary) =>
        CorrectionMiner.MineRecognitionErrors(pairs, dictionary, vocabulary: null);

    private static string Show(IEnumerable<Suggestion> list) =>
        string.Join(", ", list.Select(s => $"{s.Phrase}→{s.Replacement}{(s.Recommended ? "" : "?")}×{s.Sightings}"));

    private static Suggestion? Find(IEnumerable<Suggestion> list, string phrase, string replacement = "") =>
        list.FirstOrDefault(s => s.Phrase.Equals(phrase, StringComparison.OrdinalIgnoreCase)
                                 && s.Replacement.Equals(replacement, StringComparison.OrdinalIgnoreCase));

    // ----- CorrectionMiner.Mine -----

    private static void Corrections(Tally t)
    {
        // Your edits: strong evidence counts at once, weak on a second dictation.
        var strong = Mine(new[] { S("Let's meet Shawn tomorrow.", "Let's meet Sean tomorrow.") }, NoDictionary);
        var sean = Find(strong.FromEdits, "Shawn", "Sean");
        t.Check("edits/strong once", sean is { Recommended: true, Sightings: 1 } && sean.Origin == CorrectionMiner.EditsOrigin
                                      && sean.Kind == SuggestionKind.Correction, Show(strong.FromEdits));

        var weakOnce = Mine(new[] { S("open the jason file", "open the json file") }, NoDictionary);
        t.Check("edits/weak once not offered", weakOnce.FromEdits.Count == 0, Show(weakOnce.FromEdits));

        var weakTwice = Mine(new[]
        {
            S("open the jason file", "open the json file"),
            S("parse the jason please", "parse the json please"),
        }, NoDictionary);
        t.Check("edits/weak twice", Find(weakTwice.FromEdits, "jason", "json") is { Recommended: true, Sightings: 2 },
            Show(weakTwice.FromEdits));

        var sameSample = Mine(new[]
        {
            S("open the jason file and the jason schema", "open the json file and the json schema"),
        }, NoDictionary);
        t.Check("edits/once per dictation", sameSample.FromEdits.Count == 0, Show(sameSample.FromEdits));

        var grammar = Mine(new[]
        {
            S("I wonder weather it works", "I wonder whether it works"),
            S("not sure weather we can", "not sure whether we can"),
            S("ship the widgets today", "ship the widget today"),
            S("ship the widgets now", "ship the widget now"),
        }, NoDictionary);
        t.Check("edits/grammar never", grammar.FromEdits.Count == 0 && grammar.FromRecognizer.Count == 0, Show(grammar.FromEdits));

        var known = Mine(new[] { S("Let's meet Shawn tomorrow.", "Let's meet Sean tomorrow.") },
            new[] { Rule("shawn", "sean") });
        var sameTrigger = Mine(new[] { S("Let's meet Shawn tomorrow.", "Let's meet Sean tomorrow.") },
            new[] { Rule("Shawn", "Shaun") });
        t.Check("edits/already in dictionary", known.FromEdits.Count == 0 && sameTrigger.FromEdits.Count == 0,
            Show(known.FromEdits.Concat(sameTrigger.FromEdits)));

        var conflict = Mine(new[]
        {
            S("please ask Kara now", "please ask Cara now"),
            S("we told Kara about it", "we told Cara about it"),
            S("then Kara said no", "then Karra said no"),
        }, NoDictionary);
        t.Check("edits/most-sighted write wins",
            Find(conflict.FromEdits, "Kara", "Cara") is { Sightings: 2 } && Find(conflict.FromEdits, "Kara", "Karra") == null,
            Show(conflict.FromEdits));

        var tie = Mine(new[]
        {
            S("please ask Kara now", "please ask Cara now"),
            S("then Kara said no", "then Karra said no"),
        }, NoDictionary);
        t.Check("edits/tie dropped", tie.FromEdits.Count == 0, Show(tie.FromEdits));

        // Wispr's recogniser: two dictations to offer, three to pre-tick.
        HistorySample Heard(int i) => S("I play Arma Reforger every day.", recognized: "I play armour forger every day");
        var r1 = Mine(Repeat(1, Heard), NoDictionary);
        var r2 = Mine(Repeat(2, Heard), NoDictionary);
        var r3 = Mine(Repeat(3, Heard), NoDictionary);
        t.Check("recogniser/1 not offered", r1.FromRecognizer.Count == 0, Show(r1.FromRecognizer));
        t.Check("recogniser/2 offered unticked",
            Find(r2.FromRecognizer, "armour forger", "Arma Reforger") is { Recommended: false, Sightings: 2 }, Show(r2.FromRecognizer));
        var r3Rule = Find(r3.FromRecognizer, "armour forger", "Arma Reforger");
        t.Check("recogniser/3 pre-ticked", r3Rule is { Recommended: true } && r3Rule.Origin == CorrectionMiner.RecognizerOrigin,
            Show(r3.FromRecognizer));
        t.Check("recogniser/misheard terms counted",
            r3.MisheardTerms.TryGetValue("arma reforger", out var n) && n == 3 && r1.MisheardTerms.GetValueOrDefault("Arma Reforger") == 1,
            string.Join(", ", r3.MisheardTerms.Select(kv => $"{kv.Key}={kv.Value}")));

        // FlowType's own notes: RawText → Text only replays FlowType's own
        // rules and polish, so it is never recogniser evidence — not even for
        // a rule the user deleted after it rewrote five notes.
        HistorySample OwnNote(int i) => S("I play Arma Reforger every day.",
            recognized: "I play armour forger every day", source: HistorySource.FlowType);
        var flowTypeOnly = Mine(Repeat(3, OwnNote), NoDictionary);
        var deletedRule = Mine(Repeat(5, i => S("please ask Cara now", recognized: "please ask Kara now", source: HistorySource.FlowType)),
            NoDictionary);
        t.Check("recogniser/FlowType's own notes not mined",
            flowTypeOnly.FromRecognizer.Count == 0 && flowTypeOnly.MisheardTerms.Count == 0
            && deletedRule.FromRecognizer.Count == 0 && deletedRule.MisheardTerms.Count == 0,
            Show(flowTypeOnly.FromRecognizer.Concat(deletedRule.FromRecognizer)));
        var twoWispr = Mine(Repeat(2, Heard).Concat(Repeat(3, OwnNote)).ToArray(), NoDictionary);
        var threeWispr = Mine(Repeat(3, Heard).Concat(Repeat(3, OwnNote)).ToArray(), NoDictionary);
        var two = Find(twoWispr.FromRecognizer, "armour forger", "Arma Reforger");
        var three = Find(threeWispr.FromRecognizer, "armour forger", "Arma Reforger");
        t.Check("recogniser/only Wispr's dictations count",
            two is { Sightings: 2, Recommended: false } && three is { Sightings: 3, Recommended: true }
            && three.Origin == CorrectionMiner.RecognizerOrigin && three.Why.Contains("Wispr")
            && threeWispr.MisheardTerms.GetValueOrDefault("Arma Reforger") == 3,
            Show(twoWispr.FromRecognizer.Concat(threeWispr.FromRecognizer)));

        // A heard side the user's dictionary insists on (a word, or a rule's
        // target) is never a trigger: the rule would rewrite their own term.
        var clyde = new[] { S("we asked Claude today", "we asked Clyde today") };
        t.Check("edits/heard side is your own word",
            Find(Mine(clyde, NoDictionary).FromEdits, "Claude", "Clyde") != null
            && Find(Mine(clyde, new[] { Word("Claude") }).FromEdits, "Claude", "Clyde") == null
            && Find(Mine(clyde, new[] { Rule("clod", "Claude") }).FromEdits, "Claude", "Clyde") == null,
            Show(Mine(clyde, NoDictionary).FromEdits));

        // The user's fix outranks the recogniser's when they disagree.
        var mixed = Mine(new[]
        {
            S("please ask Kara now", "please ask Cara now"),
            S("then ping Karra today.", recognized: "then ping Kara today"),
            S("we told Karra about it.", recognized: "we told Kara about it"),
            S("so Karra said yes.", recognized: "so Kara said yes"),
        }, NoDictionary);
        t.Check("recogniser/edits win a conflict",
            Find(mixed.FromEdits, "Kara", "Cara") != null && Find(mixed.FromRecognizer, "Kara", "Karra") == null,
            Show(mixed.FromEdits.Concat(mixed.FromRecognizer)));

        // A one-word trigger whisper writes in one piece is an everyday word:
        // offered with a warning, never pre-ticked. A two-word one is not.
        var everyday = WhisperVocabulary.FromTokens(new[] { " Cloud", " cloud", " armour", " forger" });
        var cloud = Repeat(3, i => S("then ask Claude about it.", recognized: "then ask Cloud about it"));
        var cloudPlain = Find(Mine(cloud, NoDictionary).FromRecognizer, "Cloud", "Claude");
        var cloudHeld = Find(CorrectionMiner.Mine(cloud, NoDictionary, everyday).FromRecognizer, "Cloud", "Claude");
        var forgerHeld = Find(CorrectionMiner.Mine(Repeat(3, Heard), NoDictionary, everyday).FromRecognizer, "armour forger", "Arma Reforger");
        var shawnHeld = Find(CorrectionMiner.MineRecognitionErrors(new[]
        {
            ("let's meet Shawn tomorrow", "Let's meet Sean tomorrow."), ("call Shawn later", "Call Sean later."),
        }, NoDictionary, WhisperVocabulary.FromTokens(new[] { " Shawn" })), "Shawn", "Sean");
        t.Check("everyday trigger offered, not pre-ticked",
            cloudPlain is { Recommended: true } && cloudHeld is { Recommended: false } && cloudHeld.Why.Contains("every “Cloud”")
            && forgerHeld is { Recommended: true } && shawnHeld is { Recommended: false },
            $"{cloudPlain?.Recommended}/{cloudHeld?.Recommended}/{forgerHeld?.Recommended}/{shawnHeld?.Recommended} {cloudHeld?.Why}");

        // A pasted-only dictation (no edit, no raw text) teaches nothing.
        var plain = Mine(Repeat(5, i => S("I play Arma Reforger every day.")), NoDictionary);
        t.Check("plain history teaches nothing",
            plain.FromEdits.Count == 0 && plain.FromRecognizer.Count == 0 && plain.MisheardTerms.Count == 0);

        // The example is one line, at most 120 characters, and shows the fix.
        var filler = string.Join(" ", Enumerable.Repeat("we talked about the plan for the next release and", 4));
        var longFinal = $"{filler} then Shawn asked\nabout it. {filler} and that was all.";
        var longEdit = $"{filler} then Sean asked\nabout it. {filler} and that was all.";
        var example = Find(Mine(new[] { S(longFinal, longEdit) }, NoDictionary).FromEdits, "Shawn", "Sean")?.Example;
        t.Check("example/short and on point",
            example != null && example.Length <= 120 && example.Contains("Sean") && !example.Contains('\n')
            && example.StartsWith('…') && example.EndsWith('…'), example ?? "null");
        t.Check("example/short text kept whole",
            CorrectionMiner.Excerpt("Let's meet Sean tomorrow.", "Sean") == "Let's meet Sean tomorrow.");
    }

    // ----- CorrectionMiner.MineRecognitionErrors -----

    private static void RecordingFixes(Tally t)
    {
        var strongOnce = FromRecordings(
            new[] { ("let's meet Shawn tomorrow", "Let's meet Sean tomorrow.") }, NoDictionary);
        var s1 = Find(strongOnce, "Shawn", "Sean");
        t.Check("recordings/strong once offered unticked",
            s1 is { Recommended: false, Sightings: 1 } && s1.Origin == CorrectionMiner.RecordingsOrigin, Show(strongOnce));

        var strongTwice = FromRecordings(new[]
        {
            ("let's meet Shawn tomorrow", "Let's meet Sean tomorrow."),
            ("call Shawn later", "Call Sean later."),
        }, NoDictionary);
        t.Check("recordings/strong twice pre-ticked", Find(strongTwice, "Shawn", "Sean") is { Recommended: true }, Show(strongTwice));

        var weakOnce = FromRecordings(new[] { ("open the jason file", "open the json file") }, NoDictionary);
        var weakTwice = FromRecordings(new[]
        {
            ("open the jason file", "open the json file"),
            ("parse the jason please", "parse the json please"),
        }, NoDictionary);
        t.Check("recordings/weak needs two",
            weakOnce.Count == 0 && Find(weakTwice, "jason", "json") is { Recommended: true, Sightings: 2 }, Show(weakOnce.Concat(weakTwice)));

        var known = FromRecordings(new[]
        {
            ("let's meet Shawn tomorrow", "Let's meet Sean tomorrow."),
            ("call Shawn later", "Call Sean later."),
        }, new[] { Rule("Shawn", "Sean") });
        t.Check("recordings/already in dictionary", known.Count == 0, Show(known));

        // The benchmark compares FlowType (decoded with the dictionary) with
        // Wispr's transcript (without it), so it selects for "FlowType wrote
        // the user's term, Wispr wrote something else". Never a rule.
        var infusion = new[]
        {
            ("ask Enfusion now", "ask Infusion now"),
            ("open Enfusion today", "open Infusion today"),
        };
        var plainBench = FromRecordings(infusion, NoDictionary);
        var asWord = FromRecordings(infusion, new[] { Word("Enfusion") });
        var asTarget = FromRecordings(infusion, new[] { Rule("and fusion", "Enfusion") });
        t.Check("recordings/heard side is your own word",
            Find(plainBench, "Enfusion", "Infusion") is { Recommended: true }
            && Find(asWord, "Enfusion", "Infusion") == null && Find(asTarget, "Enfusion", "Infusion") == null,
            $"{Show(plainBench)} | {Show(asWord)} | {Show(asTarget)}");
    }

    // ----- VocabularyMiner -----

    private static void Vocabulary(Tally t)
    {
        var samples = new List<HistorySample>
        {
            S("I opened the Enfusion editor today."),
            S("We need Enfusion for this mod."),
            S("Open it in Workbench and check the log."),
            S("Then in Workbench we save the layout."),
            S("The Workbench crashed again."),
            S("I bought a workbench for the garage."),
            S("We should ship Flow soon."),
            S("Ask about Flow later."),
            S("The flow is broken. Fix the flow. Then the flow works."),
            S("So I was thinking. Okay let's go. Basically it works."),
            S("So that is done. Okay. Basically we are fine."),
            S("Here is the thing: Basically it works."),
            S("Another thing: Basically it is fine."),
            S("It basically works. It basically ships. It basically ends."),
            S("Ping @devon in #modding today."),
            S("Ping @devon in #modding again."),
            S("Open App.xaml.cs and fix it."),
            S("Then App.xaml.cs needs a test."),
            S("We love Reforger servers."),
            S("Most Reforger mods break."),
            S("Push it to GitHub tonight."),
            S("Check GitHub for the issue."),
            S("I made a Github account."),
            S("Only once I saw Zorblax there."),
            S("And once Quxbar showed up."),
            S("Check my_widget_id in the code."),
            S("Then my_widget_id gets reset."),
            S("Build the prefab now."),
        };
        var dictionary = new[] { Word("Reforger") };
        var misheard = new Dictionary<string, int> { ["Quxbar"] = 1, ["prefab"] = 2 };
        var got = VocabularyMiner.Mine(samples, dictionary, misheard, 900, null);
        string Names() => Show(got);

        var enfusion = Find(got, "Enfusion");
        t.Check("vocab/term used twice", enfusion is { Recommended: true, Sightings: 2, Kind: SuggestionKind.Vocabulary }
                                       && enfusion.Origin == VocabularyMiner.Origin && enfusion.Example != null, Names());
        t.Check("vocab/name kept beside a lower-case use", Find(got, "Workbench") is { Sightings: 4 }, Names());
        t.Check("vocab/ordinary word dropped (Flow vs flow)", Find(got, "Flow") == null, Names());
        t.Check("vocab/sentence starts never",
            Find(got, "So") == null && Find(got, "Okay") == null && Find(got, "Basically") == null, Names());
        t.Check("vocab/handles and files dropped",
            got.All(s => !s.Phrase.StartsWith('@') && !s.Phrase.StartsWith('#') && !s.Phrase.Contains('.')), Names());
        t.Check("vocab/already in dictionary", Find(got, "Reforger") == null, Names());
        var github = Find(got, "GitHub");
        t.Check("vocab/most frequent spelling", github is { Sightings: 3 } && github.Phrase == "GitHub", Names());
        t.Check("vocab/one dictation is not enough", Find(got, "Zorblax") == null, Names());
        var quxbar = Find(got, "Quxbar");
        t.Check("vocab/one dictation with a mishearing", quxbar is { Sightings: 1 } && quxbar.Why.Contains("misheard"), Names());
        // One dictation and one mishearing: offered, not pre-ticked. A second
        // mishearing (prefab, misheard 2×) or a second dictation is enough.
        t.Check("vocab/one dictation, one mishearing: not pre-ticked", quxbar is { Recommended: false }, Names());
        t.Check("vocab/lower-case jargon via mishearing", Find(got, "prefab") is { Sightings: 1, Recommended: true }, Names());
        var zorblaxOnce = Find(VocabularyMiner.Mine(new[] { S("Only once I saw Zorblax there.") }, NoDictionary,
            new Dictionary<string, int> { ["Zorblax"] = 1 }, 900, null), "Zorblax");
        var zorblaxTwice = Find(VocabularyMiner.Mine(new[] { S("Only once I saw Zorblax there."), S("Then Zorblax came back.") },
            NoDictionary, new Dictionary<string, int> { ["Zorblax"] = 1 }, 900, null), "Zorblax");
        t.Check("vocab/second dictation pre-ticks",
            zorblaxOnce is { Sightings: 1, Recommended: false } && zorblaxTwice is { Sightings: 2, Recommended: true },
            $"{zorblaxOnce?.Recommended}/{zorblaxTwice?.Recommended}");
        t.Check("vocab/capitalised majority keeps its capital",
            Find(got, "Workbench")?.Phrase == "Workbench", Names());
        t.Check("vocab/snake_case scored down", Find(got, "my_widget_id") is { Recommended: false, Sightings: 2 }, Names());
        t.Check("vocab/best first", got.Zip(got.Skip(1)).All(p => p.First.Score >= p.Second.Score), Names());

        // Whisper's own vocabulary: a word it already writes in one piece is
        // worth far less of the prompt.
        var tokens = VocabularyMinerTokens();
        var withModel = VocabularyMiner.Mine(samples, dictionary, misheard, 900, tokens);
        var githubKnown = Find(withModel, "GitHub");
        var enfusionNew = Find(withModel, "Enfusion");
        t.Check("vocab/known to whisper scored down",
            githubKnown is { Recommended: false } && githubKnown.Why.Contains("already knows")
            && enfusionNew is { Recommended: true } && enfusionNew.Why.Contains("doesn't know")
            && enfusionNew.Score > enfusion!.Score, Show(withModel));

        // The prompt budget: pre-ticked terms, each with ", ", fit what is left.
        var tight = VocabularyMiner.Mine(samples, dictionary, misheard, 22, null);
        var used = tight.Where(s => s.Recommended).Sum(s => s.Phrase.Length + 2);
        t.Check("vocab/fits the prompt budget", used <= 22 && tight.Any(s => s.Recommended) && tight.Any(s => !s.Recommended && s.Score >= 1),
            $"{used} chars: {Show(tight)}");
        t.Check("vocab/no budget, nothing pre-ticked", VocabularyMiner.Mine(samples, dictionary, misheard, 0, null).All(s => !s.Recommended));

        // Lower-case use only rules a word out when whisper knows the lower-case
        // word too: "flow" is one token, "workbench" is not (as in large-v3).
        var mostlyLower = new[]
        {
            S("Open it in Workbench now."), S("Then in Workbench we save."),
            S("the workbench crashed"), S("restart the workbench"), S("a workbench plugin"),
            S("We ship Flow soon."), S("Ask about Flow later."),
            S("the flow broke"), S("fix the flow"), S("a flow chart"),
        };
        var flowKnown = WhisperVocabulary.FromTokens(new[] { " flow", " Flow", " work", "bench", " W", "ork" });
        var cased = VocabularyMiner.Mine(mostlyLower, NoDictionary, null, 900, flowKnown);
        var casedNoModel = VocabularyMiner.Mine(mostlyLower, NoDictionary, null, 900, null);
        var casedMisheard = VocabularyMiner.Mine(mostlyLower, NoDictionary, new Dictionary<string, int> { ["Workbench"] = 1 }, 900, null);
        t.Check("vocab/jargon kept despite lower-case use",
            Find(cased, "Workbench") is { Sightings: 5 } && Find(cased, "Flow") == null, Show(cased));
        // …spelled the way the user mostly writes it, so the fuzzy matcher
        // does not re-case "the workbench" to "the Workbench" from then on.
        t.Check("vocab/jargon kept in the user's own casing",
            Find(cased, "Workbench")?.Phrase == "workbench"
            && Find(casedMisheard, "Workbench")?.Phrase == "workbench"
            && VocabularyMatcher.Apply("restart the workbench", new[] { "workbench" }) == "restart the workbench"
            && VocabularyMatcher.Apply("Open it in Workbench now.", new[] { "workbench" }) == "Open it in Workbench now.",
            Show(cased));

        // The same rule for a Title-case run of words: "Pull Request" in one
        // heading, "pull request" everywhere else, is ordinary English; a
        // phrase whisper does not know is kept, in lower case; a capitalised
        // majority keeps its capitals.
        var phraseTokens = WhisperVocabulary.FromTokens(new[] { " pull", " request", " Pull", " Request", " engine", " Engine" });
        var phrases = VocabularyMiner.Mine(new[]
        {
            S("please open a Pull Request now"), S("merge the pull request"), S("review the pull request"), S("close that pull request"),
            S("we use the Enfusion Engine daily"), S("the enfusion engine crashed"), S("restart the enfusion engine"), S("patch the enfusion engine"),
        }, NoDictionary, null, 900, phraseTokens);
        var titled = VocabularyMiner.Mine(new[]
        {
            S("please open a Pull Request now"), S("merge that Pull Request today"), S("close the Pull Request later"), S("review the pull request"),
        }, NoDictionary, null, 900, phraseTokens);
        t.Check("vocab/title-case phrase vs lower-case use",
            Find(phrases, "Pull Request") == null && Find(phrases, "Enfusion Engine")?.Phrase == "enfusion engine"
            && Find(titled, "Pull Request")?.Phrase == "Pull Request",
            $"{Show(phrases)} | {Show(titled)}");
        t.Check("vocab/no model: lower-case use rules out",
            Find(casedNoModel, "Workbench") == null && Find(casedNoModel, "Flow") == null
            && Find(casedMisheard, "Workbench") != null, Show(casedNoModel.Concat(casedMisheard)));

        // A lower-case word reaches the list only through a mishearing, and only
        // when whisper does not already write it in one piece.
        var jargon = new[]
        {
            S("pick a color now"), S("change the color again"), S("the color is off"),
            S("build the prefab now"), S("save the prefab first"),
        };
        var colorKnown = WhisperVocabulary.FromTokens(new[] { " color", " pre", "fab" });
        var once = new Dictionary<string, int> { ["color"] = 1, ["prefab"] = 1 };
        var twice = new Dictionary<string, int> { ["color"] = 2, ["prefab"] = 2 };
        var withTokens = VocabularyMiner.Mine(jargon, NoDictionary, once, 900, colorKnown);
        var noModelOnce = VocabularyMiner.Mine(jargon, NoDictionary, once, 900, null);
        var noModelTwice = VocabularyMiner.Mine(jargon, NoDictionary, twice, 900, null);
        t.Check("vocab/misheard word whisper knows is not jargon",
            Find(withTokens, "prefab") is { Sightings: 2, Recommended: true } && Find(withTokens, "color") == null, Show(withTokens));
        t.Check("vocab/no model: misheard twice",
            noModelOnce.Count == 0 && Find(noModelTwice, "prefab") != null && Find(noModelTwice, "color") != null,
            Show(noModelOnce.Concat(noModelTwice)));
        t.Check("vocab/no mishearing, no lower-case word", VocabularyMiner.Mine(jargon, NoDictionary, null, 900, null).Count == 0);

        // Caps: at most 80 returned, 40 pre-ticked. The names are far apart so
        // that none is a fuzzy-match neighbour of another (that is tested below).
        var many = new List<HistorySample>();
        for (var i = 0; i < 100; i++)
        {
            var name = DistinctName(i);
            many.Add(S($"we ship {name} soon"));
            many.Add(S($"then {name} works"));
        }
        var capped = VocabularyMiner.Mine(many, NoDictionary, null, 10_000, null);
        t.Check("vocab/caps", capped.Count == 80 && capped.Count(s => s.Recommended) == 40,
            $"{capped.Count} returned, {capped.Count(s => s.Recommended)} pre-ticked");

        MatcherCollisions(t);
    }

    /// <summary>"Zq" + seven consonants + "Widget": a strong-shaped name at least a few edits from any other.</summary>
    private static string DistinctName(int i)
    {
        const string consonants = "bcdfghjklmnpqrstvwxz";
        var rng = new Random(7919 + i);
        return "Zq" + new string(Enumerable.Range(0, 7).Select(_ => consonants[rng.Next(consonants.Length)]).ToArray()) + "Widget";
    }

    /// <summary>
    /// A learned word is a fuzzy-match target on every later dictation. One
    /// that would rewrite a word the user writes ("whisper" → "Wispr") is
    /// offered with a warning, never pre-ticked.
    /// </summary>
    private static void MatcherCollisions(Tally t)
    {
        // WouldRewrite must say exactly what Apply does: the run changes
        // beyond re-casing or re-spacing the term itself.
        var pairs = new (string Run, string Term, bool Expected)[]
        {
            ("the whisper model", "Wispr", true),
            ("US army faction", "Arma", true),
            ("she reforged the sword", "Reforger", true),
            ("the prefabs", "prefab", true),
            ("Claude's answer", "Claude", true),
            ("the wispr app", "Wispr", false),
            ("open work bench now", "Workbench", false),
            ("restart the workbench", "Workbench", false),
            ("we went to the garage", "Workbench", false),
            ("the in fusion engine", "Enfusion", true),
        };
        var wrong = pairs
            .Where(p => VocabularyMatcher.WouldRewrite(p.Run, p.Term) != p.Expected
                        || VocabularyMatcher.WouldRewrite(p.Run, p.Term)
                        != (VocabularyMatcher.MatchKey(VocabularyMatcher.Apply(p.Run, new[] { p.Term })) != VocabularyMatcher.MatchKey(p.Run)))
            .Select(p => $"{p.Run}/{p.Term}")
            .ToList();
        t.Check("collisions/agrees with the matcher", wrong.Count == 0, string.Join(", ", wrong));

        // The same agreement on near misses around the threshold, where the
        // index's shortcuts (length and letter-count bounds) could go wrong:
        // each term with one to three random edits, sometimes split in two,
        // sometimes after a short word.
        var seeded = new Random(2101);
        var near = new[] { "Workbench", "Enfusion", "Wispr", "Claude", "Arma Reforger", "prefab", "Kubernetes" };
        var disagree = 0;
        var rewritten = 0;
        for (var n = 0; n < 600; n++)
        {
            var term = near[n % near.Length];
            var chars = term.ToLowerInvariant().Replace(" ", "").ToList();
            for (var e = seeded.Next(1, 4); e > 0; e--)
            {
                var at = seeded.Next(chars.Count);
                switch (seeded.Next(3))
                {
                    case 0: chars[at] = (char)('a' + seeded.Next(26)); break;
                    case 1 when chars.Count > 3: chars.RemoveAt(at); break;
                    default: chars.Insert(at, (char)('a' + seeded.Next(26))); break;
                }
            }
            var word = new string(chars.ToArray());
            if (seeded.Next(3) == 0 && word.Length > 4) word = word.Insert(seeded.Next(2, word.Length - 1), " ");
            var run = seeded.Next(3) switch { 0 => "a " + word, 1 => word + " is", _ => "we saw " + word + " today" };
            var says = VocabularyMatcher.WouldRewrite(run, term);
            var does = VocabularyMatcher.MatchKey(VocabularyMatcher.Apply(run, new[] { term })) != VocabularyMatcher.MatchKey(run);
            if (says != does) disagree++;
            if (does) rewritten++;
        }
        t.Check("collisions/agrees on near misses", disagree == 0 && rewritten > 100, $"{disagree} disagree, {rewritten} of 600 rewritten");

        var wisprTexts = new List<HistorySample>
        {
            S("I dictate with Wispr daily."), S("Then Wispr pasted it."), S("We compared Wispr today."),
        };
        var clean = Find(VocabularyMiner.Mine(wisprTexts, NoDictionary, null, 900, null), "Wispr");
        var withWhisper = wisprTexts.Append(S("The whisper model is slow.")).ToList();
        var clash = Find(VocabularyMiner.Mine(withWhisper, NoDictionary, null, 900, null), "Wispr");
        var ownWord = Find(VocabularyMiner.Mine(withWhisper, new[] { Word("Whisper") }, null, 900, null), "Wispr");
        t.Check("collisions/word that would rewrite yours is not pre-ticked",
            clean is { Recommended: true } && !clean.Why.Contains(VocabularyMiner.RewritesNote)
            && clash is { Recommended: false, Sightings: 3 } && clash.Why.EndsWith(VocabularyMiner.RewritesNote),
            $"{clean?.Recommended}/{clash?.Recommended} {clash?.Why}");
        t.Check("collisions/your own dictionary word is safe",
            ownWord is { Recommended: true } && !ownWord.Why.Contains(VocabularyMiner.RewritesNote), ownWord?.Why ?? "null");

        // Two near-identical names that are both pre-ticked protect each
        // other (each run hits its own term exactly); a neighbour that is not
        // pre-ticked does not.
        var pair = Find(VocabularyMiner.Mine(new[]
        {
            S("we ship Zorvex soon"), S("then Zorvex works"), S("we ship Zorvax soon"), S("then Zorvax works"),
        }, NoDictionary, null, 900, null), "Zorvex");
        var lone = Find(VocabularyMiner.Mine(new[]
        {
            S("we ship Zorvex soon"), S("then Zorvex works"), S("once Zorvax broke"),
        }, NoDictionary, null, 900, null), "Zorvex");
        t.Check("collisions/pre-ticked neighbours protect each other",
            pair is { Recommended: true } && lone is { Recommended: false } && lone.Why.Contains(VocabularyMiner.RewritesNote),
            $"{pair?.Recommended}/{lone?.Recommended}");

        // Cost on a history-sized corpus: ~2,000 dictations, 80 candidates.
        var rng = new Random(42);
        var lexicon = Enumerable.Range(0, 600).Select(i => DistinctName(1000 + i)[2..9].ToLowerInvariant()).ToArray();
        var corpus = new List<HistorySample>();
        for (var d = 0; d < 2000; d++)
        {
            var words = Enumerable.Range(0, 40).Select(_ => lexicon[rng.Next(lexicon.Length)]).ToList();
            words[rng.Next(words.Count)] = DistinctName(d % 90);
            corpus.Add(S(string.Join(" ", words) + "."));
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var big = VocabularyMiner.Mine(corpus, NoDictionary, null, 900, null);
        var ms = clock.ElapsedMilliseconds;
        t.Check("collisions/history-sized run is quick", big.Count > 0 && ms < 15_000, $"{ms} ms");
        t.Note($"collisions/history-sized run: {corpus.Count} dictations, {big.Count} suggestions in {ms} ms");
    }

    /// <summary>A stand-in model vocabulary: " GitHub" is one token, " Enfusion" is not.</summary>
    private static WhisperVocabulary VocabularyMinerTokens() =>
        WhisperVocabulary.FromTokens(new[] { " GitHub", " Git", "Hub", " E", "n", "f", "u", "s", "i", "o", " the" });

    // ----- StyleMiner -----

    private static void Styles(Tally t)
    {
        var settings = new AppSettings();   // personal casual, work/email/other formal
        var noWispr = Array.Empty<WisprStyleChoice>();
        var when = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

        HistorySample Stop(int i, bool removed, string app = "claude") =>
            S($"Please check item {i} in the build.", removed ? $"Please check item {i} in the build" : $"Please verify item {i} in the build.", app: app);

        var removed = Repeat(9, i => Stop(i, removed: i < 7));
        var casual = StyleMiner.Mine(removed, noWispr, settings);
        var other = casual.FirstOrDefault(s => s.Phrase == StyleMiner.Other);
        t.Check("style/removed full stop → casual",
            other is { Replacement: WritingStyles.Casual, Recommended: true, Kind: SuggestionKind.Style }
            && other.Why.Contains("7 of 9") && other.Origin == StyleMiner.EditsOrigin, Show(casual));

        t.Check("style/minority not enough", StyleMiner.Mine(Repeat(9, i => Stop(i, removed: i < 2)), noWispr, settings).Count == 0);
        t.Check("style/too few edits", StyleMiner.Mine(Repeat(4, i => Stop(i, removed: true)), noWispr, settings).Count == 0);

        var rewrites = Repeat(9, i => S($"Please check item {i} in the build.", $"Please check item {i} in the build and the tests"));
        t.Check("style/rewritten ending is not punctuation", StyleMiner.Mine(rewrites, noWispr, settings).Count == 0);

        var lowered = Repeat(8, i => S($"Sounds good, item {i} is done.", i < 6 ? $"sounds good, item {i} is done" : $"Sounds great, item {i} is done."));
        t.Check("style/lowered first letter → very casual",
            StyleMiner.Mine(lowered, noWispr, settings).Any(s => s.Phrase == StyleMiner.Other && s.Replacement == WritingStyles.VeryCasual),
            Show(StyleMiner.Mine(lowered, noWispr, settings)));

        var excited = Repeat(6, i => S($"Thanks for item {i}.", i < 5 ? $"Thanks for item {i}!" : $"Thanks so much for item {i}."));
        t.Check("style/exclamation → excited",
            StyleMiner.Mine(excited, noWispr, settings).Any(s => s.Phrase == StyleMiner.Other && s.Replacement == WritingStyles.Excited));

        var already = new AppSettings { StyleOther = WritingStyles.Casual };
        t.Check("style/same as today, not offered", StyleMiner.Mine(removed, noWispr, already).Count == 0);

        var slack = StyleMiner.Mine(Repeat(9, i => Stop(i, removed: i < 7, app: "slack")), noWispr, settings);
        t.Check("style/bucket by app", slack.Count == 1 && slack[0].Phrase == StyleMiner.Work, Show(slack));

        // Wispr's settings.
        WisprStyleChoice W(string type, string style, int count) => new(type, style, count, when);
        var formalPersonal = StyleMiner.Mine(Array.Empty<HistorySample>(), new[] { W("personal", "formal", 12) }, settings);
        t.Check("style/wispr setting differs",
            formalPersonal.Count == 1 && formalPersonal[0] is { Phrase: StyleMiner.Personal, Replacement: WritingStyles.Polished }
            && formalPersonal[0].Origin == StyleMiner.WisprOrigin, Show(formalPersonal));
        t.Check("style/wispr same as today",
            StyleMiner.Mine(Array.Empty<HistorySample>(), new[] { W("personal", "casual", 12) }, settings).Count == 0);
        t.Check("style/wispr too few dictations",
            StyleMiner.Mine(Array.Empty<HistorySample>(), new[] { W("personal", "formal", 9) }, settings).Count == 0);
        var veryCasual = StyleMiner.Mine(Array.Empty<HistorySample>(),
            new[] { W("work", "very_casual", 8), W("work", "veryCasual", 7), W("work", "formal", 3) }, settings);
        t.Check("style/wispr spellings of very casual",
            veryCasual.Count == 1 && veryCasual[0] is { Phrase: StyleMiner.Work, Replacement: WritingStyles.VeryCasual }, Show(veryCasual));
        t.Check("style/wispr unknown style ignored",
            StyleMiner.Mine(Array.Empty<HistorySample>(), new[] { W("email", "pirate", 50) }, settings).Count == 0);
        var mapped = StyleMiner.Mine(Array.Empty<HistorySample>(), new[] { W("browser", "casual", 30) }, settings);
        t.Check("style/wispr other app types → everything else",
            mapped.Count == 1 && mapped[0].Phrase == StyleMiner.Other, Show(mapped));

        // Wispr's most-used style counts only while it is still the one in
        // use: 500 old Formal dictations, then a switch to Casual, is not a
        // reason to offer Formal back.
        var march = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var october = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var switchedAway = StyleMiner.Mine(Array.Empty<HistorySample>(),
            new[] { new WisprStyleChoice("personal", "formal", 500, march), new WisprStyleChoice("personal", "casual", 40, october) }, settings);
        var stillFormal = StyleMiner.Mine(Array.Empty<HistorySample>(),
            new[] { new WisprStyleChoice("personal", "formal", 500, october), new WisprStyleChoice("personal", "casual", 40, march) }, settings);
        t.Check("style/wispr switched away is not offered back",
            switchedAway.Count == 0
            && stillFormal.Count == 1 && stillFormal[0] is { Phrase: StyleMiner.Personal, Replacement: WritingStyles.Polished }
            && stillFormal[0].Why.Contains("500 of 540"),
            $"{Show(switchedAway)} | {Show(stillFormal)}");
        var switchedButEdited = StyleMiner.Mine(removed,
            new[] { new WisprStyleChoice("other", "formal", 500, march), new WisprStyleChoice("other", "very_casual", 20, october) }, settings);
        t.Check("style/edits still count after a wispr switch",
            switchedButEdited.Count == 1 && switchedButEdited[0].Origin == StyleMiner.EditsOrigin
            && switchedButEdited[0].Replacement == WritingStyles.Casual, Show(switchedButEdited));

        var disagree = StyleMiner.Mine(removed, new[] { W("other", "formal", 842) }, settings);
        t.Check("style/edits beat wispr",
            disagree.Count == 1 && disagree[0].Replacement == WritingStyles.Casual && disagree[0].Origin == StyleMiner.EditsOrigin,
            Show(disagree));

        t.Check("style/app names",
            StyleMiner.BucketOf("Discord") == StyleMiner.Personal && StyleMiner.BucketOf("outlook.exe") == StyleMiner.Email
            && StyleMiner.BucketOf("claude") == StyleMiner.Other && StyleMiner.BucketOf("") == StyleMiner.Other);
    }

    // ----- WisprDictionaryImport -----

    private static void WisprDictionary(Tally t)
    {
        var wispr = new[]
        {
            new WisprDictionaryEntry("Enfusion", "", false, "manual"),
            new WisprDictionaryEntry("claude code", "Claude Code", false, "manual"),
            new WisprDictionaryEntry("armor forger", "Arma Reforger", false, "user_edits"),
            new WisprDictionaryEntry("my email", "me@example.com", true, "manual"),
            new WisprDictionaryEntry("Enfusion", "", false, "manual"),
            new WisprDictionaryEntry("  ", "", false, "manual"),
        };
        var got = WisprDictionaryImport.Suggest(wispr, NoDictionary);
        t.Check("wispr dict/kinds",
            got.Count == 4
            && Find(got, "Enfusion")?.Kind == SuggestionKind.Vocabulary
            && Find(got, "Claude Code")?.Kind == SuggestionKind.Vocabulary
            && Find(got, "armor forger", "Arma Reforger")?.Kind == SuggestionKind.Correction
            && Find(got, "my email", "me@example.com")?.Kind == SuggestionKind.Snippet, Show(got));
        t.Check("wispr dict/all pre-ticked",
            got.All(s => s.Recommended && s.Origin == WisprDictionaryImport.Origin && s.Key == Suggestion.KeyFor(s.Kind, s.Phrase, s.Replacement)));

        var present = WisprDictionaryImport.Suggest(wispr, new[]
        {
            Word("enfusion"), Rule("Armor Forger", "Arma Reforger"), new DictionaryEntry { Phrase = "My email", Replacement = "x@y.z", IsSnippet = true },
            Rule("cloud code", "Claude Code"),
        });
        t.Check("wispr dict/skips what FlowType has", present.Count == 0, Show(present));
    }

    // ----- WhisperVocabulary -----

    /// <summary>A tiny file in the ggml layout, written to %TEMP% and deleted.</summary>
    private static string WriteGgml(Action<BinaryWriter> body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"FlowType-selftest-{Guid.NewGuid():N}.bin");
        using var stream = File.Create(path);
        using var w = new BinaryWriter(stream);
        body(w);
        return path;
    }

    private static void Header(BinaryWriter w, uint magic = 0x67676d6c, int nMel = 2, int nFft = 3)
    {
        w.Write(magic);
        foreach (var h in new[] { 51866, 1500, 1280, 20, 32, 448, 1280, 20, 32, nMel, 1 }) w.Write(h);
        w.Write(nMel);
        w.Write(nFft);
        for (var i = 0; i < nMel * nFft; i++) w.Write(0.5f);
    }

    private static void Token(BinaryWriter w, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        w.Write(bytes.Length);
        w.Write(bytes);
    }

    private static void WhisperVocabularyFile(Tally t)
    {
        var files = new List<string>();
        try
        {
            var good = WriteGgml(w =>
            {
                Header(w);
                var tokens = new[] { " the", " Git", "Hub", " GitHub", "s", " café" };
                w.Write(tokens.Length);
                foreach (var tok in tokens) Token(w, tok);
                w.Write(new byte[64]);   // stands in for the weights
            });
            files.Add(good);
            var v = WhisperVocabulary.TryLoad(good);
            t.Check("whisper vocab/parse",
                v is { Count: 6 } && v.IsSingleToken("the") && v.IsSingleToken("GitHub") && !v.IsSingleToken("Hub")
                && !v.IsSingleToken("github") && v.IsSingleToken("café"));
            t.Check("whisper vocab/estimate",
                v != null && v.EstimateTokens("GitHub") == 1 && v.EstimateTokens("GitHubs") == 2 && v.EstimateTokens("xyz") == 4);
            t.Check("whisper vocab/cached per file", v != null && ReferenceEquals(v, WhisperVocabulary.TryLoad(good)));

            var badMagic = WriteGgml(w => { Header(w, magic: 0x12345678); w.Write(1); Token(w, " the"); });
            var truncated = WriteGgml(w => { Header(w); w.Write(3); Token(w, " the"); w.Write(10); w.Write((byte)'x'); });
            var hugeToken = WriteGgml(w => { Header(w); w.Write(1); w.Write(5000); w.Write(new byte[5000]); });
            var hugeCount = WriteGgml(w => { Header(w); w.Write(200_000); Token(w, " the"); });
            var badMel = WriteGgml(w =>
            {
                w.Write(0x67676d6cu);
                foreach (var h in new[] { 51866, 1500, 1280, 20, 32, 448, 1280, 20, 32, 128, 1 }) w.Write(h);
                w.Write(100_000);
                w.Write(201);
                w.Write(new byte[64]);
            });
            files.AddRange(new[] { badMagic, truncated, hugeToken, hugeCount, badMel });
            t.Check("whisper vocab/rejects corrupt files",
                WhisperVocabulary.TryLoad(badMagic) == null && WhisperVocabulary.TryLoad(truncated) == null
                && WhisperVocabulary.TryLoad(hugeToken) == null && WhisperVocabulary.TryLoad(hugeCount) == null
                && WhisperVocabulary.TryLoad(badMel) == null
                && WhisperVocabulary.TryLoad(Path.Combine(Path.GetTempPath(), $"FlowType-missing-{Guid.NewGuid():N}.bin")) == null
                && WhisperVocabulary.TryLoad("") == null);
        }
        finally
        {
            foreach (var f in files)
            {
                try { File.Delete(f); } catch { /* temp file; Windows cleans %TEMP% */ }
            }
        }
    }

    /// <summary>Every installed model: " the" is one token, " Reforger" is not. Read-only.</summary>
    private static void InstalledModels(Tally t)
    {
        var installed = ModelCatalog.All.Where(m => m.IsDownloaded).ToList();
        if (installed.Count == 0)
        {
            t.Note("whisper vocab/installed models: skipped (no model installed)");
            return;
        }
        foreach (var model in installed)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var v = WhisperVocabulary.TryLoad(model.FilePath);
            var ms = clock.ElapsedMilliseconds;
            t.Check($"whisper vocab/{model.Id}",
                v != null && v.Count > 50_000 && v.IsSingleToken("the") && v.IsSingleToken("GitHub")
                && !v.IsSingleToken("Reforger") && !v.IsSingleToken("Enfusion") && v.EstimateTokens("Reforger") >= 2,
                v == null ? "not readable" : $"{v.Count} tokens");
            t.Note($"whisper vocab/{model.Id}: {v?.Count ?? 0} tokens read in {ms} ms");
        }
        t.Check("whisper vocab/current model", WhisperVocabulary.ForCurrentModel() != null);
    }
}
