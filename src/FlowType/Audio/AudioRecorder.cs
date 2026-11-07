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
///
/// Every event on this class is raised on NAudio's capture thread. Subscribers
/// must return quickly and marshal to the UI themselves: a slow handler delays
/// the next buffer, and a throwing one costs that handler its buffer (counted in
/// <see cref="CallbackFaults"/>) but never the take.
/// </summary>
public sealed class AudioRecorder
{
    /// <summary>Hands-free sessions auto-stop here so a forgotten mic doesn't run forever.</summary>
    public static TimeSpan MaxDuration => TimeSpan.FromMinutes(
        Math.Clamp(SettingsStore.Instance.Settings.MaxRecordingMinutes, 1, 60));

    /// <summary>The capture format: 16 kHz, what whisper.cpp consumes natively.</summary>
    public const int SampleRate = 16000;

    /// <summary>
    /// How long a take must be digitally dead from its very first sample —
    /// every sample exactly zero — before <see cref="SilentInput"/> fires.
    /// Five seconds is Wispr Flow's threshold for the same condition. Exact
    /// zeros, not quiet: a working microphone in a silent room still produces
    /// a few bits of self-noise, so a take that has never produced anything
    /// but true zeros is a muted, disabled or disconnected device (or a
    /// privacy switch), never someone who has simply not started talking.
    ///
    /// Only from the start, though: noise-gated virtual microphones (NVIDIA
    /// Broadcast, Krisp, Voicemeeter and similar gates) also output exact
    /// zeros in every ordinary pause, so a five-second run in the middle of a
    /// take that already had sound is a pause, not a dead device. That case
    /// uses <see cref="MidTakeSilentInputThreshold"/>.
    /// </summary>
    public static readonly TimeSpan SilentInputThreshold = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a take that HAS produced sound must then be exact zeros before
    /// <see cref="SilentInput"/> fires — long enough that a noise gate's
    /// thinking pause does not trip it, short enough that a hardware mute
    /// switch flipped mid-dictation is still pointed out.
    /// </summary>
    public static readonly TimeSpan MidTakeSilentInputThreshold = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The lowest adaptive noise floor <see cref="LevelNormalized"/> will use.
    /// Without a clamp, one digitally silent buffer (common in the first few
    /// milliseconds after a device opens) would pin the floor at −200 dB and
    /// every later buffer would read as full scale.
    /// </summary>
    public const float LevelFloorClampDb = -60f;

    /// <summary>Dynamic range of the normalized meter above the floor, in dB.</summary>
    public const float LevelRangeDb = 20f;

    public static AudioRecorder Instance { get; } = new();

    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private string? _currentFilePath;
    private TaskCompletionSource<RecordedTake?>? _stopTcs;
    private bool _discardOnStop;
    private bool _monitorOnly;
    private bool _autoStopRaised;
    private readonly object _gate = new();

    // Per-take measurement state. Reset under _gate when a device opens and
    // only touched by the current device's capture callback afterwards (stale
    // devices are filtered out by sender identity, see OnDataAvailable).
    private float _levelFloorDb = float.MaxValue;
    private long _zeroRunSamples;
    private bool _silentInputRaised;
    /// <summary>The take has delivered at least one non-zero sample (see <see cref="SilentInputThreshold"/>).</summary>
    private bool _sawNonZero;

    public bool IsRecording { get; private set; }
    public bool IsMonitoring { get; private set; }

    /// <summary>A take whose device stopped by itself, waiting for StopAsync to collect it.</summary>
    private RecordedTake? _orphanTake;

    /// <summary>
    /// A take whose device stopped by itself and whose WAV could not be
    /// finished (disk full): StopAsync reports this instead of a take, so the
    /// session can say why the dictation is gone rather than call it too short.
    /// </summary>
    private Exception? _orphanError;

    /// <summary>
    /// The capture device stopped without being asked (unplugged, driver
    /// reset) while recording. Raised from NAudio's stop callback. The audio up
    /// to that point is kept for the next <see cref="StopAsync"/>.
    /// </summary>
    public event Action? DeviceLost;

    /// <summary>Peak input level 0..1 (capture thread).</summary>
    public event Action<float>? LevelChanged;

