using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FlowType.Core;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace FlowType.Audio;

/// <summary>
/// Silences other apps (music, videos, calls) while the microphone is open, and
/// gives every one of them back exactly the volume it had.
///
/// <para><b>Why per-app sessions, not the master volume.</b> Muting the
/// endpoint would also silence FlowType's own start/stop chimes, and it is the
/// one setting users notice immediately if a crash leaves it behind. Every
/// Windows app that plays audio owns an audio session per output device, with
/// its own volume slider and mute flag (the ones in the Volume Mixer), so we
/// fade and mute those instead and leave the endpoint alone. This is what
/// Wispr Flow does on Windows by default.</para>
///
/// <para><b>When it acts.</b> Only when at least one other session is audibly
/// playing at the moment dictation starts. If nothing is playing there is
/// nothing to protect the microphone from, and touching a dozen idle sessions
/// would just add one more thing that can go wrong; <see cref="RestoreAsync"/>
/// is then a no-op. When something is playing, every other session on every
/// active output device is muted — an idle chat app that pings mid-sentence
/// would otherwise land in the recording too.</para>
///
/// <para><b>What it never touches.</b> FlowType's own process (its chimes must
/// still play), the Windows system-sounds session, and any session the user had
/// already muted or turned all the way down — restoring those would "unmute"
/// something the user silenced on purpose.</para>
///
/// <para><b>Ordering with the chimes.</b> The start chime plays from our own
/// session, so the caller plays it first and then calls
/// <see cref="MuteOthersAsync"/>: the fade-out runs under the chime. On stop,
/// restore before (or alongside) the stop chime. Both chimes are unaffected
/// either way because our session is excluded.</para>
///
/// <para><b>Respecting the user.</b> When restoring, the mute flag and the
/// volume are judged separately. A session whose mute flag no longer matches
/// what we left is skipped: the user (or the app) chose its mute state while
/// we held it, and that choice wins. A session still muted by us whose slider
/// moved is unmuted at the new volume, without a fade: the mute is ours to
/// undo, the volume is theirs to keep. Leaving it muted, as an earlier version
/// did, silenced the app with nothing left to undo it.</para>
///
/// <para><b>Crash safety.</b> Windows remembers per-app volume and mute across
/// restarts of the app, so a FlowType crash while holding a mute would leave
/// someone's music silent indefinitely. Before changing anything, the list of
/// sessions (process id, process name, session identifier, original volume) is
/// written to <c>%APPDATA%\FlowType\muted-sessions.json</c>; it is deleted once
/// everything is restored. <see cref="RestoreAfterCrash"/> runs at startup and
/// unmutes whatever that file names that is still muted. Entries whose app is
/// not running are kept (for up to a week) and retried on every later
/// dictation, because the app will come back muted when it is next started —
/// and, while any are pending, by a background poll (every few seconds at
/// first, then every half minute), so a relaunched app does not play silently
/// until the next dictation. Other instances of the same app that were
/// already muted, or already running, when the mute was taken are recorded
/// and never touched by that retry: only a session created later — the
/// relaunch — is ours to unmute.</para>
///
/// <para><b>Threading.</b> Core Audio objects are created and used only on
/// thread-pool threads, which are in the multithreaded apartment. Creating them
/// on the WPF (STA) thread would tie every later call — including session
/// notifications NAudio registers internally — to that thread's message pump.
/// Operations are serialized by a gate, so a stop that arrives during the
/// fade-out simply runs after it. Nothing here throws to callers: muting is a
/// courtesy, and a failure must never cost a dictation.</para>
///
/// <para>The caller checks <c>Settings.MuteMediaWhileDictating</c>; this class
/// does not read settings.</para>
/// </summary>
public sealed class MediaMuter
{
    public static MediaMuter Instance { get; } = new();

    /// <summary>
    /// A session counts as playing above this peak (linear, 0..1; ≈ -60 dBFS).
    /// Low enough to catch quiet music, high enough to ignore the dither and
    /// denormal noise some players emit while paused.
    /// </summary>
    private const float PlayingPeak = 0.001f;

