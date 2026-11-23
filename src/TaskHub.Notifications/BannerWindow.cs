using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using TaskHub.Core.Configuration;
using TaskHub.Core.Notifications;

namespace TaskHub.Notifications;

/// <summary>
/// One banner: a frameless, always-on-top window with a DWM acrylic backdrop and rounded corners
/// (Windows 11 22H2+; solid fallback elsewhere). No AllowsTransparency — that would make it a layered
/// window, which blocks the backdrop and is slow. It never takes focus (WS_EX_NOACTIVATE) except
/// while you type in its reply box. All positions are physical pixels; movement is per-frame
/// SetWindowPos with an easing curve, while the content fades/scales via render transforms.
/// </summary>
internal sealed class BannerWindow : Window
{
    private const double ShadowAllowance = 24; // px: DWM shadow extends beyond the window

    private readonly NotificationRequest _request;
    private readonly BannerStyle _style;
    private readonly NotificationOptions _options;
    private readonly Native.MonitorArea _monitor;
    private readonly ILogger _log;
    private readonly Border _root;
    private readonly Border _surface;
    private readonly ScaleTransform _contentScale = new(1, 1);
    private readonly Rectangle _countdown = new();
    private readonly ScaleTransform _countdownScale = new(1, 1);
    private readonly TextBox? _reply;
    private readonly TextBlock? _replyHint;
    private readonly DispatcherTimer _tick = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Stopwatch _visibleFor = new();

    private nint _hwnd;
    private bool _acrylic;
    private double _left;
    private double _y;
    private double _targetY;
    private Motion? _motion;
    private bool _closing;
    private bool _replyActive;
    private Point? _dragStart;
    private double _dragBaseY;

