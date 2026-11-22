namespace Lexarbor.Learning.Domain.Calendar;

/// <summary>
/// The server-owned calendar shared by daily learning features: instants are stored in UTC and
/// split into learning days in one deployment-wide time zone.
/// </summary>
public sealed class LearningCalendar(TimeZoneInfo timeZone)
{
    /// <summary>UTC+08:00 without daylight saving time, the calendar used before it was configurable.</summary>
    public static LearningCalendar Default { get; } = new(TimeZoneInfo.CreateCustomTimeZone(
        "UTC+08:00", TimeSpan.FromHours(8), "UTC+08:00", "UTC+08:00"));

    public TimeZoneInfo TimeZone { get; } = timeZone;

    /// <summary>The default calendar when no time zone is configured, otherwise the host's zone with that ID.</summary>
    /// <exception cref="TimeZoneNotFoundException">The host does not know the time zone.</exception>
    public static LearningCalendar Create(string? timeZoneId) => string.IsNullOrWhiteSpace(timeZoneId)
        ? Default
        : new(TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim()));

    public DateOnly From(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, TimeZone).DateTime);

    public DateOnly Today(TimeProvider clock) => From(clock.GetUtcNow());

    /// <summary>The zone's offset from UTC at the instant, which can change with daylight saving time.</summary>
    public int OffsetMinutes(DateTimeOffset instant) => (int)TimeZone.GetUtcOffset(instant).TotalMinutes;
}
