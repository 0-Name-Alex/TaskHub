using TaskHub.Core.Notifications;

namespace TaskHub.Core.Configuration;

/// <summary>[notifications] in config.toml.</summary>
public sealed class NotificationOptions
{
    public static readonly string[] Modes = ["stack", "queue"];
    public static readonly string[] Animations = ["spring", "slide", "drop", "fade"];
    public static readonly string[] DndBehaviors = ["queue", "silence"];
    public static readonly string[] Backdrops = ["acrylic", "solid"];
    public static readonly string[] Styles = ["island", "banner"];

    /// <summary>"island" (pill that grows into the notification) or "banner" (stacked cards).</summary>
    public string Style { get; set; } = "island";
    /// <summary>Briefly show which app started using the camera or microphone.</summary>
    public bool CameraMicIndicator { get; set; } = true;
    /// <summary>Show "Camera off" / "Camera on" when the laptop's camera key or switch is used.</summary>
    public bool CameraSwitchIndicator { get; set; } = true;
    /// <summary>How long the camera/mic indicator stays up, in seconds.</summary>
    public int IndicatorSeconds { get; set; } = 4;
    /// <summary>Keep a small dot visible while the camera/mic stays in use.</summary>
    public bool IndicatorDot { get; set; }

    /// <summary>"stack": several on screen at once; "queue": one at a time.</summary>
    public string Mode { get; set; } = "stack";
    public int MaxVisible { get; set; } = 3;
    /// <summary>Seconds before a reminder banner hides itself (0 = stay until dismissed).</summary>
    public int ReminderSeconds { get; set; } = 60;
    /// <summary>Seconds for other banners (API, scripts, app messages). 0 = stay.</summary>
    public int InfoSeconds { get; set; } = 8;
    public string Animation { get; set; } = "spring";
    public bool Sound { get; set; } = true;
    /// <summary>While Do Not Disturb is on: "queue" (show later) or "silence" (history only).</summary>
    public string DndBehavior { get; set; } = "queue";
    public bool UrgentBypassesDnd { get; set; } = true;
    /// <summary>Banner width in device-independent pixels.</summary>
    public int Width { get; set; } = 400;
    /// <summary>"acrylic" (frosted glass, Windows 11 22H2+) or "solid".</summary>
    public string Backdrop { get; set; } = "acrylic";
    /// <summary>Use a solid background while sliding (if the glass effect stutters on your GPU).</summary>
    public bool SolidWhileAnimating { get; set; }
    /// <summary>When Do Not Disturb ends, more queued notifications than this become one summary.</summary>
    public int GroupQueuedAbove { get; set; } = 3;

    public bool UseIsland() => !string.Equals(Style, "banner", StringComparison.OrdinalIgnoreCase);

    public bool IsQueueMode() => string.Equals(Mode, "queue", StringComparison.OrdinalIgnoreCase);
    public bool SilenceDuringDnd() => string.Equals(DndBehavior, "silence", StringComparison.OrdinalIgnoreCase);
    public bool UseAcrylic() => !string.Equals(Backdrop, "solid", StringComparison.OrdinalIgnoreCase);

    public AnimationPreset GetAnimation() => Animation.ToLowerInvariant() switch
    {
        "slide" => AnimationPreset.Slide,
        "drop" => AnimationPreset.Drop,
        "fade" => AnimationPreset.Fade,
        _ => AnimationPreset.Spring,
    };

    public List<string> Validate()
    {
        var errors = new List<string>();
        if (!Modes.Contains(Mode, StringComparer.OrdinalIgnoreCase)) errors.Add($"notifications.mode must be {string.Join(" or ", Modes)}.");
        if (MaxVisible is < 1 or > 10) errors.Add("notifications.max_visible must be between 1 and 10.");
        if (ReminderSeconds is < 0 or > 3600) errors.Add("notifications.reminder_seconds must be between 0 and 3600.");
        if (InfoSeconds is < 0 or > 3600) errors.Add("notifications.info_seconds must be between 0 and 3600.");
        if (!Animations.Contains(Animation, StringComparer.OrdinalIgnoreCase)) errors.Add($"notifications.animation must be one of {string.Join(", ", Animations)}.");
        if (!DndBehaviors.Contains(DndBehavior, StringComparer.OrdinalIgnoreCase)) errors.Add("notifications.dnd_behavior must be queue or silence.");
        if (Width is < 280 or > 800) errors.Add("notifications.width must be between 280 and 800.");
        if (!Backdrops.Contains(Backdrop, StringComparer.OrdinalIgnoreCase)) errors.Add("notifications.backdrop must be acrylic or solid.");
        if (GroupQueuedAbove is < 0 or > 100) errors.Add("notifications.group_queued_above must be between 0 and 100.");
        if (!Styles.Contains(Style, StringComparer.OrdinalIgnoreCase)) errors.Add("notifications.style must be island or banner.");
        if (IndicatorSeconds is < 1 or > 60) errors.Add("notifications.indicator_seconds must be between 1 and 60.");
        return errors;
    }
}
