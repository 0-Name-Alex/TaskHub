using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Configuration;
using TaskHub.Core.Events;
using TaskHub.Core.Hosting;
using TaskHub.Core.Launcher;

namespace TaskHub.App;

public partial class App : Application
{
    private readonly IHost _host;
    private readonly StartupOptions _startup;
    private readonly string _version;
    private readonly string? _startupConfigError;
    private readonly List<IDisposable> _subscriptions = [];
    private bool _quitting;

    internal App(IHost host, StartupOptions startup, string version, string? startupConfigError)
    {
        _host = host;
        _startup = startup;
        _version = version;
        _startupConfigError = startupConfigError;

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception (terminating: {Terminating})", e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    public static new App Current => (App)Application.Current;

    public IServiceProvider Services => _host.Services;

    public bool IsQuitting => _quitting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            await _host.StartAsync();

            var bus = Services.GetRequiredService<IEventBus>();
            var ui = Services.GetRequiredService<IUiDispatcher>();
            _subscriptions.Add(bus.Subscribe(EventTypes.HotkeyPressed, env => ui.Post(() => OnHotkey(env))));
            _subscriptions.Add(bus.Subscribe(EventTypes.AppActivated, env => ui.Post(() => OnActivated(env))));
            _subscriptions.Add(bus.Subscribe(EventTypes.NotificationAction, env => ui.Post(() => OnNotificationAction(env))));

            // Create the view models now so the diagnostics view includes startup events.
            Services.GetRequiredService<ViewModels.MainViewModel>();
            Services.GetRequiredService<Core.Notifications.NotificationCenter>(); // subscribe to banner events early
            await Services.GetRequiredService<ViewModels.TasksViewModel>().InitializeAsync();
            RegisterLauncherCommands();
            Services.GetRequiredService<TrayIconService>().Create();

            bus.Publish(EventTypes.AppStarted, new AppStartedEvent(_version, Environment.ProcessId));
            if (_startupConfigError is not null)
                bus.Publish(EventTypes.ConfigInvalid, new ConfigInvalidEvent(Services.GetRequiredService<AppPaths>().ConfigFile, _startupConfigError));

            var config = Services.GetRequiredService<ConfigStore>().Current;
            if (!_startup.Background && config.General.ShowMainWindowOnLaunch)
                ShowMainWindow();

            Log.Information("TaskHub is running");
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");
            MessageBox.Show($"TaskHub could not start:\n\n{ex.Message}\n\nSee the log for details.", "TaskHub",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
        }
    }

    private void OnHotkey(EventEnvelope env)
    {
        switch (env.DataAs<HotkeyPressedEvent>()?.HotkeyId)
        {
            case HotkeyIds.ShowMainWindow:
                ToggleMainWindow();
                break;
            case HotkeyIds.QuickAdd:
                ShowMainWindow();
                Services.GetRequiredService<MainWindow>().FocusQuickAdd();
                break;
            case HotkeyIds.ToggleDnd:
                Services.GetRequiredService<Core.Notifications.NotificationCenter>().ToggleDoNotDisturb();
                break;
            case HotkeyIds.Launcher:
                ShowLauncher();
                break;
            case HotkeyIds.CaptureClipboard:
                _ = CaptureAsync(screenshot: false);
                break;
            case HotkeyIds.CaptureScreenshot:
                _ = CaptureAsync(screenshot: true);
                break;
        }
    }

    public void ShowLauncher()
    {
        if (_quitting) return;
        Services.GetRequiredService<LauncherWindow>().ShowLauncher();
    }

