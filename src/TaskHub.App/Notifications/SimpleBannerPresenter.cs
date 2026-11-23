using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Events;
using TaskHub.Core.Hosting;
using TaskHub.Core.Notifications;

namespace TaskHub.App.Notifications;

/// <summary>
/// Phase 2 placeholder banner: a plain top-centre window per notification, stacked, always on top,
/// opened without taking focus. Phase 3 replaces this with the animated acrylic banner system.
/// </summary>
internal sealed class SimpleBannerPresenter(IUiDispatcher ui, IEventBus bus) : INotificationPresenter
{
    private const double Width = 400;
    private const double Gap = 8;
    private readonly List<BannerWindow> _open = [];

    public void Show(NotificationRequest request) => ui.Post(() =>
    {
        var window = new BannerWindow(request, Width, action => OnAction(request, action));
        window.Closed += (_, _) =>
        {
            _open.Remove(window);
            Layout();
        };
        _open.Add(window);
        window.SizeChanged += (_, _) => Layout();
        window.Show();
        Layout();
        if (request.Priority >= NotificationPriority.High) SystemSounds.Exclamation.Play();
        else SystemSounds.Asterisk.Play();
        bus.Publish(EventTypes.NotificationShown, new NotificationShownEvent(request.Id, request.Title), EventSources.Ui);
    });

    public void DismissForTasks(IReadOnlyCollection<Guid> taskIds) => ui.Post(() =>
    {
        foreach (var w in _open.Where(w => w.Request.TaskIds.Count > 0 && w.Request.TaskIds.All(taskIds.Contains)).ToList())
            w.Close();
    });

    private void OnAction(NotificationRequest request, string action)
    {
        bus.Publish(EventTypes.NotificationAction, new NotificationActionEvent(request.Id, action, request.TaskIds), EventSources.Ui);
    }

    /// <summary>Stack banners downward from the top centre of the primary work area.</summary>
    private void Layout()
    {
        var area = SystemParameters.WorkArea;
        var top = area.Top + 12;
        foreach (var w in _open)
        {
            w.Left = area.Left + (area.Width - Width) / 2;
            w.Top = top;
            top += (w.ActualHeight > 0 ? w.ActualHeight : 110) + Gap;
        }
    }

    private sealed class BannerWindow : Window
    {
        public BannerWindow(NotificationRequest request, double width, Action<string> onAction)
        {
            Request = request;
            Width = width;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Title = request.Title;
            BorderThickness = new Thickness(1);
            SetResourceReference(BorderBrushProperty, "ControlStrokeColorDefaultBrush");
            if (TryFindResource("SolidBackgroundFillColorBaseBrush") is Brush bg) Background = bg;
            SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");

            var accent = request.Priority switch
            {
                NotificationPriority.Urgent => new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38)),
                NotificationPriority.High => new SolidColorBrush(Color.FromRgb(0xE3, 0xA0, 0x08)),
                _ => (Brush?)TryFindResource("AccentFillColorDefaultBrush") ?? Brushes.SteelBlue,
            };

            var stack = new StackPanel { Margin = new Thickness(14, 10, 10, 12) };

            var header = new DockPanel();
            var close = new Button { Content = "✕", Padding = new Thickness(8, 2, 8, 2), ToolTip = "Dismiss", Margin = new Thickness(8, 0, 0, 0) };
            close.Click += (_, _) =>
            {
                onAction(NotificationActions.Dismiss);
                Close();
            };
            DockPanel.SetDock(close, Dock.Right);
            header.Children.Add(close);
            header.Children.Add(new TextBlock
            {
                Text = request.Title,
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            });
            stack.Children.Add(header);

            if (!string.IsNullOrWhiteSpace(request.Body))
                stack.Children.Add(new TextBlock { Text = request.Body, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), Opacity = 0.9 });
            if (!string.IsNullOrWhiteSpace(request.Detail))
                stack.Children.Add(new TextBlock { Text = request.Detail, FontSize = 12, Opacity = 0.7, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });

            if (request.Buttons.Count > 0)
            {
                var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
                foreach (var b in request.Buttons)
                {
                    var button = new Button { Content = b.Label, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
                    if (b == request.Buttons[0]) button.SetResourceReference(StyleProperty, "AccentButtonStyle");
                    var action = b.Action;
                    button.Click += (_, _) =>
                    {
                        onAction(action);
                        Close();
                    };
                    buttons.Children.Add(button);
                }
                stack.Children.Add(buttons);
            }

            var strip = new Border { Width = 4, Background = accent };
            var root = new DockPanel();
            DockPanel.SetDock(strip, Dock.Left);
            root.Children.Add(strip);
            root.Children.Add(stack);
            Content = root;

            if (request.Duration is { } d)
            {
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = d };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    if (!IsMouseOver) Close();
                    else timer.Start();
                };
                Loaded += (_, _) => timer.Start();
                Closed += (_, _) => timer.Stop();
            }
        }

        public NotificationRequest Request { get; }
    }
}
