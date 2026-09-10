using System.Runtime.InteropServices;
using FlowType.Core;

namespace FlowType.Input;

/// <summary>
/// Paste that waits for the target application to actually read the clipboard
/// before putting the user's own clipboard back.
///
/// The obvious implementation — set the clipboard, send Ctrl+V, sleep, restore —
/// is a race nobody can win. Ctrl+V is only *queued*; the target reads the
/// clipboard whenever its message loop gets around to it, which on a loaded
/// Electron app or over remote desktop can be well past any fixed delay. Lose
/// that race and the user watches their previous clipboard get pasted instead
/// of what they just said.
///
/// So instead of publishing the text, this publishes a *promise*:
/// <c>SetClipboardData(CF_UNICODETEXT, NULL)</c> from a hidden message-only
/// window. Windows then sends that window <c>WM_RENDERFORMAT</c> at the moment
/// a consumer asks for the data — a read receipt. Restoring happens 200 ms
/// after the last receipt (Chromium probes, then reads, so the first receipt is
/// not the last), and only while we still own the clipboard, so a copy the user
/// made in the meantime always wins.
///
/// Ported from Handy's <c>paste_tx</c>. Worst case here is "the transcript sits
/// on the clipboard a few seconds longer than needed"; the failure it removes
/// is "the wrong text got pasted", which is the one that matters.
/// </summary>
internal static class ReliablePaste
{
    /// <summary>Grace after the last observed read before restoring.</summary>
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(200);

    /// <summary>Hard cap on how long the transcript may occupy the clipboard.</summary>
    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromSeconds(8);

    private const uint TimerId = 1;
    private const uint TimerIntervalMs = 25;

    /// <summary>
    /// Publish, paste, and restore in the background. The returned task
    /// completes once the paste chord has been sent — the guarded restore
    /// finishes on its own afterwards. Returns false if the transaction could
    /// not be started at all, in which case the caller should fall back to the
    /// simple clipboard path.
    /// </summary>
    /// <param name="sendChord">
    /// False only for <c>--pastetest</c>, which exercises the whole transaction
    /// against a clipboard read from this process instead of firing Ctrl+V into
    /// whatever the user happens to have focused.
    /// </param>
    public static Task<bool> PasteAsync(string text, bool restoreClipboard, bool sendChord = true)
    {
        var pasted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => Run(text, restoreClipboard, sendChord, pasted))
        {
            // Deliberately a foreground thread: quitting FlowType while a
            // promise is still unrendered would leave the clipboard holding a
            // dead handle, and the next paste anywhere would produce nothing.
            // The transaction is bounded by RestoreTimeout, and in practice
            // settles about a quarter of a second after the paste.
            IsBackground = false,
            Name = "FlowType paste",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return pasted.Task;
    }

    private sealed class Transaction
    {
        public string Text = "";
        public string? Previous;
        public bool RestoreClipboard;
        public IntPtr Hwnd;
        public DateTime PublishedAt;
        public DateTime? InjectedAt;
        public DateTime? LastReceipt;
        public bool OwnershipLost;
        public bool Rendered;

        /// <summary>
        /// The transaction is over: the clipboard now holds whatever it is
        /// meant to hold for good. Windows still sends WM_RENDERALLFORMATS
        /// while the owner window is torn down, and answering it after a
        /// restore would put the transcript straight back over the user's own
        /// clipboard — which is exactly the bug this class exists to prevent.
        /// </summary>
        public bool Settled;
    }

    [ThreadStatic] private static Transaction? _current;

