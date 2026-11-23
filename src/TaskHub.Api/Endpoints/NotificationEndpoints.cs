using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TaskHub.Abstractions.Contracts;
using TaskHub.Core.Notifications;
using TaskHub.Core.Services;

namespace TaskHub.Api.Endpoints;

/// <summary>/v1/notifications — show banners from scripts and other apps, Do Not Disturb, history.</summary>
internal static partial class NotificationEndpoints
{
    [GeneratedRegex("^[a-z0-9][a-z0-9:_\\-]{0,39}$")]
    private static partial Regex ActionId();

    public static void Map(IEndpointRouteBuilder app)
    {
        var n = app.MapGroup("/v1/notifications");

        n.MapPost("", (HttpContext http, NotifyRequest req) =>
        {
            var center = Center(http);
            var request = ToRequest(req, TaskEndpoints.SourceOf(http));
            center.Show(request);
            return Results.Accepted($"/v1/notifications/history", new NotifyResult(request.Id));
        });

        n.MapGet("/dnd", (HttpContext http) => Results.Ok(new DndDto(Center(http).DoNotDisturb)));
        n.MapPut("/dnd", (HttpContext http, DndDto body) =>
        {
            var center = Center(http);
            center.SetDoNotDisturb(body.Enabled);
            return Results.Ok(new DndDto(center.DoNotDisturb));
        });

        n.MapGet("/history", (HttpContext http, int? limit) =>
            Results.Ok(Center(http).History.Snapshot().Take(Math.Clamp(limit ?? 50, 1, NotificationHistory.Capacity)).Select(e =>
                new NotificationHistoryDto(e.Id, e.At, e.Title, e.Body, e.Kind.ToString().ToLowerInvariant(),
                    e.Priority.ToString().ToLowerInvariant(), e.Status.ToString().ToLowerInvariant(), e.Outcome, e.Source))));
    }

    private static NotificationCenter Center(HttpContext http) =>
        http.RequestServices.GetService<NotificationCenter>()
        ?? throw new TaskValidationException("Notifications aren't available in this host.");

    internal static NotificationRequest ToRequest(NotifyRequest req, string source)
    {
        var title = req.Title?.Trim();
        if (string.IsNullOrEmpty(title)) throw new TaskValidationException("title is required.");
        if (title.Length > 200) throw new TaskValidationException("title is longer than 200 characters.");
        if (req.Body is { Length: > 1000 }) throw new TaskValidationException("body is longer than 1000 characters.");
        if (req.Seconds is < 0 or > 3600) throw new TaskValidationException("seconds must be between 0 and 3600.");
        var buttons = req.Buttons ?? [];
        if (buttons.Count > 4) throw new TaskValidationException("At most 4 buttons.");
        foreach (var b in buttons)
        {
            if (b.Action is null || !ActionId().IsMatch(b.Action))
                throw new TaskValidationException($"Button action '{b.Action}' must be 1–40 chars of a-z, 0-9, ':', '_' or '-'.");
            if (string.IsNullOrWhiteSpace(b.Label) || b.Label.Length > 30)
                throw new TaskValidationException("Button labels must be 1–30 characters.");
        }

        return new NotificationRequest
        {
            Title = title,
            Body = req.Body,
            Detail = req.Detail,
            Icon = req.Icon is { Length: > 0 and <= 8 } icon ? icon : null,
            Kind = NotificationKind.Info,
            Priority = req.Priority?.Trim().ToLowerInvariant() switch
            {
                null or "" or "normal" => NotificationPriority.Normal,
                "low" => NotificationPriority.Low,
                "high" => NotificationPriority.High,
                "urgent" => NotificationPriority.Urgent,
                var p => throw new TaskValidationException($"Unknown priority '{p}'. Use low, normal, high or urgent."),
            },
            Duration = req.Seconds is { } s ? TimeSpan.FromSeconds(s) : null,
            Buttons = buttons.Select(b => new NotificationButton(b.Action, b.Label.Trim())).ToList(),
            PlaySound = req.Sound,
            AllowReply = req.Reply,
            Source = source,
        };
    }
}