    /// <summary>
    /// A loudness level 0..1 that adapts to the room, for the recording
    /// waveform (capture thread, recording and monitoring alike). Computed per
    /// buffer as RMS in dBFS, measured against a noise floor that is the
    /// quietest buffer seen since the device opened (clamped to
    /// <see cref="LevelFloorClampDb"/>): level = (dB − floor) / 20 dB.
    ///
    /// Why not the peak: <see cref="LevelChanged"/> reports the loudest single
    /// sample, which is dominated by plosives and desk taps and sits near zero
    /// for a quiet speaker on a quiet mic. Measuring loudness relative to the
    /// room's own floor makes a soft voice on a cheap headset and a loud one on
    /// a studio mic both fill the meter — the same normalisation Wispr Flow
    /// applies to its meter.
    /// </summary>
    public event Action<float>? LevelNormalized;

    /// <summary>
    /// A copy of each captured PCM16 mono 16 kHz buffer, raised on the capture
    /// thread while a take is being recorded — never while monitoring. The
    /// buffers are exactly the ones written to the take's WAV, in order, so a
    /// consumer that concatenates them holds the same audio the file will.
    /// The array is the subscriber's to keep.
    /// </summary>
    public event Action<short[]>? SamplesCaptured;

    /// <summary>
    /// The take has delivered nothing but exact-zero samples since it began
    /// for <see cref="SilentInputThreshold"/>, or — after it had sound — for
    /// <see cref="MidTakeSilentInputThreshold"/> (argument: how long). Raised
    /// at most once per take, on the capture thread, while recording only.
    /// This is a dead-device signal — see <see cref="SilentInputThreshold"/>
    /// for why it does not fire on ordinary silence or a noise gate's pauses.
    /// </summary>
    public event Action<TimeSpan>? SilentInput;

    /// <summary>The 5-minute cap was hit; the session should stop-and-transcribe.</summary>
    public event Action? AutoStopRequested;

    private AudioRecorder() { }

    // ----- Recording -----

    /// <summary>
    /// Open the microphone and start writing a new take. Throws if the device
    /// cannot be opened or the WAV cannot be created; nothing is left behind
    /// when it does.
    /// </summary>
    public void StartRecording()
    {
        lock (_gate)
        {
            StopMonitorLocked();
            if (IsRecording) return;
            // A lost take nobody collected (the session was not recording any
            // more) must not be handed to the *next* dictation.
            if (_orphanTake is { } stale)
            {
                _orphanTake = null;
                TryDelete(stale.Path);
            }
            _orphanError = null;

            AppPaths.EnsureCreated();
            var path = Path.Combine(
                AppPaths.RecordingsDir, $"take-{DateTime.Now:yyyyMMdd-HHmmss-fff}.wav");

            var device = CreateWaveIn();
            WaveFileWriter? writer = null;
            try
            {
                writer = new WaveFileWriter(path, device.WaveFormat);
                _waveIn = device;
                _writer = writer;
                _currentFilePath = path;
                _discardOnStop = false;
                _monitorOnly = false;
                _autoStopRaised = false;
                _stopTcs = null;
                ResetMeasurementsLocked();

                device.DataAvailable += OnDataAvailable;
                device.RecordingStopped += OnRecordingStopped;
                device.StartRecording();
                IsRecording = true;
            }
            catch
            {
                // waveInOpen/waveInStart refused (device busy or gone, privacy
                // switch) or the WAV could not be created. The writer — an open
                // file — and possibly an opened device used to stay behind here,
                // to be silently overwritten by the next attempt. NAudio only
                // marks the device as started after waveInStart succeeds, so
                // Dispose here just closes whatever waveInOpen opened.
                device.DataAvailable -= OnDataAvailable;
                device.RecordingStopped -= OnRecordingStopped;
                try { device.Dispose(); } catch { }
                try { writer?.Dispose(); } catch { }
                if (ReferenceEquals(_waveIn, device))
                {
                    _waveIn = null;
                    _writer = null;
                    _currentFilePath = null;
                }
                TryDelete(path);
                throw;
            }
        }
    }

