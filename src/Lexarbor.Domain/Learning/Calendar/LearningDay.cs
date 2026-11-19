namespace Lexarbor.Learning.Domain.Calendar;

/// <summary>The server-owned calendar shared by daily learning features. UTC storage, Beijing dates.</summary>
public static class LearningDay
{
    public const int OffsetMinutes = 480;
    public static DateOnly From(DateTimeOffset instant) => DateOnly.FromDateTime(instant.ToOffset(TimeSpan.FromHours(8)).DateTime);
    public static DateOnly Today(TimeProvider? clock = null) => From((clock ?? TimeProvider.System).GetUtcNow());
    public static DateTimeOffset Start(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(8));
}
