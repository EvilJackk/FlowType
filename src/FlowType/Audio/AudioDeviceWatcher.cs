using System.Collections.Concurrent;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace FlowType.Audio;

/// <summary>
/// Tells the app when microphones come and go, and when the Windows default
/// microphone changes — the raw material for "stop when the mic in use is
/// unplugged", "a new mic was plugged in", and "Using {mic}" notices.
///
/// <para><b>Source.</b> Core Audio's endpoint notifications
/// (<c>IMMNotificationClient</c>, registered through NAudio's
/// <see cref="MMDeviceEnumerator.RegisterEndpointNotificationCallback"/>).
/// Polling <c>WaveInEvent.DeviceCount</c> would also work, but only on a
/// timer, and it cannot see which device is the Windows default.</para>
///
/// <para><b>Threading — subscribers must marshal.</b> Every event is raised on
/// a background thread-pool thread, never on the UI thread. WPF subscribers
/// must hop with <c>Dispatcher.BeginInvoke</c> before touching UI or session
/// state. Events are raised one at a time and in order.</para>
///
/// <para><b>Why the callback itself does nothing.</b> Windows calls the
/// notification interface on its own RPC thread and documents that a callback
/// must not block, must not register or unregister callbacks, and must not
/// release the last reference to a Core Audio object. So the callback only
/// queues a note; a single background drain re-reads the device list and works
/// out what actually changed. Re-reading instead of trusting each notification
/// also collapses the bursts Windows sends for one plug event (one state change
/// per endpoint, render and capture alike) into at most one event per device.</para>
///
/// <para><b>Names.</b> Events carry the endpoint's full friendly name, e.g.
/// <c>Microphone (Blue Yeti Pro Studio Edition)</c>. <see cref="AudioRecorder"/>
/// and the saved <c>MicDeviceName</c> use the legacy waveIn name instead, which
/// Windows truncates to 31 characters — use <see cref="WaveInNameFor"/> to
/// compare the two.</para>
/// </summary>
public sealed class AudioDeviceWatcher : IDisposable
{
    public static AudioDeviceWatcher Instance { get; } = new();

    /// <summary>
    /// The waveIn API stores device names in a fixed 32-character buffer
    /// (<c>MAXPNAMELEN</c>) including the terminating NUL, so a ProductName is
    /// the endpoint friendly name cut to at most 31 characters.
    /// </summary>
    private const int WaveInNameMax = 31;

    /// <summary>
    /// The Windows default microphone (the Sound settings "default device")
    /// changed. Raised on a background thread.
    /// </summary>
    public event Action? DefaultCaptureDeviceChanged;

    /// <summary>
    /// A microphone became available (plugged in, enabled). Carries its
    /// friendly name. Raised on a background thread.
    /// </summary>
    public event Action<string>? CaptureDeviceAdded;

    /// <summary>
    /// A microphone went away (unplugged, disabled, driver removed). Carries
    /// the friendly name it had. Raised on a background thread. Friendly names
    /// are not unique (two identical USB headsets), so a subscriber that needs
    /// to know whether the removed mic is a particular one should use
    /// <see cref="CaptureEndpointRemoved"/> instead.
    /// </summary>
    public event Action<string>? CaptureDeviceRemoved;

    /// <summary>
    /// The same removal as <see cref="CaptureDeviceRemoved"/>, carrying the
    /// endpoint id as well as the name, so it can be compared with the id
    /// <see cref="DefaultCaptureEndpoint"/> returned when a take started.
    /// Raised on a background thread, immediately before
    /// <see cref="CaptureDeviceRemoved"/> for the same device. Subscribe to
    /// one or the other.
    /// </summary>
    public event Action<CaptureEndpoint>? CaptureEndpointRemoved;

    /// <summary>
    /// A capture endpoint: Core Audio's endpoint id (unique, and stable for as
    /// long as the device is installed) and its friendly name as the Sound
    /// settings show it. The name may be empty if it could not be read.
    /// </summary>
    public sealed record CaptureEndpoint(string Id, string FriendlyName);

