using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Events;
using TaskHub.Core.Notifications;

namespace TaskHub.Platform.Windows.SystemInfo;

/// <summary>
/// Notices when an app starts or stops using the camera or microphone and publishes
/// <c>camera.started</c> / <c>camera.stopped</c> / <c>microphone.started</c> / <c>microphone.stopped</c>.
/// Windows' privacy settings record every use under CapabilityAccessManager\ConsentStore: an entry
/// whose LastUsedTimeStop is 0 is in use right now. Covers Store apps and normal desktop apps (the
/// same data behind the mic/camera icon in the taskbar). Polled once a second; reading a few registry
/// keys is cheap.
/// <para>Also notices the laptop's camera switch or key, published as <c>camera.disabled</c> /
/// <c>camera.enabled</c>: either the camera reports its privacy state itself (Lenovo's side switch and
/// other electronic shutters, see <see cref="CameraPrivacy"/>), or the camera disconnects from Windows.
/// (A plastic slide-over cover can't be detected.)</para>
/// </summary>
public sealed class DeviceUsageMonitor(IEventBus bus, ILogger<DeviceUsageMonitor> log) : BackgroundService
{
    private const string StoreKey = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private static readonly (string Registry, string Device, string Started, string Stopped)[] Devices =
    [
        ("webcam", "camera", EventTypes.CameraStarted, EventTypes.CameraStopped),
        ("microphone", "microphone", EventTypes.MicrophoneStarted, EventTypes.MicrophoneStopped),
    ];

    private readonly Dictionary<string, HashSet<string>> _inUse = new();
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string>? _cameras;        // last reported set of camera devices
    private HashSet<string>? _camerasPending; // seen once; reported when the next poll agrees
    private bool? _privacyClosed;             // camera's own privacy switch/shutter state
    private int _tickCount;

