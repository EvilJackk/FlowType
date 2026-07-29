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

    public void Record(int words, double seconds, string appName)
    {
        lock (_gate)
        {
            _data.TotalWords += words;
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
