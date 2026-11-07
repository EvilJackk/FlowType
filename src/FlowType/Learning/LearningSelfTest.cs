using System.Text;

namespace FlowType.Learning;

/// <summary>
/// Every --selftest block of "Learn from your writing" (2.1), called from
/// <c>Diagnostics/V2SelfTest</c>: the history readers, the miners, the
/// engine and the review window's pure logic. Each part is built and tested on its own, so each reports its own
/// lines; this only runs them in order and keeps one part's exception from
/// hiding the others' results.
/// </summary>
internal static class LearningSelfTest
{
    public static bool Run(StringBuilder log)
    {
        var sources = Guarded("sources", l => SourcesSelfTest.Run(l), log);
        var miners = Guarded("miners", l => MinersSelfTest.Run(l), log);
        var engine = Guarded("engine", l => EngineSelfTest.Run(l), log);
        var window = Guarded("review window", l => FlowType.UI.LearnWindow.SelfTest(l), log);
        var ok = sources && miners && engine && window;
        log.AppendLine($"learning from history {(ok ? "PASS" : "FAIL")}: sources={sources} miners={miners} engine={engine} window={window}");
        return ok;
    }

    /// <summary>
    /// Run one part; an exception is a failure of that part, logged by type
    /// only (a message could quote test data, and the shape of the log should
    /// not depend on it).
    /// </summary>
    private static bool Guarded(string part, Func<StringBuilder, bool> run, StringBuilder log)
    {
        try
        {
            return run(log);
        }
        catch (Exception ex)
        {
            log.AppendLine($"learning {part} FAIL: threw {ex.GetType().Name}");
            return false;
        }
    }
}
