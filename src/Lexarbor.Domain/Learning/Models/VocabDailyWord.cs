namespace Lexarbor.Learning.Domain.Models;

/// <summary>A new meaning's saved catalog location, independent of later plan edits.</summary>
public sealed record VocabDailyWord(string BookId, string? UnitId, string WordId, string WordKey, string MeaningId, string MeaningKey)
{
    public bool? UsesFamilyQuota { get; init; }
}
