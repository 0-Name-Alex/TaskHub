using System.Media;
using System.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using TaskHub.Core.Configuration;
using TaskHub.Core.Hosting;
using TaskHub.Core.Notifications;

namespace TaskHub.Notifications;

/// <summary>
/// Draws banners for the <see cref="NotificationCenter"/>. Each banner opens on the monitor under the
/// mouse pointer at that moment; banners on the same monitor stack top-centre, newest on top, and
/// slide to their new places when one arrives or leaves. Follows the Windows light/dark theme live.
/// </summary>
public sealed class BannerSurface : IBannerSurface, IDisposable
{
    private const double MarginDip = 12;
    private const double GapDip = 10;

    private readonly IUiDispatcher _ui;
    private readonly Func<NotificationOptions> _options;
    private readonly ILogger<BannerSurface> _log;
    private readonly List<BannerWindow> _windows = []; // oldest first; UI thread only
    private readonly Lazy<SoundPlayer?> _chime;

    public BannerSurface(IUiDispatcher ui, Func<NotificationOptions> options, ILogger<BannerSurface> log)
    {
        _ui = ui;
        _options = options;
        _log = log;
        _chime = new Lazy<SoundPlayer?>(LoadChime);
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public event EventHandler<BannerActionEventArgs>? ActionInvoked;
    public event EventHandler<BannerClosedEventArgs>? Closed;

    public void Show(NotificationRequest request, BannerStyle style) => _ui.Post(() =>
    {
        try
        {
            var monitor = Native.MonitorUnderCursor();
            var window = new BannerWindow(request, style, _options(), monitor, _log);
            window.ActionInvoked += (_, action) => ActionInvoked?.Invoke(this, new BannerActionEventArgs(request.Id, action));
            window.Finished += (_, reason) =>
            {
                _windows.Remove(window);
                Layout(monitor.Handle, entering: null);
                Closed?.Invoke(this, new BannerClosedEventArgs(request.Id, reason));
            };
            window.HeightChanged += (_, _) => Layout(monitor.Handle, entering: null);

            _windows.Add(window);
            window.Prepare();
            // Let WPF measure the content (and adapt to the monitor's DPI) before computing the stack.
            window.Dispatcher.InvokeAsync(() => Layout(monitor.Handle, entering: window), System.Windows.Threading.DispatcherPriority.Loaded);

            if (style.PlaySound) _chime.Value?.Play();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not show banner {Id}", request.Id);
            Closed?.Invoke(this, new BannerClosedEventArgs(request.Id, BannerCloseReason.Programmatic));
        }
    });

    public void Close(Guid id) => _ui.Post(() =>
    {
        _windows.FirstOrDefault(w => w.Id == id)?.Dismiss(BannerCloseReason.Programmatic);
    });

    /// <summary>Positions every banner on one monitor: newest at the top, the rest below it.</summary>
    private void Layout(nint monitor, BannerWindow? entering)
    {
        var group = _windows.Where(w => w.MonitorHandle == monitor).Reverse().ToList(); // newest first
        if (group.Count == 0) return;
        var work = group[0].Monitor; // the monitor these banners were created on

        var tops = BannerLayout.StackTops(work.Work.Top, group.Select(w => w.PixelHeight).ToList(),
            MarginDip * work.Scale, GapDip * work.Scale);
        for (var i = 0; i < group.Count; i++)
        {
            if (ReferenceEquals(group[i], entering)) group[i].Enter(tops[i]);
            else group[i].MoveTo(tops[i]);
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
            _ui.Post(() => _windows.ForEach(w => w.ApplyTheme()));
    }

    private SoundPlayer? LoadChime()
    {
        try
        {
            var stream = typeof(BannerSurface).Assembly.GetManifestResourceStream("TaskHub.Notifications.chime.wav");
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
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        if (_chime.IsValueCreated) _chime.Value?.Dispose();
    }
}
