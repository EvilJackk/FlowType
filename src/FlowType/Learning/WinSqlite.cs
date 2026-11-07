using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FlowType.Learning;

/// <summary>
/// A deliberately small SQLite reader over the copy of SQLite that ships with
/// Windows (<c>winsqlite3.dll</c> in System32, present since Windows 10 1607).
///
/// Why not a NuGet package: the only database FlowType ever reads is Wispr
/// Flow's history, read-only, a few times a day. A managed SQLite provider
/// would add a native library per architecture to the install for that, and
/// another unsigned DLL is exactly what antivirus heuristics dislike (see the
/// publish notes in FlowType.csproj). Windows already carries a signed SQLite.
///
/// What it does and does not do:
/// - <see cref="OpenReadOnly"/> opens with <c>SQLITE_OPEN_READONLY</c> and no
///   URI parsing, so a path can never smuggle in options such as
///   <c>?mode=rwc</c>. SQLite itself then refuses every write on that
///   connection — the "never modify Wispr's database" promise is enforced by
///   the engine, not by our own care.
/// - A 3-second busy timeout: Wispr keeps its database in WAL mode, where a
///   reader normally never waits, but a checkpoint or recovery in progress can
///   still make it wait briefly.
/// - Text is read as UTF-16 (<c>sqlite3_column_text16</c>), so it lands in a
///   .NET string without a UTF-8 round trip.
/// - One connection is used by one thread at a time. Rows handed out by
///   <see cref="Query"/> are views over the current step and are only valid
///   until the enumeration moves on.
/// - Every statement is finalized and every connection closed, including when
///   an exception or a cancelled enumeration cuts a read short: a statement
///   left open holds a read transaction on Wispr's live database, and while
///   it does, Wispr's checkpoints cannot finish and its WAL file keeps growing.
///
/// <see cref="OpenReadWrite"/> and <see cref="Execute"/> exist only so the
/// self-test can build a scratch database shaped like Wispr's.
/// </summary>
internal sealed class WinSqlite : IDisposable
{
    private const int SQLITE_OK = 0;
    private const int SQLITE_ROW = 100;
    private const int SQLITE_DONE = 101;

    private const int SQLITE_OPEN_READONLY = 0x1;
    private const int SQLITE_OPEN_READWRITE = 0x2;
    private const int SQLITE_OPEN_CREATE = 0x4;

    private const int SQLITE_NULL = 5;

    /// <summary>"Copy the value now": the bound string or array may move or die as soon as the call returns.</summary>
    private static readonly IntPtr SQLITE_TRANSIENT = new(-1);

    private const int BusyTimeoutMs = 3000;

    private IntPtr _db;

    private WinSqlite(IntPtr db, string path, bool readOnly)
    {
        _db = db;
        Path = path;
        IsReadOnly = readOnly;
    }

    ~WinSqlite()
    {
        // Last resort only; every caller disposes. A connection is a file
        // handle on another app's database and should not outlive its use.
        Close();
    }

    /// <summary>The database file this connection reads.</summary>
    public string Path { get; }

    /// <summary>Opened with <c>SQLITE_OPEN_READONLY</c>.</summary>
    public bool IsReadOnly { get; }

    // ----- Opening -----

    /// <summary>
    /// Open an existing database for reading only. Throws
    /// <see cref="WinSqliteException"/> when it cannot be opened, including
    /// when Windows' SQLite itself is missing (<see cref="WinSqliteException.IsUnavailable"/>).
    /// </summary>
    public static WinSqlite OpenReadOnly(string path) => Open(path, SQLITE_OPEN_READONLY, readOnly: true);

    /// <summary>Open or create a database for writing. Self-test only — never used on another app's data.</summary>
    public static WinSqlite OpenReadWrite(string path) =>
        Open(path, SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE, readOnly: false);

    private static WinSqlite Open(string path, int flags, bool readOnly)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        EnsureAvailable();

        var rc = Native.sqlite3_open_v2(Utf8z(path), out var db, flags, IntPtr.Zero);
        if (rc != SQLITE_OK)
        {
            // open_v2 hands back a handle even on failure (for the message);
            // it still has to be closed.
            var message = db == IntPtr.Zero ? "out of memory" : MessageOf(db);
            if (db != IntPtr.Zero) Native.sqlite3_close_v2(db);
            throw new WinSqliteException(rc, $"open failed: {message}");
        }