    /// <summary>
    /// The meter reports the peak of the last device period (~10 ms), so a
    /// single read can land in a gap between notes or in a song's quiet bar.
    /// A few reads a beat apart cost ~40 ms at worst and only when nothing is
    /// found on the first.
    /// </summary>
    private const int PeakReads = 3;
    private static readonly TimeSpan PeakReadGap = TimeSpan.FromMilliseconds(20);

    private const int FadeOutSteps = 5;
    private static readonly TimeSpan FadeOutDuration = TimeSpan.FromMilliseconds(150);
    private const int FadeInSteps = 6;
    private static readonly TimeSpan FadeInDuration = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// How far a read-back volume may drift from the value we set and still
    /// count as "ours". The engine stores the float we hand it, so in practice
    /// the round trip is exact; this only absorbs drivers that quantize. A user
    /// dragging the Volume Mixer slider moves it by whole percent or more.
    /// </summary>
    private const float VolumeTolerance = 0.005f;

    /// <summary>Sessions at or below this volume are inaudible already; leave them be.</summary>
    private const float SilentVolume = 0.0001f;

    /// <summary>
    /// Backstop: a hold that outlives the take means some exit path forgot to
    /// restore. Give the audio back rather than leave it silent until the next
    /// dictation. Since 2.2.0 a take has no length limit by default, so when
    /// this fires while the recorder is still recording it re-arms instead of
    /// un-muting in the middle of a long dictation.
    /// </summary>
    private static readonly TimeSpan MaxHold = TimeSpan.FromMinutes(65);

    /// <summary>
    /// How long a session we could not restore (its app had quit) is
    /// remembered. After that, a muted session with the same identity is more
    /// likely the user's own doing than our leftover.
    /// </summary>
    private static readonly TimeSpan OrphanLifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// While orphans are pending, how often to look for the app coming back.
    /// Fast at first, because the usual case is a reloaded tab or a restarted
    /// player moments later; slow after <see cref="OrphanFastWindow"/>, because
    /// a scan opens every output device and the wait can last days.
    /// </summary>
    private static readonly TimeSpan OrphanPollFast = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan OrphanPollSlow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan OrphanFastWindow = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Sessions muted by the current hold. Touched only inside the gate.</summary>
    private readonly List<HeldSession> _held = new();

    /// <summary>
    /// Sessions from an earlier hold (or a crashed run) that could not be
    /// restored because the session no longer existed. Touched only inside the gate.
    /// </summary>
    private readonly List<HeldSession> _orphans = new();

    /// <summary>
    /// The most recent request; every operation reconciles toward it rather
    /// than doing what it was queued to do. The gate is not FIFO, so a stop
    /// immediately followed by a new dictation can run the two in either
    /// order — without this, a restore landing second would un-mute the music
    /// for the whole of the new dictation. It also means a mute still queued
    /// when a restore arrives (a quick tap, a cancelled shortcut) is skipped
    /// instead of fading the music out only to fade it straight back in.
    /// </summary>
    private volatile bool _wantMuted;

    private volatile bool _isMuted;
    private System.Threading.Timer? _watchdog;

    /// <summary>The pending-orphan poll (see <see cref="OrphanPollFast"/>). Touched only inside the gate.</summary>
    private System.Threading.Timer? _orphanPoll;
    private DateTime _orphanPollSinceUtc;

    private MediaMuter() { }

    /// <summary>True while other apps' sessions are held muted by us.</summary>
    public bool IsMuted => _isMuted;

    /// <summary>
    /// One line describing what the last operation did ("muted 3 sessions",
    /// "skipped: nothing playing"…). For the attempt log and --selftest only.
    /// </summary>
    public string LastAction { get; private set; } = "none";

    /// <summary>
    /// Where the crash-recovery list lives. Settable only so the private test
    /// harness can point it at a scratch folder instead of the user's data.
    /// </summary>
    internal string StateFile { get; set; } = Path.Combine(AppPaths.DataDir, "muted-sessions.json");

    /// <summary>
    /// Test hook: when set, only sessions whose process id passes are
    /// considered at all. Lets a self-test mute a session it created itself
    /// without touching anything the user is listening to. Always null in the app.
    /// </summary>
    internal Func<uint, bool>? TargetFilter { get; set; }

    // ----- Public operations -----

