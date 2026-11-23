using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace TaskHub.Notifications;

/// <summary>One frame of the island: size, corner radius, visibility, and which content shows.</summary>
internal readonly record struct IslandShape(double Width, double Height, double Radius, double Opacity, double Scale, double CardOpacity, double PillOpacity)
{
    public const double PillHeight = 36;
    public const double CardRadius = 22;

    public static readonly IslandShape Hidden = new(PillHeight, PillHeight, PillHeight / 2, 0, 0.5, 0, 0);
    public static readonly IslandShape Circle = new(PillHeight, PillHeight, PillHeight / 2, 1, 1, 0, 0);
    public static readonly IslandShape Dot = new(22, 22, 11, 1, 1, 0, 1);

    public static IslandShape Pill(double width) => new(Math.Max(width, PillHeight), PillHeight, PillHeight / 2, 1, 1, 0, 1);

    public static IslandShape Card(double width, double height) => new(width, height, CardRadius, 1, 1, 1, 0);
}

/// <summary>
/// The window behind "island" notifications: one transparent, always-on-top, never-focused window at
/// the top-centre of a monitor, holding a black pill that morphs between a dot, a circle, a status
/// pill and a full notification card. Transparent pixels are click-through, so only the island
/// itself catches the mouse. All the sizing happens inside the window (no window resizing per frame),
/// which keeps the morph smooth.
/// </summary>
internal sealed class IslandWindow : Window
{
    public const double WindowWidth = 640;
    public const double WindowHeight = 480;
    private const double TopGap = 10;