    private static void Run(string text, bool restoreClipboard, bool sendChord,
        TaskCompletionSource<bool> pasted)
    {
        var tx = new Transaction { Text = text, RestoreClipboard = restoreClipboard };
        _current = tx;
        var signalled = false;

        try
        {
            tx.Hwnd = CreateMessageWindow();
            if (tx.Hwnd == IntPtr.Zero)
            {
                pasted.TrySetResult(false);
                return;
            }

            tx.Previous = restoreClipboard ? TryReadClipboardText() : null;

            if (!Publish(tx.Hwnd))
            {
                pasted.TrySetResult(false);
                return;
            }
            tx.PublishedAt = DateTime.UtcNow;
            Log($"published hwnd={tx.Hwnd:X} owner={GetClipboardOwner():X} previous='{tx.Previous}'");

            if (sendChord) NativeMethods.SendCtrlV();
            tx.InjectedAt = DateTime.UtcNow;
            pasted.TrySetResult(true);
            signalled = true;

            SetTimer(tx.Hwnd, (UIntPtr)TimerId, TimerIntervalMs, IntPtr.Zero);
            PumpUntilSettled();
        }
        catch
        {
            // A failure before the chord means nothing was pasted; the caller
            // still needs an answer so it can fall back.
            if (!signalled) pasted.TrySetResult(false);
        }
        finally
        {
            if (tx.Hwnd != IntPtr.Zero)
            {
                KillTimer(tx.Hwnd, (UIntPtr)TimerId);
                // Destroying the window with an unrendered promise still on the
                // clipboard makes Windows ask for it (WM_RENDERALLFORMATS), so
                // the transcript is real text by the time we are gone.
                DestroyWindow(tx.Hwnd);
                // Guarded: the interpolation would take the clipboard lock on
                // every paste, and that lock is global — holding it needlessly
                // is exactly how a target app fails to read.
                if (Tracing) Log($"after destroy clipboard='{TryReadClipboardText()}' owner={GetClipboardOwner():X}");
            }
            _current = null;
            pasted.TrySetResult(false);
        }
    }

