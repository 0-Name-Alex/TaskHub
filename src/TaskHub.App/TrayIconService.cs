using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Serilog;
using TaskHub.Core.Configuration;
using TaskHub.Core.Hosting;
using TaskHub.Core.Notifications;
using TaskHub.Core.Platform;

namespace TaskHub.App;

/// <summary>The tray icon and its menu. The tray is the app's root surface.</summary>
internal sealed class TrayIconService(AppPaths paths, ConfigStore config, IAutostartService autostart, NotificationCenter notifications, IUiDispatcher ui) : IDisposable
{
    private TaskbarIcon? _icon;
    private MenuItem? _autostartItem;
    private MenuItem? _dndItem;

    public void Create()
    {
        if (_icon is not null) return;

        _autostartItem = new MenuItem { Header = "Start with Windows", IsCheckable = true };
        _autostartItem.Click += (_, _) => ToggleAutostart();

        _dndItem = new MenuItem { Header = "Do Not Disturb", IsCheckable = true, IsChecked = notifications.DoNotDisturb };
        _dndItem.Click += (_, _) => notifications.SetDoNotDisturb(_dndItem.IsChecked);
        notifications.DoNotDisturbChanged += (_, _) => ui.Post(UpdateDnd);

        var menu = new ContextMenu();
        menu.Items.Add(Item("Open TaskHub", () => App.Current.ShowMainWindow(), bold: true));
        menu.Items.Add(Item("Quick launcher", () => App.Current.ShowLauncher()));
        menu.Items.Add(Item("Screenshot to task", () => _ = App.Current.CaptureAsync(screenshot: true)));
        menu.Items.Add(Item("Clipboard to task", () => _ = App.Current.CaptureAsync(screenshot: false)));
        menu.Items.Add(new Separator());
        menu.Items.Add(_dndItem);
        menu.Items.Add(_autostartItem);
        menu.Items.Add(Item("Trim memory", () =>
        {
            var before = TaskHub.Platform.Windows.Memory.MemoryOptimizer.GetCurrentMemoryMb();
            TaskHub.Platform.Windows.Memory.MemoryOptimizer.TrimWorkingSet();
            var after = TaskHub.Platform.Windows.Memory.MemoryOptimizer.GetCurrentMemoryMb();
            Log.Information("Manual memory trim: {Before:F1} MB -> {After:F1} MB", before, after);
        }));
        menu.Items.Add(Item("Edit config file", () => OpenPath(config.FilePath)));
        menu.Items.Add(Item("Open logs folder", () => OpenPath(paths.LogsDir)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit TaskHub", () => _ = App.Current.QuitAsync()));
        menu.Opened += (_, _) =>
        {
            _autostartItem.IsChecked = autostart.IsEnabled;
            _dndItem.IsChecked = notifications.DoNotDisturb;
        };

        _icon = new TaskbarIcon
        {
            ToolTipText = "TaskHub",
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/TaskHub.ico")),
            ContextMenu = menu,
            NoLeftClickDelay = true,
            LeftClickCommand = new RelayCommand(() => App.Current.ToggleMainWindow()),
        };
        // Efficiency mode would let Windows throttle us, which delays reminders. Keep it off.
        _icon.ForceCreate(enablesEfficiencyMode: false);
    }

    private void UpdateDnd()
    {
        if (_dndItem is not null) _dndItem.IsChecked = notifications.DoNotDisturb;
        if (_icon is not null) _icon.ToolTipText = notifications.DoNotDisturb ? "TaskHub — Do Not Disturb" : "TaskHub";
    }

    private void ToggleAutostart()
    {
        try
        {
            autostart.SetEnabled(!autostart.IsEnabled);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not change autostart");
            MessageBox.Show($"Could not change the autostart setting:\n{ex.Message}", "TaskHub");
        }
        if (_autostartItem is not null) _autostartItem.IsChecked = autostart.IsEnabled;
    }

    private static MenuItem Item(string header, Action action, bool bold = false)
    {
        var item = new MenuItem { Header = header };
        if (bold) item.FontWeight = FontWeights.SemiBold;
        item.Click += (_, _) => action();
        return item;
    }

    internal static void OpenPath(string path, string? program = null)
    {
        try
        {
            Process.Start(program is null
                ? new ProcessStartInfo(path) { UseShellExecute = true }
                : new ProcessStartInfo(program, path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not open {Path}", path);
        }
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }
}
