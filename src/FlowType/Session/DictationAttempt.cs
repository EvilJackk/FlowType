using System.IO;
using System.Text.Json;
using FlowType.Audio;
using FlowType.Core;
using FlowType.Engine;

namespace FlowType.Session;

/// <summary>How a dictation attempt ended.</summary>
public enum AttemptOutcome
{
    /// <summary>The default, deliberately: a path that forgets to set an
    /// outcome shows up as visibly wrong rather than invisibly absent.</summary>
    AbortedBeforeResult,
    TooShort,
    NoSpeech,
    NoInput,
    Canceled,
    Failed,
    DeviceTimeout,
    Inserted,
    Copied,
    NothingToType,
}

/// <summary>
/// One line per dictation, appended to %APPDATA%\FlowType\attempts.log.
///
/// FlowType had no runtime log at all: the only diagnostics were one-shot CLI
/// files, so the paths that most need counting — a cancel, a take dropped for
/// being too short, an exception in the pipeline — left no trace. Every
/// threshold in the audio gate is ultimately a judgement call about real
/// microphones, and this is the record that makes the next adjustment a
/// measurement instead of another guess.
///
/// Deliberately **measurements only, never transcript text** — lengths and word
/// counts, so the file stays safe to paste into a bug report and never has to
/// be scrubbed before a release.
///
/// Constructed with <c>using</c>, so the line is written on every exit path
/// including an exception or a cancellation.
/// </summary>
public sealed class DictationAttempt : IDisposable
{
    /// <summary>Roll the file at roughly this size; one line is ~250 bytes.</summary>
    private const long MaxBytes = 5 * 1024 * 1024;

    private static readonly object FileGate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly bool _enabled;

    public AttemptOutcome Outcome { get; set; } = AttemptOutcome.AbortedBeforeResult;
    public double AudioSeconds { get; set; }
    public TakeOutcome TakeOutcome { get; set; } = TakeOutcome.Ok;
    public TakeVerdict Verdict { get; set; } = TakeVerdict.Uncertain;
    public TakeMetrics Metrics { get; set; }
    public string ModelId { get; set; } = "";
    public string Runtime { get; set; } = "";
    public string? Language { get; set; }
    public bool Accurate { get; set; }
    public long DecodeMs { get; set; }
    public int RawLength { get; set; }
    public int FinalLength { get; set; }
    public int WordCount { get; set; }
    public string AppName { get; set; } = "";

    public DictationAttempt()
    {
        _enabled = SettingsStore.Instance.Settings.DiagnosticLog;
    }

    public void Dispose()
    {
        if (!_enabled) return;
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                t = _startedUtc.ToString("o"),
                ms = (long)(DateTime.UtcNow - _startedUtc).TotalMilliseconds,
                outcome = Outcome.ToString(),
                audio_s = Math.Round(AudioSeconds, 2),
                take = TakeOutcome.ToString(),
                verdict = Verdict.ToString(),
                rms = Round(Metrics.Rms),
                peak = Round(Metrics.Peak),
                ms_above_floor = Metrics.MsAboveVoiceFloor,
                sustained = Metrics.SustainedVoice,
                modulated = Metrics.SpeechLikeModulation,
                gain = Round(Metrics.AppliedGain),
                trimmed_ms = Metrics.TrimmedMs,
                model = ModelId,
                runtime = Runtime,
                lang = Language,
                accurate = Accurate,
                decode_ms = DecodeMs,
                raw_len = RawLength,
                final_len = FinalLength,
                words = WordCount,
                app = AppName,
            }, JsonOptions);

            lock (FileGate)
            {
                AppPaths.EnsureCreated();
                var path = AppPaths.AttemptLog;
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    // One generation back is enough to cover a session.
                    var previous = path + ".1";
                    try { File.Delete(previous); } catch { }
                    try { File.Move(path, previous); } catch { }
                }
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Diagnostics must never be able to break a dictation.
        }
    }

    private static double Round(double value) =>
        double.IsFinite(value) ? Math.Round(value, 5) : 0;
}