    private readonly object _gate = new();
    private MMDeviceEnumerator? _enumerator;
    private NotificationSink? _sink;
    private bool _started;
    private bool _disposed;

    // The drain below is the only reader/writer of these two, one job at a time.
    private Dictionary<string, string> _active = new(StringComparer.OrdinalIgnoreCase);
    private string? _defaultCaptureId;
    private bool _primed;

    // The drain's view of the Windows default mic, published for any thread
    // (see DefaultCaptureEndpoint). Written only by the drain.
    private volatile CaptureEndpoint? _defaultCapture;
    private volatile bool _defaultKnown;

    private readonly ConcurrentQueue<Action> _work = new();
    private int _draining;
    private int _resyncQueued;

    private AudioDeviceWatcher() { }

    /// <summary>True once the notification callback is registered.</summary>
    public bool IsRunning
    {
        get { lock (_gate) return _sink != null; }
    }

    /// <summary>
    /// Registers for device notifications. Idempotent; call once at startup.
    /// Blocks for a few milliseconds (at most 3 s if the audio service is
    /// unresponsive). Never throws — without notifications the app still works,
    /// it just learns about device changes at the next dictation.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed) return;
            _started = true;
        }

        try
        {
            RunOnMta(() =>
            {
                var enumerator = new MMDeviceEnumerator();
                var sink = new NotificationSink(this);
                var hr = enumerator.RegisterEndpointNotificationCallback(sink);
                if (hr != 0)
                {
                    Debug.WriteLine($"AudioDeviceWatcher: register failed 0x{hr:X8}");
                    enumerator.Dispose();
                    return;
                }
                lock (_gate)
                {
                    _enumerator = enumerator;
                    _sink = sink;
                }
            }, TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AudioDeviceWatcher: start failed: {ex.GetBaseException().Message}");
        }

        // Take the baseline on the drain so it is ordered with any callback
        // that raced in between registering and here.
        Enqueue(Prime);
    }

    /// <summary>Unregisters the callback. Safe to call more than once.</summary>
    public void Dispose()
    {
        MMDeviceEnumerator? enumerator;
        NotificationSink? sink;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            enumerator = _enumerator;
            sink = _sink;
            _enumerator = null;
            _sink = null;
        }
        if (enumerator == null) return;

        try
        {
            RunOnMta(() =>
            {
                if (sink != null) enumerator.UnregisterEndpointNotificationCallback(sink);
                enumerator.Dispose();
            }, TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AudioDeviceWatcher: stop failed: {ex.GetBaseException().Message}");
        }
    }

    // ----- Static queries -----

    /// <summary>
    /// Friendly names of every active (present and enabled) microphone, in
    /// Windows' order. Empty on failure. Safe from any thread.
    /// </summary>
    public static IReadOnlyList<string> ActiveCaptureDevices()
    {
        try
        {
            IReadOnlyList<string> names = Array.Empty<string>();
            RunOnMta(() => names = ReadActiveCapture().Values.ToList(), TimeSpan.FromSeconds(3));
            return names;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AudioDeviceWatcher: enumerate failed: {ex.GetBaseException().Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// The Windows default microphone (Console role — the one waveIn's
    /// WAVE_MAPPER, and so <see cref="AudioRecorder"/> with "follow Windows",
    /// opens), or null if there is none or it could not be read. Safe from any
    /// thread.
    ///
    /// <para>Cheap on the hot path: while the watcher is running it answers
    /// from the endpoint notifications it already tracks, without a Core Audio
    /// call (reading a device's friendly name opens its property store, which
    /// costs tens of milliseconds). Only when the watcher is not running does
    /// it read the device directly, bounded to half a second.</para>
    ///
    /// <para>For "is the removed mic the one this take is using": record the
    /// result when the take starts, then compare its <see cref="CaptureEndpoint.Id"/>
    /// with the one <see cref="CaptureEndpointRemoved"/> carries. Not the live
    /// default at removal time — Windows usually switches the default to
    /// another mic before the removal is processed.</para>
    /// </summary>
    public static CaptureEndpoint? DefaultCaptureEndpoint()
    {
        var watcher = Instance;
        if (watcher._defaultKnown && watcher.IsRunning) return watcher._defaultCapture;
        try
        {
            CaptureEndpoint? read = null;
            RunOnMta(() => read = ReadDefaultCapture(), TimeSpan.FromMilliseconds(500));
            return read;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"AudioDeviceWatcher: default read failed: {ex.GetBaseException().Message}");
            return null;
        }
    }

    /// <summary>
    /// The friendly name of the Windows default microphone, or null. Best
    /// effort and cheap; see <see cref="DefaultCaptureEndpoint"/>, which also
    /// gives the endpoint id (names are not unique, ids are).
    /// </summary>
    public static string? DefaultCaptureFriendlyName() => DefaultCaptureEndpoint()?.FriendlyName;

    /// <summary>
    /// Maps an endpoint friendly name (what Core Audio, these events and the
    /// Sound settings show) to the waveIn ProductName that
    /// <see cref="AudioRecorder"/> selects devices by and that the saved
    /// <c>MicDeviceName</c> holds.
    ///
    /// <para>The two differ because waveIn is the old winmm API: its
    /// <c>WAVEINCAPS.szPname</c> is a 32-character buffer, so Windows cuts the
    /// friendly name to 31 characters. "Microphone (Blue Yeti Pro Studio
    /// Edition)" is "Microphone (Blue Yeti Pro Studi" to waveIn. Comparing the
    /// two strings directly fails for every long name — exactly the USB and
    /// headset mics people plug in and out.</para>
    ///
    /// <para>Returns the live waveIn name that matches (exact, or a full-length
    /// 31-character prefix). If none does — typically because the device was
    /// just removed and is no longer listed — returns the 31-character
    /// truncation, which is what waveIn reports while the device is present,
    /// so the result can still be compared with a saved name.</para>
    /// </summary>
    public static string WaveInNameFor(string friendlyName)
    {
        if (string.IsNullOrEmpty(friendlyName)) return "";
        return MatchWaveInName(friendlyName, LiveWaveInNames());
    }

    /// <summary>
    /// The reverse of <see cref="WaveInNameFor"/>: the full friendly name of the
    /// active microphone a waveIn name refers to, or null if none is present.
    /// For display ("Using Microphone (Blue Yeti Pro Studio Edition)").
    /// </summary>
    public static string? FriendlyNameFor(string waveInName)
    {
        if (string.IsNullOrEmpty(waveInName)) return null;
        var live = LiveWaveInNames();
        return ActiveCaptureDevices().FirstOrDefault(f =>
            string.Equals(MatchWaveInName(f, live), waveInName, StringComparison.Ordinal));
    }

    /// <summary>
    /// Pure matching rule behind <see cref="WaveInNameFor"/>, separate so it can
    /// be tested without devices. An exact match wins. Otherwise only a waveIn
    /// name of exactly 31 characters may match as a prefix: a shorter one was
    /// not truncated, so it must equal the friendly name in full — without this
    /// rule "Microphone" would claim "Microphone (2- USB Audio Device)".
    /// </summary>
    internal static string MatchWaveInName(string friendlyName, IEnumerable<string> waveInNames)
    {
        var names = waveInNames.ToList();
        var exact = names.FirstOrDefault(n => string.Equals(n, friendlyName, StringComparison.Ordinal));
        if (exact != null) return exact;

        var prefix = names.FirstOrDefault(n => n.Length == WaveInNameMax
            && friendlyName.StartsWith(n, StringComparison.Ordinal));
        if (prefix != null) return prefix;

        return friendlyName.Length > WaveInNameMax ? friendlyName[..WaveInNameMax] : friendlyName;
    }

    // ----- Drain (one job at a time, on the thread pool) -----

    private void Enqueue(Action job)
    {
        _work.Enqueue(job);
        if (Interlocked.CompareExchange(ref _draining, 1, 0) == 0)
        {
            ThreadPool.QueueUserWorkItem(_ => Drain());
        }
    }

    private void Drain()
    {
        while (true)
        {
            while (_work.TryDequeue(out var job))
            {
                try { job(); }
                catch (Exception ex) { Debug.WriteLine($"AudioDeviceWatcher: {ex.Message}"); }
            }
            Volatile.Write(ref _draining, 0);
            // A job enqueued after the last TryDequeue but before the flag was
            // cleared would otherwise sit there until the next notification.
            if (_work.IsEmpty || Interlocked.CompareExchange(ref _draining, 1, 0) != 0) return;
        }
    }

    /// <summary>Queue one device-list re-read; further requests until it runs are no-ops.</summary>
    private void RequestResync()
    {
        if (Interlocked.Exchange(ref _resyncQueued, 1) == 0)
        {
            Enqueue(() =>
            {
                Volatile.Write(ref _resyncQueued, 0);
                Resync();
            });
        }
    }

    /// <summary>
    /// Baseline: what is plugged in and which mic is the default right now.
    /// Raises nothing — those devices were there before we started listening.
    /// </summary>
    private void Prime()
    {
        if (_primed) return;
        try { _active = ReadActiveCapture(); } catch { }
        var current = ReadDefaultCapture();
        _defaultCaptureId = current?.Id;
        PublishDefault(current);
        _primed = true;
    }

    /// <summary>Make the drain's current default visible to <see cref="DefaultCaptureEndpoint"/>.</summary>
    private void PublishDefault(CaptureEndpoint? endpoint)
    {
        _defaultCapture = endpoint;
        _defaultKnown = true;
    }

    /// <summary>The friendly name for an endpoint id: from the tracked list if it is there, else read.</summary>
    private string NameFor(string id) =>
        _active.TryGetValue(id, out var name) ? name : ReadEndpointName(id) ?? "";

    private void Resync()
    {
        if (!_primed)
        {
            Prime();
            return;
        }

        Dictionary<string, string> now;
        try
        {
            now = ReadActiveCapture();
        }
        catch (Exception ex)
        {
            // The audio service is restarting; keep the last good list rather
            // than report every mic as removed.
            Debug.WriteLine($"AudioDeviceWatcher: resync failed: {ex.Message}");
            return;
        }

        var removed = _active.Where(kv => !now.ContainsKey(kv.Key)).ToList();
        var added = now.Where(kv => !_active.ContainsKey(kv.Key)).Select(kv => kv.Value).ToList();
        _active = now;

        // A default published before its name could be read gets the name now.
        if (_defaultCapture is { FriendlyName.Length: 0 } nameless && now.TryGetValue(nameless.Id, out var known))
        {
            PublishDefault(nameless with { FriendlyName = known });
        }

        foreach (var kv in removed)
        {
            RaiseEndpoint(CaptureEndpointRemoved, new CaptureEndpoint(kv.Key, kv.Value));
            Raise(CaptureDeviceRemoved, kv.Value);
        }
        foreach (var name in added) Raise(CaptureDeviceAdded, name);
    }

    private void OnDefaultChanged(string? deviceId)
    {
        if (!_primed)
        {
            // The baseline read already sees the new default.
            Prime();
            return;
        }
        if (string.Equals(deviceId, _defaultCaptureId, StringComparison.OrdinalIgnoreCase)) return;
        _defaultCaptureId = deviceId;
        // Windows passes a null id when the last microphone went away.
        PublishDefault(string.IsNullOrEmpty(deviceId) ? null : new CaptureEndpoint(deviceId, NameFor(deviceId)));

        var handler = DefaultCaptureDeviceChanged;
        if (handler == null) return;
        foreach (var d in handler.GetInvocationList())
        {
            try { ((Action)d)(); }
            catch (Exception ex) { Debug.WriteLine($"AudioDeviceWatcher subscriber: {ex.Message}"); }
        }
    }

    /// <summary>One throwing subscriber must not stop the others or the drain.</summary>
    private static void Raise(Action<string>? handler, string name)
    {
        if (handler == null) return;
        foreach (var d in handler.GetInvocationList())
        {
            try { ((Action<string>)d)(name); }
            catch (Exception ex) { Debug.WriteLine($"AudioDeviceWatcher subscriber: {ex.Message}"); }
        }
    }

    /// <summary><see cref="Raise"/> for the endpoint events.</summary>
    private static void RaiseEndpoint(Action<CaptureEndpoint>? handler, CaptureEndpoint endpoint)
    {
        if (handler == null) return;
        foreach (var d in handler.GetInvocationList())
        {
            try { ((Action<CaptureEndpoint>)d)(endpoint); }
            catch (Exception ex) { Debug.WriteLine($"AudioDeviceWatcher subscriber: {ex.Message}"); }
        }
    }

    // ----- Core Audio reads (MTA) -----

    /// <summary>Endpoint id → friendly name for every active capture endpoint. Throws on failure.</summary>
    private static Dictionary<string, string> ReadActiveCapture()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            try
            {
                map[device.ID] = device.FriendlyName;
            }
            catch
            {
                // A device that vanished between listing and reading.
            }
            finally
            {
                try { device.Dispose(); } catch { GC.SuppressFinalize(device); }
            }
        }
        return map;
    }

    /// <summary>The default capture endpoint's id and friendly name, or null. Never throws.</summary>
    private static CaptureEndpoint? ReadDefaultCapture()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            // Console is the role the Sound settings "Default device" sets and
            // the one waveIn's WAVE_MAPPER (device -1, which AudioRecorder uses
            // for "follow Windows") follows.
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console)) return null;
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            try
            {
                var id = device.ID;
                string name;
                try { name = device.FriendlyName; } catch { name = ""; }
                return string.IsNullOrEmpty(id) ? null : new CaptureEndpoint(id, name);
            }
            finally { try { device.Dispose(); } catch { GC.SuppressFinalize(device); } }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>One endpoint's friendly name by id, or null if it cannot be read (already gone). Never throws.</summary>
    private static string? ReadEndpointName(string id)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(id);
            try { return device.FriendlyName; }
            finally { try { device.Dispose(); } catch { GC.SuppressFinalize(device); } }
        }
        catch
        {
            return null;
        }
    }

    private static List<string> LiveWaveInNames()
    {
        var names = new List<string>();
        try
        {
            for (var i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                try { names.Add(WaveInEvent.GetCapabilities(i).ProductName); } catch { }
            }
        }
        catch { }
        return names;
    }

    /// <summary>
    /// Runs Core Audio work on an MTA thread. MMDevice objects are free-threaded,
    /// but created on the WPF (STA) thread they would be bound to it, and the
    /// notification registration in particular must not depend on a UI message
    /// pump. Thread-pool threads are MTA, so off the UI thread this runs inline.
    /// </summary>
    private static void RunOnMta(Action action, TimeSpan timeout)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA)
        {
            action();
            return;
        }
        var task = Task.Run(action);
        if (!task.Wait(timeout)) throw new TimeoutException("Core Audio did not answer in time.");
    }

    /// <summary>
    /// The COM callback. Runs on a Windows audio RPC thread; only queues work.
    /// </summary>
    private sealed class NotificationSink : IMMNotificationClient
    {
        private readonly AudioDeviceWatcher _owner;

        public NotificationSink(AudioDeviceWatcher owner) => _owner = owner;

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _owner.RequestResync();

        public void OnDeviceAdded(string pwstrDeviceId) => _owner.RequestResync();

        public void OnDeviceRemoved(string deviceId) => _owner.RequestResync();

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            // Windows sends one call per role (Console, Multimedia,
            // Communications). Console alone is what "default" means to us.
            if (flow == DataFlow.Capture && role == Role.Console)
            {
                _owner.Enqueue(() => _owner.OnDefaultChanged(defaultDeviceId));
            }
        }

        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
        {
            // Fires constantly (jack sensing, format changes); nothing here needs it.
        }
    }
}
