using System.Runtime.InteropServices;
using System.Windows.Threading;
using FlowType.Core;

namespace FlowType.Input;

/// <summary>A bindable activation: one key, or a chord of key groups (any-Ctrl + any-Win…).</summary>
public sealed record HotkeyDef(string Id, string Display, int[][] Groups, int? SuppressVk)
{
    public bool IsSingleKey => Groups.Length == 1 && Groups[0].Length == 1;
}

public static class HotkeyDefs
{
    // VKs: RCTRL A3, RALT A5, RSHIFT A1, CAPS 14, F8 77, F9 78, PAUSE 13,
    //      LCTRL A2, LWIN 5B, RWIN 5C, LSHIFT A0, SPACE 20,
    //      MBUTTON 04, XBUTTON1 05, XBUTTON2 06
    public static readonly IReadOnlyList<HotkeyDef> All = new[]
    {
        new HotkeyDef("RightCtrl", "Right Ctrl", new[] { new[] { 0xA3 } }, null),
        new HotkeyDef("AnyCtrl", "Ctrl (either side)", new[] { new[] { 0xA2, 0xA3 } }, null),
        new HotkeyDef("RightAlt", "Right Alt", new[] { new[] { 0xA5 } }, null),
        new HotkeyDef("RightShift", "Right Shift", new[] { new[] { 0xA1 } }, null),
        new HotkeyDef("CapsLock", "Caps Lock", new[] { new[] { 0x14 } }, 0x14),
        new HotkeyDef("F8", "F8", new[] { new[] { 0x77 } }, 0x77),
        new HotkeyDef("F9", "F9", new[] { new[] { 0x78 } }, 0x78),
        new HotkeyDef("Pause", "Pause / Break", new[] { new[] { 0x13 } }, 0x13),
        new HotkeyDef("CtrlWin", "Ctrl + Win",
            new[] { new[] { 0xA2, 0xA3 }, new[] { 0x5B, 0x5C } }, null),
        new HotkeyDef("CtrlShiftSpace", "Ctrl + Shift + Space",
            new[] { new[] { 0xA2, 0xA3 }, new[] { 0xA0, 0xA1 }, new[] { 0x20 } }, 0x20),
        // Mouse buttons are seen by the polling watchdog (a keyboard hook can't
        // suppress them, so the button's normal action still fires).
        new HotkeyDef("MouseMiddle", "Mouse: middle button", new[] { new[] { 0x04 } }, null),
        new HotkeyDef("MouseX1", "Mouse: back button (M4)", new[] { new[] { 0x05 } }, null),
        new HotkeyDef("MouseX2", "Mouse: forward button (M5)", new[] { new[] { 0x06 } }, null),
    };

    public static HotkeyDef Get(string id) =>
        All.FirstOrDefault(d => d.Id == id) ?? All[0];

    private static readonly HashSet<int> ModifierVks = new()
        { 0x10, 0x11, 0x12, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C };

    /// <summary>Resolve the active definition, including "Detect key" customs.</summary>
    public static HotkeyDef Resolve(AppSettings settings)
    {
        if (settings.Hotkey == "Custom" && settings.CustomVks is { Length: > 0 })
        {
            var groups = settings.CustomVks.Select(vk => new[] { vk }).ToArray();
            // Swallow captured keys with side effects of their own; bare
            // modifiers pass through, mouse buttons can't be swallowed here.
            int? suppress = settings.CustomVks.Length == 1
                && settings.CustomVks[0] >= 0x08
                && !ModifierVks.Contains(settings.CustomVks[0])
                    ? settings.CustomVks[0]
                    : null;
            var label = string.IsNullOrEmpty(settings.CustomLabel) ? "Custom" : settings.CustomLabel;
            return new HotkeyDef("Custom", label, groups, suppress);
        }
        return Get(settings.Hotkey);
    }

    /// <summary>Display name of whatever hotkey is currently configured.</summary>
    public static string CurrentDisplay(AppSettings settings) => Resolve(settings).Display;
}

