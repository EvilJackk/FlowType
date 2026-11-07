using FlowType.Learning;

namespace FlowType.UI.Controls;

/// <summary>
/// Made-up "Learn from your writing" data for the hidden <c>--snapshot</c>
/// diagnostic. The review window and the Insights card are drawn from this,
/// never from the user's history, so the PNGs can be shared and looked at
/// without exposing a single word anyone dictated — and so every state can be
/// drawn on a machine that has no history at all.
///
/// It is deliberately awkward: a section long enough to need "Show more",
/// a term with no break opportunity, a multi-line snippet, a style row, a
/// benchmark, notes, and benchmark fixes that are also suggestions (the window
/// reads only the suggestions, so each shows once).
/// </summary>
internal static class LearnSamples
{
    public const string WisprDictionary = "Wispr Flow dictionary";

    public static IReadOnlyList<SourceSummary> Sources(bool wisprFound = true) => new[]
    {
        new SourceSummary(HistorySource.FlowType, true, 312, 9, 290, 0, null,
            @"C:\Users\you\AppData\Roaming\FlowType\notes.json"),
        wisprFound
            ? new SourceSummary(HistorySource.WisprFlow, true, 863, 21, 1932, 246, null,
                @"C:\Users\you\AppData\Roaming\Wispr Flow\flow.sqlite")
            : new SourceSummary(HistorySource.WisprFlow, false, 0, 0, 0, 0, "Not found on this PC",
                @"C:\Users\you\AppData\Roaming\Wispr Flow\flow.sqlite"),
    };