    /// <summary>
    /// Stop and return the WAV path + duration (null if discarded or too
    /// short). Faults with an <see cref="IOException"/> when the audio was
    /// captured but its WAV could not be finished (disk full).
    /// </summary>
    public Task<RecordedTake?> StopAsync()
    {
        TaskCompletionSource<RecordedTake?> tcs;
        TornDown torn;
        lock (_gate)
        {
            if (_orphanError is { } failed)
            {
                _orphanError = null;
                return Task.FromException<RecordedTake?>(failed);
            }
            // The device stopped on its own (unplugged) before anyone asked:
            // hand back what it captured instead of nothing.
            if (_orphanTake is { } orphan)
            {
                _orphanTake = null;
                return Task.FromResult<RecordedTake?>(orphan);
            }
            if (!IsRecording || _waveIn == null)
            {
                return Task.FromResult<RecordedTake?>(null);
            }
            tcs = new TaskCompletionSource<RecordedTake?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTcs = tcs;
            IsRecording = false;
            try
            {
                _waveIn.StopRecording();
                return tcs.Task;
            }
            catch (Exception ex)
            {
                // waveInStop/waveInReset failed (driver fault, device pulled
                // mid-call). NAudio has already marked the device as stopping,
                // and with the stop never signalled, RecordingStopped may never
                // come: do its work now and keep what was written.
                System.Diagnostics.Debug.WriteLine($"AudioRecorder: stop failed: {ex.Message}");
                torn = TearDownLocked(disposeDeviceInBackground: true);
            }
        }
        Settle(tcs, torn, TakeOutcome.RecoverableDeviceError);
        return tcs.Task;
    }

    /// <summary>Stop and throw the take away. Never throws.</summary>
    public void Cancel()
    {
        TornDown torn;
        lock (_gate)
        {
            if (!IsRecording || _waveIn == null) return;
            _discardOnStop = true;
            IsRecording = false;
            try
            {
                _waveIn.StopRecording();
                return;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AudioRecorder: cancel failed: {ex.Message}");
                torn = TearDownLocked(disposeDeviceInBackground: true);
            }
        }
        TryDelete(torn.Path);
        torn.PendingStop?.TrySetResult(null);
    }