    /// <summary>Runs until <see cref="Settle"/> posts WM_QUIT (GetMessage then returns 0).</summary>
    private static void PumpUntilSettled()
    {
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    /// <summary>Keep waiting, or settle now?</summary>
    private static bool ShouldFinish(Transaction tx, DateTime now)
    {
        if (tx.OwnershipLost) return true;
        // Only a read *after* the chord is evidence the target consumed it; an
        // earlier one is a clipboard manager reacting to the change itself.
        if (tx.LastReceipt is { } last && tx.InjectedAt is { } injected
            && last >= injected && now - last >= QuietPeriod)
        {
            return true;
        }
        return now - tx.PublishedAt >= RestoreTimeout;
    }

    private static void Settle(Transaction tx)
    {
        if (tx.Settled) return;
        // WM_QUIT is only delivered once the queue is otherwise empty, so a
        // tick already in flight would otherwise re-enter this.
        KillTimer(tx.Hwnd, (UIntPtr)TimerId);
        Log($"settle rendered={tx.Rendered} lost={tx.OwnershipLost} owns={OwnsClipboard(tx.Hwnd)} restore={tx.RestoreClipboard} previous='{tx.Previous}'");
        // Make the promise real before letting go of it, so whatever happens
        // next the clipboard holds text and not an empty handle.
        if (!tx.Rendered && OwnsClipboard(tx.Hwnd) && OpenClipboardWithRetry(tx.Hwnd))
        {
            RenderText(tx);
            CloseClipboard();
        }
        tx.Settled = true;

        if (tx.RestoreClipboard && tx.Previous != null && !tx.OwnershipLost && OwnsClipboard(tx.Hwnd))
        {
            if (OpenClipboardWithRetry(tx.Hwnd))
            {
                EmptyClipboard();
                var ok = WriteText(tx.Previous);
                CloseClipboard();
                if (Tracing) Log($"restored ok={ok} clipboard now='{TryReadClipboardText()}'");
            }
            else Log("restore: could not open clipboard");
        }
        else Log("restore skipped");

        PostQuitMessage(0);
    }

    // ----- test hooks (--pastetest) -----

    /// <summary>Event trace, populated only while <see cref="Tracing"/> is on.</summary>
    internal static readonly List<string> Trace = new();
    internal static bool Tracing;

    private static void Log(string line)
    {
        if (!Tracing) return;
        lock (Trace) Trace.Add($"{DateTime.UtcNow:HH:mm:ss.fff} {line}");
    }

    /// <summary>Reads the clipboard exactly the way a target application does,
    /// which is what makes the owner window receive its read receipt.</summary>
    internal static string? ReadClipboardAsConsumer() => TryReadClipboardText();

    internal static bool SeedClipboard(string text)
    {
        if (!OpenClipboardWithRetry(IntPtr.Zero)) return false;
        try
        {
            EmptyClipboard();
            return WriteText(text);
        }
        finally
        {
            CloseClipboard();
        }
    }

    // ----- clipboard plumbing -----

    private static bool Publish(IntPtr hwnd)
    {
        if (!OpenClipboardWithRetry(hwnd)) return false;
        try
        {
            if (!EmptyClipboard()) return false;
            // NULL data = delayed rendering: we are promising the text, and
            // Windows will come back and ask for it when someone reads.
            SetClipboardData(CF_UNICODETEXT, IntPtr.Zero);
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>Writes the transcript into an already-open clipboard.</summary>
    private static void RenderText(Transaction tx)
    {
        if (WriteText(tx.Text)) tx.Rendered = true;
    }

    private static bool WriteText(string text)
    {
        var bytes = (text.Length + 1) * 2;
        var handle = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
        if (handle == IntPtr.Zero) return false;

        var target = GlobalLock(handle);
        if (target == IntPtr.Zero)
        {
            GlobalFree(handle);
            return false;
        }
        try
        {
            Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
            Marshal.WriteInt16(target, text.Length * 2, 0);
        }
        finally
        {
            GlobalUnlock(handle);
        }

        if (SetClipboardData(CF_UNICODETEXT, handle) == IntPtr.Zero)
        {
            GlobalFree(handle);
            return false;
        }
        return true;   // the clipboard owns the handle now
    }

    private static string? TryReadClipboardText()
    {
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
        if (!OpenClipboardWithRetry(IntPtr.Zero)) return null;
        try
        {
            var handle = GetClipboardData(CF_UNICODETEXT);
            if (handle == IntPtr.Zero) return null;
            var pointer = GlobalLock(handle);
            if (pointer == IntPtr.Zero) return null;
            try
            {
                return Marshal.PtrToStringUni(pointer);
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>The clipboard is a single global lock; another app may hold it.</summary>
    private static bool OpenClipboardWithRetry(IntPtr hwnd)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (OpenClipboard(hwnd)) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    private static bool OwnsClipboard(IntPtr hwnd) => GetClipboardOwner() == hwnd;

    // ----- message-only window -----

    private static IntPtr _classAtom;
    private static WndProc? _wndProc;
    private static readonly object ClassGate = new();

    private static IntPtr CreateMessageWindow()
    {
        lock (ClassGate)
        {
            if (_classAtom == IntPtr.Zero)
            {
                _wndProc = Proc;
                var wc = new WNDCLASS
                {
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                    hInstance = NativeMethods.GetModuleHandle(null),
                    lpszClassName = "FlowTypePasteTx",
                };
                var atom = RegisterClass(ref wc);
                if (atom == 0) return IntPtr.Zero;
                _classAtom = (IntPtr)atom;
            }
        }

        return CreateWindowEx(0, "FlowTypePasteTx", null, 0, 0, 0, 0, 0,
            HWND_MESSAGE, IntPtr.Zero, NativeMethods.GetModuleHandle(null), IntPtr.Zero);
    }

    private static IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var tx = _current;
        if (tx != null && tx.Hwnd == hwnd)
        {
            switch (msg)
            {
                case WM_RENDERFORMAT:
                    if (tx.Settled) return IntPtr.Zero;
                    // A consumer asked for the data: the clipboard is already
                    // open on our behalf, so just write it.
                    tx.LastReceipt = DateTime.UtcNow;
                    Log($"receipt format={(uint)wParam}");
                    if ((uint)wParam == CF_UNICODETEXT) RenderText(tx);
                    return IntPtr.Zero;

                case WM_RENDERALLFORMATS:
                    if (tx.Settled) return IntPtr.Zero;
                    // Sent while the window is being destroyed with the promise
                    // unrendered. Not a consumer read, so no receipt — and
                    // unlike WM_RENDERFORMAT the clipboard is ours to open.
                    if (OpenClipboardWithRetry(hwnd))
                    {
                        if (OwnsClipboard(hwnd)) RenderText(tx);
                        CloseClipboard();
                    }
                    return IntPtr.Zero;

                case WM_DESTROYCLIPBOARD:
                    Log("ownership lost");
                    tx.OwnershipLost = true;
                    return IntPtr.Zero;

                case WM_TIMER:
                    if (ShouldFinish(tx, DateTime.UtcNow)) Settle(tx);
                    return IntPtr.Zero;
            }
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    // ----- interop -----

    private const uint CF_UNICODETEXT = 13;
    private const uint WM_RENDERFORMAT = 0x0305;
    private const uint WM_RENDERALLFORMATS = 0x0306;
    private const uint WM_DESTROYCLIPBOARD = 0x0307;
    private const uint WM_TIMER = 0x0113;
    private const uint GMEM_MOVEABLE = 0x0002;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr hWnd, UIntPtr uIDEvent);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out NativeMethods.MSG lpMsg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref NativeMethods.MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMethods.MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);
}
