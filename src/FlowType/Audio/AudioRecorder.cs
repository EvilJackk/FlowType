using System.IO;
using FlowType.Core;
using NAudio.Wave;

namespace FlowType.Audio;

/// <summary>How a capture ended.</summary>
public enum TakeOutcome
{
    /// <summary>Stopped cleanly.</summary>
    Ok,

    /// <summary>The device faulted, but the audio up to that point was written.</summary>
    RecoverableDeviceError,

    /// <summary>The device never reported it had stopped; the file may be short.</summary>
    Unfinalized,
}

public readonly record struct RecordedTake(string Path, double DurationSeconds, TakeOutcome Outcome);

/// <summary>
/// Microphone capture at 16 kHz mono 16-bit (whisper.cpp's native format).
/// Two modes: recording (writes a WAV, auto-stops at the cap) and monitoring
/// (levels only, for the mic test meter). <see cref="LevelChanged"/> fires from
/// the capture thread with a 0..1 peak for the waveform UI.
/// </summary>
public sealed class AudioRecorder
{
    /// <summary>Hands-free sessions auto-stop here so a forgotten mic doesn't run forever.</summary>
    public static TimeSpan MaxDuration => TimeSpan.FromMinutes(
        Math.Clamp(SettingsStore.Instance.Settings.MaxRecordingMinutes, 1, 60));

    public static AudioRecorder Instance { get; } = new();

    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private string? _currentFilePath;
    private TaskCompletionSource<RecordedTake?>? _stopTcs;
    private bool _discardOnStop;
    private bool _monitorOnly;
    private bool _autoStopRaised;
    private readonly object _gate = new();

    public bool IsRecording { get; private set; }
    public bool IsMonitoring { get; private set; }

    /// <summary>Peak input level 0..1 (capture thread).</summary>
    public event Action<float>? LevelChanged;

    /// <summary>The 5-minute cap was hit; the session should stop-and-transcribe.</summary>
    public event Action? AutoStopRequested;

    private AudioRecorder() { }

    // ----- Recording -----

    public void StartRecording()
    {
        lock (_gate)
        {
            StopMonitorLocked();
            if (IsRecording) return;

            AppPaths.EnsureCreated();
            _currentFilePath = Path.Combine(
                AppPaths.RecordingsDir, $"take-{DateTime.Now:yyyyMMdd-HHmmss-fff}.wav");

            _waveIn = CreateWaveIn();
            _writer = new WaveFileWriter(_currentFilePath, _waveIn.WaveFormat);
            _discardOnStop = false;
            _monitorOnly = false;
            _autoStopRaised = false;
            _stopTcs = null;

            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;
            _waveIn.StartRecording();
            IsRecording = true;
        }
    }

    /// <summary>Stop and return the WAV path + duration (null if discarded).</summary>
    public Task<RecordedTake?> StopAsync()
    {
        lock (_gate)
        {
            if (!IsRecording || _waveIn == null)
            {
                return Task.FromResult<RecordedTake?>(null);
            }
            _stopTcs = new TaskCompletionSource<RecordedTake?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            IsRecording = false;
            _waveIn.StopRecording();
            return _stopTcs.Task;
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            if (!IsRecording || _waveIn == null) return;
            _discardOnStop = true;
            IsRecording = false;
            _waveIn.StopRecording();
        }
    }

    // ----- Monitoring (mic test) -----

    public void StartMonitor()
    {
        lock (_gate)
        {
            // _stopTcs != null means a take is still draining: IsRecording is
            // already false but RecordingStopped has not fired yet. Opening the
            // monitor in that window used to replace _waveIn and swallow the
            // pending result, leaving the session in Processing forever.
            if (IsRecording || IsMonitoring || _stopTcs != null) return;
            _waveIn = CreateWaveIn();
            _monitorOnly = true;
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.RecordingStopped += OnRecordingStopped;
            _waveIn.StartRecording();
            IsMonitoring = true;
        }
    }

    public void StopMonitor()
    {
        lock (_gate) StopMonitorLocked();
    }

    private void StopMonitorLocked()
    {
        if (!IsMonitoring || _waveIn == null) return;
        IsMonitoring = false;
        _waveIn.StopRecording();
    }

    // ----- Plumbing -----

