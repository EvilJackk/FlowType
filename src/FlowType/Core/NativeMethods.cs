using System.Runtime.InteropServices;

namespace FlowType.Core;

/// <summary>
/// Win32 interop: the low-level keyboard hook + its private message pump,
/// SendInput (paste chord and unicode typing), non-activating window styles
/// for the flow bar, and foreground-app identification for stats.
/// </summary>
internal static class NativeMethods
{
    // ----- Low-level keyboard hook -----

    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;
    public const uint LLKHF_INJECTED = 0x10;

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(
        int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    // ----- Message pump for the dedicated hook thread -----

    public const uint WM_QUIT = 0x0012;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll")]
    public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    // ----- SendInput -----

    public const int INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;
    public const ushort VK_CONTROL = 0x11;
    public const ushort VK_SHIFT = 0x10;
    public const ushort VK_RETURN = 0x0D;
    public const ushort VK_V = 0x56;
    public const int VK_ESCAPE = 0x1B;

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        // Mouse variant is the largest member; keeps the union the right size.
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public int type;
        public InputUnion u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    public static INPUT KeyInput(ushort vk, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                dwExtraInfo = InjectionGuard.Signature,
            },
        },
    };

    public static INPUT UnicodeInput(char c, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = c,
                dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0),
                dwExtraInfo = InjectionGuard.Signature,
            },
        },
    };

    /// <summary>
    /// Send synthetic input, stamped so our own hook ignores it and the polling
    /// watchdog stays quiet for a moment afterwards.
    /// </summary>
    public static void Send(params INPUT[] inputs)
    {
        InjectionGuard.Extend(250);
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    // ----- Async key state (polling watchdog) -----

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    /// <summary>Toggle state (Caps Lock) for the calling thread's input queue.</summary>
    [DllImport("user32.dll")]
    public static extern short GetKeyState(int vKey);

    public static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public const int VK_LBUTTON = 0x01;
    public const int VK_RBUTTON = 0x02;
    public const int VK_BACK = 0x08;

    /// <summary>
    /// Whether a mouse button went down since the last time this was asked.
    /// GetAsyncKeyState's low bit is exactly that, which is enough to notice a
    /// click moved the caret without installing a second global hook.
    /// </summary>
    public static bool WasClicked() =>
        (GetAsyncKeyState(VK_LBUTTON) & 0x0001) != 0 || (GetAsyncKeyState(VK_RBUTTON) & 0x0001) != 0;

    /// <summary>Post Ctrl+V to the foreground app.</summary>
    public static void SendCtrlV() => Send(
        KeyInput(VK_CONTROL, false), KeyInput(VK_V, false),
        KeyInput(VK_V, true), KeyInput(VK_CONTROL, true));

    /// <summary>Shift+Enter — a newline that won't submit chat inputs.</summary>
    public static void SendShiftEnter() => Send(
        KeyInput(VK_SHIFT, false), KeyInput(VK_RETURN, false),
        KeyInput(VK_RETURN, true), KeyInput(VK_SHIFT, true));

    /// <summary>Enter on its own — the "press enter" voice command, sent after a paste.</summary>
    public static void SendEnter() => Send(
        KeyInput(VK_RETURN, false), KeyInput(VK_RETURN, true));

    public const ushort VK_C = 0x43;

    /// <summary>
    /// Ctrl+C into the focused app. Used only by Command Mode, and only when
    /// UI Automation could not tell us what is selected: the app is asked to
    /// copy its own selection so the instruction has something to work on.
    /// </summary>
    public static void SendCtrlC() => Send(
        KeyInput(VK_CONTROL, false), KeyInput(VK_C, false),
        KeyInput(VK_C, true), KeyInput(VK_CONTROL, true));

    // ----- Window styles (flow bar must never steal focus) -----

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    // ----- Foreground app (for Notes/stats "top apps") -----

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    // InternalGetWindowText reads the caption the window manager already has.
    // GetWindowText sends WM_GETTEXT instead, which blocks (measured 2.4 s)
    // when the window belongs to a thread of this process that is busy.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int InternalGetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    /// <summary>The window's caption, or "" — never throws.</summary>
    public static string WindowTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        try
        {
            var sb = new System.Text.StringBuilder(512);
            return InternalGetWindowText(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Whether this window belongs to FlowType itself (the flow bar, the main window…).</summary>
    public static bool IsOwnProcessWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    // ----- UIPI: can we type into this window at all? -----

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenIntegrityLevel = 25;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

    /// <summary>The mandatory integrity RID (0x2000 medium, 0x3000 high…), or null if unreadable.</summary>
    private static uint? IntegrityOf(IntPtr process)
    {
        if (!OpenProcessToken(process, TOKEN_QUERY, out var token)) return null;
        var buffer = IntPtr.Zero;
        try
        {
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var needed);
            if (needed <= 0) return null;
            buffer = Marshal.AllocHGlobal(needed);
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, needed, out _)) return null;
            var sid = Marshal.ReadIntPtr(buffer);   // TOKEN_MANDATORY_LABEL.Label.Sid
            var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            if (count == 0) return null;
            return (uint)Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            CloseHandle(token);
        }
    }

    private static uint? _ownIntegrity;

    /// <summary>
    /// True only when the window's process runs at a *higher* integrity level
    /// than FlowType — in practice an app started as administrator while
    /// FlowType is not. Windows (User Interface Privilege Isolation) silently
    /// drops synthetic input to such a window, so a paste "succeeds" and
    /// nothing appears; FlowType copies the text instead and says why.
    ///
    /// Wispr Flow's helper decides this from a denied process open. That also
    /// fires for processes that are merely protected — anti-cheat games among
    /// them, where pasting works fine — so FlowType compares the two tokens'
    /// integrity levels and, when it cannot read them, lets the paste go ahead
    /// exactly as 1.x did.
    /// </summary>
    public static bool IsElevatedBeyondUs(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) return false;

        _ownIntegrity ??= IntegrityOf(GetCurrentProcess());
        if (_ownIntegrity is not { } own) return false;

        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return false;
        try
        {
            return IntegrityOf(handle) is { } target && target > own;
        }
        finally
        {
            CloseHandle(handle);
        }
    }
}
