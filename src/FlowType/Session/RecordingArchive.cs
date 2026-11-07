using System.Diagnostics;
using System.IO;
using FlowType.Core;
using NAudio.Wave;

namespace FlowType.Session;

/// <summary>
/// Keeps the most recent dictation takes (<see cref="AppSettings.KeepRecentRecordings"/>)
/// under <see cref="AppPaths.AudioArchiveDir"/> as <c>{noteId}.wav</c>, so a
/// note in History can be played back, saved, or transcribed again with a
/// different model. Wispr Flow keeps the audio of every History entry for the
/// same reason (its "retry" and "play audio" row actions); it ages audio out by
/// time, FlowType by count, because a count bounds disk use no matter how much
/// someone dictates in a day.
///
/// Design notes:
/// - <b>A separate folder from the in-flight takes.</b>
///   <c>AudioRecorder.CleanupOldRecordings()</c> wipes
///   <see cref="AppPaths.RecordingsDir"/> at every start-up, by design (a crash
///   mid-dictation must not leave speech lying around). The archive therefore
///   lives in its own directory that nothing but this class ever deletes from.
/// - <b><see cref="Keep"/> takes ownership of the take.</b> When it returns,
///   the WAV is no longer in the recordings folder — it was either moved into
///   the archive or deleted (archive off, or the move failed). Recordings are
///   the most private thing FlowType handles; a take that silently survives in
///   a folder the user never sees would break the "keep none" promise.
///   Files that are <i>not</i> in the recordings folder (a future "import
///   audio" path, a test) are copied instead and never deleted.
/// - <b>Every file operation is serialised</b> on one lock, so a prune can't
///   race a keep of the same file. Playback has its own lock and never takes the
///   file lock, so the order is always files → playback and cannot deadlock.
/// - <b>Playback never holds a file lock that blocks deletion.</b> Short takes
///   are played from memory; long ones through a stream opened with
///   <see cref="FileShare.Delete"/>. Deleting a note mid-playback therefore
///   always succeeds, and the UI never has to sequence "stop, wait, delete".
/// - Events are raised on the calling thread (or on NAudio's playback thread
///   for a clip that ends by itself when <see cref="Play"/> was not called from
///   the UI thread). Window code marshals with <c>Dispatcher.BeginInvoke</c>,
///   as it already does for <see cref="NotesStore.Changed"/>.
/// </summary>
public sealed class RecordingArchive
{
    /// <summary>
    /// Takes up to this size (~17 minutes of 16 kHz mono 16-bit audio) are read
    /// into memory for playback, so no file handle is held at all. Bigger ones
    /// stream from disk — a 60-minute cap would otherwise mean ~115 MB of RAM
    /// for one play button.
    /// </summary>
    private const long InMemoryPlaybackLimit = 32L * 1024 * 1024;

    /// <summary>
    /// <see cref="PruneOrphans"/> leaves files this young alone. The session
    /// archives a take just <i>before</i> it adds the note (so the History row
    /// appears with its play button already live), which means a take that has
    /// only just landed legitimately has no note yet.
    /// </summary>
    private static readonly TimeSpan OrphanGrace = TimeSpan.FromMinutes(2);

    private const string TakeExtension = ".wav";

    /// <summary>Suffix of a copy in progress; renamed into place only when complete.</summary>
    private const string PartialSuffix = ".partial";

    public static RecordingArchive Instance { get; } = new();

    private readonly object _gate = new();
    private readonly object _playGate = new();
    private readonly Func<int> _keepCount;

    /// <summary>The folder whose files <see cref="Keep"/> owns and may move or delete.</summary>
    private readonly string _takesDir;

    private Playback? _playback;

    /// <summary>Raised after the set of archived takes changed (kept, pruned, deleted).</summary>
    public event Action? Changed;

    /// <summary>
    /// (noteId, isPlaying). Raised with <c>true</c> when a recording starts and
    /// <c>false</c> when it ends by itself or is stopped. Switching from one
    /// note to another raises <c>(B, true)</c> and then <c>(A, false)</c>, so
    /// track state per note (or read <see cref="NowPlaying"/>), not globally.
    /// </summary>
    public event Action<Guid, bool>? PlaybackStateChanged;