    /// <summary>
    /// After start-up, waking from sleep or unlocking, Windows re-connects the camera, which looks
    /// exactly like the switch being used. For this long afterwards, camera changes only update what
    /// "normal" is — no "Camera off/on" — so opening the lid doesn't announce anything.
    /// </summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(20);
    private DateTime _quietUntil = DateTime.UtcNow + SettleTime;
    private volatile bool _rebaseline;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        foreach (var d in Devices) _inUse[d.Device] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var powerEvents = bus.Subscribe("system.*", e =>
        {
            if (e.Type is EventTypes.SystemResumed or EventTypes.SystemUnlocked or EventTypes.SystemSuspended or EventTypes.SystemLocked)
                _rebaseline = true;
        });
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var failures = 0;
        do
        {
            try
            {
                Poll();
                if (_rebaseline)
                {
                    _rebaseline = false;
                    _quietUntil = DateTime.UtcNow + SettleTime;
                }
                PollCameraSwitch();
                if (_tickCount++ % 2 == 0) PollPrivacySwitch(); // every 2 s
                failures = 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                if (++failures == 1) log.LogWarning(ex, "Can't read camera/microphone usage");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private void Poll()
    {
        foreach (var (registry, device, started, stopped) in Devices)
        {
            var now = ReadInUse(registry);
            var before = _inUse[device];
            foreach (var app in now.Where(a => !before.Contains(a)))
            {
                var name = NameOf(app);
                log.LogInformation("{App} started using the {Device}", name, device);
                bus.Publish(started, new DeviceUsageEvent(device, name, app), EventSources.Platform);
            }
            foreach (var app in before.Where(a => !now.Contains(a)))
            {
                log.LogInformation("{App} stopped using the {Device}", NameOf(app), device);
                bus.Publish(stopped, new DeviceUsageEvent(device, NameOf(app), app), EventSources.Platform);
            }
            _inUse[device] = now;
        }
    }

    /// <summary>Reports the camera being switched off/on (after two polls agree, so a switch that
    /// reconnects the camera in stages produces one event).</summary>
    private void PollCameraSwitch()
    {
        var now = CameraInterfaces();
        if (now is null) return;
        if (_cameras is null || DateTime.UtcNow < _quietUntil)
        {
            _cameras = now; // startup / just woke up: just remember what's there
            _camerasPending = null;
            return;
        }
        if (now.SetEquals(_cameras))
        {
            _camerasPending = null;
            return;
        }
        if (_camerasPending is null || !now.SetEquals(_camerasPending))
        {
            _camerasPending = now;
            return;
        }
        var fewer = now.Count < _cameras.Count;
        _cameras = now;
        _camerasPending = null;
        log.LogInformation("Camera switched {State} ({Count} camera device(s) available)", fewer ? "off" : "on", now.Count);
        bus.Publish(fewer ? EventTypes.CameraDisabled : EventTypes.CameraEnabled, new CameraSwitchEvent(now.Count), EventSources.Platform);
    }

    /// <summary>The camera's own privacy switch or shutter (e.g. Lenovo's side switch): reported instantly.</summary>
    private void PollPrivacySwitch()
    {
        bool? closed;
        try
        {
            closed = CameraPrivacy.AnyClosed();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or NotSupportedException)
        {
            closed = null;
        }
        if (closed is null) return;
        if (_privacyClosed is null || DateTime.UtcNow < _quietUntil)
        {
            if (_privacyClosed is not null && _privacyClosed == closed) return;
            _privacyClosed = closed; // startup / just woke up: just remember
            log.LogInformation("Camera privacy switch is {State}", closed.Value ? "off (closed)" : "on (open)");
            return;
        }
        if (closed == _privacyClosed) return;
        _privacyClosed = closed;
        log.LogInformation("Camera privacy switch turned {State}", closed.Value ? "off" : "on");
        bus.Publish(closed.Value ? EventTypes.CameraDisabled : EventTypes.CameraEnabled,
            new CameraSwitchEvent(closed.Value ? 0 : 1), EventSources.Platform);
    }

    // KSCATEGORY_VIDEO_CAMERA: every camera Windows can use (colour and IR). Only enabled, present
    // devices are listed, so a camera turned off by its privacy key drops out of the list.
    private static Guid _cameraCategory = new("E5323777-F976-4F5B-9B55-B94699C46E44");
    private const int CrSuccess = 0;
    private const int CrBufferSmall = 0x1A;

    private static HashSet<string>? CameraInterfaces()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_Interface_List_SizeW(out var length, ref _cameraCategory, null, 0) != CrSuccess) return null;
            var buffer = new char[Math.Max(length, 1)];
            var result = CM_Get_Device_Interface_ListW(ref _cameraCategory, null, buffer, (uint)buffer.Length, 0);
            if (result == CrBufferSmall) continue; // a device appeared in between
            if (result != CrSuccess) return null;
            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        return null;
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out uint length, ref Guid interfaceClass, string? deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CM_Get_Device_Interface_ListW(ref Guid interfaceClass, string? deviceId, [Out] char[] buffer, uint length, uint flags);

    /// <summary>App ids (package names, or exe paths for desktop apps) currently using the device.</summary>
    private static HashSet<string> ReadInUse(string device)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var root = Registry.CurrentUser.OpenSubKey($@"{StoreKey}\{device}");
        if (root is null) return result;
        foreach (var name in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(name);
            if (key is null) continue;
            if (string.Equals(name, "NonPackaged", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var exe in key.GetSubKeyNames())
                {
                    using var app = key.OpenSubKey(exe);
                    if (app is not null && IsInUse(app)) result.Add(exe.Replace('#', '\\'));
                }
            }
            else if (IsInUse(key))
            {
                result.Add(name);
            }
        }
        return result;
    }

    private static bool IsInUse(RegistryKey key) =>
        ToLong(key.GetValue("LastUsedTimeStart")) > 0 && ToLong(key.GetValue("LastUsedTimeStop")) == 0;

    private static long ToLong(object? value) => value switch
    {
        long l => l,
        int i => i,
        byte[] { Length: 8 } b => BitConverter.ToInt64(b, 0),
        _ => -1,
    };

    private string NameOf(string appId)
    {
        if (_names.TryGetValue(appId, out var cached)) return cached;
        var name = DeviceAppNames.Friendly(appId);
        try
        {
            if (appId.Contains('\\', StringComparison.Ordinal) && File.Exists(appId)
                && FileVersionInfo.GetVersionInfo(appId).FileDescription is { Length: > 0 } description)
                name = description.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // keep the file name
        }
        _names[appId] = name;
        return name;
    }
}