/// <summary>
/// Global hotkey watcher with two independent detection paths, because a single
/// path proved fragile in the field:
///
/// * **Polling watchdog** (primary detector) — a background thread reading
///   GetAsyncKeyState every 15 ms. It cannot be starved by a slow hook, sees
///   mouse buttons, is immune to keyboard software that re-injects keystrokes,
///   and keeps working if Windows drops the hook.
/// * **Low-level hook** (suppression + combo detection + key observation) —
///   swallows keys with side effects (Caps Lock, F-keys), notices "another key
///   pressed while holding", and feeds the live key monitor / detect-key UI.
///
/// The hook lives on a dedicated thread with its own message pump (Windows
/// silently uninstalls slow hooks). Never filter on LLKHF_INJECTED: keyboard
/// software (Logitech G HUB, PowerToys, AutoHotkey, RDP) re-injects the user's
/// real keystrokes with that flag set. FlowType identifies only its own
/// synthetic input, via the InjectionGuard dwExtraInfo signature.
///
/// Both paths funnel into <see cref="SetActive"/>, which de-duplicates.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Thread _hookThread;
    private readonly Thread _pollThread;
    private readonly NativeMethods.LowLevelKeyboardProc _hookProc;  // GC anchor
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _stateGate = new();

    private IntPtr _hookHandle = IntPtr.Zero;
    private uint _hookThreadId;

    /// <summary>Hook-thread view of which hotkey member keys are physically down.</summary>
    private readonly HashSet<int> _hookPressed = new();

    /// <summary>The single source of truth for "the hotkey is currently held".</summary>
    private bool _active;

    private volatile HotkeyDef _def;

    // ----- Detect-key capture state -----
    private volatile Action<int>? _captureCallback;
    private readonly bool[] _captureBaseline = new bool[256];

    /// <summary>True once the hook has observed any real keystroke (diagnostics).</summary>
    public bool HookSawInput { get; private set; }

    public bool HookInstalled => _hookHandle != IntPtr.Zero;

    /// <summary>Live view for the settings indicator.</summary>
    public bool IsHotkeyDown { get { lock (_stateGate) return _active; } }

    /// <summary>
    /// Set by the session for the whole dictation — capture *and* decode — so
    /// Esc cancels and is swallowed. It used to be cleared the moment recording
    /// stopped, which made Esc-during-transcription silently do nothing and left
    /// the cancellation machinery downstream unreachable.
    /// </summary>
    public volatile bool SessionActive;

    /// <summary>
    /// True only while audio is being captured (not while transcribing).
    /// Space is treated as a gesture key — and kept from the focused app —
    /// only then.
    /// </summary>
    public volatile bool RecordingActive;

    /// <summary>Raw hook trace for the --keylog diagnostic; null in normal runs.</summary>
    public Action<string>? Trace;

    public event Action? Pressed;
    public event Action? Released;

    /// <summary>
    /// A non-hotkey key went down while the hotkey is held, with its vk. The
    /// session decides: Space = go hands-free, Alt = Command Mode, anything
    /// else = "this was a keyboard shortcut", cancel quietly.
    /// </summary>
    public event Action<int>? OtherKeyPressed;
    public event Action? CancelRequested;

    /// <summary>Alt+Shift+Z — paste the last dictation again (Wispr's default too).</summary>
    public event Action? PasteLastRequested;

    /// <summary>Alt+Shift+X — copy the last dictation to the clipboard.</summary>
    public event Action? CopyLastRequested;

    /// <summary>Every real (non-FlowType) keydown the hook sees — feeds the live key monitor.</summary>
    public event Action<int>? KeyObserved;

    /// <summary>
    /// The same keydowns, with the character each produces where there is one,
    /// for <see cref="Session.CorrectionWatcher"/>. Only ever consumed for a
    /// few seconds after FlowType itself inserted text, and never recorded.
    /// </summary>
    public event Action<int, char>? KeyTyped;

    public HotkeyManager(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _hookProc = HookCallback;
        _def = HotkeyDefs.Resolve(SettingsStore.Instance.Settings);
        SettingsStore.Instance.Changed += RefreshDef;

        using var ready = new ManualResetEventSlim();
        _hookThread = new Thread(() =>
        {
            _hookThreadId = NativeMethods.GetCurrentThreadId();
            _hookHandle = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_KEYBOARD_LL, _hookProc,
                NativeMethods.GetModuleHandle(null), 0);
            ready.Set();

            if (_hookHandle == IntPtr.Zero) return;   // polling carries on alone

            while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }
            NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        })
        {
            Name = "FlowType hotkey hook",
            IsBackground = true,
        };
        _hookThread.SetApartmentState(ApartmentState.STA);
        _hookThread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));

        _pollThread = new Thread(PollLoop)
        {
            Name = "FlowType hotkey poll",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
        _pollThread.Start();
    }

    private void RefreshDef() => _def = HotkeyDefs.Resolve(SettingsStore.Instance.Settings);

    // ----- Detect-key capture -----

    /// <summary>
    /// One-shot: report the next key or mouse button the user presses (whatever
    /// virtual-key their hardware/software stack actually delivers) and swallow
    /// it. Normal hotkey processing pauses while capturing. Detection works
    /// through both the hook and polling, so it also sees mouse buttons and
    /// keys arriving only via re-injection.
    /// </summary>
    public void CaptureNextKey(Action<int> onCaptured)
    {
        for (var vk = 3; vk < 256; vk++)
        {
            _captureBaseline[vk] = NativeMethods.IsKeyDown(vk);
        }
        _captureCallback = onCaptured;
    }

    public void CancelCapture() => _captureCallback = null;

    private void CompleteCapture(int vk)
    {
        var callback = Interlocked.Exchange(ref _captureCallback, null);
        if (callback != null) Post(() => callback(vk));
    }

    // ----- Path 1: polling watchdog -----

    private void PollLoop()
    {
        var token = _shutdown.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_captureCallback != null)
                {
                    CapturePollSweep();
                }
                // Ignore our own paste/type-out, which GetAsyncKeyState sees as
                // genuine key presses (it has no dwExtraInfo to inspect).
                else if (!InjectionGuard.IsActive)
                {
                    var def = _def;
                    var allGroupsDown = true;
                    foreach (var group in def.Groups)
                    {
                        var groupDown = false;
                        foreach (var vk in group)
                        {
                            if (NativeMethods.IsKeyDown(vk)) { groupDown = true; break; }
                        }
                        if (!groupDown) { allGroupsDown = false; break; }
                    }
                    SetActive(allGroupsDown);
                }
            }
            catch
            {
                // Never let the watchdog die — it is the reliable path.
            }
            Thread.Sleep(15);
        }
    }

    /// <summary>While capturing: first newly-pressed key wins (skips L/R click).</summary>
    private void CapturePollSweep()
    {
        for (var vk = 3; vk < 256; vk++)
        {
            var down = NativeMethods.IsKeyDown(vk);
            if (down && !_captureBaseline[vk])
            {
                CompleteCapture(vk);
                return;
            }
            if (!down) _captureBaseline[vk] = false;
        }
    }

    // ----- Path 2: low-level hook -----

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        var message = wParam.ToInt64();
        var isDown = message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
        var isUp = message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;
        if (!isDown && !isUp)
        {
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        // KBDLLHOOKSTRUCT: vkCode @0, flags @8, dwExtraInfo @16.
        var vk = Marshal.ReadInt32(lParam);
        var flags = (uint)Marshal.ReadInt32(lParam, 8);
        var extraInfo = Marshal.ReadIntPtr(lParam, 16);

        // Skip ONLY our own synthetic input (dwExtraInfo signature), never the
        // LLKHF_INJECTED flag — see class remarks.
        if (extraInfo == InjectionGuard.Signature)
        {
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        HookSawInput = true;
        Trace?.Invoke($"vk=0x{vk:X2} {(isDown ? "down" : "up  ")} " +
            $"injected={((flags & NativeMethods.LLKHF_INJECTED) != 0 ? 1 : 0)} " +
            $"extra=0x{extraInfo.ToInt64():X}");

        if (isDown && KeyTyped != null)
        {
            var typed = KeyTyped;
            var ch = CharacterFor(vk);
            Post(() => typed(vk, ch));
        }

        if (isDown && KeyObserved != null)
        {
            var observed = KeyObserved;
            Post(() => observed(vk));
        }

        // Detect-key capture is modal: grab the key, swallow it, do nothing else.
        if (_captureCallback != null)
        {
            if (isDown)
            {
                CompleteCapture(vk);
                return new IntPtr(1);
            }
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        if (vk == NativeMethods.VK_ESCAPE && isDown && SessionActive)
        {
            Post(CancelRequested);
            return new IntPtr(1);
        }

        var def = _def;
        var isMember = false;
        foreach (var group in def.Groups)
        {
            if (Array.IndexOf(group, vk) >= 0) { isMember = true; break; }
        }

        if (isMember)
        {
            if (isDown) _hookPressed.Add(vk);
            else _hookPressed.Remove(vk);

            var nowActive = true;
            foreach (var group in def.Groups)
            {
                var groupDown = false;
                foreach (var member in group)
                {
                    if (_hookPressed.Contains(member)) { groupDown = true; break; }
                }
                if (!groupDown) { nowActive = false; break; }
            }
            SetActive(nowActive);

            // Swallow keys that would otherwise act on the focused app
            // (Caps Lock toggling, F-keys, the Space in Ctrl+Shift+Space) —
            // but only as part of the chord. Swallowing on membership alone
            // ate every Space system-wide for the Ctrl+Shift+Space hotkey.
            if (def.SuppressVk == vk)
            {
                if (isDown && (nowActive || _swallowed.Contains(vk)))
                {
                    _swallowed.Add(vk);
                    return new IntPtr(1);
                }
                if (isUp && _swallowed.Remove(vk))
                {
                    return new IntPtr(1);
                }
            }
        }
        else if (_swallowed.Contains(vk))
        {
            // A key whose down-stroke was swallowed keeps its auto-repeats and
            // its up-stroke swallowed too, whatever happened in between —
            // otherwise a take ending while Space is still held would let the
            // repeats through and leave an up-stroke without its down.
            if (isUp) _swallowed.Remove(vk);
            return new IntPtr(1);
        }
        else if (IsHotkeyDown)
        {
            // While a take is being recorded, Space is part of the gesture
            // (hands-free): the focused app must not see it, because
            // Ctrl+Win+Space switches the keyboard layout. Alt (Command Mode)
            // is NOT swallowed — swallowing it turned Ctrl+Alt+X shortcuts
            // into Ctrl+X — it is only menu-masked so releasing it does not
            // open the menu bar.
            if (isDown && RecordingActive && vk == VkSpace)
            {
                _swallowed.Add(vk);
                var other = OtherKeyPressed;
                Post(() => other?.Invoke(vk));
                SendMenuMask();
                return new IntPtr(1);
            }
            if (isDown)
            {
                if (RecordingActive && IsAltKey(vk)) SendMenuMask();
                var other = OtherKeyPressed;
                Post(() => other?.Invoke(vk));
            }
        }

        if (TryHandleLastDictationChord(vk, isDown, isUp))
        {
            return new IntPtr(1);
        }

        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    // ----- Gesture keys and the last-dictation chords -----

    private const int VkSpace = 0x20;
    private const int VkZ = 0x5A;
    private const int VkX = 0x58;

    /// <summary>
    /// An unassigned virtual key. Injecting it between a modifier's down and up
    /// strokes tells Windows "something else was pressed" — the same trick
    /// AutoHotkey uses (its menu-mask key) — so releasing Alt or Win after a
    /// swallowed key does not open the menu bar or the Start menu, and Alt+Shift
    /// is not taken as the switch-keyboard-layout gesture.
    /// </summary>
    private const ushort VkMenuMask = 0xE8;

    /// <summary>Keys this hook swallowed on the way down; their up-strokes go too.</summary>
    private readonly HashSet<int> _swallowed = new();

    private int _pendingChord;

    private static bool IsAltKey(int vk) => vk is 0x12 or 0xA4 or 0xA5;

    private static void SendMenuMask() => NativeMethods.Send(
        NativeMethods.KeyInput(VkMenuMask, false), NativeMethods.KeyInput(VkMenuMask, true));

    /// <summary>
    /// Alt+Shift+Z / Alt+Shift+X. The letter is swallowed, and the action fires
    /// only once Alt and Shift are both up again (plus a beat) — firing while
    /// they are still held would turn the Ctrl+V we inject into Alt+Shift+Ctrl+V.
    /// Wispr Flow waits for the release the same way. Runs on the hook thread,
    /// so the waiting happens on the thread pool, never here.
    /// </summary>
    private bool TryHandleLastDictationChord(int vk, bool isDown, bool isUp)
    {
        if (vk is not (VkZ or VkX)) return false;
        if (!isDown || _captureCallback != null) return false;
        if (!SettingsStore.Instance.Settings.PasteLastShortcut) return false;

        var alt = NativeMethods.IsKeyDown(0x12);
        var shift = NativeMethods.IsKeyDown(0x10);
        var ctrl = NativeMethods.IsKeyDown(0x11);
        var win = NativeMethods.IsKeyDown(0x5B) || NativeMethods.IsKeyDown(0x5C);
        if (!alt || !shift || ctrl || win) return false;

        _swallowed.Add(vk);
        SendMenuMask();
        // Key repeat while held must not queue a second paste.
        if (Interlocked.CompareExchange(ref _pendingChord, vk, 0) != 0) return true;

        var handler = vk == VkZ ? PasteLastRequested : CopyLastRequested;
        _ = Task.Run(async () =>
        {
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while ((NativeMethods.IsKeyDown(0x12) || NativeMethods.IsKeyDown(0x10))
                       && DateTime.UtcNow < deadline)
                {
                    await Task.Delay(15);
                }
                await Task.Delay(25);
                Post(handler);
            }
            finally
            {
                Interlocked.Exchange(ref _pendingChord, 0);
            }
        });
        return true;
    }

    /// <summary>De-duplicating funnel for both detection paths.</summary>
    /// <summary>
    /// The character a key produces, for the letters, digits and few symbols a
    /// dictionary term can contain. Deliberately not ToUnicodeEx: that mutates
    /// dead-key state and must never be called from inside a hook. Anything
    /// else reports no character and simply is not part of a correction.
    /// </summary>
    private static char CharacterFor(int vk)
    {
        var shift = NativeMethods.IsKeyDown(0x10);
        var caps = (NativeMethods.GetKeyState(0x14) & 1) != 0;

        if (vk is >= 0x41 and <= 0x5A)
        {
            return (char)(shift ^ caps ? vk : vk + 32);
        }
        if (vk is >= 0x30 and <= 0x39 && !shift) return (char)vk;
        if (vk is >= 0x60 and <= 0x69) return (char)('0' + (vk - 0x60));
        if (vk == 0x20) return ' ';
        if (vk == 0xBD && !shift) return '-';
        if (vk == 0xDE && !shift) return (char)39;   // apostrophe
        return (char)0;
    }

    private void SetActive(bool active)
    {
        bool changed;
        lock (_stateGate)
        {
            changed = _active != active;
            if (changed) _active = active;
        }
        if (!changed) return;

        // Keep the hook's key set in step when polling drove the change, so a
        // stale entry can't wedge the chord as permanently held.
        if (!active) _hookPressed.Clear();

        Post(active ? Pressed : Released);
    }

    private void Post(Action? handler)
    {
        if (handler != null) _dispatcher.BeginInvoke(handler);
    }

    public void Dispose()
    {
        SettingsStore.Instance.Changed -= RefreshDef;
        _shutdown.Cancel();
        if (_hookThreadId != 0)
        {
            NativeMethods.PostThreadMessage(_hookThreadId, NativeMethods.WM_QUIT,
                IntPtr.Zero, IntPtr.Zero);
        }
    }
}
