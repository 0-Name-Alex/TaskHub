namespace TaskHub.Core.Platform;

/// <summary>Start TaskHub when the user signs in (HKCU Run key on Windows).</summary>
public interface IAutostartService
{
    bool IsEnabled { get; }

    void SetEnabled(bool enabled);
}
