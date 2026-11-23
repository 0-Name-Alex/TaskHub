using System.Diagnostics;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using TaskHub.Core.Configuration;
using TaskHub.Core.Hosting;
using TaskHub.Core.Notifications;

namespace TaskHub.Notifications;

/// <summary>
/// "Island" notifications: a black pill at the top-centre of the screen. A notification appears as a
/// small circle that grows into the card; when it's done the card shrinks back to the circle and
/// disappears (or the next one grows out of it). One card at a time — others wait their turn and
/// show as "+2" on the card. Camera/microphone indicators appear as a short pill ("Zoom is using
/// your camera") and go away; optionally a small dot stays while in use. Cards wait while a
/// full-screen app or presentation is running (urgent ones and indicators don't).
/// Everything runs on the UI thread; public methods may be called from any thread.
/// </summary>
public sealed class IslandSurface : IBannerSurface, IStatusIndicator, IDisposable
{
    private static readonly Color CameraColor = Color.FromRgb(0x34, 0xC7, 0x59);
    private static readonly Color MicColor = Color.FromRgb(0xFF, 0x9F, 0x0A);
    private static readonly Color OffColor = Color.FromRgb(0x9A, 0x9A, 0xA0);
    private const string IconFont = "Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Emoji";

    private enum Mode { Hidden, Circle, Card, Pill, Dot }

    private sealed class Item(NotificationRequest request, BannerStyle style)
    {
        public NotificationRequest Request { get; } = request;
        public BannerStyle Style { get; } = style;
    }

    private readonly IUiDispatcher _ui;
    private readonly Func<NotificationOptions> _options;
    private readonly ILogger<IslandSurface> _log;
    private readonly LinkedList<Item> _queue = new();
    private readonly Dictionary<string, IndicatorKind> _dots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lazy<SoundPlayer?> _chime;
    // DispatcherTimers belong to the thread that creates them: created on the UI thread in EnsureTimers().
    private DispatcherTimer _tick = null!;
    private DispatcherTimer _indicatorTimer = null!;
    private DispatcherTimer _busyTimer = null!;
    private DispatcherTimer _badgeTimer = null!;
    private bool _timersReady;
    private readonly Stopwatch _visibleFor = new();

    private IslandWindow? _window;
    private Mode _mode = Mode.Hidden;
    private bool _transitioning;
    private Item? _current;
    private (IndicatorRequest Request, DateTime Until)? _pendingIndicator;

    // parts of the current card
    private ScaleTransform? _countdownScale;
    private TextBlock? _moreBadge;
    private Ellipse? _deviceBadge;
    private TextBox? _reply;
    private TextBlock? _replyHint;

    public IslandSurface(IUiDispatcher ui, Func<NotificationOptions> options, ILogger<IslandSurface> log)
    {
        _ui = ui;
        _options = options;
        _log = log;
        _chime = new Lazy<SoundPlayer?>(LoadChime);
    }

    private void EnsureTimers()
    {
        if (_timersReady) return;
        _timersReady = true;
        _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _indicatorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _busyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _badgeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _tick.Tick += OnTick;
        _indicatorTimer.Tick += (_, _) =>
        {
            _indicatorTimer.Stop();
            _pendingIndicator = null;
            if (_mode == Mode.Pill && !_transitioning) Next();
        };
        _busyTimer.Tick += (_, _) =>
        {
            if (_queue.Count == 0) _busyTimer.Stop();
            else if (!_transitioning && _mode != Mode.Card) Next();
        };
        _badgeTimer.Tick += (_, _) =>
        {
            _badgeTimer.Stop();
            if (_deviceBadge is not null) _deviceBadge.Visibility = Visibility.Collapsed;
        };
    }

    public event EventHandler<BannerActionEventArgs>? ActionInvoked;
    public event EventHandler<BannerClosedEventArgs>? Closed;

    private IslandWindow Isle => _window ??= new IslandWindow();

    // ================================================================== IBannerSurface

    public void Show(NotificationRequest request, BannerStyle style) => _ui.Post(() =>
    {
        EnsureTimers();
        try
        {
            var item = new Item(request, style);
            if (request.Priority == NotificationPriority.Urgent) _queue.AddFirst(item);
            else _queue.AddLast(item);
            UpdateMoreBadge();
            if (!_transitioning && _mode != Mode.Card) Next();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not show island notification {Id}", request.Id);
            Closed?.Invoke(this, new BannerClosedEventArgs(request.Id, BannerCloseReason.Programmatic));
        }
    });