    private static WaveInEvent CreateWaveIn() => new()
    {
        DeviceNumber = ResolveDeviceIndex(SettingsStore.Instance.Settings.MicDeviceName),
        WaveFormat = new WaveFormat(16000, 16, 1),
        BufferMilliseconds = 50,
    };

    /// <summary>Faults swallowed inside the capture callback; surfaced by --selftest.</summary>
    public int CallbackFaults => _callbackFaults;
    private int _callbackFaults;

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        // An exception escaping the capture callback tears down the capture
        // loop, and the take goes with it. A full disk or a throwing subscriber
        // must cost one buffer, not the whole dictation.
        try
        {
            WriteBuffer(e);
        }
        catch
        {
            Interlocked.Increment(ref _callbackFaults);
        }
    }

    private void WriteBuffer(WaveInEventArgs e)
    {
        WaveFileWriter? writer;
        bool shouldAutoStop = false;
        lock (_gate)
        {
            writer = _monitorOnly ? null : _writer;
            if (writer != null)
            {
                writer.Write(e.Buffer, 0, e.BytesRecorded);
                if (!_autoStopRaised && writer.TotalTime > MaxDuration)
                {
                    _autoStopRaised = true;
                    shouldAutoStop = true;
                }
            }
        }

        float max = 0;
        for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
        {
            var sample = Math.Abs((int)BitConverter.ToInt16(e.Buffer, i));
            if (sample > max) max = sample;
        }
        LevelChanged?.Invoke(max / 32768f);

        if (shouldAutoStop) AutoStopRequested?.Invoke();
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        string? path;
        double duration;
        bool discard, monitor;
        TaskCompletionSource<RecordedTake?>? tcs;

        lock (_gate)
        {
            duration = _writer?.TotalTime.TotalSeconds ?? 0;
            _writer?.Dispose();
            _writer = null;

            if (_waveIn != null)
            {
                _waveIn.DataAvailable -= OnDataAvailable;
                _waveIn.RecordingStopped -= OnRecordingStopped;
                _waveIn.Dispose();
                _waveIn = null;
            }

            path = _currentFilePath;
            _currentFilePath = null;
            discard = _discardOnStop;
            monitor = _monitorOnly;
            _monitorOnly = false;
            tcs = _stopTcs;
            _stopTcs = null;
            IsMonitoring = false;
        }

        // Always answer a pending stop, even on the monitor path — dropping it
        // here is what used to strand the session in Processing.
        if (monitor)
        {
            tcs?.TrySetResult(null);
            return;
        }

        if (discard || path == null)
        {
            TryDelete(path);
            tcs?.TrySetResult(null);
            return;
        }

        if (e.Exception != null)
        {
            // The writer was already disposed above, so whatever was captured
            // before the device faulted is a valid WAV. Unplugging a USB mic at
            // second 9 of a 10-second dictation used to delete all nine.
            if (duration >= 0.3 && File.Exists(path))
            {
                tcs?.TrySetResult(new RecordedTake(path, duration, TakeOutcome.RecoverableDeviceError));
                return;
            }
            TryDelete(path);
            tcs?.TrySetException(e.Exception);
            return;
        }

        tcs?.TrySetResult(new RecordedTake(path, duration, TakeOutcome.Ok));
    }

    // ----- Devices -----

    public static List<string> GetInputDeviceNames()
    {
        var names = new List<string>();
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            names.Add(WaveInEvent.GetCapabilities(i).ProductName);
        }
        return names;
    }

    /// <summary>-1 (WAVE_MAPPER) = system default; also the fallback for unplugged devices.</summary>
    private static int ResolveDeviceIndex(string deviceName)
    {
        if (string.IsNullOrEmpty(deviceName)) return -1;
        for (var i = 0; i < WaveInEvent.DeviceCount; i++)
        {
            if (WaveInEvent.GetCapabilities(i).ProductName == deviceName) return i;
        }
        return -1;
    }

    public static void TryDelete(string? path)
    {
        if (path == null) return;
        try { File.Delete(path); } catch { }
    }

    public static void CleanupOldRecordings()
    {
        try
        {
            if (!Directory.Exists(AppPaths.RecordingsDir)) return;
            foreach (var file in Directory.GetFiles(AppPaths.RecordingsDir, "*.wav"))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch { }
    }
}