    private RecordingArchive()
        : this(AppPaths.AudioArchiveDir, AppPaths.RecordingsDir,
            () => SettingsStore.Instance.Settings.KeepRecentRecordings)
    {
    }

    /// <summary>
    /// Test seam: an archive over arbitrary folders with its own retention
    /// count, so the keep/prune rules can be exercised on scratch files without
    /// touching the user's real recordings or settings. Does no I/O.
    /// </summary>
    internal RecordingArchive(string archiveDir, string takesDir, Func<int> keepCount)
    {
        ArchiveDir = archiveDir;
        _takesDir = takesDir;
        _keepCount = keepCount;
    }

    /// <summary>Where kept takes live (for an "Open folder" button).</summary>
    public string ArchiveDir { get; }

    /// <summary>How many takes the archive keeps right now (the setting, never negative).</summary>
    public int KeepCount => Math.Max(0, _keepCount());

    /// <summary>The note whose recording is playing, or null.</summary>
    public Guid? NowPlaying
    {
        get { lock (_playGate) return _playback?.NoteId; }
    }

    // ----- Keeping and pruning -----

    /// <summary>
    /// Move a finished take into the archive under this note's id, then prune
    /// the archive back to <see cref="KeepCount"/>. Returns the archived path,
    /// or null when nothing was kept (archive off, no such file, failure).
    ///
    /// Call it before <see cref="NotesStore.Add"/> for the same note, and only
    /// when the note is actually being saved — a take with no note is an
    /// orphan and is deleted at the next start-up.
    /// </summary>
    /// <remarks>
    /// The move is a rename within %APPDATA%\FlowType, so it is atomic: the
    /// archive never holds half a take. If the rename fails (another process
    /// has the file open without delete sharing), the take is copied to a
    /// <c>.partial</c> file and renamed into place, and the original is
    /// deleted — retried in the background if it is still locked, and swept by
    /// the start-up cleanup in the worst case.
    /// </remarks>
    public string? Keep(Guid noteId, string? wavPath)
    {
        if (string.IsNullOrWhiteSpace(wavPath)) return null;

        var target = PathOf(noteId);
        string? kept = null;
        lock (_gate)
        {
            if (SamePath(wavPath, target))
            {
                // Already archived (e.g. a re-transcription handing back the
                // archived path). Nothing to move; don't reorder it either.
                kept = File.Exists(target) ? target : null;
            }
            else if (!File.Exists(wavPath))
            {
                return null;
            }
            else
            {
                var owned = IsUnder(wavPath, _takesDir);
                if (KeepCount == 0)
                {
                    if (owned) DeleteSource(wavPath);
                }
                else
                {
                    // A replaced take may be the one playing; stop it rather than
                    // play audio that no longer belongs to the note.
                    StopPlayback(noteId);
                    kept = owned ? MoveIn(wavPath, target) : CopyIn(wavPath, target);
                    if (kept != null) Touch(kept);
                }
            }
            PruneLocked();
        }
        RaiseChanged();
        return kept;
    }

    /// <summary>
    /// Delete the oldest takes beyond <see cref="KeepCount"/> (all of them when
    /// it is 0) and any abandoned partial copies. Returns how many takes were
    /// deleted. Cheap; call it whenever the setting changes.
    /// </summary>
    public int Prune()
    {
        int removed;
        lock (_gate) removed = PruneLocked();
        if (removed > 0) RaiseChanged();
        return removed;
    }

