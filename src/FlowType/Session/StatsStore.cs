using System.IO;
using System.Text.Json;
using FlowType.Core;

namespace FlowType.Session;

public class StatsData
{
    public long TotalWords { get; set; }
    public long TotalUtterances { get; set; }
    public double TotalSpeakingSeconds { get; set; }
    public Dictionary<string, long> WordsPerApp { get; set; } = new();
    public Dictionary<string, long> WordsPerDay { get; set; } = new();  // "yyyy-MM-dd"

    /// <summary>Filler words, mis-heard names and mangled symbols FlowType put right.</summary>
    public long WordsCorrected { get; set; }

    /// <summary>Times one of the user's own dictionary rules fired.</summary>
    public long DictionaryFixes { get; set; }

    /// <summary>Paragraph breaks and list items the formatter added.</summary>
    public long FormattingFixes { get; set; }

    /// <summary>Best speaking pace over a single dictation of 20 words or more.</summary>
    public int BestWpm { get; set; }
}

/// <summary>
/// Lifetime dictation stats for the Home dashboard — words, speaking pace,
/// day streak, top apps (Wispr Flow's usage dashboard, local edition).
/// </summary>
public sealed class StatsStore
{
    public static StatsStore Instance { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly StatsData _data;

    public event Action? Changed;

    private StatsStore()
    {
        _data = Load();
    }

    public void Record(int words, double seconds, string appName,
        Engine.FormatterReport? report = null)
    {
        lock (_gate)
        {
            _data.TotalWords += words;
            if (report != null)
            {
                _data.WordsCorrected += report.WordsCorrected;
                _data.DictionaryFixes += report.DictionaryFixes;
                _data.FormattingFixes += report.ParagraphBreaks + report.ListItems;
            }
            // Only from takes long enough for the number to mean something.
            if (words >= 20 && seconds >= 1)
            {
                var wpm = (int)Math.Round(words / (seconds / 60.0));
                if (wpm > _data.BestWpm && wpm < 400) _data.BestWpm = wpm;
            }
            _data.TotalUtterances += 1;
            _data.TotalSpeakingSeconds += seconds;

            if (!string.IsNullOrEmpty(appName))
            {
                _data.WordsPerApp[appName] =
                    _data.WordsPerApp.GetValueOrDefault(appName) + words;
            }
            var day = DateTime.Now.ToString("yyyy-MM-dd");
            _data.WordsPerDay[day] = _data.WordsPerDay.GetValueOrDefault(day) + words;

            Save();
        }
        Changed?.Invoke();
    }

    public long TotalWords { get { lock (_gate) return _data.TotalWords; } }
    public long TotalUtterances { get { lock (_gate) return _data.TotalUtterances; } }
    public long WordsCorrected { get { lock (_gate) return _data.WordsCorrected; } }
    public long DictionaryFixes { get { lock (_gate) return _data.DictionaryFixes; } }
    public long FormattingFixes { get { lock (_gate) return _data.FormattingFixes; } }
    public long TotalFixes => WordsCorrected + DictionaryFixes + FormattingFixes;
    public int BestWpm { get { lock (_gate) return _data.BestWpm; } }
    public double TotalSpeakingSeconds { get { lock (_gate) return _data.TotalSpeakingSeconds; } }

    /// <summary>Words dictated per day, for the activity grid.</summary>
    public IReadOnlyDictionary<string, long> WordsPerDay
    {
        get { lock (_gate) return new Dictionary<string, long>(_data.WordsPerDay); }
    }

    /// <summary>Words per app, most-used first.</summary>
    public IReadOnlyList<(string App, long Words)> AppUsage
    {
        get
        {
            lock (_gate)
            {
                return _data.WordsPerApp
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => (kv.Key, kv.Value))
                    .ToList();
            }
        }
    }

    /// <summary>Words per category, most-used first, with the app count per category.</summary>
    public IReadOnlyList<(AppKind Kind, long Words, int Apps)> CategoryUsage
    {
        get
        {
            lock (_gate)
            {
                return _data.WordsPerApp
                    .GroupBy(kv => AppCategory.Classify(kv.Key))
                    .Select(g => (g.Key, g.Sum(kv => kv.Value), g.Count()))
                    .OrderByDescending(t => t.Item2)
                    .ToList();
            }
        }
    }

    /// <summary>The longest run of consecutive days ever dictated on.</summary>
    public int LongestStreakDays
    {
        get
        {
            lock (_gate)
            {
                var days = _data.WordsPerDay.Keys
                    .Select(k => DateTime.TryParse(k, out var d) ? d.Date : (DateTime?)null)
                    .Where(d => d != null).Select(d => d!.Value)
                    .Distinct().OrderBy(d => d).ToList();
                if (days.Count == 0) return 0;

                var best = 1;
                var run = 1;
                for (var i = 1; i < days.Count; i++)
                {
                    run = (days[i] - days[i - 1]).TotalDays == 1 ? run + 1 : 1;
                    if (run > best) best = run;
                }
                return best;
            }
        }
    }

    /// <summary>
    /// How much faster this is than typing. 40 wpm is the widely quoted average
    /// for an adult on a full keyboard — a real number the user can check,
    /// rather than a percentile against a population FlowType cannot see.
    /// </summary>
    public const int AverageTypingWpm = 40;

    /// <summary>Average speaking pace in words per minute (0 when unused).</summary>
    public int AverageWpm
    {
        get
        {
            lock (_gate)
            {
                return _data.TotalSpeakingSeconds < 1 ? 0
                    : (int)Math.Round(_data.TotalWords / (_data.TotalSpeakingSeconds / 60.0));
            }
        }
    }

    /// <summary>Consecutive days (ending today or yesterday) with at least one dictation.</summary>
    public int StreakDays
    {
        get
        {
            lock (_gate)
            {
                var streak = 0;
                var day = DateTime.Today;
                if (!_data.WordsPerDay.ContainsKey(day.ToString("yyyy-MM-dd")))
                {
                    day = day.AddDays(-1);  // today untouched yet — streak still alive
                }
                while (_data.WordsPerDay.ContainsKey(day.ToString("yyyy-MM-dd")))
                {
                    streak++;
                    day = day.AddDays(-1);
                }
                return streak;
            }
        }
    }

    public (string App, long Words)? TopApp
    {
        get
        {
            lock (_gate)
            {
                if (_data.WordsPerApp.Count == 0) return null;
                var top = _data.WordsPerApp.OrderByDescending(kv => kv.Value).First();
                return (top.Key, top.Value);
            }
        }
    }

    private void Save() => JsonFile.Write(AppPaths.StatsFile, _data);

    private static StatsData Load() => JsonFile.Read<StatsData>(AppPaths.StatsFile) ?? new();
}
