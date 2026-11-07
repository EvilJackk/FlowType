using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using FlowType.Core;

namespace FlowType.UI.Bar;

/// <summary>
/// Keeps the flow bar's window click-through everywhere except the pill.
///
/// <para><b>Why a fixed window plus WS_EX_TRANSPARENT, and not a window sized
/// to the pill.</b> The pill morphs between 44×8 and ~420×50 in 160–190 ms and
/// toasts grow out of it. Resizing and re-centring a layered
/// (AllowsTransparency) window on every animation frame makes WPF reallocate
/// and re-upload the whole surface each frame, and the move and the resize
/// land on screen as separate steps, so the pill visibly wobbles. A fixed
/// 460×84 window animates smoothly, but its empty area would sit on top of
/// whatever is underneath — the bottom of a maximised chat app, a game's HUD.</para>
///
/// <para>Windows already passes clicks through fully transparent pixels of a
/// per-pixel-alpha layered window, but not through the pill's soft shadow
/// (alpha &gt; 0), and it gives us no say over the hover band around the
/// resting sliver. So this class polls the cursor and sets WS_EX_TRANSPARENT
/// (the whole window ignores the mouse) whenever the cursor is outside the hot
/// rectangle, clearing it the moment the cursor enters. A low-level mouse hook
/// would be instant but costs every mouse event system-wide a trip through our
/// process; a poll of GetCursorPos costs microseconds. The poll runs at 25 Hz
/// near the bar and 7 Hz when the cursor is far away, and only while the bar
/// is visible.</para>
///
/// <para>The same poll doubles as hover detection (the window cannot see the
/// mouse while it is transparent, so WPF's own IsMouseOver would never fire
/// for the first move onto the bar).</para>
/// </summary>
internal sealed class ClickThroughGate
{
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const double NearDistancePx = 160;
    private static readonly TimeSpan NearInterval = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan FarInterval = TimeSpan.FromMilliseconds(150);

    private readonly Func<Rect?> _hotRect;
    private readonly DispatcherTimer _timer;
    private IntPtr _hwnd;
    private bool? _transparent;

    /// <param name="hotRect">
    /// The clickable area in device pixels (screen coordinates), or null when
    /// nothing should be clickable.
    /// </param>
    public ClickThroughGate(Func<Rect?> hotRect)
    {
        _hotRect = hotRect;
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = NearInterval };
        _timer.Tick += (_, _) => Poll();
    }

    /// <summary>Whether the cursor is over the hot rectangle (as of the last poll).</summary>
    public bool IsInside { get; private set; }

    /// <summary>
    /// Keep the window clickable regardless of the cursor — while the context
    /// menu is up the user may click the bar again to dismiss it.
    /// </summary>
    public bool HoldOpen
    {
        get => _holdOpen;
        set
        {
            _holdOpen = value;
            if (value) SetTransparent(false);
        }
    }
    private bool _holdOpen;

    /// <summary>Raised when the cursor enters (true) or leaves (false) the hot rectangle.</summary>
    public event Action<bool>? InsideChanged;

    public void Attach(IntPtr hwnd) => _hwnd = hwnd;

    public void Start()
    {
        if (_timer.IsEnabled) return;
        _timer.Start();
        Poll();
    }

    public void Stop()
    {
        _timer.Stop();
        if (IsInside)
        {
            IsInside = false;
            InsideChanged?.Invoke(false);
        }
    }

    /// <summary>Re-evaluate now (e.g. right after the pill changed size).</summary>
    public void Poll()
    {
        if (!GetCursorPos(out var p)) return;   // secure desktop, session switch…

        Rect? hot;
        try { hot = _hotRect(); }
        catch { hot = null; }

        var inside = hot is { } r && r.Contains(p.X, p.Y);
        SetTransparent(!(inside || _holdOpen));

        var near = hot is { } h && DistanceTo(h, p.X, p.Y) < NearDistancePx;
        var interval = near ? NearInterval : FarInterval;
        if (_timer.Interval != interval) _timer.Interval = interval;

        if (inside != IsInside)
        {
            IsInside = inside;
            InsideChanged?.Invoke(inside);
        }
    }

    private void SetTransparent(bool transparent)
    {
        if (_hwnd == IntPtr.Zero || _transparent == transparent) return;
        try
        {
            var style = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
            style = transparent ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
            NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, style);
            _transparent = transparent;
        }
        catch
        {
            // Worst case the bar stays as clickable as it always was.
        }
    }

    private static double DistanceTo(Rect r, double x, double y)
    {
        var dx = Math.Max(0, Math.Max(r.Left - x, x - r.Right));
        var dy = Math.Max(0, Math.Max(r.Top - y, y - r.Bottom));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);
}