    /// <summary>
    /// Fade out and mute every other app's audio session, if anything is
    /// audibly playing. Idempotent while already muted. Never throws.
    /// </summary>
    public Task MuteOthersAsync()
    {
        _wantMuted = true;
        return RunExclusiveAsync(MuteCoreAsync);
    }

    /// <summary>
    /// Unmute and fade back to the exact original volume every session this
    /// instance muted, except ones the user has changed since. A no-op when
    /// nothing is held. Never throws.
    /// </summary>
    public Task RestoreAsync()
    {
        _wantMuted = false;
        return RunExclusiveAsync(RestoreCoreAsync);
    }

    /// <summary>
    /// Startup recovery: unmute sessions named in the crash-recovery file that
    /// are still muted, and put their original volume back. Synchronous (at most
    /// a few seconds) so it completes before the first dictation can start.
    /// Never throws.
    /// </summary>
    public void RestoreAfterCrash()
    {
        try
        {
            RunExclusiveAsync(RecoverCoreAsync).Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MediaMuter: crash recovery failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Read-only listing of every audio session on every active output device:
    /// device, process, volume, mute, live peak, state, and whether it would be
    /// a mute target. For --selftest and diagnostics; changes nothing.
    /// </summary>
    public static IReadOnlyList<string> DescribeSessions()
    {
        try
        {
            var scanTask = Task.Run(() =>
            {
                var result = new List<string>();
                var own = (uint)Environment.ProcessId;
                using var scan = SessionScan.Open();
                foreach (var s in scan.Sessions)
                {
                    var role = s.IsSystem ? "system"
                        : s.ProcessId == own ? "own"
                        : s.State == AudioSessionState.AudioSessionStateExpired ? "expired"
                        : ReadMute(s) == true ? "user-muted"
                        : (ReadVolume(s) ?? 0) <= SilentVolume ? "silent-slider"
                        : "target";
                    result.Add($"{s.Endpoint.Value} | {ProcessNameOf(s.ProcessId)} (pid {s.ProcessId}) | " +
                        $"vol {Fmt(ReadVolume(s))} | mute {Fmt(ReadMute(s))} | peak {ReadPeak(s):0.0000} | " +
                        $"{ShortState(s.State)} | {role}");
                }
                if (result.Count == 0) result.Add("(no audio sessions on any active output device)");
                return result;
            });
            return scanTask.Wait(TimeSpan.FromSeconds(5))
                ? scanTask.Result
                : new[] { "error: session scan timed out" };
        }
        catch (Exception ex)
        {
            return new[] { $"error: {ex.GetBaseException().Message}" };
        }
    }

    // ----- Core (always on a thread-pool thread, inside the gate) -----

    private async Task MuteCoreAsync()
    {
        if (_isMuted)
        {
            // Still holding from the previous dictation (its restore lost the
            // race, see _wantMuted). The backstop counts from this one.
            ArmWatchdog();
            return;
        }
        if (!_wantMuted)
        {
            LastAction = "skipped: restore requested before mute";
            return;
        }

        var own = (uint)Environment.ProcessId;
        using var scan = SessionScan.Open();
        var orphansChanged = ResolveOrphans(scan);
        if (orphansChanged) UpdateOrphanPoll(restartFast: false);

        // Everything we would mute: not ours, not system sounds, still alive,
        // audible (not muted, slider above zero), and allowed by the test filter.
        var targets = new List<(SessionScan.Entry Session, float Volume)>();
        foreach (var s in scan.Sessions)
        {
            if (s.IsSystem || s.ProcessId == own) continue;
            if (s.State == AudioSessionState.AudioSessionStateExpired) continue;
            if (TargetFilter != null && !TargetFilter(s.ProcessId)) continue;
            if (ReadMute(s) != false) continue;
            var volume = ReadVolume(s);
            if (volume is not > SilentVolume) continue;
            targets.Add((s, volume.Value));
        }

        if (targets.Count == 0 || !await AnyPlayingAsync(targets.Select(t => t.Session)).ConfigureAwait(false))
        {
            LastAction = "skipped: nothing playing";
            if (orphansChanged) SaveState();
            return;
        }
        // A restore that arrived while we were listening wins.
        if (!_wantMuted)
        {
            LastAction = "skipped: restore requested before mute";
            if (orphansChanged) SaveState();
            return;
        }

        var now = DateTime.UtcNow;
        _held.Clear();
        foreach (var (s, volume) in targets)
        {
            _held.Add(new HeldSession
            {
                ProcessId = s.ProcessId,
                ProcessName = ProcessNameOf(s.ProcessId),
                SessionIdentifier = s.SessionIdentifier,
                InstanceIdentifier = s.InstanceIdentifier,
                OriginalVolume = volume,
                LeftMuted = true,
                LeftVolume = volume,
                MutedAtUtc = now,
                LeaveAlone = OtherInstances(scan, s),
            });
        }

        // Written before the first change: a crash at any point after this
        // line is recoverable from the file.
        SaveState();
        _isMuted = true;

        await FadeAsync(targets.Select(t => (t.Session, t.Volume, 0f)).ToList(),
            FadeOutSteps, FadeOutDuration, easeOut: true).ConfigureAwait(false);

        // Mute, then put the slider back where it was while muted. A crash now
        // leaves only a mute flag behind (one click in the Volume Mixer), never
        // a slider at zero that the user would have to find and drag back.
        for (var i = 0; i < targets.Count; i++)
        {
            var (s, volume) = targets[i];
            var held = _held[i];
            var muted = false;
            try { s.Volume!.Mute = true; muted = true; } catch { }
            // Only with the mute in place: putting the slider back on a session
            // that refused to mute would bring the music back mid-dictation.
            if (muted)
            {
                try { s.Volume!.Volume = volume; } catch { }
            }
            // Remember what is actually there now, not what we asked for: the
            // restore compares against it to tell our state from the user's.
            held.LeftMuted = ReadMute(s) ?? muted;
            held.LeftVolume = ReadVolume(s) ?? volume;
        }

        ArmWatchdog();
        LastAction = $"muted {_held.Count} session(s): " +
            string.Join(", ", _held.Select(h => h.ProcessName).Distinct());
    }

    private async Task RestoreCoreAsync()
    {
        // Not holding anything, or a newer dictation already wants the
        // silence kept (see _wantMuted): nothing to do.
        if (!_isMuted || _wantMuted) return;

        _watchdog?.Dispose();
        _watchdog = null;

        using var scan = SessionScan.Open();
        var byInstance = new Dictionary<string, SessionScan.Entry>(StringComparer.Ordinal);
        foreach (var s in scan.Sessions)
        {
            if (s.InstanceIdentifier.Length > 0) byInstance.TryAdd(s.InstanceIdentifier, s);
        }

        var ramps = new List<(SessionScan.Entry Session, float From, float To)>();
        int restored = 0, userChanged = 0, keptVolume = 0, gone = 0;
        foreach (var held in _held)
        {
            if (!byInstance.TryGetValue(held.InstanceIdentifier, out var s)
                || s.State == AudioSessionState.AudioSessionStateExpired)
            {
                // The app quit (or its device went away) while muted. Windows
                // may hand that mute back to it on its next launch, so keep the
                // record and retry by session identity on later operations.
                _orphans.Add(held);
                gone++;
                continue;
            }

            var mute = ReadMute(s);
            var volume = ReadVolume(s);
            if (mute == null || volume == null) { gone++; continue; }

            // The mute flag and the slider are separate decisions. If the flag
            // is not what we left, the user (or the app) chose the mute state:
            // theirs, leave it.
            if (mute.Value != held.LeftMuted)
            {
                userChanged++;
                continue;
            }
            if (Math.Abs(volume.Value - held.LeftVolume) > VolumeTolerance)
            {
                if (held.LeftMuted)
                {
                    // Still muted by us, but the slider moved (dragged in the
                    // Volume Mixer, or the app set its own level). The mute is
                    // ours to undo and the volume theirs to keep: unmute at the
                    // new level, with no zeroing and no fade. Skipping it, as
                    // before, left the app muted with nothing to undo it.
                    try { s.Volume!.Mute = false; } catch { }
                    keptVolume++;
                }
                else
                {
                    // The mute had failed and the fade-out left the slider
                    // down; they have moved it since, so it is theirs now.
                    userChanged++;
                }
                continue;
            }

            float from;
            if (held.LeftMuted)
            {
                // Drop the (inaudible, muted) slider to zero first so unmuting
                // does not jump straight to full volume, then fade it up.
                try { s.Volume!.Volume = 0f; } catch { }
                try { s.Volume!.Mute = false; } catch { }
                from = 0f;
            }
            else
            {
                // The mute itself failed earlier; the session sits at whatever
                // the fade-out left it at. Fade up from there.
                from = volume.Value;
            }
            ramps.Add((s, from, held.OriginalVolume));
            restored++;
        }

        await FadeAsync(ramps, FadeInSteps, FadeInDuration, easeOut: false).ConfigureAwait(false);

        _held.Clear();
        _isMuted = false;
        // A held app that restarted mid-dictation (a reloaded browser tab, a
        // relaunched player) has a new session that Windows started muted
        // from our hold; it is in this scan, so fix it now, not next time.
        ResolveOrphans(scan);
        SaveState();
        UpdateOrphanPoll(restartFast: gone > 0);
        LastAction = $"restored {restored}" +
            (keptVolume > 0 ? $", unmuted {keptVolume} at the user's new volume" : "") +
            (userChanged > 0 ? $", left {userChanged} the user changed" : "") +
            (gone > 0 ? $", {gone} gone (kept for retry)" : "");
    }

    private Task RecoverCoreAsync()
    {
        var saved = LoadState();
        if (saved.Count == 0) return Task.CompletedTask;

        foreach (var entry in saved)
        {
            var known = _held.Any(h => h.InstanceIdentifier == entry.InstanceIdentifier)
                || _orphans.Any(o => o.InstanceIdentifier == entry.InstanceIdentifier);
            if (!known) _orphans.Add(entry);
        }

        using var scan = SessionScan.Open();
        ResolveOrphans(scan);
        SaveState();
        UpdateOrphanPoll(restartFast: true);
        LastAction = $"crash recovery: {saved.Count} recorded, {_orphans.Count} still pending";
        return Task.CompletedTask;
    }

    /// <summary>
    /// The background retry for pending orphans (see <see cref="OrphanPollFast"/>).
    /// Runs under the gate like every other operation.
    /// </summary>
    private Task PollOrphansAsync()
    {
        // During a dictation a relaunched app stays muted like everything
        // else; the restore at the end resolves it from its own scan.
        if (_orphans.Count > 0 && !_isMuted)
        {
            using var scan = SessionScan.Open();
            if (ResolveOrphans(scan)) SaveState();
        }
        UpdateOrphanPoll(restartFast: false);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Start, reschedule or stop the orphan poll so it runs exactly while
    /// orphans are pending. One-shot timer, re-armed after each run, so a slow
    /// scan can never pile up behind itself. Inside the gate.
    /// </summary>
    private void UpdateOrphanPoll(bool restartFast)
    {
        if (_orphans.Count == 0)
        {
            _orphanPoll?.Dispose();
            _orphanPoll = null;
            return;
        }
        var now = DateTime.UtcNow;
        if (restartFast || _orphanPoll == null) _orphanPollSinceUtc = now;
        var due = now - _orphanPollSinceUtc < OrphanFastWindow ? OrphanPollFast : OrphanPollSlow;
        // Thread-pool callback (MTA); the scan itself runs inside RunExclusiveAsync.
        _orphanPoll ??= new System.Threading.Timer(_ => _ = RunExclusiveAsync(PollOrphansAsync), null,
            Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _orphanPoll.Change(due, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// The instance identifiers of every other session of the same app on the
    /// same device (same session identifier) present when the mute was taken:
    /// ones the user had muted, ones excluded for any other reason, and other
    /// sessions we hold under their own record. An orphan retry must leave all
    /// of them alone — only a session created after this moment can be the
    /// app relaunched under the mute Windows remembered from our hold.
    /// </summary>
    private static List<string> OtherInstances(SessionScan scan, SessionScan.Entry target)
    {
        if (target.SessionIdentifier.Length == 0) return new List<string>();
        return scan.Sessions
            .Where(o => !ReferenceEquals(o, target) && o.InstanceIdentifier.Length > 0
                        && string.Equals(o.SessionIdentifier, target.SessionIdentifier, StringComparison.OrdinalIgnoreCase))
            .Select(o => o.InstanceIdentifier)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Unmute sessions left over from an earlier hold. Matched by session
    /// identifier (app + device, stable across restarts of the app) rather
    /// than instance identifier, because the usual case is the app having
    /// been restarted and Windows reapplying the remembered mute. Sessions
    /// recorded in <see cref="HeldSession.LeaveAlone"/> are never candidates:
    /// another instance of the same app that the user muted themselves is not
    /// ours to unmute. Returns true if the orphan list changed.
    /// </summary>
    private bool ResolveOrphans(SessionScan scan)
    {
        if (_orphans.Count == 0) return false;

        var own = (uint)Environment.ProcessId;
        var now = DateTime.UtcNow;
        var changed = _orphans.RemoveAll(o => now - o.MutedAtUtc > OrphanLifetime) > 0;
        // An entry without a session identifier would match every session
        // that also failed to report one; it can never be resolved safely.
        changed |= _orphans.RemoveAll(o => string.IsNullOrEmpty(o.SessionIdentifier)) > 0;

        foreach (var orphan in _orphans.ToList())
        {
            var leaveAlone = new HashSet<string>(orphan.LeaveAlone ?? new List<string>(), StringComparer.Ordinal);
            var matches = scan.Sessions.Where(s => !s.IsSystem && s.ProcessId != own
                && s.State != AudioSessionState.AudioSessionStateExpired
                && string.Equals(s.SessionIdentifier, orphan.SessionIdentifier, StringComparison.OrdinalIgnoreCase)
                && !leaveAlone.Contains(s.InstanceIdentifier))
                .ToList();
            // App not running yet — or only the instances that were already
            // there when we muted, which say nothing about the relaunch.
            if (matches.Count == 0) continue;

            foreach (var s in matches)
            {
                // Only a session that is still muted is ours to fix. One that
                // is playing already was unmuted by the user — leave it.
                if (ReadMute(s) != true) continue;
                try { s.Volume!.Volume = orphan.OriginalVolume; } catch { }
                try { s.Volume!.Mute = false; } catch { }
            }
            _orphans.Remove(orphan);
            changed = true;
        }
        return changed;
    }

    // ----- Helpers -----

    /// <summary>(Re)starts the <see cref="MaxHold"/> backstop. Inside the gate.</summary>
    private void ArmWatchdog()
    {
        _watchdog?.Dispose();
        _watchdog = new System.Threading.Timer(_ => OnWatchdog(), null,
            MaxHold, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// The <see cref="MaxHold"/> backstop fired. A take still in progress is a
    /// long dictation, not a forgotten restore: check again later. Re-armed
    /// through the gate so it never races a real restore's watchdog reset.
    /// </summary>
    private void OnWatchdog()
    {
        if (AudioRecorder.Instance.IsRecording)
        {
            _ = RunExclusiveAsync(() =>
            {
                if (_watchdog != null && AudioRecorder.Instance.IsRecording) ArmWatchdog();
                return Task.CompletedTask;
            });
            return;
        }
        _ = RestoreAsync();
    }

    /// <summary>
    /// Runs one operation on the thread pool (MTA) under the gate and swallows
    /// every failure. The returned task always completes successfully.
    /// </summary>
    private Task RunExclusiveAsync(Func<Task> body) => Task.Run(async () =>
    {
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await body().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            LastAction = $"error: {ex.GetBaseException().Message}";
            Debug.WriteLine($"MediaMuter: {ex}");
        }
    });

    private static async Task<bool> AnyPlayingAsync(IEnumerable<SessionScan.Entry> sessions)
    {
        var list = sessions.ToList();
        for (var i = 0; i < PeakReads; i++)
        {
            if (list.Any(s => ReadPeak(s) > PlayingPeak)) return true;
            if (i < PeakReads - 1) await Task.Delay(PeakReadGap).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>
    /// Steps every ramp together. The curve is shaped so most of the change
    /// happens at the loud end: hearing is logarithmic, and a linear amplitude
    /// ramp sounds like nothing happens and then a sudden cut. Out:
    /// 1-(1-t)², fast drop then a soft landing. In: t², a soft start that
    /// arrives at full level. The last step writes the target exactly, so the
    /// restored volume is the original float, not an interpolated neighbour.
    /// </summary>
    private static async Task FadeAsync(
        IReadOnlyList<(SessionScan.Entry Session, float From, float To)> ramps,
        int steps, TimeSpan duration, bool easeOut)
    {
        if (ramps.Count == 0) return;
        var gap = TimeSpan.FromTicks(duration.Ticks / steps);
        for (var k = 1; k <= steps; k++)
        {
            var t = k / (float)steps;
            var shaped = easeOut ? 1f - (1f - t) * (1f - t) : t * t;
            foreach (var (s, from, to) in ramps)
            {
                var value = k == steps ? to : from + (to - from) * shaped;
                try { s.Volume!.Volume = Math.Clamp(value, 0f, 1f); } catch { }
            }
            if (k < steps) await Task.Delay(gap).ConfigureAwait(false);
        }
    }

    private static bool? ReadMute(SessionScan.Entry s)
    {
        try { return s.Volume?.Mute; } catch { return null; }
    }

    private static float? ReadVolume(SessionScan.Entry s)
    {
        try { return s.Volume?.Volume; } catch { return null; }
    }

    private static float ReadPeak(SessionScan.Entry s)
    {
        try { return s.Meter?.MasterPeakValue ?? 0f; } catch { return 0f; }
    }

    private static string ProcessNameOf(uint pid)
    {
        if (pid == 0) return "(shared)";
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return "(exited)";
        }
    }

    private static string Fmt(float? v) => v is { } f ? f.ToString("0.000") : "?";
    private static string Fmt(bool? b) => b is { } x ? (x ? "yes" : "no") : "?";

    private static string ShortState(AudioSessionState state) => state switch
    {
        AudioSessionState.AudioSessionStateActive => "active",
        AudioSessionState.AudioSessionStateInactive => "inactive",
        AudioSessionState.AudioSessionStateExpired => "expired",
        _ => state.ToString(),
    };

    // ----- Crash-recovery file -----

    /// <summary>
    /// Writes held + orphaned sessions, or deletes the file when there are
    /// none. Deliberately not <see cref="JsonFile"/>: that helper keeps a .bak
    /// and falls back to it when the main file is missing, which here would
    /// resurrect an already-restored list and unmute things on the next start.
    /// </summary>
    private void SaveState()
    {
        try
        {
            var all = _held.Concat(_orphans).ToList();
            if (all.Count == 0)
            {
                TryDeleteFile(StateFile);
                TryDeleteFile(StateFile + ".tmp");
                return;
            }

            var dir = Path.GetDirectoryName(StateFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var temp = StateFile + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, all, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, StateFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MediaMuter: could not save {StateFile}: {ex.Message}");
        }
    }

    private List<HeldSession> LoadState()
    {
        try
        {
            if (!File.Exists(StateFile)) return new List<HeldSession>();
            var json = File.ReadAllText(StateFile);
            if (string.IsNullOrWhiteSpace(json)) return new List<HeldSession>();
            return (JsonSerializer.Deserialize<List<HeldSession>>(json) ?? new List<HeldSession>())
                .Where(h => !string.IsNullOrEmpty(h.SessionIdentifier))
                .ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MediaMuter: could not read {StateFile}: {ex.Message}");
            return new List<HeldSession>();
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>One muted session, as held in memory and as written to the recovery file.</summary>
    private sealed class HeldSession
    {
        public uint ProcessId { get; set; }
        public string ProcessName { get; set; } = "";

        /// <summary>App + device identity; stable across restarts of the app. Used for crash recovery.</summary>
        public string SessionIdentifier { get; set; } = "";

        /// <summary>This exact session (includes the process id). Used for a normal restore.</summary>
        public string InstanceIdentifier { get; set; } = "";

        public float OriginalVolume { get; set; }

        /// <summary>The mute flag and volume read back after we muted — "the value we set".</summary>
        public bool LeftMuted { get; set; }
        public float LeftVolume { get; set; }

        public DateTime MutedAtUtc { get; set; }

        /// <summary>
        /// Instance identifiers of the other sessions with this session
        /// identifier that existed when we muted (see <see cref="OtherInstances"/>).
        /// Empty in files written before it existed, which keeps the old matching.
        /// </summary>
        public List<string>? LeaveAlone { get; set; } = new();
    }

    // ----- Session enumeration -----

    /// <summary>
    /// A snapshot of every session on every active render endpoint. Owns every
    /// COM wrapper it creates, so one Dispose releases them all.
    ///
    /// The disposal is the delicate part. NAudio's AudioSessionManager
    /// registers a session-notification callback in its constructor and
    /// unregisters it in Dispose — and its finalizer calls the same Dispose,
    /// which throws if the unregister fails (audio service restarted, device
    /// unplugged). An exception on the finalizer thread terminates the process.
    /// So every device is disposed explicitly here, and if that throws, the
    /// finalizers are suppressed so it cannot happen again later.
    /// </summary>
    private sealed class SessionScan : IDisposable
    {
        public sealed class Entry
        {
            public required AudioSessionControl Control { get; init; }
            public SimpleAudioVolume? Volume { get; init; }
            public AudioMeterInformation? Meter { get; init; }
            public uint ProcessId { get; init; }
            public bool IsSystem { get; init; }
            public string SessionIdentifier { get; init; } = "";
            public string InstanceIdentifier { get; init; } = "";
            /// <summary>Device friendly name, read on first use (see <see cref="Open"/>).</summary>
            public required Lazy<string> Endpoint { get; init; }
            public AudioSessionState State { get; init; }
        }

        private readonly MMDeviceEnumerator? _enumerator;
        private readonly List<(MMDevice Device, AudioSessionManager? Manager)> _devices = new();

        public List<Entry> Sessions { get; } = new();

        private SessionScan(MMDeviceEnumerator? enumerator) => _enumerator = enumerator;

        public static SessionScan Open()
        {
            MMDeviceEnumerator? enumerator = null;
            try { enumerator = new MMDeviceEnumerator(); } catch { }
            var scan = new SessionScan(enumerator);
            if (enumerator == null) return scan;

            try
            {
                foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                {
                    AudioSessionManager? manager = null;
                    try
                    {
                        manager = device.AudioSessionManager;
                    }
                    catch
                    {
                        // Some virtual devices refuse session management.
                    }
                    scan._devices.Add((device, manager));
                    if (manager?.Sessions is not { } sessions) continue;

                    // Lazy: opening a device's property store costs 20-40 ms per
                    // endpoint (measured, 4 endpoints = ~110 ms), and that would sit
                    // between the hotkey and the fade. Only the diagnostic listing
                    // needs the name.
                    var endpoint = new Lazy<string>(() =>
                    {
                        try { return device.FriendlyName; } catch { return "(unknown device)"; }
                    });

                    int count;
                    try { count = sessions.Count; } catch { continue; }
                    for (var i = 0; i < count; i++)
                    {
                        try
                        {
                            var control = sessions[i];
                            scan.Sessions.Add(new Entry
                            {
                                Control = control,
                                Volume = control.SimpleAudioVolume,
                                Meter = control.AudioMeterInformation,
                                ProcessId = Try(() => control.GetProcessID, 0u),
                                IsSystem = Try(() => control.IsSystemSoundsSession, false),
                                SessionIdentifier = Try(() => control.GetSessionIdentifier, "") ?? "",
                                InstanceIdentifier = Try(() => control.GetSessionInstanceIdentifier, "") ?? "",
                                Endpoint = endpoint,
                                State = Try(() => control.State, AudioSessionState.AudioSessionStateExpired),
                            });
                        }
                        catch
                        {
                            // A session that vanished mid-enumeration; skip it.
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MediaMuter: endpoint enumeration failed: {ex.Message}");
            }
            return scan;
        }

        public void Dispose()
        {
            foreach (var s in Sessions)
            {
                try { s.Control.Dispose(); } catch { GC.SuppressFinalize(s.Control); }
            }
            foreach (var (device, manager) in _devices)
            {
                try
                {
                    device.Dispose();
                }
                catch
                {
                    GC.SuppressFinalize(device);
                    if (manager != null) GC.SuppressFinalize(manager);
                }
            }
            try { _enumerator?.Dispose(); } catch { }
        }

        private static T Try<T>(Func<T> read, T fallback)
        {
            try { return read(); } catch { return fallback; }
        }
    }
}
