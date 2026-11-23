using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Serilog;
using TaskHub.Core.Launcher;

namespace TaskHub.App;

/// <summary>A row in the launcher list.</summary>
public sealed record LauncherRow(LauncherItem Item)
{
    public string Title => Item.Title;
    public string? Subtitle => Item.Subtitle;
    public string Icon => Item.Icon;
    public string? Hint => Item.AltHint;
}

/// <summary>
/// The quick launcher (Ctrl+Alt+Space): a search box over apps, tasks, rules and commands. Appears
/// near the top of the monitor the mouse is on; hides when it loses focus or after running something.
/// </summary>
public partial class LauncherWindow : Window
{
    private readonly LauncherService _launcher;
    private readonly ObservableCollection<LauncherRow> _rows = [];
    private CancellationTokenSource? _searchCts;
    private int _searchVersion;

    public LauncherWindow(LauncherService launcher)
    {
        _launcher = launcher;
        InitializeComponent();
        Results.ItemsSource = _rows;
        Root.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        Loaded += (_, _) =>
        {
            if (Root.Background is null) Root.Background = SystemColors.WindowBrush;
        };
        Deactivated += (_, _) => Hide();
    }

    /// <summary>Shows (or re-shows) the launcher with an empty query.</summary>
    public void ShowLauncher()
    {
        QueryBox.Text = "";
        Placeholder.Visibility = Visibility.Visible;
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        PlaceOnMouseMonitor(helper.Handle);
        Show();
        Activate();
        // A hotkey gives us the right to take the foreground; make sure keyboard focus lands in the box.
        Topmost = true;
        QueryBox.Focus();
        Keyboard.Focus(QueryBox);
        _ = SearchAsync("");
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        Placeholder.Visibility = QueryBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _ = SearchAsync(QueryBox.Text);
    }

    private async Task SearchAsync(string query)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;
        var version = ++_searchVersion;
        try
        {
            if (query.Length > 0) await Task.Delay(60, ct);
            var items = await _launcher.SearchAsync(query, ct);
            if (version != _searchVersion) return;
            _rows.Clear();
            foreach (var item in items) _rows.Add(new LauncherRow(item));
            if (_rows.Count > 0) Results.SelectedIndex = 0;
            Results.Visibility = _rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            // superseded by newer typing
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Launcher search failed");
        }
    }

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Hide();
                e.Handled = true;
                break;
            case Key.Down:
                Move(+1);
                e.Handled = true;
                break;
            case Key.Up:
                Move(-1);
                e.Handled = true;
                break;
            case Key.PageDown:
                Move(+5);
                e.Handled = true;
                break;
            case Key.PageUp:
                Move(-5);
                e.Handled = true;
                break;
            case Key.Enter:
                _ = RunSelectedAsync(alt: Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                e.Handled = true;
                break;
        }
    }

    private void Move(int by)
    {
        if (_rows.Count == 0) return;
        var index = Math.Clamp(Results.SelectedIndex + by, 0, _rows.Count - 1);
        Results.SelectedIndex = index;
        Results.ScrollIntoView(Results.SelectedItem);
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e) => _ = RunSelectedAsync(alt: false);

    private async Task RunSelectedAsync(bool alt)
    {
        if (Results.SelectedItem is not LauncherRow row)
        {
            // Results may still be loading: wait for the current search once.
            await SearchAsync(QueryBox.Text);
            if (Results.SelectedItem is not LauncherRow late) return;
            row = late;
        }
        Hide();
        // Give the window a moment to disappear (matters for screenshots).
        await Task.Delay(120);
        await _launcher.ExecuteAsync(row.Item, alt);
    }

    // ------------------------------------------------------------------ placement

    private void PlaceOnMouseMonitor(nint hwnd)
    {
        try
        {
            if (!GetCursorPos(out var pt)) return;
            var monitor = MonitorFromPoint(pt, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) return;
            var scale = GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 ? dpiX / 96.0 : VisualTreeHelper.GetDpi(this).DpiScaleX;
            var work = info.Work;
            var widthPx = (int)(Width * scale);
            var x = work.Left + (work.Right - work.Left - widthPx) / 2;
            var y = work.Top + (work.Bottom - work.Top) / 6;
            SetWindowPos(hwnd, 0, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Debug(ex, "Launcher placement fell back to defaults");
        }
    }

    private const int MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint MonitorFromPoint(NativePoint point, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("shcore.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
}
