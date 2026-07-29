namespace FlowType.Core;

/// <summary>
/// Marks input that FlowType itself synthesizes (the paste chord, type-out) so
/// the hotkey watcher can ignore its own keystrokes.
///
/// Two mechanisms, because each covers a case the other can't:
///
/// 1. <see cref="Signature"/> is stamped into every synthetic event's
///    dwExtraInfo. The keyboard hook filters on exactly this value.
///    Deliberately NOT the generic LLKHF_INJECTED flag: keyboard software
///    (Logitech G HUB, Razer Synapse, PowerToys Keyboard Manager, AutoHotkey,
///    remote desktop, KVMs) re-injects the user's real keystrokes, and those
///    carry LLKHF_INJECTED too. Filtering on that flag silently discards every
///    keystroke on such machines.
///
/// 2. <see cref="IsActive"/> is a short time window used by the polling
///    watchdog, which reads GetAsyncKeyState and therefore cannot see
///    dwExtraInfo at all.
/// </summary>
public static class InjectionGuard
{
    /// <summary>'FLOW' — dwExtraInfo stamp on FlowType's own synthetic input.</summary>
    public static readonly IntPtr Signature = new(0x464C4F57);

    private static long _activeUntilTicks;

    /// <summary>Whether FlowType is (or just was) synthesizing input.</summary>
    public static bool IsActive => Environment.TickCount64 < Interlocked.Read(ref _activeUntilTicks);

    /// <summary>Cover the next <paramref name="milliseconds"/> of synthetic input.</summary>
    public static void Extend(int milliseconds)
    {
        var until = Environment.TickCount64 + milliseconds;
        long current;
        do
        {
            current = Interlocked.Read(ref _activeUntilTicks);
            if (current >= until) return;
        }
        while (Interlocked.CompareExchange(ref _activeUntilTicks, until, current) != current);
    }
}