    private readonly Border _island;
    private readonly Grid _inner;
    private readonly ContentControl _cardHost = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Focusable = false };
    private readonly ContentControl _pillHost = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Height = IslandShape.PillHeight, Focusable = false };
    private readonly ScaleTransform _scale = new(1, 1);
    private nint _hwnd;
    private Native.MonitorArea _monitor;
    private bool _placed;
    private IslandShape _shape = IslandShape.Hidden;
    private Motion? _motion;
    private bool _replyActive;

    public IslandWindow()
    {
        Title = "TaskHub notifications";
        AllowsTransparency = true;
        WindowStyle = WindowStyle.None;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = WindowWidth;
        Height = WindowHeight;
        Left = -32000;
        Top = -32000;
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        _inner = new Grid();
        _inner.Children.Add(_cardHost);
        _inner.Children.Add(_pillHost);

        _island = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, TopGap, 0, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0C)),
            Child = _inner,
            RenderTransform = _scale,
            RenderTransformOrigin = new Point(0.5, 0),
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 3, Direction = 270, Opacity = 0.4, Color = Colors.Black },
        };
        var root = new Grid { Background = Brushes.Transparent };
        root.Children.Add(_island);
        Content = root;

        Apply(_shape);
        SourceInitialized += OnSourceInitialized;
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(Place), System.Windows.Threading.DispatcherPriority.Loaded);
        Deactivated += (_, _) => EndReply();
    }

    public IslandShape Shape => _shape;
    public bool IsAnimating => _motion is not null;
    public bool IsMouseOverIsland => _island.IsMouseOver;
    public bool IsReplying => _replyActive;
    public UIElement Island => _island;

    public void SetCard(UIElement? card, double width)
    {
        _cardHost.Width = width;
        _cardHost.Content = card;
    }

    public void SetPill(UIElement? pill) => _pillHost.Content = pill;

    /// <summary>Measures a card's height at a given width (before it is shown).</summary>
    public static double MeasureHeight(FrameworkElement card, double width)
    {
        card.Measure(new Size(width, double.PositiveInfinity));
        return Math.Ceiling(card.DesiredSize.Height);
    }

    /// <summary>Width a pill's content needs.</summary>
    public static double MeasureWidth(FrameworkElement pill)
    {
        pill.Measure(new Size(double.PositiveInfinity, IslandShape.PillHeight));
        return Math.Ceiling(pill.DesiredSize.Width);
    }

    /// <summary>Makes the window visible on the monitor under the mouse.</summary>
    public void Appear()
    {
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        _hwnd = helper.Handle;
        _monitor = Native.MonitorUnderCursor();
        _placed = true;
        Place();
        if (!IsVisible) Show();
        Place();
    }

    public void Vanish()
    {
        EndReply();
        if (IsVisible) Hide();
    }

    private void Place()
    {
        if (_hwnd == 0 || !_placed) return;
        var widthPx = WindowWidth * _monitor.Scale;
        var x = _monitor.Work.Left + (_monitor.Work.Width - widthPx) / 2;
        Native.SetWindowPos(_hwnd, Native.HwndTopmost, (int)Math.Round(x), _monitor.Work.Top, 0, 0,
            Native.SwpNoSize | Native.SwpNoActivate | Native.SwpNoOwnerZOrder);
    }

    // ================================================================== morphing

    private sealed record Motion(IslandShape From, IslandShape To, IEasingFunction Ease, Stopwatch Clock, double DurationMs, Action? OnDone);

    public void MorphTo(IslandShape to, double ms, IEasingFunction ease, Action? onDone = null)
    {
        if (_motion is null) CompositionTarget.Rendering += OnFrame;
        _motion = new Motion(_shape, to, ease, Stopwatch.StartNew(), Math.Max(ms, 1), onDone);
    }

    /// <summary>Jumps straight to a shape (no animation).</summary>
    public void Snap(IslandShape shape)
    {
        if (_motion is not null)
        {
            CompositionTarget.Rendering -= OnFrame;
            _motion = null;
        }
        Apply(shape);
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (_motion is not { } m) return;
        var t = Math.Clamp(m.Clock.Elapsed.TotalMilliseconds / m.DurationMs, 0, 1);
        var p = m.Ease.Ease(t);
        static double Lerp(double a, double b, double k) => a + (b - a) * k;

        // Content fades in late (once there's room for it) and fades out early.
        static double Fade(double from, double to, double t) => to > from
            ? Lerp(from, to, Math.Clamp((t - 0.45) / 0.55, 0, 1))
            : Lerp(from, to, Math.Clamp(t / 0.35, 0, 1));

        Apply(new IslandShape(
            Lerp(m.From.Width, m.To.Width, p),
            Lerp(m.From.Height, m.To.Height, p),
            Lerp(m.From.Radius, m.To.Radius, p),
            Lerp(m.From.Opacity, m.To.Opacity, Math.Clamp(t * 1.6, 0, 1)),
            Lerp(m.From.Scale, m.To.Scale, p),
            Fade(m.From.CardOpacity, m.To.CardOpacity, t),
            Fade(m.From.PillOpacity, m.To.PillOpacity, t)));

        if (t >= 1)
        {
            CompositionTarget.Rendering -= OnFrame;
            _motion = null;
            Apply(m.To);
            m.OnDone?.Invoke();
        }
    }

    private void Apply(IslandShape s)
    {
        _shape = s;
        var w = Math.Max(1, s.Width);
        var h = Math.Max(1, s.Height);
        var r = Math.Clamp(s.Radius, 0, Math.Min(w, h) / 2);
        _island.Width = w;
        _island.Height = h;
        _island.CornerRadius = new CornerRadius(r);
        _inner.Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
        _island.Opacity = Math.Clamp(s.Opacity, 0, 1);
        _scale.ScaleX = _scale.ScaleY = Math.Max(0.05, s.Scale);
        _cardHost.Opacity = Math.Clamp(s.CardOpacity, 0, 1);
        _pillHost.Opacity = Math.Clamp(s.PillOpacity, 0, 1);
        _cardHost.IsHitTestVisible = s.CardOpacity > 0.5;
        _pillHost.IsHitTestVisible = s.PillOpacity > 0.5;
    }

    // ================================================================== focus (only for the reply box)

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.SetExtendedStyle(_hwnd, Native.WsExNoActivate | Native.WsExToolWindow, 0);
    }

    /// <summary>Lets the window take keyboard focus while the user types a reply.</summary>
    public void BeginReply(IInputElement box)
    {
        if (_replyActive || _hwnd == 0) return;
        _replyActive = true;
        Native.SetExtendedStyle(_hwnd, 0, Native.WsExNoActivate);
        Activate();
        box.Focus();
        Keyboard.Focus(box);
    }

    public void EndReply()
    {
        if (!_replyActive || _hwnd == 0) return;
        _replyActive = false;
        Native.SetExtendedStyle(_hwnd, Native.WsExNoActivate, 0);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_motion is not null) CompositionTarget.Rendering -= OnFrame;
        base.OnClosed(e);
    }
}