    public void Close(Guid id) => _ui.Post(() =>
    {
        EnsureTimers();
        if (_current?.Request.Id == id)
        {
            DismissCurrent(BannerCloseReason.Programmatic);
            return;
        }
        var node = _queue.First;
        while (node is not null && node.Value.Request.Id != id) node = node.Next;
        if (node is null) return;
        _queue.Remove(node);
        UpdateMoreBadge();
        Closed?.Invoke(this, new BannerClosedEventArgs(id, BannerCloseReason.Programmatic));
    });

    // ================================================================== IStatusIndicator

    public void Show(IndicatorRequest request) => _ui.Post(() =>
    {
        EnsureTimers();
        if (request.KeepDot) _dots[request.Key] = request.Kind;
        if (_mode == Mode.Card && !_transitioning)
        {
            // Don't interrupt the card: flash a coloured dot on it instead.
            if (_deviceBadge is not null)
            {
                _deviceBadge.Fill = new SolidColorBrush(ColorOf(request.Kind));
                _deviceBadge.ToolTip = request.Text;
                _deviceBadge.Visibility = Visibility.Visible;
                _badgeTimer.Interval = request.ShowFor;
                _badgeTimer.Stop();
                _badgeTimer.Start();
            }
            return;
        }
        _pendingIndicator = (request, DateTime.UtcNow + request.ShowFor);
        if (!_transitioning) Next();
    });

    public void Clear(string key) => _ui.Post(() =>
    {
        EnsureTimers();
        if (!_dots.Remove(key)) return;
        if (_mode == Mode.Dot && !_transitioning) Next();
    });

    // ================================================================== state machine

    /// <summary>Decides what the island shows now: a card, an indicator pill, a dot, or nothing.</summary>
    private void Next()
    {
        var busy = Native.UserIsBusy();
        var item = _queue.FirstOrDefault(i => !busy || i.Request.Priority == NotificationPriority.Urgent);
        if (item is not null)
        {
            _queue.Remove(item);
            ShowCard(item);
            return;
        }
        if (_queue.Count > 0 && !_busyTimer.IsEnabled) _busyTimer.Start(); // waiting for full-screen to end

        if (_pendingIndicator is { } pending && pending.Until > DateTime.UtcNow)
        {
            ShowPill(pending.Request, pending.Until - DateTime.UtcNow);
            return;
        }
        _pendingIndicator = null;

        if (_dots.Count > 0)
        {
            ShowDot(_dots.Values.Contains(IndicatorKind.Camera) ? IndicatorKind.Camera : IndicatorKind.Microphone);
            return;
        }

        if (_mode == Mode.Hidden) return;
        _mode = Mode.Hidden;
        Isle.SetPill(null);
        Isle.MorphTo(IslandShape.Hidden, 220, new CubicEase { EasingMode = EasingMode.EaseIn }, () =>
        {
            if (_mode != Mode.Hidden) return;
            Isle.SetCard(null, 0);
            Isle.Vanish();
        });
    }

    private void ShowCard(Item item)
    {
        _current = item;
        _mode = Mode.Card;
        var width = Math.Clamp(_options().Width, 280, 600);
        var card = BuildCard(item.Request, item.Style, width);
        var height = IslandWindow.MeasureHeight(card, width);
        Isle.SetCard(card, width);
        UpdateMoreBadge();

        var urgent = item.Request.Priority == NotificationPriority.Urgent;
        var grow = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = urgent ? 0.55 : 0.3 };
        var target = IslandShape.Card(width, height);

        if (_window is null || !_window.IsVisible || Isle.Shape.Opacity < 0.05)
        {
            // Appear as a small circle, then grow into the card.
            Isle.Appear();
            Isle.Snap(IslandShape.Hidden);
            _transitioning = true;
            Isle.MorphTo(IslandShape.Circle, 170, new CubicEase { EasingMode = EasingMode.EaseOut }, () =>
                Isle.MorphTo(target, urgent ? 560 : 480, grow, () =>
                {
                    _transitioning = false;
                    StartCountdown();
                }));
        }
        else
        {
            _transitioning = true;
            Isle.MorphTo(target, 460, grow, () =>
            {
                _transitioning = false;
                StartCountdown();
            });
        }

