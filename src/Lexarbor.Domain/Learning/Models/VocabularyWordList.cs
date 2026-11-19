namespace Lexarbor.Learning.Domain.Models;

/// <summary>
/// One page of a student's learned words for staff: mastered words (grouped by word, matching Vocabulary Size)
/// or due reviews (one meaning per item, matching the due-review count).
/// </summary>
public sealed record VocabularyWordList(Guid StudentId, DateTimeOffset RequestNow, string View, string? Query,
    int Page, int Size, int Total, VocabularyWordList.CountsDto Counts, IReadOnlyList<VocabularyWordListItem> Items)
{
    public const string MasteredView = "mastered";
    public const string DueView = "due";

    /// <summary>Unfiltered totals of both views at the same snapshot.</summary>
    public sealed record CountsDto(int Mastered, int Due);
}
