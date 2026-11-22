namespace Lexarbor.Learning.Domain.Models;

/// <summary>A new meaning's saved catalog location, independent of later plan edits.</summary>
public sealed record VocabDailyWord(string BookId, string? UnitId, string WordId, string WordKey, string MeaningId, string MeaningKey)
{
    public bool? UsesFamilyQuota { get; init; }

    /// <summary>Null on rosters frozen before sources were recorded.</summary>
    public VocabularySourceKind? Source { get; init; }

    /// <summary>The catalog location alone, for comparing locations regardless of quota and source.</summary>
    public VocabDailyWord LocationOnly() => this with { UsesFamilyQuota = null, Source = null };
}
