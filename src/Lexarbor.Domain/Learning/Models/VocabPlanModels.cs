namespace Lexarbor.Learning.Domain.Models;
public sealed class VocabPlanModel
{
    public Guid Id { get; set; }

    public Guid StudentId { get; set; }

    /// <summary>Ordered new-word scope with at least one book for Active plans; each book occurs at most once. Retired rows may be empty.</summary>
    public List<VocabPlanSegment> Segments { get; set; } = [];

    public string BookId
    {
        get => Segments.Count > 0 ? Segments[0].BookId : string.Empty;
        set => SetFirstSegment(Segments.Count > 0 ? Segments[0] with { BookId = value } : new VocabPlanSegment(value, null));
    }

    public List<string>? UnitIds
    {
        get => Segments.Count > 0 ? Segments[0].UnitIds : null;
        set => SetFirstSegment(Segments.Count > 0 ? Segments[0] with { UnitIds = value } : new VocabPlanSegment(string.Empty, value));
    }

    /// <summary>Compatibility field name for the daily per-group word limit, 1-200.</summary>
    public int DailyNewWords { get; set; }

    public VocabPlanController Controller { get; set; }

    public Guid? ControllerTeacherId { get; set; }

    public VocabPlanStatus Status { get; set; }

    public DateOnly StartDate { get; set; }

    public int TotalWords { get; set; }

    public int Version { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    private void SetFirstSegment(VocabPlanSegment segment)
    {
        if (Segments.Count == 0) Segments.Add(segment);
        else Segments[0] = segment;
    }
}
public sealed class VocabPlanChangeModel
{
    public Guid Id { get; set; }

    public Guid PlanId { get; set; }

    public VocabPlanActorType ActorType { get; set; }

    public Guid? ActorTeacherId { get; set; }

    public string Action { get; set; } = string.Empty;

    public string? BeforeValues { get; set; }

    public string? AfterValues { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public static class VocabPlanChangeActions
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Adopt = "adopt";
    public const string Release = "release";
    public const string End = "end";

    public const string Complete = "complete";

    public const string Reactivate = "reactivate";
}

public sealed record VocabPlanActor(VocabPlanActorType Type, Guid? TeacherId)
{
    public static VocabPlanActor AsStudent() => new(VocabPlanActorType.Student, null);

    public static VocabPlanActor AsTeacher(Guid teacherId) => new(VocabPlanActorType.Teacher, teacherId);
}
