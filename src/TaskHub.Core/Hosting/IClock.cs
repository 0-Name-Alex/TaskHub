namespace TaskHub.Core.Hosting;

/// <summary>Injected everywhere instead of DateTimeOffset.UtcNow so tests can control time.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>The user's time zone; due dates, "today" and recurrences are evaluated in it.</summary>
    TimeZoneInfo LocalZone { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public TimeZoneInfo LocalZone => TimeZoneInfo.Local;
}

public static class ClockExtensions
{
    public static DateTime LocalNow(this IClock clock) =>
        TimeZoneInfo.ConvertTimeFromUtc(clock.UtcNow.UtcDateTime, clock.LocalZone);

    public static DateOnly Today(this IClock clock) => DateOnly.FromDateTime(clock.LocalNow());

    /// <summary>
    /// Converts a local wall-clock date+time to UTC. Times that don't exist (skipped by a DST
    /// jump) move forward by the gap; ambiguous times (repeated hour) take the first occurrence.
    /// </summary>
    public static DateTimeOffset ToUtc(this IClock clock, DateOnly date, TimeOnly time)
    {
        var zone = clock.LocalZone;
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            var adjustment = zone.GetAdjustmentRules()
                .FirstOrDefault(r => r.DateStart <= local && r.DateEnd >= local)?.DaylightDelta ?? TimeSpan.FromHours(1);
            local = local.Add(adjustment);
        }
        if (zone.IsAmbiguousTime(local))
        {
            var offsets = zone.GetAmbiguousTimeOffsets(local);
            var first = offsets.Max(); // earlier instant = larger offset
            return new DateTimeOffset(local, first).ToUniversalTime();
        }
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }
}
