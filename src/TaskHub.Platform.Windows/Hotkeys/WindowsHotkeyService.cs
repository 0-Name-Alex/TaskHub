using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using TaskHub.Core.Hosting;
using TaskHub.Core.Hotkeys;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace TaskHub.Platform.Windows.Hotkeys;

/// <summary>
/// Global hotkeys via RegisterHotKey on a hidden message-only window. RegisterHotKey binds a
/// hotkey to the thread that owns the window, so all work is marshalled to the UI thread.
/// </summary>
public sealed class WindowsHotkeyService : IHotkeyService, IDisposable
{
    private const int ErrorHotkeyAlreadyRegistered = 1409;
    private static readonly nint HwndMessage = -3;

    private readonly IUiDispatcher _ui;
    private readonly ILogger<WindowsHotkeyService> _log;
    private readonly Dictionary<string, int> _idsByName = new();
    private readonly Dictionary<int, string> _namesById = new();
    private HwndSource? _source;
    private int _nextId = 0x0100;

    public WindowsHotkeyService(IUiDispatcher ui, ILogger<WindowsHotkeyService> log)
    {
        _ui = ui;
        _log = log;
    }

    public event EventHandler<string>? Pressed;

    public HotkeyRegistrationResult Register(string id, HotkeyGesture gesture) =>
        _ui.Invoke(() => RegisterOnUiThread(id, gesture));

    public void Unregister(string id) =>
        _ui.Invoke(() =>
        {
            UnregisterOnUiThread(id);
            return true;
        });

    private HotkeyRegistrationResult RegisterOnUiThread(string id, HotkeyGesture gesture)
    {
        UnregisterOnUiThread(id);
        if (!VirtualKeys.TryGet(gesture.Key, out var vk))
            return HotkeyRegistrationResult.Fail($"Key '{gesture.Key}' can't be used as a hotkey.");

        var hwnd = EnsureWindow();
        var nativeId = _nextId++;
        var modifiers = (HOT_KEY_MODIFIERS)(uint)gesture.Modifiers | HOT_KEY_MODIFIERS.MOD_NOREPEAT;

        if (!PInvoke.RegisterHotKey(hwnd, nativeId, modifiers, vk))
        {
            var error = Marshal.GetLastWin32Error();
            return HotkeyRegistrationResult.Fail(error == ErrorHotkeyAlreadyRegistered
                ? $"{gesture} is already used by another app or by Windows."
                : $"RegisterHotKey failed (Win32 error {error}).");
        }

        _idsByName[id] = nativeId;
        _namesById[nativeId] = id;
        return HotkeyRegistrationResult.Ok;
    }

    private void UnregisterOnUiThread(string id)
    {
        if (!_idsByName.Remove(id, out var nativeId)) return;
        _namesById.Remove(nativeId);
        if (_source is not null && !PInvoke.UnregisterHotKey(new HWND(_source.Handle), nativeId))
            _log.LogDebug("UnregisterHotKey({Id}) failed: {Error}", id, Marshal.GetLastWin32Error());
    }

    private HWND EnsureWindow()
    {
        if (_source is null)
        {
            var parameters = new HwndSourceParameters("TaskHub.Hotkeys")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
                ParentWindow = HwndMessage, // message-only window: invisible, no taskbar entry
            };
            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
        }
        return new HWND(_source.Handle);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == (int)PInvoke.WM_HOTKEY && _namesById.TryGetValue((int)wParam, out var id))
        {
            handled = true;
            try
            {
                Pressed?.Invoke(this, id);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Hotkey handler for {Id} failed", id);
            }
        }
        return 0;
    }

    public void Dispose()
    {
        if (_source is null) return;
        try
        {
            _ui.Invoke(() =>
            {
                foreach (var id in _idsByName.Keys.ToList())
                    UnregisterOnUiThread(id);
                _source.RemoveHook(WndProc);
                _source.Dispose();
                _source = null;
                return true;
            });
        }
        catch (InvalidOperationException)
        {
            // UI thread already gone; the OS frees hotkeys when the window/process goes away.
        }
    }
}

internal static class VirtualKeys
{
    public static bool TryGet(string key, out uint vk)
    {
        // WPF names digits D0..D9 and Enter "Return"; everything else in HotkeyGesture matches.
        var name = key switch
        {
            { Length: 1 } when char.IsAsciiDigit(key[0]) => "D" + key,
            "Enter" => "Return",
            "PrintScreen" => "Snapshot",
            _ => key,
        };
        if (Enum.TryParse<Key>(name, ignoreCase: true, out var wpfKey) && wpfKey != Key.None)
        {
            vk = (uint)KeyInterop.VirtualKeyFromKey(wpfKey);
            return vk != 0;
        }
        vk = 0;
        return false;
    }
}
