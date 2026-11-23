namespace TaskHub.Core.Hotkeys;

public sealed record HotkeyRegistrationResult(bool Success, string? Error)
{
    public static readonly HotkeyRegistrationResult Ok = new(true, null);
    public static HotkeyRegistrationResult Fail(string error) => new(false, error);
}

/// <summary>System-wide hotkeys. Implemented with RegisterHotKey on Windows.</summary>
public interface IHotkeyService
{
    /// <summary>Registers (or re-registers) the hotkey with this id.</summary>
    HotkeyRegistrationResult Register(string id, HotkeyGesture gesture);

    void Unregister(string id);

    /// <summary>Raised with the hotkey id. May be raised on the UI thread.</summary>
    event EventHandler<string>? Pressed;
}