        if (item.Style.PlaySound) _chime.Value?.Play();
    }

    private void ShowPill(IndicatorRequest request, TimeSpan remaining)
    {
        _mode = Mode.Pill;
        var pill = BuildIndicatorPill(request);
        var width = IslandWindow.MeasureWidth(pill);
        Isle.SetPill(pill);
        _indicatorTimer.Stop();
        _indicatorTimer.Interval = remaining > TimeSpan.FromMilliseconds(500) ? remaining : TimeSpan.FromMilliseconds(500);
        _indicatorTimer.Start();

        if (_window is null || !_window.IsVisible || Isle.Shape.Opacity < 0.05)
        {
            Isle.Appear();
            Isle.Snap(IslandShape.Hidden);
        }
        Isle.MorphTo(IslandShape.Pill(width), 420, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 });
    }

    private void ShowDot(IndicatorKind kind)
    {
        _mode = Mode.Dot;
        Isle.SetPill(new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = new SolidColorBrush(ColorOf(kind)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, IslandShape.PillHeight - 22), // centre within the 22px dot
        });
        if (_window is null || !_window.IsVisible || Isle.Shape.Opacity < 0.05)
        {
            Isle.Appear();
            Isle.Snap(IslandShape.Hidden);
        }
        Isle.MorphTo(IslandShape.Dot, 320, new CubicEase { EasingMode = EasingMode.EaseOut });
    }

    /// <summary>The card shrinks back into the circle; then whatever is next.</summary>
    private void DismissCurrent(BannerCloseReason reason)
    {
        if (_current is not { } item) return;
        _current = null;
        _tick.Stop();
        _visibleFor.Reset();
        Isle.EndReply();
        _reply = null;
        _replyHint = null;
        Closed?.Invoke(this, new BannerClosedEventArgs(item.Request.Id, reason));

        _transitioning = true;
        Isle.MorphTo(IslandShape.Circle, 260, new CubicEase { EasingMode = EasingMode.EaseIn }, () =>
        {
            _transitioning = false;
            _mode = Mode.Circle; // transitional; Next() picks what comes after
            Isle.SetCard(null, 0);
            _countdownScale = null;
            _moreBadge = null;
            _deviceBadge = null;
            Next();
        });
    }

    private void Act(string action)
    {
        if (_current is not { } item) return;
        ActionInvoked?.Invoke(this, new BannerActionEventArgs(item.Request.Id, action));
        DismissCurrent(BannerCloseReason.Action);
    }

    // ================================================================== countdown

    private void StartCountdown()
    {
        if (_current?.Style.Duration is null || _tick.IsEnabled) return;
        _visibleFor.Restart();
        _tick.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_current?.Style.Duration is not { } duration || _window is null) return;
        var paused = _window.IsMouseOverIsland || _window.IsReplying;
        if (paused && _visibleFor.IsRunning) _visibleFor.Stop();
        else if (!paused && !_visibleFor.IsRunning) _visibleFor.Start();

        var remaining = BannerLayout.Remaining(duration, _visibleFor.Elapsed);
        if (_countdownScale is not null) _countdownScale.ScaleX = remaining;
        if (remaining <= 0) DismissCurrent(BannerCloseReason.Timeout);
    }

    // ================================================================== content

    private FrameworkElement BuildCard(NotificationRequest request, BannerStyle style, double width)
    {
        var accent = AccentFor(request.Priority);
        var white = Brushes.White;

        var icon = new TextBlock
        {
            Text = request.Icon ?? "",
            FontFamily = new FontFamily(IconFont),
            FontSize = 20,
            Foreground = accent,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 12, 0),
        };

        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = request.Title,
            FontSize = 14.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = white,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxHeight = 62,
        });
        if (!string.IsNullOrWhiteSpace(request.Body))
            text.Children.Add(Secondary(request.Body, 13, 0.8, new Thickness(0, 3, 0, 0), 96));
        if (!string.IsNullOrWhiteSpace(request.Detail))
            text.Children.Add(Secondary(request.Detail, 12, 0.55, new Thickness(0, 4, 0, 0), 40));

        _moreBadge = new TextBlock
        {
            FontSize = 11,
            Foreground = white,
            Opacity = 0.6,
            Margin = new Thickness(8, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
        };
        _deviceBadge = new Ellipse
        {
            Width = 8,
            Height = 8,
            Margin = new Thickness(8, 6, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
        };
        var close = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily(IconFont),
            FontSize = 10,
            Foreground = white,
            Opacity = 0.55,
            Padding = new Thickness(8, 4, 2, 6),
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = Cursors.Hand,
            ToolTip = "Dismiss",
        };
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            DismissCurrent(BannerCloseReason.Dismissed);
        };

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        right.Children.Add(_deviceBadge);
        right.Children.Add(_moreBadge);
        right.Children.Add(close);

        var header = new DockPanel();
        DockPanel.SetDock(right, Dock.Right);
        DockPanel.SetDock(icon, Dock.Left);
        header.Children.Add(right);
        header.Children.Add(icon);
        header.Children.Add(text);

        var body = new StackPanel { Margin = new Thickness(18, 14, 12, 14) };
        body.Children.Add(header);

        if (request.Buttons.Count > 0)
        {
            var buttons = new WrapPanel { Margin = new Thickness(32, 12, 0, 0) };
            for (var i = 0; i < request.Buttons.Count; i++)
            {
                var b = request.Buttons[i];
                buttons.Children.Add(PillButton(b.Label, i == 0 ? accent : new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), b.Action));
            }
            body.Children.Add(buttons);
        }

        if (style.AllowReply)
        {
            _reply = new TextBox
            {
                Margin = new Thickness(32, 10, 4, 0),
                FontSize = 12.5,
                Padding = new Thickness(8, 5, 8, 5),
                Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
                Foreground = white,
                CaretBrush = white,
                BorderThickness = new Thickness(0),
                ToolTip = "snooze 10m · 1h · done · tomorrow 9am — Enter to apply, Esc to cancel",
            };
            _replyHint = new TextBlock
            {
                Text = "Reply: snooze 10m · done · tomorrow 9am",
                FontSize = 11.5,
                Foreground = white,
                Opacity = 0.5,
                Margin = new Thickness(34, 4, 0, 0),
            };
            var box = _reply;
            box.PreviewMouseLeftButtonDown += (_, _) => Isle.BeginReply(box);
            box.KeyDown += OnReplyKey;
            box.LostKeyboardFocus += (_, _) => Isle.EndReply();
            body.Children.Add(box);
            body.Children.Add(_replyHint);
        }

        var countdown = new Rectangle
        {
            Height = 2.5,
            Fill = accent,
            Opacity = 0.75,
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(IslandShape.CardRadius, 0, IslandShape.CardRadius, 3),
            RenderTransformOrigin = new Point(0, 0.5),
            Visibility = style.Duration is null ? Visibility.Collapsed : Visibility.Visible,
        };
        _countdownScale = new ScaleTransform(1, 1);
        countdown.RenderTransform = _countdownScale;

        var card = new Grid { Width = width, Background = Brushes.Transparent, Cursor = Cursors.Arrow };
        card.Children.Add(body);
        card.Children.Add(countdown);
        // A click on the card itself (not a button or the reply box) dismisses it.
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (e.Handled || IsInteractive(e.OriginalSource as DependencyObject, card)) return;
            DismissCurrent(BannerCloseReason.Dismissed);
        };
        return card;
    }

    private FrameworkElement PillButton(string label, Brush background, string action)
    {
        var button = new Border
        {
            Background = background,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(14, 5, 14, 6),
            Margin = new Thickness(0, 0, 8, 4),
            Cursor = Cursors.Hand,
            Tag = "button",
            Child = new TextBlock { Text = label, Foreground = Brushes.White, FontSize = 12.5, FontWeight = FontWeights.SemiBold },
        };
        button.MouseEnter += (_, _) => button.Opacity = 0.85;
        button.MouseLeave += (_, _) => button.Opacity = 1;
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Act(action);
        };
        return button;
    }

    private FrameworkElement BuildIndicatorPill(IndicatorRequest request)
    {
        var color = ColorOf(request.Kind);
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 16, 0),
            Height = IslandShape.PillHeight,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = "Click to hide",
        };
        panel.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        panel.Children.Add(new TextBlock
        {
            Text = request.Kind == IndicatorKind.Microphone ? "\uE720" : "\uE714",
            FontFamily = new FontFamily(IconFont),
            FontSize = 13,
            Foreground = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });
        panel.Children.Add(new TextBlock
        {
            Text = request.Text,
            FontSize = 12.5,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 420,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        panel.MouseLeftButtonUp += (_, _) =>
        {
            _indicatorTimer.Stop();
            _pendingIndicator = null;
            if (!_transitioning) Next();
        };
        return panel;
    }

    private void OnReplyKey(object sender, KeyEventArgs e)
    {
        if (_reply is null) return;
        if (e.Key == Key.Escape)
        {
            _reply.Text = "";
            Keyboard.ClearFocus();
            Isle.EndReply();
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
                _replyHint.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B));
            }
        }
    }

    private void UpdateMoreBadge()
    {
        if (_moreBadge is null) return;
        _moreBadge.Text = $"+{_queue.Count}";
        _moreBadge.Visibility = _queue.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool IsInteractive(DependencyObject? d, DependencyObject root)
    {
        for (; d is not null && !ReferenceEquals(d, root); d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is TextBox or ButtonBase || (d is FrameworkElement { Tag: "button" })) return true;
        }
        return false;
    }

    private static Color ColorOf(IndicatorKind kind) => kind switch
    {
        IndicatorKind.Camera or IndicatorKind.CameraOn => CameraColor,
        IndicatorKind.CameraOff => OffColor,
        _ => MicColor,
    };

    private Brush AccentFor(NotificationPriority priority) => priority switch
    {
        NotificationPriority.Urgent => new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)),
        NotificationPriority.High => new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x0A)),
        _ => Application.Current?.TryFindResource("AccentFillColorDefaultBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(0x4C, 0xC2, 0xFF)),
    };

    private static TextBlock Secondary(string text, double size, double opacity, Thickness margin, double maxHeight) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = Brushes.White,
        Opacity = opacity,
        Margin = margin,
        TextWrapping = TextWrapping.Wrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxHeight = maxHeight,
    };

    private SoundPlayer? LoadChime()
    {
        try
        {
            var stream = typeof(IslandSurface).Assembly.GetManifestResourceStream("TaskHub.Notifications.chime.wav");
            if (stream is null) return null;
            var player = new SoundPlayer(stream);
            player.Load();
            return player;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Notification sound unavailable");
            return null;
        }
    }

    public void Dispose()
    {
        if (_timersReady)
        {
            _tick.Stop();
            _indicatorTimer.Stop();
            _busyTimer.Stop();
            _badgeTimer.Stop();
        }
        if (_chime.IsValueCreated) _chime.Value?.Dispose();
    }
}