    /// <summary>
    /// Give up on the take in flight without waiting for the device any more.
    /// For when <see cref="StopAsync"/> did not answer in time: the device is
    /// released (on a background thread — it has already failed to answer
    /// once, and a wedged driver must not hang the caller), the writer is
    /// closed, the WAV is deleted, and a pending stop is answered with null.
    /// Without this the device handle and the open WAV stayed alive and
    /// <c>_stopTcs</c> stayed set, which also kept the mic test from opening.
    /// A mic-test monitor is left alone. Safe to call at any time; never throws.
    /// </summary>
    public void Abandon()
    {
        TornDown torn = default;
        RecordedTake? orphan;
        lock (_gate)
        {
            orphan = _orphanTake;
            _orphanTake = null;
            _orphanError = null;
            var monitorRunning = _monitorOnly && _stopTcs == null;
            if (!monitorRunning) torn = TearDownLocked(disposeDeviceInBackground: true);
        }
        TryDelete(torn.Path);
        TryDelete(orphan?.Path);
        torn.PendingStop?.TrySetResult(null);
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
            var device = CreateWaveIn();
            _waveIn = device;
            _monitorOnly = true;
            ResetMeasurementsLocked();
            device.DataAvailable += OnDataAvailable;
            device.RecordingStopped += OnRecordingStopped;
            try
            {
                device.StartRecording();
            }
            catch
            {
                // Same as StartRecording: do not leave a half-opened device in
                // _waveIn for the next caller to overwrite.
                device.DataAvailable -= OnDataAvailable;
                device.RecordingStopped -= OnRecordingStopped;
                try { device.Dispose(); } catch { }
                _waveIn = null;
                _monitorOnly = false;
                throw;
            }
            IsMonitoring = true;
        }
    }

    /// <summary>Stop the mic-test meter. Never throws.</summary>
    public void StopMonitor()
    {
        lock (_gate) StopMonitorLocked();
    }

    private void StopMonitorLocked()
    {
        if (!IsMonitoring || _waveIn == null) return;
        IsMonitoring = false;
        try
        {
            _waveIn.StopRecording();
        }
        catch (Exception ex)
        {
            // A monitor writes no file: just let go of the device, so the
            // StartRecording that called this still gets a fresh one instead
            // of failing the dictation over the meter.
            System.Diagnostics.Debug.WriteLine($"AudioRecorder: monitor stop failed: {ex.Message}");
            var torn = TearDownLocked(disposeDeviceInBackground: true);
            torn.PendingStop?.TrySetResult(null);
        }
    }

    private void ResetMeasurementsLocked()
    {
        _levelFloorDb = float.MaxValue;
        _zeroRunSamples = 0;
        _silentInputRaised = false;
        _sawNonZero = false;
    }

    // ----- Teardown without RecordingStopped -----

    /// <summary>What <see cref="TearDownLocked"/> let go of.</summary>
    private readonly record struct TornDown(
        string? Path, double Duration, Exception? WriterFault, TaskCompletionSource<RecordedTake?>? PendingStop);

    /// <summary>
    /// Release the current device and writer and reset every per-take field,
    /// without waiting for NAudio's RecordingStopped. Inside the gate. Never
    /// throws. The caller deletes or hands on <see cref="TornDown.Path"/> and
    /// answers <see cref="TornDown.PendingStop"/> outside the gate.
    ///
    /// <paramref name="disposeDeviceInBackground"/>: true whenever the device
    /// has just failed (a stop that threw, a stop that never answered). Its
    /// Dispose calls back into the same driver — waveInStop, waveInReset,
    /// waveInClose — and these callers run on the UI thread, which a wedged
    /// driver must not hang. False only after a clean RecordingStopped, where
    /// the device is idle and closing it is immediate.
    /// </summary>
    private TornDown TearDownLocked(bool disposeDeviceInBackground)
    {
        var device = _waveIn;
        _waveIn = null;
        if (device != null)
        {
            // Unhooked first, so a buffer still in flight or a RecordingStopped
            // NAudio posts later finds nothing to act on (the sender checks in
            // both handlers would also ignore it now that _waveIn is null).
            device.DataAvailable -= OnDataAvailable;
            device.RecordingStopped -= OnRecordingStopped;
        }

        var (duration, writerFault) = CloseWriterLocked();

        if (device != null)
        {
            // WaveInEvent.Dispose calls StopRecording again while the device is
            // still marked Stopping, so the fault that brought us here can throw
            // a second time; and a driver that never answered may block.
            if (disposeDeviceInBackground)
            {
                _ = Task.Run(() =>
                {
                    try { device.Dispose(); } catch { }
                });
            }
            else
            {
                try { device.Dispose(); } catch { }
            }
        }

        var path = _currentFilePath;
        _currentFilePath = null;
        var pending = _stopTcs;
        _stopTcs = null;
        IsRecording = false;
        IsMonitoring = false;
        _monitorOnly = false;
        _discardOnStop = false;
        return new TornDown(path, duration, writerFault, pending);
    }

    /// <summary>
    /// Finish the WAV: WaveFileWriter only writes the RIFF/data sizes into the
    /// header when it is disposed, and that write (and the final flush) is
    /// where a full disk shows up. Inside the gate. Never throws; a fault is
    /// returned instead, and a WAV whose header failed is not a usable take.
    /// </summary>
    private (double Duration, Exception? Fault) CloseWriterLocked()
    {
        var writer = _writer;
        _writer = null;
        if (writer == null) return (0, null);
        double duration = 0;
        try { duration = writer.TotalTime.TotalSeconds; } catch { }
        try
        {
            writer.Dispose();
            return (duration, null);
        }
        catch (Exception ex)
        {
            return (duration, ex);
        }
    }

    /// <summary>
    /// Answer a stop for a take that ended without a clean RecordingStopped:
    /// the audio up to that point if its WAV was finished and is long enough to
    /// be worth transcribing, an error if the WAV could not be finished, and
    /// otherwise nothing (the file is deleted).
    /// </summary>
    private static void Settle(TaskCompletionSource<RecordedTake?> tcs, TornDown torn, TakeOutcome outcome)
    {
        if (torn.WriterFault is { } fault)
        {
            TryDelete(torn.Path);
            tcs.TrySetException(SaveFailed(fault));
            return;
        }
        if (torn.Path != null && torn.Duration >= 0.3 && File.Exists(torn.Path))
        {
            tcs.TrySetResult(new RecordedTake(torn.Path, torn.Duration, outcome));
            return;
        }
        TryDelete(torn.Path);
        tcs.TrySetResult(null);
    }

    /// <summary>The error a session shows when the take was captured but could not be saved.</summary>
    private static IOException SaveFailed(Exception inner) =>
        new($"The recording could not be saved ({inner.Message})", inner);

    // ----- Plumbing -----

    private static WaveInEvent CreateWaveIn() => new()
    {
        DeviceNumber = ResolveDeviceIndex(SettingsStore.Instance.Settings.MicDeviceName),
        WaveFormat = new WaveFormat(SampleRate, 16, 1),
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
            WriteBuffer(sender, e);
        }
        catch
        {
            Interlocked.Increment(ref _callbackFaults);
        }
    }

    private void WriteBuffer(object? sender, WaveInEventArgs e)
    {
        // One pass over the buffer for every measurement below.
        var count = e.BytesRecorded / 2;
        int peak = 0;
        double sumSquares = 0;
        var trailingZeros = 0;
        for (var i = 0; i < count; i++)
        {
            int sample = BitConverter.ToInt16(e.Buffer, i * 2);
            var magnitude = Math.Abs(sample);
            if (magnitude > peak) peak = magnitude;
            sumSquares += (double)sample * sample;
            trailingZeros = sample == 0 ? trailingZeros + 1 : 0;
        }
        var db = RmsDbfs(sumSquares, count);

        WaveFileWriter? writer;
        bool shouldAutoStop = false;
        TimeSpan? silentFor = null;
        float normalized;
        lock (_gate)
        {
            // A device this recorder has already replaced can still deliver a
            // buffer or two: StartRecording stops the mic-test monitor and opens
            // a new device immediately, but the old one's thread drains on its
            // own schedule. Without this check those stray monitor buffers were
            // written into the new take's WAV.
            if (!ReferenceEquals(sender, _waveIn)) return;

            writer = _monitorOnly ? null : _writer;
            if (writer != null)
            {
                writer.Write(e.Buffer, 0, e.BytesRecorded);
                if (!_autoStopRaised && writer.TotalTime > MaxDuration)
                {
                    _autoStopRaised = true;
                    shouldAutoStop = true;
                }

                if (peak > 0) _sawNonZero = true;
                _zeroRunSamples = ExtendZeroRun(_zeroRunSamples, count, trailingZeros);
                if (!_silentInputRaised && SilentInputDue(_zeroRunSamples, _sawNonZero))
                {
                    _silentInputRaised = true;
                    silentFor = TimeSpan.FromSeconds(_zeroRunSamples / (double)SampleRate);
                }
            }

            normalized = NormalizeLevel(db, ref _levelFloorDb);
        }

        // Each subscriber is isolated: a throwing waveform must not starve the
        // live transcriber of audio, and vice versa.
        if (writer != null && count > 0 && SamplesCaptured is { } samplesHandler)
        {
            var copy = new short[count];
            Buffer.BlockCopy(e.Buffer, 0, copy, 0, count * 2);
            Raise(() => samplesHandler(copy));
        }
        if (LevelChanged is { } levelHandler) Raise(() => levelHandler(peak / 32768f));
        if (LevelNormalized is { } normalizedHandler) Raise(() => normalizedHandler(normalized));
        if (silentFor is { } dead && SilentInput is { } silentHandler) Raise(() => silentHandler(dead));
        if (shouldAutoStop && AutoStopRequested is { } autoStopHandler) Raise(autoStopHandler);
    }

    // ----- Measurement (pure, so --selftest can drive them without a device) -----

    /// <summary>RMS of a PCM16 buffer in dBFS (−200 for digital silence), from its sum of squares.</summary>
    internal static float RmsDbfs(double sumSquares, int count)
    {
        var rms = count > 0 ? Math.Sqrt(sumSquares / count) / 32768.0 : 0;
        return (float)(20 * Math.Log10(rms + 1e-10));
    }

    /// <summary>
    /// The exact-zero run after one more buffer: a buffer of nothing but zeros
    /// extends it, anything else restarts it from that buffer's own trailing
    /// zeros (so a run is measured to the sample, not to the buffer).
    /// </summary>
    internal static long ExtendZeroRun(long run, int count, int trailingZeros) =>
        trailingZeros == count && count > 0 ? run + count : trailingZeros;

    /// <summary>
    /// Whether an exact-zero run of <paramref name="zeroRunSamples"/> means a
    /// dead input: <see cref="SilentInputThreshold"/> while the take has never
    /// had a non-zero sample, <see cref="MidTakeSilentInputThreshold"/> once it
    /// has (a noise gate's pause, unless it goes on and on).
    /// </summary>
    internal static bool SilentInputDue(long zeroRunSamples, bool sawNonZero) =>
        zeroRunSamples >= (long)((sawNonZero ? MidTakeSilentInputThreshold : SilentInputThreshold).TotalSeconds
                                 * SampleRate);

    /// <summary>
    /// One step of the normalized meter: lower the min-hold floor if this
    /// buffer is the quietest yet (never below <see cref="LevelFloorClampDb"/>),
    /// then report where the buffer sits in the 20 dB above it.
    /// </summary>
    internal static float NormalizeLevel(float db, ref float floorDb)
    {
        floorDb = Math.Max(LevelFloorClampDb, Math.Min(floorDb, db));
        return Math.Clamp((db - floorDb) / LevelRangeDb, 0f, 1f);
    }

    private void Raise(Action invoke)
    {
        try
        {
            invoke();
        }
        catch
        {
            Interlocked.Increment(ref _callbackFaults);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        string? path;
        double duration;
        Exception? writerFault;
        bool discard, monitor;
        var deviceLost = false;
        TaskCompletionSource<RecordedTake?>? tcs;

        lock (_gate)
        {
            // NAudio posts RecordingStopped to the UI thread, so it can arrive
            // after StartRecording has already replaced the mic-test monitor
            // with a new device. Acting on it then used to dispose the *new*
            // take's writer and device — the dictation silently came back
            // empty. A stale device only needs releasing.
            if (!ReferenceEquals(sender, _waveIn))
            {
                if (sender is WaveInEvent stale)
                {
                    stale.DataAvailable -= OnDataAvailable;
                    stale.RecordingStopped -= OnRecordingStopped;
                    try { stale.Dispose(); } catch { }
                }
                return;
            }

            discard = _discardOnStop;
            monitor = _monitorOnly;
            // The device has stopped, so this is the ordinary teardown — but it
            // runs on the UI thread, and the writer's final header write is
            // where a full disk throws. That used to escape here, crash the app
            // and leave the pending stop unanswered; now each step is guarded
            // and the fault is reported instead.
            var torn = TearDownLocked(disposeDeviceInBackground: false);
            path = torn.Path;
            duration = torn.Duration;
            writerFault = torn.WriterFault;
            tcs = torn.PendingStop;
            if (tcs == null && !monitor && !discard && path != null)
            {
                // Nobody asked for this stop: the device went away mid-take.
                // IsRecording used to stay true here, so the session's later
                // StopAsync returned null and the whole take was lost (and its
                // WAV left behind). Keep it for StopAsync and say so.
                var lostOutcome = e.Exception != null ? TakeOutcome.RecoverableDeviceError : TakeOutcome.Ok;
                if (writerFault != null)
                {
                    // The header never got its sizes: not a take to hand on.
                    _orphanError = SaveFailed(writerFault);
                }
                else if (duration >= 0.3 && File.Exists(path))
                {
                    _orphanTake = new RecordedTake(path, duration, lostOutcome);
                    path = null;   // owned by _orphanTake now
                }
                deviceLost = true;
            }
        }
        if (deviceLost)
        {
            TryDelete(path);
            try { DeviceLost?.Invoke(); } catch { }
            return;
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

        if (writerFault != null)
        {
            // The WAV's header sizes were never written, so whatever the file
            // holds would read as empty or garbage. Say why instead.
            TryDelete(path);
            tcs?.TrySetException(SaveFailed(writerFault));
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