    public BannerWindow(NotificationRequest request, BannerStyle style, NotificationOptions options, Native.MonitorArea monitor, ILogger log)
    {
        _log = log;
        _request = request;
        _style = style;
        _options = options;
        _monitor = monitor;

        Title = request.Title;
        Width = options.Width;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000;
        Top = -32000;
        Background = Brushes.Transparent;
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        _acrylic = options.UseAcrylic() && Native.SupportsSystemBackdrop;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = _acrylic ? new Thickness(-1) : new Thickness(0),
            UseAeroCaptionButtons = false,
            CornerRadius = new CornerRadius(0),
        });

        // ---------------------------------------------------------------- content
        var accent = AccentFor(request.Priority);
        var icon = new TextBlock
        {
            Text = request.Icon ?? "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Emoji"),
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 12, 0),
            Foreground = accent,
        };

        var close = new Button
        {
            Content = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 10 },
            Padding = new Thickness(6),
            MinWidth = 0,
            MinHeight = 0,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
            ToolTip = "Dismiss",
            Focusable = false,
        };
        close.Click += (_, _) => Dismiss(BannerCloseReason.Dismissed);

        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = request.Title,
            FontSize = 14.5,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 64,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (!string.IsNullOrWhiteSpace(request.Body))
            text.Children.Add(Secondary(request.Body, 13, 0.85, new Thickness(0, 3, 0, 0), maxHeight: 96));
        if (!string.IsNullOrWhiteSpace(request.Detail))
            text.Children.Add(Secondary(request.Detail, 12, 0.65, new Thickness(0, 4, 0, 0)));

        var header = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(icon, Dock.Left);
        header.Children.Add(close);
        header.Children.Add(icon);
        header.Children.Add(text);

        var body = new StackPanel { Margin = new Thickness(16, 14, 10, 14) };
        body.Children.Add(header);

        if (request.Buttons.Count > 0)
        {
            var buttons = new WrapPanel { Margin = new Thickness(32, 12, 0, 0) };
            for (var i = 0; i < request.Buttons.Count; i++)
            {
                var b = request.Buttons[i];
                var button = new Button
                {
                    Content = b.Label,
                    Margin = new Thickness(0, 0, 8, 4),
                    Padding = new Thickness(14, 5, 14, 5),
                    Focusable = false,
                };
                if (i == 0) button.SetResourceReference(StyleProperty, "AccentButtonStyle");
                var action = b.Action;
                button.Click += (_, _) => Act(action);
                buttons.Children.Add(button);
            }
            body.Children.Add(buttons);
        }

        if (style.AllowReply)
        {
            _reply = new TextBox
            {
                Margin = new Thickness(32, 8, 6, 0),
                FontSize = 12.5,
                ToolTip = "snooze 10m · 1h · done · tomorrow 9am — Enter to apply, Esc to cancel",
            };
            _replyHint = new TextBlock
            {
                Text = "Reply: snooze 10m · done · tomorrow 9am",
                FontSize = 11.5,
                Opacity = 0.55,
                Margin = new Thickness(34, 3, 0, 0),
            };
            _reply.PreviewMouseLeftButtonDown += (_, _) => BeginReply();
            _reply.KeyDown += OnReplyKey;
            _reply.LostKeyboardFocus += (_, _) => EndReply();
            body.Children.Add(_reply);
            body.Children.Add(_replyHint);
        }

        // countdown bar (D)
        _countdown.Height = 2.5;
        _countdown.HorizontalAlignment = HorizontalAlignment.Stretch;
        _countdown.VerticalAlignment = VerticalAlignment.Bottom;
        _countdown.Fill = accent;
        _countdown.Opacity = 0.7;
        _countdown.RenderTransformOrigin = new Point(0, 0.5);
        _countdown.RenderTransform = _countdownScale;
        _countdown.Visibility = style.Duration is null ? Visibility.Collapsed : Visibility.Visible;

        // urgent/high: coloured strip on the left (A)
        var strip = new Border
        {
            Width = 4,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = accent,
            Visibility = request.Priority >= NotificationPriority.High ? Visibility.Visible : Visibility.Collapsed,
        };

        var layers = new Grid();
        layers.Children.Add(body);
        layers.Children.Add(strip);
        layers.Children.Add(_countdown);

        _surface = new Border { Child = layers };
        _surface.RenderTransform = _contentScale;
        _surface.RenderTransformOrigin = new Point(0.5, 0);
        _surface.Opacity = 0;

        _root = new Border { Child = _surface, Background = Brushes.Transparent };
        ApplySolidBackground(!_acrylic);
        Content = _root;

        // ---------------------------------------------------------------- interaction
        _root.MouseLeftButtonDown += OnPointerDown;
        _root.MouseMove += OnPointerMove;
        _root.MouseLeftButtonUp += OnPointerUp;
        _tick.Tick += OnTick;
        SourceInitialized += OnSourceInitialized;
        Deactivated += (_, _) => EndReply();
    }

    public Guid Id => _request.Id;
    public NotificationRequest Request => _request;
    public nint MonitorHandle => _monitor.Handle;
    public Native.MonitorArea Monitor => _monitor;

    /// <summary>Height in physical pixels (for stacking).</summary>
    public double PixelHeight => ActualHeight * _monitor.Scale;

    public event EventHandler<string>? ActionInvoked;
    public event EventHandler<BannerCloseReason>? Finished;
    public event EventHandler? HeightChanged;

    // ================================================================== lifecycle

    /// <summary>Creates the window above the top edge of its monitor, ready to slide in.</summary>
    public void Prepare()
    {
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        _hwnd = helper.Handle;
        _left = BannerLayout.CenterLeft(_monitor.Work.Left, _monitor.Work.Width, Width * _monitor.Scale);
        _y = BannerLayout.AboveScreen(_monitor.Work.Top, Math.Max(PixelHeight, 120 * _monitor.Scale), ShadowAllowance);
        Place();
        Show();
        SizeChanged += (_, _) => HeightChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Slides in to <paramref name="targetY"/> with the configured preset.</summary>
    public void Enter(double targetY)
    {
        _targetY = targetY;
        var urgent = _request.Priority == NotificationPriority.Urgent;
        var (ease, ms) = _style.Animation switch
        {
            AnimationPreset.Slide => ((IEasingFunction)new CubicEase { EasingMode = EasingMode.EaseOut }, 320),
            AnimationPreset.Drop => (new BounceEase { EasingMode = EasingMode.EaseOut, Bounces = urgent ? 3 : 2, Bounciness = urgent ? 2.5 : 4 }, urgent ? 900 : 700),
            AnimationPreset.Fade => (new CubicEase { EasingMode = EasingMode.EaseOut }, 220),
            _ => (new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = urgent ? 0.9 : 0.4 }, urgent ? 620 : 520),
        };

        if (_style.Animation == AnimationPreset.Fade)
        {
            _y = targetY; // no movement: fade + scale in place
            Place();
        }

        _contentScale.ScaleX = _contentScale.ScaleY = _style.Animation == AnimationPreset.Fade ? 0.94 : 0.97;
        Animate(targetY, ms, ease, fadeTo: 1, scaleTo: 1, onDone: StartCountdown);
    }

    /// <summary>Moves to a new stack position (another banner arrived or left).</summary>
    public void MoveTo(double targetY)
    {
        if (_closing || Math.Abs(targetY - _targetY) < 0.5) return;
        _targetY = targetY;
        if (_dragStart is not null) return; // picked up when the drag ends
        // If this interrupts the entrance, finish its fade/scale and still start the countdown.
        Animate(targetY, 260, new CubicEase { EasingMode = EasingMode.EaseOut },
            fadeTo: _surface.Opacity < 1 ? 1 : null,
            scaleTo: _contentScale.ScaleX < 1 ? 1 : null,
            onDone: StartCountdown);
    }

    /// <summary>Slides out upward and closes.</summary>
    public void Dismiss(BannerCloseReason reason)
    {
        if (_closing) return;
        _closing = true;
        _tick.Stop();
        EndReply();
        var to = BannerLayout.AboveScreen(_monitor.Work.Top, PixelHeight, ShadowAllowance);
        if (_style.Animation == AnimationPreset.Fade) to = _y;
        Animate(to, 240, new CubicEase { EasingMode = EasingMode.EaseIn }, fadeTo: 0, scaleTo: 0.97, onDone: () =>
        {
            Finished?.Invoke(this, reason);
            Close();
        });
    }

    private void Act(string action)
    {
        ActionInvoked?.Invoke(this, action);
        Dismiss(BannerCloseReason.Action);
    }

    // ================================================================== animation

    private sealed record Motion(double FromY, double ToY, double FromOpacity, double? ToOpacity, double FromScale, double? ToScale,
        IEasingFunction Ease, Stopwatch Clock, double DurationMs, Action? OnDone);

    private void Animate(double toY, double ms, IEasingFunction ease, double? fadeTo, double? scaleTo, Action? onDone)
    {
        if (_motion is null) CompositionTarget.Rendering += OnFrame;
        _motion = new Motion(_y, toY, _surface.Opacity, fadeTo, _contentScale.ScaleX, scaleTo, ease, Stopwatch.StartNew(), ms, onDone);
        if (_options.SolidWhileAnimating && _acrylic) SetBackdrop(false);
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (_motion is not { } m) return;
        var t = Math.Clamp(m.Clock.Elapsed.TotalMilliseconds / m.DurationMs, 0, 1);
        var p = m.Ease.Ease(t);
        _y = m.FromY + (m.ToY - m.FromY) * p;
        Place();

        // Content fades/scales on a gentler curve (no overshoot) over the first part of the motion.
        var c = Math.Clamp(t / 0.6, 0, 1);
        var cp = 1 - Math.Pow(1 - c, 3);
        if (m.ToOpacity is { } o) _surface.Opacity = m.FromOpacity + (o - m.FromOpacity) * cp;
        if (m.ToScale is { } s) _contentScale.ScaleX = _contentScale.ScaleY = m.FromScale + (s - m.FromScale) * cp;

        if (t >= 1)
        {
            CompositionTarget.Rendering -= OnFrame;
            _motion = null;
            if (_options.SolidWhileAnimating && _acrylic) SetBackdrop(true);
            m.OnDone?.Invoke();
        }
    }

    private void Place()
    {
        if (_hwnd == 0) return;
        Native.SetWindowPos(_hwnd, Native.HwndTopmost, (int)Math.Round(_left), (int)Math.Round(_y), 0, 0,
            Native.SwpNoSize | Native.SwpNoActivate | Native.SwpNoOwnerZOrder);
    }

    // ================================================================== countdown + hover (D)

    private void StartCountdown()
    {
        if (_style.Duration is null || _closing || _tick.IsEnabled) return;
        _visibleFor.Start();
        _tick.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_style.Duration is not { } duration) return;
        var paused = IsMouseOver || _replyActive || _dragStart is not null;
        if (paused && _visibleFor.IsRunning) _visibleFor.Stop();
        else if (!paused && !_visibleFor.IsRunning) _visibleFor.Start();

        var remaining = BannerLayout.Remaining(duration, _visibleFor.Elapsed);
        _countdownScale.ScaleX = remaining;
        if (remaining <= 0) Dismiss(BannerCloseReason.Timeout);
    }

    // ================================================================== drag up / click to dismiss

    private void OnPointerDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractive(e.OriginalSource as DependencyObject)) return;
        _dragStart = PointToScreen(e.GetPosition(this));
        _dragBaseY = _y;
        _root.CaptureMouse();
        e.Handled = true;
    }

    private void OnPointerMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || _closing) return;
        var now = PointToScreen(e.GetPosition(this));
        var dy = Math.Min(0, now.Y - start.Y); // only upward
        _y = _dragBaseY + dy;
        _surface.Opacity = Math.Clamp(1 + dy / (PixelHeight * 1.2), 0.2, 1);
        Place();
    }

    private void OnPointerUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is not { } start) return;
        _root.ReleaseMouseCapture();
        var now = PointToScreen(e.GetPosition(this));
        _dragStart = null;
        var dy = now.Y - start.Y;
        if (dy < -24 * _monitor.Scale || (Math.Abs(dy) < 4 && Math.Abs(now.X - start.X) < 4))
        {
            Dismiss(BannerCloseReason.Dismissed); // dragged up, or a plain click
        }
        else
        {
            _surface.Opacity = 1;
            Animate(_targetY, 220, new CubicEase { EasingMode = EasingMode.EaseOut }, fadeTo: 1, scaleTo: null, onDone: null);
        }
    }

    private bool IsInteractive(DependencyObject? d)
    {
        for (; d is not null && !ReferenceEquals(d, _root); d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is ButtonBase or TextBox) return true;
        }
        return false;
    }

    // ================================================================== inline reply (B)

    private void BeginReply()
    {
        if (_replyActive || _hwnd == 0) return;
        _replyActive = true;
        // Allow activation only while typing, then take keyboard focus.
        Native.SetExtendedStyle(_hwnd, 0, Native.WsExNoActivate);
        Activate();
        _reply?.Focus();
    }

    private void EndReply()
    {
        if (!_replyActive || _hwnd == 0) return;
        _replyActive = false;
        Native.SetExtendedStyle(_hwnd, Native.WsExNoActivate, 0);
    }

    private void OnReplyKey(object sender, KeyEventArgs e)
    {
        if (_reply is null) return;
        if (e.Key == Key.Escape)
        {
            _reply.Text = "";
            Keyboard.ClearFocus();
            EndReply();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (ReplyParser.TryParse(_reply.Text, DateTime.Now, out var action, out var error))
            {
                Act(action);
            }
            else if (_replyHint is not null)
            {
                _replyHint.Text = error ?? ReplyParser.Help;
                _replyHint.Opacity = 1;
                _replyHint.Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38));
            }
        }
    }

    // ================================================================== theme + backdrop

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.SetExtendedStyle(_hwnd, Native.WsExNoActivate | Native.WsExToolWindow, 0);
        if (Native.SupportsRoundedCorners) Native.SetDwmInt(_hwnd, Native.DwmwaWindowCornerPreference, Native.DwmwcpRound);
        if (_acrylic && HwndSource.FromHwnd(_hwnd) is { CompositionTarget: { } target })
            target.BackgroundColor = Colors.Transparent;
        ApplyTheme();
        var requested = _acrylic;
        var accepted = _acrylic && SetBackdrop(true);
        if (_acrylic && !accepted)
        {
            _acrylic = false;
            ApplySolidBackground(true);
        }
        _log.LogInformation(
            "Banner backdrop: configured={Configured}, OS build {Build} supports acrylic={Supported}, requested={Requested}, DWM accepted={Accepted}, dark={Dark}",
            _options.Backdrop, Environment.OSVersion.Version.Build, Native.SupportsSystemBackdrop, requested, accepted, !Native.AppsUseLightTheme());

        // Windows draws acrylic only on *active* windows and falls back to a flat colour otherwise.
        // The banner must never take focus, so instead we tell DWM its frame is always active:
        // answer WM_NCACTIVATE as "active" (without activating) and send one now.
        HwndSource.FromHwnd(_hwnd)?.AddHook(KeepFrameActive);
        Native.SendMessage(_hwnd, Native.WmNcActivate, 1, 0);

        // Priority border (A): red for urgent, orange for high, Windows' default otherwise.
        var border = _request.Priority switch
        {
            NotificationPriority.Urgent => Native.ColorRef(0xE8, 0x3B, 0x3B),
            NotificationPriority.High => Native.ColorRef(0xF0, 0x9A, 0x1A),
            _ => Native.DwmColorDefault,
        };
        Native.SetDwmInt(_hwnd, Native.DwmwaBorderColor, border);
    }

    private nint KeepFrameActive(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != Native.WmNcActivate || wParam != 0) return 0;
        handled = true;
        return Native.DefWindowProc(hwnd, msg, 1, lParam);
    }

    /// <summary>Re-applies dark/light window chrome (call when the Windows theme changes).</summary>
    public void ApplyTheme()
    {
        if (_hwnd == 0) return;
        Native.SetDwmInt(_hwnd, Native.DwmwaUseImmersiveDarkMode, Native.AppsUseLightTheme() ? 0 : 1);
    }

    private bool SetBackdrop(bool acrylic)
    {
        if (_hwnd == 0) return false;
        ApplySolidBackground(!acrylic);
        return Native.SetDwmInt(_hwnd, Native.DwmwaSystemBackdropType, acrylic ? Native.DwmsbtTransientWindow : Native.DwmsbtNone);
    }

    private void ApplySolidBackground(bool solid)
    {
        if (solid)
        {
            _root.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
            if (_root.Background is null) _root.Background = SystemColors.WindowBrush;
        }
        else
        {
            _root.Background = Brushes.Transparent;
        }
    }

    // ================================================================== helpers

    private Brush AccentFor(NotificationPriority priority) => priority switch
    {
        NotificationPriority.Urgent => new SolidColorBrush(Color.FromRgb(0xE8, 0x3B, 0x3B)),
        NotificationPriority.High => new SolidColorBrush(Color.FromRgb(0xF0, 0x9A, 0x1A)),
        _ => (Brush?)TryFindResource("AccentFillColorDefaultBrush") ?? SystemColors.HighlightBrush,
    };

    private static TextBlock Secondary(string text, double size, double opacity, Thickness margin, double maxHeight = double.PositiveInfinity) => new()
    {
        Text = text,
        FontSize = size,
        Opacity = opacity,
        Margin = margin,
        TextWrapping = TextWrapping.Wrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxHeight = maxHeight,
    };

    protected override void OnClosed(EventArgs e)
    {
        _tick.Stop();
        if (_motion is not null) CompositionTarget.Rendering -= OnFrame;
        base.OnClosed(e);
    }
}
