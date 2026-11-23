using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Configuration;
using TaskHub.Core.Events;

namespace TaskHub.Core.Notifications;

public enum IndicatorKind
{
    Camera,
    Microphone,
    /// <summary>The camera was switched off (privacy key) — grey.</summary>
    CameraOff,
    /// <summary>The camera was switched back on — green.</summary>
    CameraOn,
}

/// <summary>A short status pill ("Zoom is using your camera"), not a notification: no history, no buttons.</summary>
/// <param name="Key">Identifies it for <see cref="IStatusIndicator.Clear"/>, e.g. "camera:C:\…\Zoom.exe".</param>
/// <param name="KeepDot">After <paramref name="ShowFor"/>, keep a small dot until cleared.</param>
public sealed record IndicatorRequest(string Key, IndicatorKind Kind, string Text, TimeSpan ShowFor, bool KeepDot);

/// <summary>Draws status indicators (the island in the app).</summary>
public interface IStatusIndicator
{
    void Show(IndicatorRequest request);

    /// <summary>The thing it was about has ended (e.g. the camera was turned off).</summary>
    void Clear(string key);
}

/// <summary>
/// Turns <c>camera.*</c> / <c>microphone.*</c> events into a brief indicator: which app started using
/// the camera or mic, shown for a few seconds and then gone (optionally a small dot while in use).
/// Not subject to Do Not Disturb — it's a privacy signal.
/// </summary>
public sealed class DeviceIndicatorService(IEventBus bus, IStatusIndicator indicator, Func<NotificationOptions> options,
    ILogger<DeviceIndicatorService> log) : IHostedService
{
    private IDisposable[] _subscriptions = [];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscriptions = [bus.Subscribe("camera.*", Handle), bus.Subscribe("microphone.*", Handle)];
        return Task.CompletedTask;
    }

    public void Handle(EventEnvelope e)
    {
        if (e.Type is EventTypes.CameraDisabled or EventTypes.CameraEnabled)
        {
            var o2 = options();
            if (!o2.CameraSwitchIndicator) return;
            var off = e.Type == EventTypes.CameraDisabled;
            log.LogDebug("Camera switched {State}", off ? "off" : "on");
            indicator.Show(new IndicatorRequest(
                "camera-switch",
                off ? IndicatorKind.CameraOff : IndicatorKind.CameraOn,
                off ? "Camera off" : "Camera on",
                TimeSpan.FromSeconds(Math.Min(o2.IndicatorSeconds, 3)),
                KeepDot: false));
            return;
        }
        if (e.DataAs<DeviceUsageEvent>() is not { } usage) return;
        var camera = usage.Device == "camera";
        var key = $"{usage.Device}:{usage.AppId}";
        if (e.Type is EventTypes.CameraStopped or EventTypes.MicrophoneStopped)
        {
            indicator.Clear(key);
            return;
        }
        var o = options();
        if (!o.CameraMicIndicator) return;
        log.LogDebug("{App} started using the {Device}", usage.App, usage.Device);
        indicator.Show(new IndicatorRequest(
            key,
            camera ? IndicatorKind.Camera : IndicatorKind.Microphone,
            $"{usage.App} is using your {(camera ? "camera" : "microphone")}",
            TimeSpan.FromSeconds(o.IndicatorSeconds),
            o.IndicatorDot));
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var s in _subscriptions) s.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>Readable app names from what Windows records about camera/mic use.</summary>
public static class DeviceAppNames
{
    /// <summary>
    /// "Microsoft.WindowsCamera_8wekyb3d8bbwe" → "Windows Camera";
    /// "C:\Program Files\Zoom\bin\Zoom.exe" → "Zoom" (callers may prefer the file's description).
    /// </summary>
    public static string Friendly(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return "An app";
        if (appId.Contains('\\', StringComparison.Ordinal) || appId.Contains('/', StringComparison.Ordinal))
        {
            var file = Path.GetFileNameWithoutExtension(appId.Replace('/', '\\').Split('\\')[^1]);
            return file.Length == 0 ? "An app" : file;
        }
        var name = appId.Split('_')[0];
        var last = name.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? name;
        return SplitWords(last);
    }

    private static string SplitWords(string s)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (i > 0 && char.IsUpper(s[i]) && char.IsLower(s[i - 1])) sb.Append(' ');
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
