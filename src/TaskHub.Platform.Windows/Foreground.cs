using Windows.Win32;

namespace TaskHub.Platform.Windows;

public static class Foreground
{
    private const uint AsfwAny = unchecked((uint)-1);

    /// <summary>
    /// Called by a second launch just before it asks the running instance to show its window.
    /// The launch has foreground rights (the user just started it) and passes them on; without
    /// this, Windows would only flash the taskbar button instead of bringing the window forward.
    /// </summary>
    public static void AllowAnyProcess() => PInvoke.AllowSetForegroundWindow(AsfwAny);
}
