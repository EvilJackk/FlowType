using System.IO;
using FlowType.Core;
using FlowType.Session;

namespace FlowType.Learning;

/// <summary>
/// The "does it learn by itself?" half of the feature: about once a day,
/// while nothing is being dictated, the text-only part of
/// <see cref="HistoryLearner"/> reruns over the histories the user already
/// allowed and leaves new suggestions waiting for review (the Insights dot).
///
/// It checks every 30 minutes (the first time 10 minutes after start-up, so
/// it never competes with the model warm-up) and runs only when all of these
/// hold: <see cref="AppSettings.LearnInBackground"/> is on, no run is in
/// progress, the last run of either kind was at least 24 hours ago, the user
/// is not dictating, and a history has changed since the last run — no new
/// writing, nothing new to learn. Wispr Flow is read only when
/// <see cref="AppSettings.LearnFromWispr"/> says the user chose it before. It
/// never runs the benchmark: decoding half an hour of audio is the user's
/// call, not a timer's.
///
/// With <see cref="AppSettings.AutoAddLearnedWords"/> on, the recommended
/// <i>words</i> are added straight away (tagged, undoable) and the flow bar
/// says so; rules and styles still wait for review, because a wrong rule
/// changes every future dictation. A word the user removed again — by "Undo
/// everything learned" or by deleting it — is remembered as dismissed
/// (<see cref="HistoryLearner.UndoAll"/>, <see cref="HistoryLearner.Forget"/>),
/// so it is never quietly added back.
///
/// Never throws: everything it does is optional, and a timer callback that
/// throws would take the process down.
/// </summary>
public static class BackgroundLearner
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MinimumGap = TimeSpan.FromHours(24);

    private static readonly object Gate = new();
    private static System.Threading.Timer? _timer;
    private static CancellationTokenSource? _cts;
    private static int _checking;

    /// <summary>Start the daily check. Safe to call twice, from any thread.</summary>
    public static void Start()
    {
        try
        {
            lock (Gate)
            {
                if (_timer != null) return;
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _timer = new System.Threading.Timer(_ => _ = CheckAsync(token), null, FirstCheck, CheckEvery);
            }
        }
        catch
        {
            // No daily run this session; the button still works.
        }
    }

    /// <summary>Stop checking and cancel a run in progress (app exit).</summary>
    public static void Stop()
    {
        try
        {
            lock (Gate)
            {
                _timer?.Dispose();
                _timer = null;
                // Cancelled, not disposed: a run in flight may still be
                // registering on its token, and the process is ending anyway.
                _cts?.Cancel();
                _cts = null;
            }
        }
        catch
        {
        }
    }

    private static async Task CheckAsync(CancellationToken ct)
    {
        // Timer callbacks can overlap if a run outlasts the interval.
        if (Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            var settings = SettingsStore.Instance.Settings;
            if (!ShouldRun(settings)) return;

            var request = new LearningRequest(
                UseFlowType: true, UseWispr: settings.LearnFromWispr, RunBench: false, Background: true);
            LearningReport report;
            try
            {
                report = await HistoryLearner.Instance.RunAsync(request, null, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                return;   // the user started one by hand in the meantime
            }

            if (settings.AutoAddLearnedWords && !ct.IsCancellationRequested)
            {
                var words = report.OfKind(SuggestionKind.Vocabulary).Where(s => s.Recommended).ToList();
                if (words.Count > 0) OnUiThread(() => AddWords(words));
            }
        }
        catch
        {
            // Cancelled at exit, or a source failed: try again next check.
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    /// <summary>The cheap conditions first; counting a history touches the disk.</summary>
    private static bool ShouldRun(AppSettings settings)
    {
        if (!settings.LearnInBackground) return false;
        var learner = HistoryLearner.Instance;
        if (learner.IsRunning) return false;
        if (learner.LastRunUtc is { } last && DateTime.UtcNow - last < MinimumGap) return false;
        if (DictationSession.Instance.State != SessionState.Idle) return false;
        return HasSomethingNew(learner, settings);
    }

    /// <summary>
    /// A history changed since the last run that read it. A count that went
    /// down also counts — deleted notes change what is worth suggesting too.
    /// FlowType's notes are read (they are already in memory; nothing is kept)
    /// because its count stops moving at History's 1000-note cap, where every
    /// new note pushes the oldest out: a newer newest note is new writing as
    /// well. Wispr Flow's rows are only counted; they are never pruned.
    /// </summary>
    private static bool HasSomethingNew(HistoryLearner learner, AppSettings settings)
    {
        var (lastFlowType, lastWispr) = learner.LastSourceCounts;
        try
        {
            var samples = FlowTypeHistory.ReadSamples();
            var available = samples.Count > 0 || File.Exists(AppPaths.NotesFile);
            if (available && FlowTypeChanged(samples.Count, HistoryLearner.NewestOf(samples, HistorySource.FlowType),
                    lastFlowType, learner.LastFlowTypeNewestUtc))
            {
                return true;
            }
        }
        catch
        {
            // FlowType's history couldn't be read just now: Wispr may still have news.
        }
        if (!settings.LearnFromWispr) return false;
        var wispr = WisprHistory.Summarize();
        return wispr.Available && wispr.Dictations != lastWispr;
    }

    /// <summary>
    /// FlowType's half of <see cref="HasSomethingNew"/> (pure, for --selftest):
    /// the count changed, or the newest dictation is not the one the last run
    /// saw.
    /// </summary>
    internal static bool FlowTypeChanged(int count, DateTime? newestUtc, int lastCount, DateTime? lastNewestUtc) =>
        count != lastCount || newestUtc != lastNewestUtc;

    /// <summary>UI thread: DictionaryStore raises Changed synchronously, and its subscribers are pages.</summary>
    private static void AddWords(IReadOnlyList<Suggestion> words)
    {
        try
        {
            var result = HistoryLearner.Instance.ApplyAutomatically(words);
            if (result.Words > 0)
            {
                DictationSession.Instance.Notify(
                    $"Learned {result.Words} new {(result.Words == 1 ? "word" : "words")} from your writing");
            }
        }
        catch
        {
            // The words stay in the report for the user to review instead.
        }
    }

    private static void OnUiThread(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) action();
        else dispatcher.BeginInvoke(action);
    }
}
