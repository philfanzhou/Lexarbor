namespace Lexarbor.Learning.Domain.Models;

/// <summary>
/// Whether a notebook item points at a catalog meaning, at a meaning submission still under review,
/// or at a submission Lexarbor rejected. A rejected item stays in the notebook with the reason and is not learned.
/// </summary>
public enum VocabNotebookItemStatus : short
{
    Ready = 1,
    Pending = 2,
    Rejected = 3,
}

/// <summary>Who added a notebook item; only a teacher can remove an item a teacher added (ADR-0027).</summary>
public enum VocabNotebookActorType : short
{
    Student = 1,
    Teacher = 2,
}

public sealed record VocabNotebookActor(VocabNotebookActorType Type, Guid? TeacherId)
{
    public static VocabNotebookActor AsStudent() => new(VocabNotebookActorType.Student, null);

    public static VocabNotebookActor AsTeacher(Guid teacherId) => new(VocabNotebookActorType.Teacher, teacherId);
}
public sealed class VocabNotebookItem
{
    public Guid Id { get; set; }
    public Guid StudentId { get; set; }
    public string WordKey { get; set; } = string.Empty;
    public string MeaningKey { get; set; } = string.Empty;
    public string Word { get; set; } = string.Empty;
    public string? PartOfSpeech { get; set; }
    public string Meaning { get; set; } = string.Empty;
    public VocabNotebookItemStatus Status { get; set; }
    public string? BookId { get; set; }
    public string? WordId { get; set; }
    public string? MeaningId { get; set; }
    public string? SubmissionId { get; set; }

    /// <summary>
    /// Lexarbor's reason for a rejected submission, or its note on a revised item; null on other items or when no
    /// reason or note was given.
    /// </summary>
    public string? ReviewComment { get; set; }

    /// <summary>The text entered before review changed it (the revised mark); null on items that were not revised.</summary>
    public VocabNotebookOriginal? Original { get; set; }

    public List<VocabNotebookFamilySource> FamilySources { get; } = [];

    // Compatibility projection for callers that still consume a single source.
    public VocabNotebookFamilySource? FamilySource
    {
        get => FamilySources.FirstOrDefault();
        set { FamilySources.Clear(); if (value is not null) FamilySources.Add(value); }
    }

    public bool CanAttachFamily(VocabNotebookFamilySource source) => !FamilySources.Any(prior => prior.Covers(source));

    public void AttachFamily(VocabNotebookFamilySource source)
    {
        if (!CanAttachFamily(source)) return;
        FamilySources.RemoveAll(prior => prior.SameRelation(source) && (!prior.HasMeaningPair || prior.SameIdentity(source)));
        FamilySources.Add(source);
    }

    public bool FamilyOnly { get; set; }

    public VocabNotebookActorType AddedBy { get; set; }
    public Guid? AddedByTeacherId { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public CardKey Key => new(WordKey, MeaningKey);
}

/// <summary>What the student or teacher entered for a submission that review approved with changed text.</summary>
public sealed record VocabNotebookOriginal(string Word, string? PartOfSpeech, string Meaning);

public sealed record VocabNotebookFamilySource(string WordKey, string Word, string WordId, string RelationType, string Direction,
    string? RelationId = null, string? MeaningId = null, string? BookId = null,
    VocabularyCatalogReference? Target = null)
{
    public bool HasMeaningPair => RelationId is not null && MeaningId is not null && BookId is not null;

    public bool SameRelation(VocabNotebookFamilySource other) => WordId == other.WordId
        && RelationType == other.RelationType && Direction == other.Direction && Target == other.Target;

    public bool SameIdentity(VocabNotebookFamilySource other) => SameRelation(other)
        && MeaningId == other.MeaningId && BookId == other.BookId;

    public bool Covers(VocabNotebookFamilySource other) => SameRelation(other)
        && (!other.HasMeaningPair || HasMeaningPair && SameIdentity(other) && RelationId == other.RelationId);
}

/// <summary>An item as a reader sees it: Learned means the student already holds a memory card for it.</summary>
public sealed record VocabNotebookEntry(VocabNotebookItem Item, bool Learned);

public sealed record VocabNotebookSummary(int Words, int Meanings, int Teacher, int Recent, int Rejected);

public sealed record VocabNotebookQuery(string Keyword, bool Teacher, bool Recent, bool Rejected, int Page)
{
    public const int PageSize = 20;
}

public sealed record VocabNotebookPage<T>(int Total, int Page, IReadOnlyList<T> Items);

public static class VocabNotebook
{
    public const string SubmissionBookId = "lexarbor-submission";

    public const int MaxWordLength = 100;
    public const int MaxMeaningLength = 500;
    public const int MaxPartOfSpeechLength = 32;

    /// <summary>Lexarbor's documented maximum for a review comment or note; a longer value is cut to this length.</summary>
    public const int MaxReviewCommentLength = 500;
}
