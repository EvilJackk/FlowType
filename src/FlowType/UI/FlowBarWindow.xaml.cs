using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlowType.Audio;
using FlowType.Core;
using FlowType.Input;
using FlowType.Session;
using FlowType.UI.Bar;
using Microsoft.Win32;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ContextMenuEventArgs = System.Windows.Controls.ContextMenuEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using ToolTipService = System.Windows.Controls.ToolTipService;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace FlowType.UI;

/// <summary>
/// The flow bar — FlowType's always-on dictation pill, pinned bottom-centre
/// (or top-centre) of the primary screen.
///
/// <para><b>What it shows.</b> One pill that morphs between states rather
/// than separate widgets appearing and disappearing, so the eye never has to
/// find something new:</para>
/// <list type="bullet">
/// <item><b>Resting</b> 44×8 — a translucent sliver with a hairline edge. Out
///   of the way, but always findable.</item>
/// <item><b>Ready</b> 52×30 — what the sliver becomes under the cursor: a
///   microphone and a tooltip that teaches the hotkey. Click to start.</item>
/// <item><b>Listening</b> 96×30 — push-to-talk: just the waveform.</item>
/// <item><b>Hands-free</b> ~154×30 — ✕ cancel, waveform, elapsed time,
///   ■ finish. Hands-free has no key held down, so it needs on-screen ways
///   out; push-to-talk does not.</item>
/// <item><b>Command</b> — amber edge, amber bars and the word "Command", so a
///   spoken instruction is never mistaken for dictation.</item>
/// <item><b>Processing</b> 56×30 — a spinner; grows to show the session's
///   detail ("Polishing…", "Thinking…") when there is one.</item>
/// <item><b>Error</b> — red hairline and the message, until the session resets.</item>
/// <item><b>Toasts</b> — the pill itself grows into the message (mini 3 s,
///   standard 6 s, ≤ 420 DIP, two lines at most), then shrinks back. Messages
///   that arrive mid-dictation become a small caption above the pill instead,
///   because covering the waveform while someone is talking is worse than the
///   message being slightly less prominent.</item>
/// </list>
///
/// <para><b>What it never does.</b> Take focus (WS_EX_NOACTIVATE — the text is
/// going into whatever app you were in), appear in Alt+Tab (WS_EX_TOOLWINDOW),
/// or eat clicks meant for the app underneath: the window is click-through
/// everywhere except the pill (<see cref="ClickThroughGate"/>).</para>
///
/// <para>Design notes: the state set, centre-weighted waveform and "toast grows
/// out of the pill" idea are inspired by the reference app studied for
/// FlowType 2.0; sizes, colours, glyphs, maths and timings here are our own.</para>
/// </summary>
public partial class FlowBarWindow : Window
{
    /// <summary>Internal (not private) so <see cref="BarSelfTest"/> can check the hover geometry.</summary>
    internal enum BarView
    {
        Hidden,
        Resting,
        Ready,
        Listening,
        Processing,
        Error,
        Toast,
    }

    // ----- Geometry (DIPs) -----

    internal static readonly Size RestingSize = new(44, 8);
    internal static readonly Size ReadySize = new(52, 30);
    private static readonly Size PttSize = new(96, 30);
    private static readonly Size SpinnerOnlySize = new(56, 30);
    private const double PillHeight = 30;
    private const double MaxMessageWidth = 420;
    private const double MaxCornerRadius = 17;

    /// <summary>Glyph (14) + gap (8) in front of toast and error text.</summary>
    private const double GlyphSpace = 22;

    /// <summary>Distance from the work-area edge (taskbar) to the pill.</summary>
    private const double EdgeGap = 6;

    /// <summary>
    /// Extra room around the resting sliver that still counts as "on the bar",
    /// so finding an 8 px target is not a precision exercise. Kept while the
    /// bar is Ready as well — see <see cref="HoverZone"/>.
    /// </summary>
    internal const double RestingHoverPadX = 14, RestingHoverPadY = 10;

    /// <summary>Waveform opacity until the first real audio buffer arrives.</summary>
    private const double DimBrightness = 0.4;

    // ----- Timing -----

    /// <summary>State-to-state morph. Short enough to never feel like waiting.</summary>
    private static readonly Duration StateMorph = new(TimeSpan.FromMilliseconds(160));

    /// <summary>Toasts get a touch longer: they travel further.</summary>
    private static readonly Duration ToastMorph = new(TimeSpan.FromMilliseconds(180));

    /// <summary>Leaving the bar this briefly (a shaky hand) does not collapse it.</summary>
    private static readonly TimeSpan HoverExitGrace = TimeSpan.FromMilliseconds(220);

    /// <summary>A toast you are pointing at stays this much longer each time it would expire.</summary>
    private static readonly TimeSpan ToastHoverExtension = TimeSpan.FromMilliseconds(1500);

    private static readonly TimeSpan CaptionDuration = TimeSpan.FromSeconds(2.8);
    private static readonly TimeSpan CaptionErrorDuration = TimeSpan.FromSeconds(4.5);

    /// <summary>
    /// Quintic ease-out: most of the distance in the first third, then a long
    /// soft landing — reads as "snappy" without overshoot.
    /// </summary>
    private static readonly IEasingFunction MorphEase = CreateMorphEase();

    // ----- Collaborators -----

    private readonly MicLevelMeter _meter = new();
    private readonly ClickThroughGate _gate;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _frameTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _captionTimer;
    private readonly DispatcherTimer _hoverExitTimer;