        Native.sqlite3_busy_timeout(db, BusyTimeoutMs);
        return new WinSqlite(db, path, readOnly);
    }

    /// <summary>sqlite3_open_v2 takes the file name as null-terminated UTF-8.</summary>
    private static byte[] Utf8z(string text)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
        Encoding.UTF8.GetBytes(text, 0, text.Length, bytes, 0);
        return bytes;
    }

    // ----- Availability -----

    /// <summary>Every export this class calls. Checked once, up front, so a missing one is a clear problem rather than a crash mid-read.</summary>
    private static readonly string[] RequiredExports =
    {
        "sqlite3_open_v2", "sqlite3_close_v2", "sqlite3_busy_timeout", "sqlite3_errmsg16",
        "sqlite3_prepare16_v2", "sqlite3_step", "sqlite3_finalize", "sqlite3_total_changes",
        "sqlite3_column_count", "sqlite3_column_name16", "sqlite3_column_type",
        "sqlite3_column_text16", "sqlite3_column_bytes16", "sqlite3_column_blob",
        "sqlite3_column_bytes", "sqlite3_column_int64", "sqlite3_column_double",
        "sqlite3_bind_parameter_count", "sqlite3_bind_text16", "sqlite3_bind_blob",
        "sqlite3_bind_int64", "sqlite3_bind_double", "sqlite3_bind_null",
    };

    private static readonly Lazy<string?> UnavailableReason = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Null when Windows' SQLite can be used; otherwise why not (for diagnostics, not the UI).</summary>
    public static string? WhyUnavailable => UnavailableReason.Value;

    private static void EnsureAvailable()
    {
        var reason = UnavailableReason.Value;
        if (reason != null) throw new WinSqliteException(WinSqliteException.Unavailable, reason);
    }

    /// <summary>
    /// Load the DLL the same way the P/Invokes below will (System32 only) and
    /// look up every export. Without this, a missing DLL or an export absent
    /// from an old Windows build would surface as DllNotFoundException or
    /// EntryPointNotFoundException from whichever call happened to run first.
    /// </summary>
    private static string? Probe()
    {
        try
        {
            if (!NativeLibrary.TryLoad(Native.Library, typeof(WinSqlite).Assembly,
                    DllImportSearchPath.System32, out var handle))
            {
                return "winsqlite3.dll was not found in System32";
            }
            foreach (var name in RequiredExports)
            {
                if (!NativeLibrary.TryGetExport(handle, name, out _)) return $"winsqlite3.dll has no {name}";
            }
            return null;
        }
        catch (Exception ex)
        {
            return $"winsqlite3.dll could not be loaded ({ex.GetType().Name})";
        }
    }

    // ----- Schema -----

    /// <summary>The table's column names in declaration order; empty when the table does not exist.</summary>
    public IReadOnlyList<string> Columns(string table)
    {
        var names = new List<string>();
        foreach (var row in Query($"PRAGMA table_info({QuoteIdentifier(table)})"))
        {
            var name = row.GetText("name");
            if (!string.IsNullOrEmpty(name)) names.Add(name);
        }
        return names;
    }

    /// <summary>Whether a table (or view) of this name exists, ignoring case as SQLite does.</summary>
    public bool HasTable(string table)
    {
        foreach (var _ in Query(
            "SELECT 1 FROM sqlite_master WHERE type IN ('table', 'view') AND name = ? COLLATE NOCASE LIMIT 1", table))
        {
            return true;
        }
        return false;
    }

    /// <summary>An identifier in double quotes, with embedded quotes doubled — for names that cannot be bound as parameters.</summary>
    public static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    // ----- Reading -----

    /// <summary>
    /// Run one statement and enumerate its rows. <paramref name="args"/> bind
    /// to its <c>?</c> parameters in order (string, byte[], integers, bool,
    /// floating point, or null). The statement is prepared on the first
    /// <c>MoveNext</c> and finalized when the enumeration ends or is disposed,
    /// so always consume it with <c>foreach</c>. The yielded row object is
    /// reused: read what you need before moving on.
    /// </summary>
    public IEnumerable<WinSqliteRow> Query(string sql, params object?[] args)
    {
        ThrowIfDisposed();
        var stmt = Prepare(sql, out var tail);
        try
        {
            if (stmt == IntPtr.Zero) yield break; // blank or comment-only SQL
            if (!string.IsNullOrWhiteSpace(tail))
            {
                throw new ArgumentException("Query runs exactly one statement.", nameof(sql));
            }
            Bind(stmt, args);

            var row = new WinSqliteRow(stmt);
            while (true)
            {
                var rc = Native.sqlite3_step(stmt);
                if (rc == SQLITE_DONE) yield break;
                if (rc != SQLITE_ROW) throw Error(rc, "step");
                yield return row;
            }
        }
        finally
        {
            if (stmt != IntPtr.Zero) Native.sqlite3_finalize(stmt);
        }
    }

    // ----- Writing (self-test only) -----

    /// <summary>
    /// Run one or more statements, returning the rows they inserted, updated or
    /// deleted. With <paramref name="args"/>, the SQL must be a single
    /// statement. Self-test only: a read-only connection refuses it anyway.
    /// </summary>
    public int Execute(string sql, params object?[] args)
    {
        ThrowIfDisposed();
        var before = Native.sqlite3_total_changes(_db);
        var remaining = sql;
        var first = true;
        while (!string.IsNullOrWhiteSpace(remaining))
        {
            var stmt = Prepare(remaining, out var tail);
            try
            {
                if (stmt == IntPtr.Zero) break;
                if (args.Length > 0 && !string.IsNullOrWhiteSpace(tail))
                {
                    throw new ArgumentException("Execute with parameters runs exactly one statement.", nameof(sql));
                }
                if (first) Bind(stmt, args);

                int rc;
                while ((rc = Native.sqlite3_step(stmt)) == SQLITE_ROW) { }
                if (rc != SQLITE_DONE) throw Error(rc, "step");
            }
            finally
            {
                if (stmt != IntPtr.Zero) Native.sqlite3_finalize(stmt);
            }
            remaining = tail;
            first = false;
        }
        return Native.sqlite3_total_changes(_db) - before;
    }

    // ----- Statement plumbing -----

    /// <summary>
    /// Compile the first statement of <paramref name="sql"/>; <paramref name="tail"/>
    /// receives whatever follows it. The text is copied to unmanaged memory so
    /// the tail pointer SQLite hands back can be turned into an offset.
    /// </summary>
    private IntPtr Prepare(string sql, out string tail)
    {
        var buffer = Marshal.StringToHGlobalUni(sql);
        try
        {
            var rc = Native.sqlite3_prepare16_v2(_db, buffer, -1, out var stmt, out var tailPtr);
            if (rc != SQLITE_OK)
            {
                if (stmt != IntPtr.Zero) Native.sqlite3_finalize(stmt);
                throw Error(rc, "prepare");
            }

            var consumed = tailPtr == IntPtr.Zero
                ? sql.Length
                : (int)((tailPtr.ToInt64() - buffer.ToInt64()) / sizeof(char));
            tail = sql.Substring(Math.Clamp(consumed, 0, sql.Length));
            return stmt;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void Bind(IntPtr stmt, object?[] args)
    {
        var expected = Native.sqlite3_bind_parameter_count(stmt);
        if (expected != args.Length)
        {
            throw new ArgumentException($"The statement takes {expected} parameter(s); {args.Length} given.", nameof(args));
        }

        for (var i = 0; i < args.Length; i++)
        {
            var index = i + 1; // SQLite parameters are 1-based
            var rc = args[i] switch
            {
                null or DBNull => Native.sqlite3_bind_null(stmt, index),
                string s => Native.sqlite3_bind_text16(stmt, index, s, s.Length * sizeof(char), SQLITE_TRANSIENT),
                byte[] b => Native.sqlite3_bind_blob(stmt, index, b, b.Length, SQLITE_TRANSIENT),
                bool flag => Native.sqlite3_bind_int64(stmt, index, flag ? 1 : 0),
                sbyte or byte or short or ushort or int or uint or long =>
                    Native.sqlite3_bind_int64(stmt, index, Convert.ToInt64(args[i], System.Globalization.CultureInfo.InvariantCulture)),
                float or double or decimal =>
                    Native.sqlite3_bind_double(stmt, index, Convert.ToDouble(args[i], System.Globalization.CultureInfo.InvariantCulture)),
                _ => throw new ArgumentException($"Cannot bind a {args[i]!.GetType().Name}.", nameof(args)),
            };
            if (rc != SQLITE_OK) throw Error(rc, "bind");
        }
    }

    private WinSqliteException Error(int rc, string stage) => new(rc, $"{stage} failed: {MessageOf(_db)}");

    private static string MessageOf(IntPtr db)
    {
        var ptr = Native.sqlite3_errmsg16(db);
        return ptr == IntPtr.Zero ? "unknown error" : Marshal.PtrToStringUni(ptr) ?? "unknown error";
    }

    // ----- Lifetime -----

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_db == IntPtr.Zero, this);
    }

    public void Dispose()
    {
        Close();
        GC.SuppressFinalize(this);
    }

    private void Close()
    {
        var db = Interlocked.Exchange(ref _db, IntPtr.Zero);
        if (db == IntPtr.Zero) return;
        // close_v2, not close: if a statement were somehow still open it
        // defers instead of failing, so the handle is never simply leaked.
        var rc = Native.sqlite3_close_v2(db);
        if (rc != SQLITE_OK) Debug.WriteLine($"WinSqlite close returned {rc}");
    }

    /// <summary>
    /// The C API. Every import is pinned to System32 (the attribute is per
    /// method), so a winsqlite3.dll dropped next to FlowType.exe or in the
    /// working folder is never loaded instead of Windows' own.
    /// </summary>
    private static class Native
    {
        public const string Library = "winsqlite3.dll";

        [DllImport(Library), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

        [DllImport(Library), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int sqlite3_close_v2(IntPtr db);

        [DllImport(Library), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int sqlite3_busy_timeout(IntPtr db, int ms);

        [DllImport(Library), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern IntPtr sqlite3_errmsg16(IntPtr db);

        [DllImport(Library), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int sqlite3_total_changes(IntPtr db);

        [DllImport(Library), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int sqlite3_prepare16_v2(IntPtr db, IntPtr sql, int numBytes, out IntPtr stmt, out IntPtr tail);

        [DllImport(Library), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int sqlite3_step(IntPtr stmt);

        [DllImport(Library), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int sqlite3_finalize(IntPtr stmt);

        [DllImport(Library), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int sqlite3_column_count(IntPtr stmt);

        [DllImport(Library), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern IntPtr sqlite3_column_name16(IntPtr stmt, int column);

        [DllImport(Library), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int sqlite3_column_type(IntPtr stmt, int column);

        [DllImport(Library), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern IntPtr sqlite3_column_text16(IntPtr stmt, int column);

        [DllImport(Library), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int sqlite3_column_bytes16(IntPtr stmt, int column);

        [DllImport(Library), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern IntPtr sqlite3_column_blob(IntPtr stmt, int column);

        [DllImport(Library), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int sqlite3_column_bytes(IntPtr stmt, int column);

        [DllImport(Library), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern long sqlite3_column_int64(IntPtr stmt, int column);

        [DllImport(Library), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern double sqlite3_column_double(IntPtr stmt, int column);

        [DllImport(Library), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int sqlite3_bind_parameter_count(IntPtr stmt);

        [DllImport(Library), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int sqlite3_bind_text16(IntPtr stmt, int index,
            [MarshalAs(UnmanagedType.LPWStr)] string value, int numBytes, IntPtr destructor);

        [DllImport(Library), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int sqlite3_bind_blob(IntPtr stmt, int index, byte[] value, int numBytes, IntPtr destructor);

        [DllImport(Library), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int sqlite3_bind_int64(IntPtr stmt, int index, long value);

        [DllImport(Library), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int sqlite3_bind_double(IntPtr stmt, int index, double value);

        [DllImport(Library), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int sqlite3_bind_null(IntPtr stmt, int index);
    }

    /// <summary>Column access for <see cref="WinSqliteRow"/> without exposing the raw API.</summary>
    internal static class RowAccess
    {
        public const int Null = SQLITE_NULL;
        public static int Count(IntPtr stmt) => Native.sqlite3_column_count(stmt);
        public static string Name(IntPtr stmt, int i) => Marshal.PtrToStringUni(Native.sqlite3_column_name16(stmt, i)) ?? "";
        public static int Type(IntPtr stmt, int i) => Native.sqlite3_column_type(stmt, i);

        public static string Text(IntPtr stmt, int i)
        {
            // text16 first, then bytes16: the documented order, because the
            // first call may convert the value and the length is of the result.
            var ptr = Native.sqlite3_column_text16(stmt, i);
            if (ptr == IntPtr.Zero) return "";
            var bytes = Native.sqlite3_column_bytes16(stmt, i);
            return bytes <= 0 ? "" : Marshal.PtrToStringUni(ptr, bytes / sizeof(char));
        }

        public static byte[] Blob(IntPtr stmt, int i)
        {
            var ptr = Native.sqlite3_column_blob(stmt, i);
            var bytes = Native.sqlite3_column_bytes(stmt, i);
            var copy = new byte[Math.Max(0, bytes)];
            if (ptr != IntPtr.Zero && copy.Length > 0) Marshal.Copy(ptr, copy, 0, copy.Length);
            return copy;
        }

        public static long Int64(IntPtr stmt, int i) => Native.sqlite3_column_int64(stmt, i);
        public static double Double(IntPtr stmt, int i) => Native.sqlite3_column_double(stmt, i);
    }
}

/// <summary>
/// The current row of a <see cref="WinSqlite.Query"/>. Valid only until the
/// enumeration moves on. Lookups by name return null / 0 / "is null" for a
/// column the query did not produce, so a reader written against one version
/// of another app's schema keeps working when a column disappears.
/// </summary>
internal sealed class WinSqliteRow
{
    private readonly IntPtr _stmt;
    private readonly Dictionary<string, int> _ordinals = new(StringComparer.OrdinalIgnoreCase);

    internal WinSqliteRow(IntPtr stmt)
    {
        _stmt = stmt;
        FieldCount = WinSqlite.RowAccess.Count(stmt);
        for (var i = 0; i < FieldCount; i++)
        {
            // First wins, as in SQL itself when two result columns share a name.
            _ordinals.TryAdd(WinSqlite.RowAccess.Name(stmt, i), i);
        }
    }

    public int FieldCount { get; }

    /// <summary>The result column's index, or −1 when the query has no column of that name.</summary>
    public int Ordinal(string name) => _ordinals.TryGetValue(name, out var i) ? i : -1;

    public bool Has(string name) => _ordinals.ContainsKey(name);

    public bool IsNull(int i) => WinSqlite.RowAccess.Type(_stmt, i) == WinSqlite.RowAccess.Null;

    /// <summary>The value as text (numbers are converted by SQLite), or null when it is NULL.</summary>
    public string? GetText(int i) => IsNull(i) ? null : WinSqlite.RowAccess.Text(_stmt, i);

    /// <summary>The value's bytes, or null when it is NULL. An empty blob is an empty array.</summary>
    public byte[]? GetBlob(int i) => IsNull(i) ? null : WinSqlite.RowAccess.Blob(_stmt, i);

    /// <summary>The value as an integer (SQLite converts; NULL is 0).</summary>
    public long GetInt64(int i) => WinSqlite.RowAccess.Int64(_stmt, i);

    /// <summary>The value as a double (SQLite converts; NULL is 0).</summary>
    public double GetDouble(int i) => WinSqlite.RowAccess.Double(_stmt, i);

    public bool IsNull(string name) => Ordinal(name) is var i && (i < 0 || IsNull(i));
    public string? GetText(string name) => Ordinal(name) is var i && i >= 0 ? GetText(i) : null;
    public byte[]? GetBlob(string name) => Ordinal(name) is var i && i >= 0 ? GetBlob(i) : null;
    public long GetInt64(string name) => Ordinal(name) is var i && i >= 0 ? GetInt64(i) : 0;
    public double GetDouble(string name) => Ordinal(name) is var i && i >= 0 ? GetDouble(i) : 0;
}

/// <summary>
/// A SQLite error, carrying SQLite's own result code and message
/// (<c>sqlite3_errmsg16</c>). The message is technical; callers that show
/// something to the user translate the code into plain words.
/// </summary>
internal sealed class WinSqliteException : Exception
{
    /// <summary><see cref="Code"/> when Windows' SQLite itself could not be loaded.</summary>
    public const int Unavailable = -1;

    private const int SQLITE_PERM = 3;
    private const int SQLITE_BUSY = 5;
    private const int SQLITE_LOCKED = 6;
    private const int SQLITE_READONLY = 8;
    private const int SQLITE_IOERR = 10;
    private const int SQLITE_CORRUPT = 11;
    private const int SQLITE_CANTOPEN = 14;
    private const int SQLITE_NOTADB = 26;

    public WinSqliteException(int code, string message) : base(message)
    {
        Code = code;
    }

    /// <summary>SQLite's result code (possibly extended), or <see cref="Unavailable"/>.</summary>
    public int Code { get; }

    /// <summary>The primary result code (the low byte of an extended one).</summary>
    public int PrimaryCode => Code < 0 ? Code : Code & 0xFF;

    public bool IsUnavailable => Code == Unavailable;

    /// <summary>Another connection holds a lock: worth waiting and trying again.</summary>
    public bool IsBusy => PrimaryCode is SQLITE_BUSY or SQLITE_LOCKED;

    /// <summary>A write was refused because the connection (or file) is read-only.</summary>
    public bool IsReadOnly => PrimaryCode == SQLITE_READONLY;

    /// <summary>The file is not a database, or is damaged.</summary>
    public bool IsDamaged => PrimaryCode is SQLITE_CORRUPT or SQLITE_NOTADB;

    /// <summary>The file could not be opened or read (permissions, missing folder, I/O error).</summary>
    public bool IsCantOpen => PrimaryCode is SQLITE_CANTOPEN or SQLITE_PERM or SQLITE_IOERR;
}