    /// <summary>
    /// Start-up sweep: delete takes whose note no longer exists (deleted while
    /// the app was closed, trimmed past the history cap, or kept just before a
    /// crash), then <see cref="Prune"/>. Takes younger than two minutes are
    /// left alone — see <see cref="OrphanGrace"/>. Returns how many were deleted.
    /// </summary>
    public int PruneOrphans(IEnumerable<Guid> liveNoteIds)
    {
        var live = new HashSet<Guid>(liveNoteIds);
        var cutoff = DateTime.UtcNow - OrphanGrace;
        var removed = 0;
        lock (_gate)
        {
            foreach (var take in ListTakes())
            {
                if (live.Contains(take.NoteId) || take.WrittenUtc > cutoff) continue;
                if (TryDeleteFile(take.Path)) removed++;
            }
            removed += PruneLocked();
        }
        if (removed > 0) RaiseChanged();
        return removed;
    }

    // ----- Lookup -----

    /// <summary>Whether this note still has its recording.</summary>
    public bool Has(Guid noteId) => File.Exists(PathOf(noteId));

    /// <summary>The archived WAV for this note, or null when there is none.</summary>
    public string? PathFor(Guid noteId)
    {
        var path = PathOf(noteId);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Number of archived takes and their total size, for the Privacy settings line.</summary>
    public (int Count, long Bytes) Usage()
    {
        lock (_gate)
        {
            var takes = ListTakes();
            return (takes.Count, takes.Sum(t => t.Bytes));
        }
    }

    /// <summary>
    /// Copy a note's recording somewhere the user chose ("Save audio…").
    /// Returns false when the note has no recording; I/O errors propagate so
    /// the window can show them, as <see cref="NotesStore.ExportTo"/> does.
    /// </summary>
    public bool ExportTo(Guid noteId, string destinationPath)
    {
        // Deliberately outside the file lock: an hour-long take takes a while
        // to copy and must not hold up the Keep at the end of a dictation. A
        // prune that lands mid-copy just makes the copy fail visibly.
        var path = PathFor(noteId);
        if (path == null) return false;
        File.Copy(path, destinationPath, overwrite: true);
        return true;
    }

    // ----- Deleting -----

    /// <summary>Delete one note's recording (stopping it first if it is playing).</summary>
    public bool Delete(Guid noteId)
    {
        StopPlayback(noteId);
        bool removed;
        lock (_gate)
        {
            var path = PathOf(noteId);
            removed = File.Exists(path) && TryDeleteFile(path);
        }
        if (removed) RaiseChanged();
        return removed;
    }

    /// <summary>Delete every archived take and any partial copy (History → Clear).</summary>
    public int DeleteAll()
    {
        StopPlayback();
        var removed = 0;
        lock (_gate)
        {
            foreach (var take in ListTakes())
            {
                if (TryDeleteFile(take.Path)) removed++;
            }
            foreach (var partial in ListPartials())
            {
                TryDeleteFile(partial);
            }
        }
        if (removed > 0) RaiseChanged();
        return removed;
    }

    // ----- Playback -----

    /// <summary>
    /// Play a note's recording through the default output device, stopping
    /// whatever was playing. Returns false when there is no recording or no
    /// usable output device. One playback at a time.
    /// </summary>
    public bool Play(Guid noteId)
    {
        var path = PathFor(noteId);
        if (path == null) return false;

        Playback session;
        lock (_playGate)
        {
            var previous = _playback;
            _playback = null;
            if (previous != null) StopSession(previous);

            try
            {
                session = Playback.Open(noteId, path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Recording playback could not open the take: {ex.Message}");
                return false;
            }

            try
            {
                // Created on the UI thread, WaveOutEvent captures its
                // SynchronizationContext and posts PlaybackStopped back to it.
                session.Output.PlaybackStopped += (_, _) => OnPlaybackStopped(session);
                session.Output.Init(session.Reader);
                session.Output.Play();
                _playback = session;
            }
            catch (Exception ex)
            {
                // No output device, device busy, driver refused the format…
                Debug.WriteLine($"Recording playback failed to start: {ex.Message}");
                session.Dispose();
                return false;
            }
        }

        // Announce outside the playback lock. If the clip already ended (only
        // possible off the UI thread), neither event is raised: from the
        // listener's point of view nothing is playing, which is the truth.
        var announce = false;
        lock (session.Sync)
        {
            if (!session.Ended)
            {
                session.Announced = true;
                announce = true;
            }
        }
        if (announce) RaisePlayback(noteId, true);
        return true;
    }

    /// <summary>Stop whatever recording is playing. Safe to call when nothing is.</summary>
    public void StopPlayback()
    {
        Playback? current;
        lock (_playGate)
        {
            current = _playback;
            _playback = null;
        }
        if (current != null) StopSession(current);
    }

    /// <summary>Stop playback only if it is this note's recording.</summary>
    private void StopPlayback(Guid noteId)
    {
        Playback? current;
        lock (_playGate)
        {
            if (_playback == null || _playback.NoteId != noteId) return;
            current = _playback;
            _playback = null;
        }
        StopSession(current);
    }

    private void StopSession(Playback session)
    {
        // Stop() makes NAudio's playback thread exit and raise PlaybackStopped,
        // which is where the device and reader are disposed (NAudio's own
        // recommended pattern: disposing from outside races the thread's last
        // buffer read). If Stop itself fails, that event may never come, so
        // finish the session by hand.
        if (!session.TryStop()) OnPlaybackStopped(session);
    }

    private void OnPlaybackStopped(Playback session)
    {
        lock (_playGate)
        {
            if (ReferenceEquals(_playback, session)) _playback = null;
        }
        session.Dispose();

        bool announce;
        lock (session.Sync)
        {
            if (session.Ended) return;
            session.Ended = true;
            announce = session.Announced;
        }
        if (announce) RaisePlayback(session.NoteId, false);
    }

    /// <summary>One playing take: the output device, its reader and the bytes behind it.</summary>
    private sealed class Playback : IDisposable
    {
        public Guid NoteId { get; }
        public WaveOutEvent Output { get; }
        public WaveFileReader Reader { get; }
        private readonly Stream _source;
        private int _disposed;

        /// <summary>Guards <see cref="Announced"/>/<see cref="Ended"/> so start/stop events pair up.</summary>
        public object Sync { get; } = new();
        public bool Announced { get; set; }
        public bool Ended { get; set; }

        private Playback(Guid noteId, Stream source, WaveFileReader reader, WaveOutEvent output)
        {
            NoteId = noteId;
            _source = source;
            Reader = reader;
            Output = output;
        }

        public static Playback Open(Guid noteId, string path)
        {
            Stream source;
            var length = new FileInfo(path).Length;
            if (length <= InMemoryPlaybackLimit)
            {
                source = new MemoryStream(File.ReadAllBytes(path), writable: false);
            }
            else
            {
                source = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
            }

            WaveFileReader? reader = null;
            try
            {
                reader = new WaveFileReader(source);
                return new Playback(noteId, source, reader, new WaveOutEvent());
            }
            catch
            {
                reader?.Dispose();
                source.Dispose();
                throw;
            }
        }

        public bool TryStop()
        {
            try
            {
                Output.Stop();
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Recording playback stop failed: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { Output.Dispose(); } catch { }
            // WaveFileReader(Stream) does not own the stream; close both.
            try { Reader.Dispose(); } catch { }
            try { _source.Dispose(); } catch { }
        }
    }

    // ----- File helpers (call with _gate held) -----

    private readonly record struct Take(Guid NoteId, string Path, DateTime WrittenUtc, long Bytes);

    private string PathOf(Guid noteId) => Path.Combine(ArchiveDir, noteId.ToString("D") + TakeExtension);

    /// <summary>Archived takes, newest first. Only files named <c>{guid}.wav</c> count — anything else in the folder is not ours to delete.</summary>
    private List<Take> ListTakes()
    {
        var takes = new List<Take>();
        try
        {
            if (!Directory.Exists(ArchiveDir)) return takes;
            foreach (var file in Directory.EnumerateFiles(ArchiveDir, "*" + TakeExtension))
            {
                if (!string.Equals(Path.GetExtension(file), TakeExtension, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "D", out var id)) continue;
                try
                {
                    var info = new FileInfo(file);
                    takes.Add(new Take(id, file, info.LastWriteTimeUtc, info.Length));
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Archive skipped {file}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Archive listing failed: {ex.Message}");
        }

        // Newest first; the name breaks ties so the order is deterministic.
        takes.Sort((a, b) =>
        {
            var byTime = b.WrittenUtc.CompareTo(a.WrittenUtc);
            return byTime != 0 ? byTime : string.CompareOrdinal(a.Path, b.Path);
        });
        return takes;
    }

    /// <summary>
    /// Leftover <c>{guid}.wav.partial</c> copies. A partial only exists for the
    /// length of one <see cref="Keep"/>, which holds the file lock, so any seen
    /// by another locked operation was abandoned by a crash.
    /// </summary>
    private List<string> ListPartials()
    {
        const string suffix = TakeExtension + PartialSuffix;
        var partials = new List<string>();
        try
        {
            if (!Directory.Exists(ArchiveDir)) return partials;
            foreach (var file in Directory.EnumerateFiles(ArchiveDir, "*" + suffix))
            {
                var name = Path.GetFileName(file);
                if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Guid.TryParseExact(name[..^suffix.Length], "D", out _)) continue;
                partials.Add(file);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Archive partial listing failed: {ex.Message}");
        }
        return partials;
    }

    private int PruneLocked()
    {
        var keep = KeepCount;
        var removed = 0;
        foreach (var take in ListTakes().Skip(keep))
        {
            if (TryDeleteFile(take.Path)) removed++;
        }
        // A partial is only ever alive for the length of one Keep (which holds
        // this lock), so any that exist now were abandoned by a crash.
        foreach (var partial in ListPartials())
        {
            TryDeleteFile(partial);
        }
        return removed;
    }

    /// <summary>Rename the take into the archive; fall back to copy + delete.</summary>
    private string? MoveIn(string source, string target)
    {
        try
        {
            Directory.CreateDirectory(ArchiveDir);
            File.Move(source, target, overwrite: true);
            return target;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Archive move failed, copying instead: {ex.Message}");
        }

        var result = CopyIn(source, target);
        // Whether or not the copy worked, the take must not stay behind.
        DeleteSource(source);
        return result;
    }

    /// <summary>Copy into a partial file, then rename it into place, so the archive never shows half a take.</summary>
    private string? CopyIn(string source, string target)
    {
        var partial = target + PartialSuffix;
        try
        {
            Directory.CreateDirectory(ArchiveDir);
            File.Copy(source, partial, overwrite: true);
            File.Move(partial, target, overwrite: true);
            return target;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Archive copy failed: {ex.Message}");
            TryDeleteFile(partial);
            return null;
        }
    }

    /// <summary>
    /// Delete an in-flight take. If something still has it open (an antivirus
    /// scan, usually), retry a few times in the background rather than block
    /// the caller — which is the UI thread at the end of a dictation.
    /// </summary>
    private static void DeleteSource(string source)
    {
        if (TryDeleteFile(source)) return;
        _ = Task.Run(async () =>
        {
            for (var attempt = 1; attempt <= 6; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt)).ConfigureAwait(false);
                if (TryDeleteFile(source)) return;
            }
            // Give up; AudioRecorder.CleanupOldRecordings sweeps it next start.
        });
    }

    /// <summary>Delete a file; true when it is gone afterwards (including "was never there").</summary>
    private static bool TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Archive delete failed for {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Stamp a newly kept take with the time it was kept, so "newest" means
    /// "most recently dictated" even when a copy preserved an older timestamp
    /// or the clock moved.
    /// </summary>
    private static void Touch(string path)
    {
        try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { }
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUnder(string file, string directory)
    {
        try
        {
            var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(file).StartsWith(dir, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // ----- Events -----

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { Debug.WriteLine($"Archive Changed handler threw: {ex.Message}"); }
    }

    private void RaisePlayback(Guid noteId, bool playing)
    {
        try { PlaybackStateChanged?.Invoke(noteId, playing); }
        catch (Exception ex) { Debug.WriteLine($"PlaybackStateChanged handler threw: {ex.Message}"); }
    }
}