    private readonly Action<SessionState, string> _onStateChanged;
    private readonly Action<string, bool> _onFlash;
    private readonly Action<float> _onLevel;

    // ----- Palette (from this window's own resources) -----

    private readonly Brush _barFill, _barFillHover, _restFill, _restStroke, _barStroke;
    private readonly Brush _ink, _inkMuted, _commandBrush, _dangerBrush, _warningBrush;

    // ----- State -----

    /// <summary>
    /// True for <c>--snapshot</c>: the window is laid out and rasterised but
    /// never shown, and no timers run. Detected from the command line so the
    /// snapshot run never flashes a bar onto a screen a game may own.
    /// </summary>
    private bool _offscreen;
    private bool _snapshotting;

    /// <summary>Snapshot-only override of the bar's edge (null = use settings).</summary>
    private bool? _snapshotTop;

    private SessionState _session = SessionState.Idle;
    private string _detail = "";
    private bool _handsFree;
    private bool _command;
    private BarToast? _toast;
    private bool _toastStarted;
    private bool _hover;
    private bool _menuOpen;
    private BarView _view = BarView.Hidden;
    private string _layoutKey = "";
    private FrameworkElement? _lastFace;
    private double _waveBrightness = DimBrightness;
    private double _lastFrameSeconds;
    private int _captionGeneration;
    private bool? _positionedTop;

    private bool _allowClose;
    private IntPtr _barHandle = IntPtr.Zero;
    private IntPtr _previousForeground = IntPtr.Zero;
    private Action? _pendingMenuAction;

    public event Action? OpenRequested;
    public event Action? SettingsRequested;
    public event Action? QuitRequested;

    public FlowBarWindow()
    {
        InitializeComponent();

        _offscreen = Environment.GetCommandLineArgs().Contains("--snapshot");

        _barFill = (Brush)Resources["BarFill"];
        _barFillHover = (Brush)Resources["BarFillHover"];
        _restFill = (Brush)Resources["RestFill"];
        _restStroke = (Brush)Resources["RestStroke"];
        _barStroke = (Brush)Resources["BarStroke"];
        _ink = (Brush)Resources["BarInk"];
        _inkMuted = (Brush)Resources["BarInkMuted"];
        _commandBrush = (Brush)Resources["CommandAccent"];
        _dangerBrush = (Brush)Resources["DangerAccent"];
        _warningBrush = (Brush)Resources["WarningAccent"];

        _frameTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _frameTimer.Tick += (_, _) => OnFrame();
        _toastTimer = new DispatcherTimer();
        _toastTimer.Tick += (_, _) => OnToastElapsed();
        _captionTimer = new DispatcherTimer();
        _captionTimer.Tick += (_, _) =>
        {
            _captionTimer.Stop();
            HideCaption(animate: true);
        };
        _hoverExitTimer = new DispatcherTimer { Interval = HoverExitGrace };
        _hoverExitTimer.Tick += (_, _) =>
        {
            _hoverExitTimer.Stop();
            SetHover(false);
        };

        _gate = new ClickThroughGate(HotRectInDevicePixels);
        _gate.InsideChanged += OnGateInsideChanged;

        // CornerRadius cannot be animated directly; deriving it from the live
        // height keeps every frame of the morph a true pill.
        Pill.SizeChanged += (_, _) => Pill.CornerRadius = RadiusFor(Pill.ActualHeight);

        _onStateChanged = (state, detail) => Dispatcher.BeginInvoke(() => OnSessionState(state, detail));
        _onFlash = (message, isError) => Dispatcher.BeginInvoke(() => OnFlash(message, isError));
        // Capture thread → meter directly: no dispatcher post per buffer.
        _onLevel = _meter.Ingest;

        DictationSession.Instance.StateChanged += _onStateChanged;
        DictationSession.Instance.Flash += _onFlash;
        AudioRecorder.Instance.LevelChanged += _onLevel;
        SettingsStore.Instance.Changed += OnSettingsChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;

        ApplyPosition();
        _session = DictationSession.Instance.State;
        Render(animate: false);
    }

    // ----- Session → view -----

    private void OnSessionState(SessionState state, string detail)
    {
        var wasRecording = _session == SessionState.Recording;
        _session = state;
        _detail = detail ?? "";

        switch (state)
        {
            case SessionState.Recording:
                if (!wasRecording)
                {
                    // A fresh take: forget the last one's noise floor and start dim.
                    _meter.Reset();
                    _waveBrightness = DimBrightness;
                    _command = false;
                    _handsFree = false;
                    Wave.Update(0, _clock.Elapsed.TotalSeconds, DimBrightness);
                    TimeText.Text = "0:00";
                    TimeText.Foreground = _inkMuted;
                }
                _handsFree |= DictationSession.Instance.IsHandsFree;
                _command |= _detail == "command" || DictationSession.Instance.IsCommandMode;
                // A new dictation supersedes whatever the last one had to say.
                DismissToast();
                break;

            case SessionState.Processing:
                _command |= DictationSession.Instance.IsCommandMode;
                break;

            default:
                _command = false;
                _handsFree = false;
                HideCaption(animate: true);
                break;
        }
        Render(animate: true);
    }

    private void OnFlash(string message, bool isError)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var toast = BarToast.From(message, isError);

        // Routed by the session's *live* state, not the last state event we
        // handled: "✓ 42 words" is raised a moment before the session goes
        // Idle, and belongs on the pill, not in a caption.
        var live = DictationSession.Instance.State;
        if (live is SessionState.Recording or SessionState.Processing)
        {
            ShowCaption(toast);
            return;
        }

