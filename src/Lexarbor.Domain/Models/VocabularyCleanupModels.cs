namespace Lexarbor.Domain.Models;

public sealed record VocabularyCleanupSelection(string Action, string? WordId = null, string? MeaningId = null,
    IReadOnlyList<string>? WordIds = null, string? ConfirmedBookName = null);
public sealed record VocabularyCleanupCounts(int AffectedWordCount, int MeaningCount, int OrphanWordCount);
public sealed record VocabularyCleanupPreview(string BookId, string BookName, string Action,
    int AffectedWordCount, int MeaningCount, int OrphanWordCount);
public sealed record VocabularyCleanupResult(string BookId, string Action, int AffectedWordCount,
    int DeletedMeaningCount, int DeletedWordCount, bool DeletedBook);
