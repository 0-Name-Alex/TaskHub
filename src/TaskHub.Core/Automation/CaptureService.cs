using System.Globalization;
using Microsoft.Extensions.Logging;
using TaskHub.Abstractions.Events;
using TaskHub.Core.Configuration;
using TaskHub.Core.Domain;
using TaskHub.Core.Events;
using TaskHub.Core.Hosting;
using TaskHub.Core.Notifications;
using TaskHub.Core.Services;

namespace TaskHub.Core.Automation;

/// <summary>
/// Clipboard → task and screenshot → task. Text becomes the title (first line) and notes (all of it);
/// an image or a screenshot is saved under attachments\ and attached; copied files are attached
/// where they are. A banner confirms it, with "Open" to edit the new task.
/// </summary>
public sealed class CaptureService(
    TaskService tasks,
    IClipboardService clipboard,
    IScreenCapture screen,
    INotificationPresenter presenter,
    IEventBus bus,
    IClock clock,
    AppPaths paths,
    Func<AutomationOptions> options,
    ILogger<CaptureService> log)
{
    public const int MaxTitleLength = 120;

    public async Task<TaskItem?> CaptureClipboardAsync(string source = EventSources.Capture, CancellationToken ct = default)
    {
        var content = clipboard.Read();
        if (content.IsEmpty)
        {
            presenter.Show(new NotificationRequest
            {
                Title = "The clipboard is empty",
                Body = "Copy some text, an image or files first.",
                Icon = "\uE77F",
                Priority = NotificationPriority.Low,
                Source = source,
            });
            return null;
        }

        var stamp = clock.LocalNow();
        string title;
        string? notes = null;
        var attachments = new List<string>();
        string kind;

        if (content.Files.Count > 0)
        {
            kind = "files";
            attachments.AddRange(content.Files.Take(TaskItem.MaxAttachments));
            var names = content.Files.Select(f => Path.GetFileName(f)).ToList();
            title = names.Count == 1 ? names[0]! : $"{names.Count} files: {string.Join(", ", names.Take(3))}{(names.Count > 3 ? ", …" : "")}";
            if (content.Files.Count > 1) notes = string.Join(Environment.NewLine, content.Files);
        }
        else if (!string.IsNullOrWhiteSpace(content.Text))
        {
            kind = "text";
            (title, notes) = FromText(content.Text);
        }
        else
        {
            kind = "image";
            var file = await SaveImageAsync(content.PngImage!, "clipboard", stamp, ct).ConfigureAwait(false);
            attachments.Add(file);
            title = $"Clipboard image {stamp.ToString("d MMM HH:mm", CultureInfo.CurrentCulture)}";
        }

        var task = await CreateAsync(title, notes, attachments, source, ct).ConfigureAwait(false);
        log.LogInformation("Captured clipboard ({Kind}) as task {Id}", kind, task.Id);
        bus.Publish(EventTypes.ClipboardCaptured, new CaptureEvent(task.ToDto(), kind), source);
        Confirm(task, kind == "image" ? "Clipboard image saved as a task" : "Clipboard saved as a task", source);
        return task;
    }

    public async Task<TaskItem?> CaptureScreenshotAsync(string source = EventSources.Capture, CancellationToken ct = default)
    {
        var png = await screen.SelectRegionAsync(ct).ConfigureAwait(false);
        if (png is null) return null; // cancelled
        var stamp = clock.LocalNow();
        var file = await SaveImageAsync(png, "screenshot", stamp, ct).ConfigureAwait(false);
        var title = $"Screenshot {stamp.ToString("d MMM HH:mm", CultureInfo.CurrentCulture)}";
        var task = await CreateAsync(title, null, [file], source, ct).ConfigureAwait(false);
        log.LogInformation("Captured a screenshot as task {Id} ({File})", task.Id, file);
        bus.Publish(EventTypes.ScreenshotCaptured, new CaptureEvent(task.ToDto(), "screenshot"), source);
        Confirm(task, "Screenshot saved as a task", source);
        return task;
    }

    /// <summary>First non-empty line → title (shortened if needed); the full text → notes when there's more.</summary>
    public static (string Title, string? Notes) FromText(string text)
    {
        var trimmed = text.Trim();
        var firstLine = trimmed.Split('\n')[0].Trim();
        var title = string.Join(' ', firstLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (Uri.TryCreate(title, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            var shortUrl = (uri.Host + uri.AbsolutePath).TrimEnd('/');
            title = "Link: " + (shortUrl.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? shortUrl[4..] : shortUrl);
        }
        if (title.Length > MaxTitleLength) title = title[..(MaxTitleLength - 1)].TrimEnd() + "…";
        var notes = trimmed == title ? null : trimmed;
        if (notes is { Length: > TaskItem.MaxNotesLength }) notes = notes[..TaskItem.MaxNotesLength];
        return (title.Length == 0 ? "Clipboard" : title, notes);
    }

    private async Task<TaskItem> CreateAsync(string title, string? notes, List<string> attachments, string source, CancellationToken ct)
    {
        Guid? listId = null;
        var listName = options().CaptureList.Trim();
        if (listName.Length > 0)
        {
            var lists = await tasks.GetListsAsync(ct).ConfigureAwait(false);
            listId = lists.FirstOrDefault(l => string.Equals(l.Name, listName, StringComparison.OrdinalIgnoreCase))?.Id
                     ?? (await tasks.CreateListAsync(listName, null, source, ct).ConfigureAwait(false)).Id;
        }
        return await tasks.CreateAsync(new NewTask
        {
            Title = title,
            Notes = notes,
            ListId = listId,
            Attachments = attachments,
            ReminderOffsets = [],
        }, source, ct).ConfigureAwait(false);
    }

    private async Task<string> SaveImageAsync(byte[] png, string prefix, DateTime stamp, CancellationToken ct)
    {
        var folder = Path.Combine(paths.AttachmentsDir, stamp.ToString("yyyy-MM", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        var baseName = $"{prefix}-{stamp.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
        var path = Path.Combine(folder, baseName + ".png");
        for (var i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{baseName}-{i}.png");
        await File.WriteAllBytesAsync(path, png, ct).ConfigureAwait(false);
        return path;
    }

    private void Confirm(TaskItem task, string headline, string source) =>
        presenter.Show(new NotificationRequest
        {
            Title = headline,
            Body = task.Title,
            Icon = "\uE73E",
            TaskIds = [task.Id],
            Buttons = [new NotificationButton(NotificationActions.Open, "Open"), new NotificationButton(NotificationActions.Dismiss, "OK")],
            Duration = TimeSpan.FromSeconds(6),
            PlaySound = false,
            Source = source,
        });
}