        _toast = toast;
        _toastStarted = false;
        _layoutKey = "";
        Render(animate: true);
    }

    private BarView ResolveView()
    {
        var settings = SettingsStore.Instance.Settings;
        if (!_snapshotting && settings.BarPosition == "hidden") return BarView.Hidden;

        switch (_session)
        {
            case SessionState.Recording: return BarView.Listening;
            case SessionState.Processing: return BarView.Processing;
            case SessionState.Error: return BarView.Error;
        }
        if (_toast != null) return BarView.Toast;
        if (_hover || _menuOpen) return BarView.Ready;
        return settings.ShowBarWhenIdle || _snapshotting ? BarView.Resting : BarView.Hidden;
    }

    /// <summary>Bring the pill in line with the current state. Cheap when nothing changed.</summary>
    private void Render(bool animate)
    {
        var view = ResolveView();
        if (view == BarView.Hidden)
        {
            _view = BarView.Hidden;
            HideBar();
            return;
        }

        if (!IsVisible && !_offscreen)
        {
            // Grow out of a sliver rather than popping in at full size.
            _layoutKey = "";
            MorphTo(RestingSize, animate: false, StateMorph);
        }

        if (view == BarView.Toast && !_toastStarted && _toast != null)
        {
            // The clock starts when the toast is actually on screen, not when
            // it was raised (it may have waited out an error pill).
            _toastStarted = true;
            if (!_offscreen)
            {
                _toastTimer.Stop();
                _toastTimer.Interval = _toast.Duration;
                _toastTimer.Start();
            }
        }

        _view = view;
        LayoutFor(view, animate && !_offscreen);
        ApplyFill();
        UpdateTooltip();
        ShowBar();
        UpdateFrameTimer();
    }

    private string LayoutKeyFor(BarView view) => view switch
    {
        BarView.Resting => $"R|{IsPausedLook()}",
        BarView.Listening => $"L|{_handsFree}|{_command}",
        BarView.Processing => $"P|{_command}|{_detail}",
        BarView.Error => $"E|{_detail}",
        BarView.Toast => $"T|{_toast?.Kind}|{_toast?.Mini}|{_toast?.Text}",
        _ => view.ToString(),
    };

    private bool IsPausedLook() => !_snapshotting && SettingsStore.Instance.Settings.DictationPaused;

    private void LayoutFor(BarView view, bool animate)
    {
        var key = LayoutKeyFor(view);
        if (key == _layoutKey) return;
        _layoutKey = key;

        FrameworkElement? face = view switch
        {
            BarView.Ready => MicFace,
            BarView.Listening => ListenFace,
            BarView.Processing => ProcessingFace,
            BarView.Error or BarView.Toast => MessageFace,
            _ => null,
        };
        ShowOnly(face);

        var stroke = _barStroke;
        var strokeWidth = 1.0;
        var shadow = 1.0;
        var duration = StateMorph;
        Size target;

        switch (view)
        {
            case BarView.Resting:
                stroke = _restStroke;
                shadow = 0;   // a shadow under a translucent sliver just looks muddy
                target = RestingSize;
                break;

            case BarView.Listening:
                if (_command)
                {
                    stroke = _commandBrush;
                    strokeWidth = 1.5;
                }
                ConfigureListenFace();
                target = !_handsFree && !_command
                    ? PttSize
                    : MeasureFace(ListenFace, _handsFree ? 6 : 15, strokeWidth);
                break;

            case BarView.Processing:
                if (_command)
                {
                    stroke = _commandBrush;
                    strokeWidth = 1.5;
                }
                ProcessingText.Text = _detail;
                ProcessingText.Visibility = _detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                Spinner.SpinnerBrush = _command ? _commandBrush : _ink;
                target = _detail.Length == 0
                    ? SpinnerOnlySize
                    : MeasureFace(ProcessingFace, 14, strokeWidth);
                break;

            case BarView.Error:
                stroke = _dangerBrush;
                strokeWidth = 1.5;
                ConfigureMessageFace(
                    string.IsNullOrWhiteSpace(_detail) ? "Something went wrong" : _detail,
                    ToastKind.Error, mini: false, hPad: 14, strokeWidth);
                target = MeasureFace(MessageFace, 14, strokeWidth, minHeight: 34, vPad: 8);
                break;

            case BarView.Toast:
                var toast = _toast!;
                if (toast.Kind == ToastKind.Error) stroke = _dangerBrush;
                var hPad = toast.Mini ? 13 : 15;
                ConfigureMessageFace(toast.Text, toast.Kind, toast.Mini, hPad, strokeWidth);
                target = MeasureFace(MessageFace, hPad, strokeWidth,
                    minHeight: toast.Mini ? PillHeight : 34, vPad: toast.Mini ? 6 : 8);
                duration = ToastMorph;
                break;

            default: // Ready
                target = ReadySize;
                break;
        }

        Pill.BorderBrush = stroke;
        Pill.BorderThickness = new Thickness(strokeWidth);
        Pill.Opacity = view == BarView.Resting && IsPausedLook() ? 0.45 : 1;
        FadeTo(PillShadow, shadow, animate ? 120 : 0);

        // A new toast re-fades even though it reuses the message face, so one
        // message visibly replaces another instead of the text just swapping.
        if (face != null && (!ReferenceEquals(face, _lastFace) || view == BarView.Toast))
        {
            FadeInFace(face, animate);
        }
        _lastFace = face;

        MorphTo(target, animate, duration);
    }

    private void ConfigureListenFace()
    {
        var handsFree = _handsFree ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = handsFree;
        TimeText.Visibility = handsFree;
        FinishButton.Visibility = handsFree;
        CommandLabel.Visibility = _command ? Visibility.Visible : Visibility.Collapsed;
        Wave.BarBrush = _command ? _commandBrush : _ink;
        if (_command) FinishButton.Background = _commandBrush;
        else FinishButton.ClearValue(System.Windows.Controls.Border.BackgroundProperty);
    }

    private void ConfigureMessageFace(string text, ToastKind kind, bool mini, double hPad, double strokeWidth)
    {
        var glyph = kind != ToastKind.Info;
        GlyphHost.Visibility = glyph ? Visibility.Visible : Visibility.Collapsed;
        SuccessGlyph.Visibility = kind == ToastKind.Success ? Visibility.Visible : Visibility.Collapsed;
        ErrorGlyph.Visibility = kind == ToastKind.Error ? Visibility.Visible : Visibility.Collapsed;

        MessageText.Text = text;
        MessageText.FontSize = mini ? 12 : 12.5;
        // Two lines at most (one for a mini); the rest is trimmed with "…".
        MessageText.MaxHeight = mini ? 16.5 : 32.5;
        MessageText.MaxWidth = Math.Max(40,
            MaxMessageWidth - 2 * hPad - 2 * strokeWidth - (glyph ? GlyphSpace : 0));
    }

    private void ShowOnly(FrameworkElement? face)
    {
        foreach (var candidate in new FrameworkElement[] { MicFace, ListenFace, ProcessingFace, MessageFace })
        {
            candidate.Visibility = ReferenceEquals(candidate, face) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// The pill size that fits <paramref name="face"/> with the given padding.
    /// Measured with unlimited space, exactly as <see cref="CenterStage"/> will.
    /// </summary>
    private static Size MeasureFace(FrameworkElement face, double hPad, double strokeWidth,
        double minHeight = PillHeight, double vPad = 6)
    {
        // Measure() returns the cached size when the element itself is not
        // dirty, even if a child's text just changed — that once sized a
        // "42 words" toast to the previous error message. Dirty the subtree.
        InvalidateMeasureDeep(face);
        face.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var d = face.DesiredSize;
        var width = Math.Ceiling(d.Width + 2 * hPad + 2 * strokeWidth);
        var height = Math.Max(minHeight, Math.Ceiling(d.Height + 2 * vPad + 2 * strokeWidth));
        return new Size(Math.Min(MaxMessageWidth, width), height);
    }

    private static void InvalidateMeasureDeep(DependencyObject node)
    {
        if (node is UIElement element) element.InvalidateMeasure();
        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++) InvalidateMeasureDeep(VisualTreeHelper.GetChild(node, i));
    }

    // ----- Morph and fades -----

    private void MorphTo(Size target, bool animate, Duration duration)
    {
        if (!animate)
        {
            Pill.BeginAnimation(WidthProperty, null);
            Pill.BeginAnimation(HeightProperty, null);
            Pill.Width = target.Width;
            Pill.Height = target.Height;
            Pill.CornerRadius = RadiusFor(target.Height);
            return;
        }

        // No From: each morph starts wherever the previous one had got to, so
        // a state change mid-animation bends smoothly instead of jumping.
        Pill.BeginAnimation(WidthProperty,
            new DoubleAnimation(target.Width, duration) { EasingFunction = MorphEase });
        Pill.BeginAnimation(HeightProperty,
            new DoubleAnimation(target.Height, duration) { EasingFunction = MorphEase });
    }

    private static CornerRadius RadiusFor(double height) =>
        new(Math.Max(0, Math.Min(height / 2, MaxCornerRadius)));

    /// <summary>
    /// New content holds back for 40 ms (the pill is still mostly the old
    /// shape) and then fades in, so text is never seen squeezed by the morph.
    /// </summary>
    private static void FadeInFace(FrameworkElement face, bool animate)
    {
        face.BeginAnimation(OpacityProperty, null);
        if (!animate)
        {
            face.Opacity = 1;
            return;
        }
        var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(180) };
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(40))));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        face.BeginAnimation(OpacityProperty, fade);
    }

    private static void FadeTo(UIElement element, double opacity, int milliseconds)
    {
        if (milliseconds <= 0)
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = opacity;
            return;
        }
        element.BeginAnimation(OpacityProperty,
            new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(milliseconds)));
    }

    private void ApplyFill()
    {
        Pill.Background = _view == BarView.Resting
            ? _restFill
            : _hover ? _barFillHover : _barFill;
    }

    private static IEasingFunction CreateMorphEase()
    {
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    // ----- Live frames (waveform, spinner, timer) -----

    private void UpdateFrameTimer()
    {
        var needed = !_offscreen && IsVisible
            && (_view == BarView.Listening || _view == BarView.Processing);
        if (needed)
        {
            if (!_frameTimer.IsEnabled)
            {
                _lastFrameSeconds = _clock.Elapsed.TotalSeconds;
                _frameTimer.Start();
            }
        }
        else
        {
            _frameTimer.Stop();
        }
    }

    /// <summary>
    /// ~30 fps while listening or processing, and not at all otherwise. 30
    /// rather than 60 because the bar is a layered window: every repaint is a
    /// CPU composite of the whole surface, and the user may be running a game.
    /// </summary>
    private void OnFrame()
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = Math.Clamp(now - _lastFrameSeconds, 0, 0.1);
        _lastFrameSeconds = now;

        if (_view == BarView.Listening)
        {
            // A quick tap turns push-to-talk into hands-free, and Alt mid-take
            // turns on Command Mode, without necessarily raising a state event.
            // Both only ever switch on within a take, so latch them.
            var session = DictationSession.Instance;
            var handsFree = _handsFree || session.IsHandsFree;
            var command = _command || session.IsCommandMode;
            if (handsFree != _handsFree || command != _command)
            {
                _handsFree = handsFree;
                _command = command;
                Render(animate: true);
            }

            var level = _meter.Advance(dt);
            if (_meter.MicActive && _waveBrightness < 1)
            {
                _waveBrightness = Math.Min(1, _waveBrightness + dt / 0.15 * (1 - DimBrightness));
            }
            Wave.Update(level, now, _waveBrightness);
            if (_handsFree) ShowElapsed(DateTime.Now - session.RecordingStartTime);
        }
        else if (_view == BarView.Processing)
        {
            Spinner.Update(now);
        }
    }

    private void ShowElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        var text = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
        if (TimeText.Text != text) TimeText.Text = text;

        // The last 30 s before the length cap turn amber: the take is about to
        // stop on its own, and that should not come as a surprise. No cap set
        // (the default) = never amber.
        var nearCap = AudioRecorder.MaxDuration is { } cap && cap - elapsed <= TimeSpan.FromSeconds(30);
        var brush = nearCap ? _warningBrush : _inkMuted;
        if (!ReferenceEquals(TimeText.Foreground, brush)) TimeText.Foreground = brush;
    }

    // ----- Toasts and captions -----

    private void OnToastElapsed()
    {
        _toastTimer.Stop();
        if (_toast == null) return;
        if (_hover)
        {
            // Being read: keep it.
            _toastTimer.Interval = ToastHoverExtension;
            _toastTimer.Start();
            return;
        }
        DismissToast();
        Render(animate: true);
    }

    private void DismissToast()
    {
        _toast = null;
        _toastStarted = false;
        _toastTimer.Stop();
    }

    private void ShowCaption(BarToast toast)
    {
        _captionGeneration++;
        var error = toast.Kind == ToastKind.Error;
        CaptionText.Text = toast.Original.Length > 0 ? toast.Original : toast.Text;
        CaptionText.Foreground = error ? _dangerBrush : _ink;
        Caption.BorderBrush = error ? _dangerBrush : _barStroke;
        if (Caption.Visibility != Visibility.Visible)
        {
            // Fade in from nothing, not from whatever a cut-short fade left behind.
            Caption.BeginAnimation(OpacityProperty, null);
            Caption.Opacity = 0;
            Caption.Visibility = Visibility.Visible;
        }
        FadeTo(Caption, 1, _offscreen ? 0 : 120);

        _captionTimer.Stop();
        if (!_offscreen)
        {
            _captionTimer.Interval = error ? CaptionErrorDuration : CaptionDuration;
            _captionTimer.Start();
        }
    }

    private void HideCaption(bool animate)
    {
        _captionTimer.Stop();
        if (Caption.Visibility != Visibility.Visible) return;
        var generation = ++_captionGeneration;
        if (!animate || _offscreen)
        {
            Caption.BeginAnimation(OpacityProperty, null);
            Caption.Visibility = Visibility.Collapsed;
            return;
        }
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) =>
        {
            // A newer caption may have started while this one faded.
            if (generation == _captionGeneration) Caption.Visibility = Visibility.Collapsed;
        };
        Caption.BeginAnimation(OpacityProperty, fade);
    }

    // ----- Hover and click-through -----

    /// <summary>The pill's clickable area in screen pixels, or null when hidden.</summary>
    private Rect? HotRectInDevicePixels()
    {
        if (!IsVisible || _view == BarView.Hidden) return null;
        if (PresentationSource.FromVisual(Pill) == null) return null;

        var topLeft = Pill.PointToScreen(new Point(0, 0));
        var bottomRight = Pill.PointToScreen(new Point(Pill.ActualWidth, Pill.ActualHeight));
        var rect = new Rect(topLeft, bottomRight);
        var scale = Pill.ActualWidth > 0 ? rect.Width / Pill.ActualWidth : 1;
        return HoverZone(rect, _view, scale);
    }

    /// <summary>
    /// The hover/click zone for a pill occupying <paramref name="pill"/> (screen
    /// pixels, <paramref name="scale"/> pixels per DIP) while the bar shows
    /// <paramref name="view"/>.
    ///
    /// <para><b>Why the pad stays on while Ready.</b> Hovering the padded band
    /// around the resting sliver turns the bar Ready (52×30). If Ready's zone
    /// were the bare pill, a cursor in the band but outside 52×30 — say 30 DIP
    /// right of centre, just above the taskbar — would be "inside" while
    /// Resting and "outside" while Ready, and the bar would flip between the
    /// two every hover-exit grace (~300 ms) for as long as the cursor sat
    /// there. With the same pad on Ready, Ready's zone contains Resting's
    /// (both pills share the anchored edge and Ready is wider and taller), so
    /// entering is the only edge that matters and leaving needs a real move
    /// away. It also covers the 160 ms morph, when the pill is still close to
    /// sliver size. Other views keep the exact pill: their size does not
    /// depend on hover, and the pad would only eat clicks meant for the app
    /// underneath.</para>
    /// </summary>
    internal static Rect HoverZone(Rect pill, BarView view, double scale)
    {
        if (view is BarView.Resting or BarView.Ready)
        {
            pill.Inflate(RestingHoverPadX * scale, RestingHoverPadY * scale);
        }
        return pill;
    }

    private void OnGateInsideChanged(bool inside)
    {
        if (inside)
        {
            _hoverExitTimer.Stop();
            SetHover(true);
        }
        else if (_hover)
        {
            _hoverExitTimer.Stop();
            _hoverExitTimer.Start();
        }
    }

    private void SetHover(bool hover)
    {
        if (_hover == hover) return;
        _hover = hover;
        // Resting ↔ Ready, and the hover tint everywhere else.
        Render(animate: true);
    }

    private void UpdateTooltip()
    {
        var settings = SettingsStore.Instance.Settings;
        var hotkey = HotkeyDefs.CurrentDisplay(settings);
        var tip = _view switch
        {
            BarView.Listening when _handsFree => $"Press {hotkey} or click ■ to finish · ✕ or Esc cancels",
            BarView.Listening => $"Listening — let go of {hotkey} to finish · Esc cancels",
            BarView.Processing => "Working on it — Esc cancels",
            BarView.Error => "Click to try again",
            BarView.Toast => "Click to dismiss",
            _ when settings.DictationPaused => $"{hotkey} is paused — click to dictate anyway",
            _ => settings.ActivationMode switch
            {
                "hold" => $"Hold {hotkey} to dictate · click to start",
                "toggle" => $"Press {hotkey} to start and stop · click to start",
                _ => $"Hold {hotkey} to dictate · tap for hands-free · click to start",
            },
        };
        if (!Equals(Pill.ToolTip, tip))
        {
            Pill.ToolTip = tip;
            AutomationProperties.SetName(Pill, tip);
        }
    }

    // ----- Interaction -----

    private void Pill_Click(object sender, MouseButtonEventArgs e)
    {
        // A left click while the menu is up means "dismiss", not "dictate".
        if (BarMenu.IsOpen)
        {
            BarMenu.IsOpen = false;
            return;
        }

        switch (_view)
        {
            case BarView.Toast:
                DismissToast();
                Render(animate: true);
                break;
            case BarView.Processing:
                // Nothing to toggle: Esc cancels, and a click must not queue a second take.
                break;
            default:
                DictationSession.Instance.Toggle();
                break;
        }
    }

    private void CancelButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        DictationSession.Instance.Cancel();
    }

    private void FinishButton_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (DictationSession.Instance.State == SessionState.Recording) DictationSession.Instance.Toggle();
    }

    /// <summary>
    /// The flow bar is a WS_EX_NOACTIVATE window so it never steals focus from
    /// whatever you're dictating into. That style also breaks its context menu:
    /// a menu whose owner can't activate never gains focus, so it can't notice
    /// you clicking elsewhere and stays stuck open until you pick something.
    ///
    /// Fix: drop WS_EX_NOACTIVATE for exactly as long as the menu is up and let
    /// the bar activate normally, so the menu behaves like any other menu
    /// (click-away and Esc both dismiss it). We remember which window had focus
    /// beforehand and hand it straight back on close, so the next dictation
    /// still lands in the app you were using.
    /// </summary>
    private void Pill_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var state = DictationSession.Instance.State;
        MenuDictate.Header = state == SessionState.Recording ? "Stop dictating" : "Start dictating";
        MenuDictate.IsEnabled = state != SessionState.Processing;

        _menuOpen = true;
        _gate.HoldOpen = true;
        Render(animate: true);

        _previousForeground = NativeMethods.GetForegroundWindow();
        SetNoActivate(false);
        if (_barHandle != IntPtr.Zero) NativeMethods.SetForegroundWindow(_barHandle);
    }

    private void BarMenu_Closed(object sender, RoutedEventArgs e)
    {
        SetNoActivate(true);
        if (_previousForeground != IntPtr.Zero && _previousForeground != _barHandle)
        {
            NativeMethods.SetForegroundWindow(_previousForeground);
        }
        _previousForeground = IntPtr.Zero;

        _menuOpen = false;
        _gate.HoldOpen = false;
        _gate.Poll();

        if (_pendingMenuAction is { } action)
        {
            _pendingMenuAction = null;
            RunAfterFocusReturns(action);
        }
        Render(animate: true);
    }

    private void SetNoActivate(bool enabled)
    {
        if (_barHandle == IntPtr.Zero) return;
        var style = NativeMethods.GetWindowLong(_barHandle, NativeMethods.GWL_EXSTYLE);
        style = enabled
            ? style | NativeMethods.WS_EX_NOACTIVATE
            : style & ~NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLong(_barHandle, NativeMethods.GWL_EXSTYLE, style);
    }

    /// <summary>
    /// Run <paramref name="action"/> once the menu has closed and focus is back
    /// in the app the user was in. WPF raises a menu item's Click and the
    /// menu's Closed in an order that is not guaranteed, so this works from
    /// either side: before Closed it parks the action; after, it runs it.
    /// </summary>
    private void AfterMenuCloses(Action action)
    {
        if (BarMenu.IsOpen) _pendingMenuAction = action;
        else RunAfterFocusReturns(action);
    }

    private void RunAfterFocusReturns(Action action)
    {
        // SetForegroundWindow returns before the target has actually processed
        // its activation; a paste sent in that gap lands nowhere.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try { action(); }
            catch { }
        };
        timer.Start();
    }

    private void MenuDictate_Click(object sender, RoutedEventArgs e) =>
        DictationSession.Instance.Toggle();

    private void MenuPasteLast_Click(object sender, RoutedEventArgs e) =>
        AfterMenuCloses(() => _ = PasteLastSafelyAsync());

    private void MenuCopyLast_Click(object sender, RoutedEventArgs e)
    {
        try { UserActions.CopyLast(); }
        catch { }
    }

    private static async Task PasteLastSafelyAsync()
    {
        try { await UserActions.PasteLastAsync(); }
        catch { }
    }

    private void MenuOpen_Click(object sender, RoutedEventArgs e) => OpenRequested?.Invoke();
    private void MenuSettings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();
    private void MenuQuit_Click(object sender, RoutedEventArgs e) => QuitRequested?.Invoke();

    private void MenuHideBar_Click(object sender, RoutedEventArgs e)
    {
        SettingsStore.Instance.Settings.BarPosition = "hidden";
        SettingsStore.Instance.Save();
        Render(animate: false);
    }

    // ----- Showing, hiding, placement -----

    private void ShowBar()
    {
        if (_offscreen) return;
        if (!IsVisible)
        {
            ApplyPosition();
            Show();
        }
        _gate.Start();
    }

    private void HideBar()
    {
        _gate.Stop();
        _hoverExitTimer.Stop();
        _frameTimer.Stop();
        _hover = false;
        _layoutKey = "";
        if (IsVisible) Hide();
    }

    /// <summary>
    /// Centre on the primary work area, hugging the taskbar edge (or the top),
    /// and put the caption on the side away from the screen edge.
    /// </summary>
    private void ApplyPosition()
    {
        var top = _snapshotTop ?? SettingsStore.Instance.Settings.BarPosition == "top";
        if (_positionedTop != top)
        {
            _positionedTop = top;
            Stage.Children.Clear();
            if (top)
            {
                Stage.Children.Add(PillHost);
                Stage.Children.Add(Caption);
            }
            else
            {
                Stage.Children.Add(Caption);
                Stage.Children.Add(PillHost);
            }
            Stage.VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            Stage.Margin = top ? new Thickness(0, EdgeGap, 0, 0) : new Thickness(0, 0, 0, EdgeGap);
            Caption.Margin = top ? new Thickness(0, 6, 0, 0) : new Thickness(0, 0, 0, 6);
            ToolTipService.SetPlacement(Pill, top ? PlacementMode.Bottom : PlacementMode.Top);
            ToolTipService.SetVerticalOffset(Pill, top ? 6 : -6);
        }

        try
        {
            var area = SystemParameters.WorkArea;
            Left = Math.Round(area.Left + (area.Width - Width) / 2);
            Top = top ? area.Top : area.Bottom - Height;
        }
        catch
        {
            // Mid display-change the work area can be momentarily unavailable;
            // the next change notification puts the bar back.
        }
    }

    private void OnSettingsChanged() => Dispatcher.BeginInvoke(() =>
    {
        ApplyPosition();
        // Pause state, hotkey and position can all change the idle look.
        _layoutKey = "";
        Render(animate: true);
    });

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(ApplyPosition);

    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The taskbar moved, resized or auto-hid: the work area changed under us.
        if (e.PropertyName == nameof(SystemParameters.WorkArea))
        {
            Dispatcher.BeginInvoke(ApplyPosition);
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyPosition();
    }

    // ----- Snapshot (hidden diagnostic) -----

    /// <summary>
    /// Render every bar state to PNG (1× and 2×) without showing the window:
    /// bar-resting, -ready, -listening(-waiting), -hands-free, -command,
    /// -command-hands-free, -caption, -processing, -processing-with-detail,
    /// -processing-command, -caption-error, -error, -toast-success,
    /// -toast-info, -toast-error, -toast-long, -top-caption, plus
    /// -resting-light / -listening-light over a light backdrop to check the
    /// hairline and shadow on white apps. Each also has an @2x twin.
    /// </summary>
    internal void SnapshotStates(string dir)
    {
        Directory.CreateDirectory(dir);
        var dark = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B));
        var light = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3));
        dark.Freeze();
        light.Freeze();

        var wasOffscreen = _offscreen;
        _offscreen = true;
        _snapshotting = true;
        _toastTimer.Stop();
        _captionTimer.Stop();

        // A never-shown Window does not lay its content out at 460×84, and a
        // parentless element is arranged at its own desired size. So the stage
        // is rendered detached, as a root of its own, pinned to the window's
        // size, and put back afterwards.
        var attached = ReferenceEquals(Content, Root);
        if (attached) Content = null;
        Root.Width = Width;
        Root.Height = Height;
        _snapshotTop = false;
        ApplyPosition();
        try
        {
            void Set(SessionState state, string detail = "", bool handsFree = false, bool command = false,
                BarToast? toast = null, bool hover = false)
            {
                _session = state;
                _detail = detail;
                _handsFree = handsFree;
                _command = command;
                _toast = toast;
                _toastStarted = toast != null;
                _hover = hover;
                _menuOpen = false;
                _layoutKey = "";
                _lastFace = null;
                HideCaption(animate: false);
                Render(animate: false);
            }

            void Shoot(string name, Brush? backdrop = null)
            {
                var size = new Size(Width, Height);
                Root.Measure(size);
                Root.Arrange(new Rect(size));
                Root.UpdateLayout();
                // SizeChanged is not guaranteed outside a real layout pass.
                Pill.CornerRadius = RadiusFor(Pill.ActualHeight);
                SavePng(Root, Path.Combine(dir, $"bar-{name}.png"), backdrop ?? dark, 1);
                SavePng(Root, Path.Combine(dir, $"bar-{name}@2x.png"), backdrop ?? dark, 2);
            }

            Set(SessionState.Idle);
            Shoot("resting");
            Shoot("resting-light", light);

            Set(SessionState.Idle, hover: true);
            Shoot("ready");

            Set(SessionState.Recording);
            Wave.Update(0, 0.2, DimBrightness);
            Shoot("listening-waiting");

            Set(SessionState.Recording);
            Wave.Update(0.6, 0.37, 1);
            Shoot("listening");
            Shoot("listening-light", light);

            Set(SessionState.Recording, handsFree: true);
            Wave.Update(0.65, 0.81, 1);
            ShowElapsed(TimeSpan.FromSeconds(7));
            Shoot("hands-free");

            Set(SessionState.Recording, command: true);
            Wave.Update(0.7, 0.52, 1);
            Shoot("command");

            Set(SessionState.Recording, handsFree: true, command: true);
            Wave.Update(0.55, 1.4, 1);
            ShowElapsed(TimeSpan.FromSeconds(12));
            Shoot("command-hands-free");

            Set(SessionState.Recording, handsFree: true);
            Wave.Update(0.45, 2.1, 1);
            ShowElapsed(TimeSpan.FromSeconds(3));
            ShowCaption(BarToast.From("Hands-free — press the hotkey again to finish", false));
            Shoot("caption");

            Set(SessionState.Processing);
            Spinner.Update(0.3);
            Shoot("processing");

            Set(SessionState.Processing, "Polishing…");
            Shoot("processing-with-detail");

            Set(SessionState.Processing, "Thinking…", command: true);
            Shoot("processing-command");

            Set(SessionState.Processing);
            Spinner.Update(0.55);
            ShowCaption(BarToast.From("Microphone disconnected — transcribing what was captured", true));
            Shoot("caption-error");

            Set(SessionState.Error, "Microphone unavailable: no input device");
            Shoot("error");

            Set(SessionState.Idle, toast: BarToast.From("✓ 42 words", false));
            Shoot("toast-success");

            Set(SessionState.Idle, toast: BarToast.From("Canceled", false));
            Shoot("toast-info");

            Set(SessionState.Idle, toast: BarToast.From("No sound from the microphone — check it's not muted", true));
            Shoot("toast-error");

            Set(SessionState.Idle, toast: BarToast.From(
                "Added to dictionary: \"kube cuddle\" → kubectl, \"post gress\" → Postgres, and four more terms you corrected while writing today's release notes",
                false));
            Shoot("toast-long");

            // Top-of-screen bar: pill hugs the top edge, caption hangs below it.
            _snapshotTop = true;
            ApplyPosition();
            Set(SessionState.Recording, handsFree: true);
            Wave.Update(0.5, 0.9, 1);
            ShowElapsed(TimeSpan.FromSeconds(41));
            ShowCaption(BarToast.From("Hands-free — press the hotkey again to finish", false));
            Shoot("top-caption");
        }
        finally
        {
            _snapshotTop = null;
            ApplyPosition();
            Root.ClearValue(WidthProperty);
            Root.ClearValue(HeightProperty);
            if (attached) Content = Root;
            _snapshotting = false;
            _offscreen = wasOffscreen;
            _session = DictationSession.Instance.State;
            _detail = "";
            _handsFree = false;
            _command = false;
            _toast = null;
            _toastStarted = false;
            _hover = false;
            _layoutKey = "";
            HideCaption(animate: false);
            Render(animate: false);
        }
    }

    /// <summary>
    /// Rasterise <paramref name="element"/> at <paramref name="scale"/>× over a
    /// backdrop. The 2× copy is for reviewing edges and text at high-DPI.
    /// </summary>
    private static void SavePng(FrameworkElement element, string path, Brush backdrop, double scale)
    {
        var width = element.ActualWidth;
        var height = element.ActualHeight;
        if (width <= 0 || height <= 0) return;

        var backdropVisual = new DrawingVisual();
        using (var dc = backdropVisual.RenderOpen())
        {
            dc.DrawRectangle(backdrop, null, new Rect(0, 0, width, height));
        }

        // Rendered directly (not through a VisualBrush): a VisualBrush frames
        // the *drawn* bounds — shadow included — which shifts the pill.
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(backdropVisual);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    // ----- Window plumbing -----

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _barHandle = new WindowInteropHelper(this).Handle;
        _gate.Attach(_barHandle);
        var style = NativeMethods.GetWindowLong(_barHandle, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(_barHandle, NativeMethods.GWL_EXSTYLE,
            style | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            HideBar();
            return;
        }

        // The session, recorder and settings are singletons that outlive this
        // window; leaving handlers attached would keep it alive and repainting.
        DictationSession.Instance.StateChanged -= _onStateChanged;
        DictationSession.Instance.Flash -= _onFlash;
        AudioRecorder.Instance.LevelChanged -= _onLevel;
        SettingsStore.Instance.Changed -= OnSettingsChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        _gate.Stop();
        _frameTimer.Stop();
        _toastTimer.Stop();
        _captionTimer.Stop();
        _hoverExitTimer.Stop();
        base.OnClosing(e);
    }

    public void AllowClose()
    {
        _allowClose = true;
        Close();
    }
}