    public static LearningReport Report()
    {
        var list = new List<Suggestion>();

        (string Term, bool Recommended, string Why)[] words =
        {
            ("Reforger", true, "Used in 61 dictations · Wispr misheard it 9×"),
            ("Enfusion", true, "Used in 24 dictations · Wispr misheard it 4×"),
            ("Workbench", true, "Used in 19 dictations · Whisper doesn't know this word"),
            ("Claude Code", true, "Used in 17 dictations"),
            ("prefab", true, "Used in 15 dictations · Whisper doesn't know this word"),
            ("RplComponent", true, "Used in 11 dictations · Wispr misheard it 3×"),
            ("SCR_AIGroupUtilityComponentWithAnExtremelyLongNameThatHasNoSpacesAtAll", true,
                "Used in 4 dictations · Whisper doesn't know this word"),
            ("ScenarioFramework", true, "Used in 9 dictations"),
            ("Arma Reforger Tools", true, "Used in 8 dictations"),
            ("Wispr", true, "Used in 8 dictations · Whisper doesn't know this word"),
            ("replication", true, "Used in 7 dictations"),
            ("GameMode", true, "Used in 7 dictations"),
            ("EntitySpawnParams", true, "Used in 6 dictations"),
            ("Conflict", true, "Used in 6 dictations"),
            ("Everon", false, "Used in 3 dictations"),
            ("Arland", false, "Used in 3 dictations"),
            ("BaseGameMode", false, "Used in 2 dictations"),
            ("WorldEditor", false, "Used in 2 dictations"),
            ("ResourceManager", false, "Used in 2 dictations"),
            ("Kolguyev", false, "Used in 2 dictations"),
            ("load order", false, "Used in 2 dictations"),
            ("Steam Workshop", false, "Used in 2 dictations"),
        };
        for (var i = 0; i < words.Length; i++)
        {
            var (term, rec, why) = words[i];
            list.Add(new Suggestion(SuggestionKind.Vocabulary, Suggestion.KeyFor(SuggestionKind.Vocabulary, term),
                term, "", 30 - i, 6.0 - i * 0.2, rec, why, "your writing",
                $"…so the {term} side of it needs a second look before we ship the next build…"));
        }

        void Fix(string heard, string write, int seen, bool rec, string why, string origin) =>
            list.Add(new Suggestion(SuggestionKind.Correction, Suggestion.KeyFor(SuggestionKind.Correction, heard, write),
                heard, write, seen, seen, rec, why, origin, $"…open it in {write} and check the prefab…"));

        Fix("armor forger", "Arma Reforger", 7, true, "You corrected this in 7 dictations", "your edits");
        Fix("work bench", "Workbench", 5, true, "Wispr's recogniser wrote it this way 5×; the final text fixed it", "Wispr's recogniser");
        Fix("and fusion", "Enfusion", 3, true, "FlowType misheard it in 3 of your recordings", "your recordings");
        Fix("clawed", "Claude", 4, true, "You corrected this in 4 dictations", "your edits");
        Fix("get hub", "GitHub", 2, false, "You corrected this in 2 dictations", "your edits");
        Fix("replication component rpl", "RplComponent", 2, false, "Wispr's recogniser wrote it this way 2×", "Wispr's recogniser");

        list.Add(new Suggestion(SuggestionKind.Vocabulary, Suggestion.KeyFor(SuggestionKind.Vocabulary, "FlowType"),
            "FlowType", "", 1, 1, true, "In your Wispr Flow dictionary", WisprDictionary));
        list.Add(new Suggestion(SuggestionKind.Correction, Suggestion.KeyFor(SuggestionKind.Correction, "flow type", "FlowType"),
            "flow type", "FlowType", 1, 1, true, "In your Wispr Flow dictionary", WisprDictionary));
        list.Add(new Suggestion(SuggestionKind.Snippet, Suggestion.KeyFor(SuggestionKind.Snippet, "sign off"),
            "sign off",
            "Best regards,\nSam — sent by voice with FlowType. This is a deliberately long snippet, so the review "
            + "window has to shorten it rather than let it run off the edge of the row and push the checkbox away.",
            1, 1, true, "A snippet in your Wispr Flow dictionary", WisprDictionary));
        list.Add(new Suggestion(SuggestionKind.Snippet, Suggestion.KeyFor(SuggestionKind.Snippet, "my calendar link"),
            "my calendar link", "https://cal.example.com/sam/30-minutes", 1, 1, true,
            "A snippet in your Wispr Flow dictionary", WisprDictionary));

        list.Add(new Suggestion(SuggestionKind.Style, Suggestion.KeyFor(SuggestionKind.Style, "work", "casual"),
            "work", "casual", 412, 2, true, "Wispr Flow wrote 412 of your work messages in Casual", "Wispr Flow settings"));
        list.Add(new Suggestion(SuggestionKind.Style, Suggestion.KeyFor(SuggestionKind.Style, "other", "very-casual"),
            "other", "very-casual", 9, 1.5, true, "You deleted the final full stop in 7 of 9 edits", "your edits"));
        list.Add(new Suggestion(SuggestionKind.Style, Suggestion.KeyFor(SuggestionKind.Style, "personal", "excited"),
            "personal", "excited", 11, 0.8, false, "Wispr Flow wrote 11 of your personal messages in Excited",
            "Wispr Flow settings"));

        // The benchmark's fixes, as HistoryLearner hands them over: each one is
        // also merged into the suggestions (the window shows only those), so
        // "pre fab" is added to the list here too — the same shape a real run has.
        var preFab = new Suggestion(SuggestionKind.Correction, Suggestion.KeyFor(SuggestionKind.Correction, "pre fab", "prefab"),
            "pre fab", "prefab", 3, 3, true, "FlowType misheard it in 3 of your recordings", "your recordings");
        list.Insert(list.FindIndex(s => s.Phrase == "get hub"), preFab); // with the other pre-ticked fixes
        var benchFixes = new[] { list.First(s => s.Phrase == "and fusion"), preFab };
        var bench = new BenchResult(21, 1932, 4870, 0.912, 0.931, 30, 12, 27, 54, 3, 0.05, "large-v3-turbo",
            DateTime.UtcNow, benchFixes);

        return new LearningReport(DateTime.UtcNow.AddMinutes(-2), Sources(), 1175, list, bench,
            TimeSpan.FromSeconds(4.2), new[]
            {
                "Wispr Flow's history was busy for a moment and was read on the second try.",
                "2 recordings couldn't be played back and were skipped.",
            });
    }

    /// <summary>A run that found nothing — no suggestions, no benchmark.</summary>
    public static LearningReport EmptyReport() => new(DateTime.UtcNow.AddMinutes(-1), Sources(), 1175,
        Array.Empty<Suggestion>(), null, TimeSpan.FromSeconds(3.1), Array.Empty<string>());
}