/// <summary>
/// Sends each notification to the island or the classic banners, per <c>[notifications] style</c>
/// (switchable live). Closing goes to both, so nothing gets stuck if the style changes mid-way.
/// </summary>
public sealed class NotificationSurfaceRouter : IBannerSurface
{
    private readonly BannerSurface _banners;
    private readonly IslandSurface _island;
    private readonly Func<NotificationOptions> _options;

    public NotificationSurfaceRouter(BannerSurface banners, IslandSurface island, Func<NotificationOptions> options)
    {
        _banners = banners;
        _island = island;
        _options = options;
        _banners.ActionInvoked += (s, e) => ActionInvoked?.Invoke(this, e);
        _island.ActionInvoked += (s, e) => ActionInvoked?.Invoke(this, e);
        _banners.Closed += (s, e) => Closed?.Invoke(this, e);
        _island.Closed += (s, e) => Closed?.Invoke(this, e);
    }

    public event EventHandler<BannerActionEventArgs>? ActionInvoked;
    public event EventHandler<BannerClosedEventArgs>? Closed;

    public void Show(NotificationRequest request, BannerStyle style)
    {
        if (_options().UseIsland()) _island.Show(request, style);
        else _banners.Show(request, style);
    }

    public void Close(Guid id)
    {
        _island.Close(id);
        _banners.Close(id);
    }
}
