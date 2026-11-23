using System.IO;
using System.Drawing.Imaging;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using TaskHub.Core.Automation;
using TaskHub.Core.Hosting;

namespace TaskHub.Automation;

/// <summary>
/// Clipboard access. The clipboard needs an STA thread, so everything runs on the UI thread; another
/// app briefly holding the clipboard open is retried a few times.
/// </summary>
public sealed class ClipboardService(IUiDispatcher ui, ILogger<ClipboardService> log) : IClipboardService
{
    public ClipboardContent Read() => ui.Invoke(() => WithRetry(ReadOnUiThread));

    public void SetText(string text) => ui.Invoke(() => WithRetry(() =>
    {
        Clipboard.SetText(text.Length == 0 ? " " : text);
        return true;
    }));

    private static ClipboardContent ReadOnUiThread()
    {
        if (Clipboard.ContainsFileDropList())
        {
            var files = Clipboard.GetFileDropList().Cast<string>().Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
            if (files.Count > 0) return new ClipboardContent(null, null, files);
        }
        if (Clipboard.ContainsText())
        {
            var text = Clipboard.GetText();
            if (!string.IsNullOrWhiteSpace(text)) return new ClipboardContent(text, null, []);
        }
        if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
        {
            using (image)
            using (var stream = new MemoryStream())
            {
                image.Save(stream, ImageFormat.Png);
                return new ClipboardContent(null, stream.ToArray(), []);
            }
        }
        return new ClipboardContent(null, null, []);
    }

    private T WithRetry<T>(Func<T> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (ExternalException ex) when (attempt < 5)
            {
                log.LogDebug(ex, "Clipboard busy, retrying");
                Thread.Sleep(50 * attempt);
            }
        }
    }
}
