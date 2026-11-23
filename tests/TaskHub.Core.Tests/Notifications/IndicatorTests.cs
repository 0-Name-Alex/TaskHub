using Microsoft.Extensions.Logging.Abstractions;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Configuration;
using TaskHub.Core.Notifications;

namespace TaskHub.Core.Tests.Notifications;

public class IndicatorTests
{
    private sealed class RecordingIndicator : IStatusIndicator
    {
        public List<IndicatorRequest> Shown { get; } = [];
        public List<string> Cleared { get; } = [];
        public void Show(IndicatorRequest request) { lock (Shown) Shown.Add(request); }
        public void Clear(string key) { lock (Cleared) Cleared.Add(key); }
    }

    private static EventEnvelope Usage(string type, string device, string app, string id) =>
        EventEnvelope.Create(type, new DeviceUsageEvent(device, app, id), "platform", DateTimeOffset.UtcNow);

    [Fact]
    public void Camera_and_mic_use_shows_a_brief_indicator_and_clears_when_it_stops()
    {
        var indicator = new RecordingIndicator();
        var options = new NotificationOptions { IndicatorSeconds = 5, IndicatorDot = true };
        var service = new DeviceIndicatorService(TestBus.Create(), indicator, () => options, NullLogger<DeviceIndicatorService>.Instance);

        service.Handle(Usage(EventTypes.CameraStarted, "camera", "Zoom", @"C:\Zoom\Zoom.exe"));
        service.Handle(Usage(EventTypes.MicrophoneStarted, "microphone", "Zoom", @"C:\Zoom\Zoom.exe"));
        service.Handle(Usage(EventTypes.CameraStopped, "camera", "Zoom", @"C:\Zoom\Zoom.exe"));

        Assert.Equal(2, indicator.Shown.Count);
        Assert.Equal("Zoom is using your camera", indicator.Shown[0].Text);
        Assert.Equal(IndicatorKind.Camera, indicator.Shown[0].Kind);
        Assert.Equal(TimeSpan.FromSeconds(5), indicator.Shown[0].ShowFor);
        Assert.True(indicator.Shown[0].KeepDot);
        Assert.Equal("Zoom is using your microphone", indicator.Shown[1].Text);
        Assert.Equal([@"camera:C:\Zoom\Zoom.exe"], indicator.Cleared);
    }

    [Fact]
    public void Camera_privacy_key_shows_camera_off_and_on()
    {
        var indicator = new RecordingIndicator();
        var options = new NotificationOptions();
        var service = new DeviceIndicatorService(TestBus.Create(), indicator, () => options, NullLogger<DeviceIndicatorService>.Instance);

        service.Handle(EventEnvelope.Create(EventTypes.CameraDisabled, new CameraSwitchEvent(0), "platform", DateTimeOffset.UtcNow));
        service.Handle(EventEnvelope.Create(EventTypes.CameraEnabled, new CameraSwitchEvent(2), "platform", DateTimeOffset.UtcNow));
        options.CameraSwitchIndicator = false;
        service.Handle(EventEnvelope.Create(EventTypes.CameraDisabled, new CameraSwitchEvent(0), "platform", DateTimeOffset.UtcNow));

        Assert.Equal(2, indicator.Shown.Count);
        Assert.Equal(IndicatorKind.CameraOff, indicator.Shown[0].Kind);
        Assert.Equal("Camera off", indicator.Shown[0].Text);
        Assert.Equal(IndicatorKind.CameraOn, indicator.Shown[1].Kind);
        Assert.Equal("Camera on", indicator.Shown[1].Text);
        Assert.Empty(indicator.Cleared);
    }

    [Fact]
    public void Can_be_switched_off()
    {
        var indicator = new RecordingIndicator();
        var options = new NotificationOptions { CameraMicIndicator = false };
        var service = new DeviceIndicatorService(TestBus.Create(), indicator, () => options, NullLogger<DeviceIndicatorService>.Instance);

        service.Handle(Usage(EventTypes.CameraStarted, "camera", "Teams", "MSTeams_8wekyb3d8bbwe"));

        Assert.Empty(indicator.Shown);
    }

    [Theory]
    [InlineData("Microsoft.WindowsCamera_8wekyb3d8bbwe", "Windows Camera")]
    [InlineData("MSTeams_8wekyb3d8bbwe", "MSTeams")]
    [InlineData(@"C:\Program Files\Zoom\bin\Zoom.exe", "Zoom")]
    [InlineData("", "An app")]
    public void Friendly_app_names(string id, string expected) => Assert.Equal(expected, DeviceAppNames.Friendly(id));

    [Fact]
    public void Island_is_the_default_style_and_banner_is_still_available()
    {
        Assert.True(new NotificationOptions().UseIsland());
        Assert.False(new NotificationOptions { Style = "Banner" }.UseIsland());
        Assert.Contains("style", string.Join(" ", new NotificationOptions { Style = "bubble" }.Validate()), StringComparison.Ordinal);
    }
}