    /// <summary>Clipboard → task, or drag-a-rectangle screenshot → task.</summary>
    public async Task CaptureAsync(bool screenshot)
    {
        try
        {
            var capture = Services.GetRequiredService<Core.Automation.CaptureService>();
            if (screenshot)
            {
                // Let the tray menu / launcher close before the screen is frozen.
                await Task.Delay(150);
                await capture.CaptureScreenshotAsync();
            }
            else
            {
                await capture.CaptureClipboardAsync();
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Capture failed");
            Services.GetRequiredService<Core.Notifications.INotificationPresenter>().Show(new Core.Notifications.NotificationRequest
            {
                Title = screenshot ? "Screenshot failed" : "Couldn't read the clipboard",
                Body = ex.Message,
                Priority = Core.Notifications.NotificationPriority.High,
                Source = EventSources.Capture,
            });
        }
    }

    /// <summary>Built-in commands offered by the quick launcher.</summary>
    private void RegisterLauncherCommands()
    {
        var launcher = Services.GetRequiredService<Core.Launcher.LauncherService>();
        var dnd = Services.GetRequiredService<Core.Notifications.NotificationCenter>();
        var ui = Services.GetRequiredService<IUiDispatcher>();
        launcher.OpenTask = async id =>
        {
            ShowMainWindow();
            Services.GetRequiredService<MainWindow>().ShowTasksTab();
            await Services.GetRequiredService<ViewModels.TasksViewModel>().ShowTasksAsync([id]);
        };

        Task OnUi(Action action)
        {
            ui.Post(action);
            return Task.CompletedTask;
        }

        launcher.AddCommand(new("open", "Open TaskHub", "show main window tasks", "\uE8A7", _ => OnUi(ShowMainWindow)));
        launcher.AddCommand(new("today", "Today's tasks", "today due agenda", "\uE8BF", _ => OnUi(() =>
        {
            ShowMainWindow();
            Services.GetRequiredService<MainWindow>().ShowTasksTab();
            Services.GetRequiredService<ViewModels.TasksViewModel>().ShowView(Core.Domain.TaskView.Today);
        })));
        launcher.AddCommand(new("screenshot", "Screenshot to task", "capture snip screen region image", "\uE722", _ => CaptureAsync(screenshot: true)));
        launcher.AddCommand(new("clipboard", "Clipboard to task", "capture paste clip", "\uE77F", _ => CaptureAsync(screenshot: false)));
        launcher.AddCommand(new("dnd", "Toggle Do Not Disturb", "dnd quiet focus mute notifications", "\uE708", _ => OnUi(dnd.ToggleDoNotDisturb)));
        launcher.AddCommand(new("automation", "Automation rules", "rules watch automation", "\uE945", _ => OnUi(() =>
        {
            ShowMainWindow();
            Services.GetRequiredService<MainWindow>().ShowAutomationTab();
        })));
        launcher.AddCommand(new("notifications", "Notification history", "notifications banners history", "\uEA8F", _ => OnUi(() =>
        {
            ShowMainWindow();
            Services.GetRequiredService<MainWindow>().ShowNotificationsTab();
        })));
        launcher.AddCommand(new("config", "Edit config file", "settings config toml preferences", "\uE713",
            _ => OnUi(() => TrayIconService.OpenPath(Services.GetRequiredService<ConfigStore>().FilePath))));
        launcher.AddCommand(new("quit", "Quit TaskHub", "exit close", "\uE7E8", ct => OnUi(() => _ = QuitAsync())));
    }

    /// <summary>"Open"/"Review" on a banner: bring the window up on that task (or the overdue list).</summary>
    private async void OnNotificationAction(EventEnvelope env)
    {
        if (env.DataAs<NotificationActionEvent>() is not { Action: Core.Notifications.NotificationActions.Open } a) return;
        try
        {
            ShowMainWindow();
            var window = Services.GetRequiredService<MainWindow>();
            if (a.TaskIds.Count == 0)
            {
                window.ShowNotificationsTab(); // e.g. "Review" on a Do Not Disturb summary
                return;
            }
            window.ShowTasksTab();
            await Services.GetRequiredService<ViewModels.TasksViewModel>().ShowTasksAsync(a.TaskIds);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not open tasks from a notification");
        }
    }

    private void OnActivated(EventEnvelope env)
    {
        var args = env.DataAs<AppActivatedEvent>()?.Args ?? [];
        if (!StartupOptions.Parse(args).Background)
            ShowMainWindow();
    }

    public void ShowMainWindow()
    {
        if (_quitting) return;
        var window = Services.GetRequiredService<MainWindow>();
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        // Nudge to the front even if another app currently owns the foreground.
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    public void ToggleMainWindow()
    {
        var window = Services.GetRequiredService<MainWindow>();
        if (window.IsVisible && window.IsActive) window.Hide();
        else ShowMainWindow();
    }

    /// <summary>Orderly exit: stop services while the dispatcher is still running, then shut WPF down.</summary>
    public async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;
        Log.Information("Quitting");

        try
        {
            Services.GetRequiredService<IEventBus>().Publish(EventTypes.AppStopping);
            foreach (var s in _subscriptions) s.Dispose();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _host.StopAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error while stopping services");
        }

        Services.GetRequiredService<TrayIconService>().Dispose();
        Shutdown(0);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows sign-out / shutdown: we can't await here, so do a bounded synchronous stop.
        Log.Information("Session ending ({Reason})", e.ReasonSessionEnding);
        _quitting = true;
        try { _host.StopAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); }
        catch (Exception ex) { Log.Warning(ex, "Error stopping services at session end"); }
        base.OnSessionEnding(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the tray app alive: log and carry on.
        Log.Error(e.Exception, "Unhandled UI exception");
        e.Handled = true;
    }
}
