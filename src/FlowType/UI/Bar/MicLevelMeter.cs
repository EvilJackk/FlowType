namespace FlowType.UI.Bar;

/// <summary>
/// Turns the recorder's raw per-buffer peak (0..1, one value every ~50 ms on
/// the capture thread) into a calm 0..1 "how loudly is someone talking" level
/// for the flow bar's waveform.
///
/// Why not just draw the peak: a linear peak is useless as a meter. A quiet
/// voice on a laptop mic peaks around 0.03, a headset at 0.4, and both should
/// look like "talking". Loudness is logarithmic, so the peak is converted to
/// dBFS and measured against the room's own noise floor, which differs by
/// 30 dB between a gaming headset and a far-field webcam mic.
///
/// The model (our own, documented so it can be re-tuned):
/// <list type="number">
/// <item>dB = 20·log10(peak). Exact zeros (a muted device) become -90 dB.</item>
/// <item>Noise floor: snaps down instantly to any quieter buffer, and drifts up
///   at <see cref="FloorRiseDbPerSecond"/> otherwise, clamped to
///   [<see cref="FloorMinDb"/>, <see cref="FloorMaxDb"/>]. The drift is what
///   stops one dropout of digital silence pinning the floor at the bottom for
///   the rest of the take; the gaps between words keep re-anchoring it.</item>
/// <item>Target = (dB − floor − <see cref="DeadZoneDb"/>) / <see cref="SpanDb"/>,
///   clamped 0..1. The dead zone keeps breathing and fan noise from twitching
///   the bars; the 32 dB span puts ordinary speech in the upper half without
///   pinning every syllable at full height (a headset hears 35–45 dB of
///   speech above its own floor).</item>
/// <item>Smoothing: an asymmetric one-pole filter — fast attack
///   (<see cref="AttackSeconds"/>) so syllables register, slower release
///   (<see cref="ReleaseSeconds"/>) so the bars fall rather than flicker. It is
///   time-based, so the result is the same at any frame rate.</item>
/// </list>
/// Only the peak is available from <c>AudioRecorder.LevelChanged</c>, so this
/// is peak-dB rather than RMS-dB; the dead zone and span were chosen with that
/// in mind (peaks sit a few dB above RMS for speech).
/// </summary>
internal sealed class MicLevelMeter
{
    private const double SilenceDb = -90;
    private const double FloorMinDb = -62;
    private const double FloorMaxDb = -30;
    private const double FloorRiseDbPerSecond = 1.2;
    private const double DeadZoneDb = 4;
    private const double SpanDb = 32;
    private const double AttackSeconds = 0.045;
    private const double ReleaseSeconds = 0.20;

    /// <summary>No buffer for this long means the device stalled: let the bars fall.</summary>
    private const double StaleSeconds = 0.25;

    /// <summary>≈ −80 dBFS. Anything above it is a live microphone, not a muted one.</summary>
    private const float SignalPeak = 1e-4f;

    private readonly object _gate = new();

    // Written on the capture thread, consumed on the UI thread (under _gate).
    private float _pendingPeak;
    private bool _hasPending;
    private bool _sawSignal;

    // UI-thread state.
    private bool _floorInitialised;
    private double _floorDb = -50;
    private double _currentDb = SilenceDb;
    private double _sinceBuffer;

    /// <summary>Smoothed level, 0..1.</summary>
    public double Level { get; private set; }

    /// <summary>
    /// True once a buffer with real signal has arrived in this take. The bar
    /// keeps its waveform dim until then, so a muted or still-opening mic
    /// looks different from one that is hearing you.
    /// </summary>
    public bool MicActive { get; private set; }

    /// <summary>Forget the previous take (UI thread, at the start of a recording).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _pendingPeak = 0;
            _hasPending = false;
            _sawSignal = false;
        }
        _floorInitialised = false;
        _floorDb = -50;
        _currentDb = SilenceDb;
        _sinceBuffer = 0;
        Level = 0;
        MicActive = false;
    }

    /// <summary>
    /// Record one buffer's peak. Called on the capture thread, so it only
    /// stores the loudest value since the last frame — no allocation, no
    /// dispatcher post, nothing that could slow the audio callback down.
    /// </summary>
    public void Ingest(float peak)
    {
        if (float.IsNaN(peak)) return;
        lock (_gate)
        {
            if (!_hasPending || peak > _pendingPeak) _pendingPeak = peak;
            _hasPending = true;
            if (peak > SignalPeak) _sawSignal = true;
        }
    }

    /// <summary>Advance the meter by <paramref name="dt"/> seconds (UI thread) and return the level.</summary>
    public double Advance(double dt)
    {
        if (dt <= 0) return Level;

        float peak;
        bool has;
        lock (_gate)
        {
            has = _hasPending;
            peak = _pendingPeak;
            _hasPending = false;
            _pendingPeak = 0;
            if (_sawSignal) MicActive = true;
        }

        if (has)
        {
            _sinceBuffer = 0;
            _currentDb = peak > 0 ? Math.Max(SilenceDb, 20 * Math.Log10(peak)) : SilenceDb;
            if (!_floorInitialised)
            {
                // The first buffer is almost always the moment the hotkey went
                // down — before the first word — so it is a fair floor guess.
                _floorDb = Math.Clamp(_currentDb, FloorMinDb, FloorMaxDb);
                _floorInitialised = true;
            }
            if (_currentDb < _floorDb) _floorDb = _currentDb;
        }
        else
        {
            _sinceBuffer += dt;
            if (_sinceBuffer > StaleSeconds) _currentDb = SilenceDb;
        }

        _floorDb = Math.Clamp(_floorDb + FloorRiseDbPerSecond * dt, FloorMinDb, FloorMaxDb);

        var target = Math.Clamp((_currentDb - _floorDb - DeadZoneDb) / SpanDb, 0, 1);
        var tau = target > Level ? AttackSeconds : ReleaseSeconds;
        Level += (target - Level) * (1 - Math.Exp(-dt / tau));
        if (Level < 0.001) Level = 0;
        return Level;
    }
}
