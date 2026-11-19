using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Services;
using static Lexarbor.Learning.Service.VocabularyCatalogErrorCodes;

namespace Lexarbor.Learning.Service;

public interface ILexarborClient
{
    Task<LexarborAudio> GetAudioAsync(string wordId, string bookId, string meaningId, string locale,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LexarborUnit>> GetUnitsAsync(string bookId, CancellationToken cancellationToken = default);
    Task<LexarborEntryPage> GetEntriesAsync(string bookId, string? unitId, int page, int size, CancellationToken cancellationToken = default);

    /// <summary>
    /// On-demand question (Lexarbor POST /api/vocabulary/learning-question). Returns a validated question,
    /// or null only for 404 QUESTION_TARGET_NOT_FOUND or 422 QUESTION_SOURCE_UNAVAILABLE/QUESTION_UNAVAILABLE.
    /// Unlike the list calls, every other outcome (missing configuration, other statuses or codes, invalid
    /// responses, timeouts and network failures) throws VocabCatalogUnavailableException. Cancellation propagates.
    /// </summary>
    Task<VocabGeneratedQuestion?> GetLearningQuestionAsync(LexarborLearningQuestionRequest request, CancellationToken cancellationToken = default);
}
public sealed class LexarborCatalogReader : IVocabularyPlanScopeReader
{

    private const int PageSize = 100;

    private readonly ILexarborClient _client;

    public LexarborCatalogReader(ILexarborClient client)
    {
        _client = client;
    }

    public async Task<VocabularyPlanScope> ReadScopeAsync(IReadOnlyList<VocabPlanSegment> segments, CancellationToken cancellationToken = default)
    {
        if (segments.Count == 0)
        {
            throw new LexarborCatalogException(BookNotFound, retryable: false, "The vocabulary plan scope has no book.");
        }

        if (segments.Count == 1 && Normalize(segments[0].UnitIds) is null)
        {

            var firstPage = await _client.GetEntriesAsync(RequireBookId(segments[0].BookId), null, page: 1, size: 1, cancellationToken).ConfigureAwait(false);
            return new VocabularyPlanScope(firstPage.WordCount);
        }

        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in segments)
        {
            await ReadSegmentWordsAsync(segment, words, cancellationToken).ConfigureAwait(false);
        }

        return new VocabularyPlanScope(words.Count);
    }

    private async Task ReadSegmentWordsAsync(VocabPlanSegment segment, HashSet<string> words, CancellationToken cancellationToken)
    {
        var bookId = RequireBookId(segment.BookId);
        var normalizedUnits = Normalize(segment.UnitIds);
        if (normalizedUnits is null)
        {

            await ReadPagesAsync(bookId, null, words, new HashSet<string>(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
            return;
        }


        var units = await _client.GetUnitsAsync(bookId, cancellationToken).ConfigureAwait(false);
        var knownUnitIds = units.Select(unit => unit.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var unitId in normalizedUnits)
        {
            if (!knownUnitIds.Contains(unitId))
            {
                throw new LexarborCatalogException(UnitNotFound, retryable: false,
                    $"The vocabulary unit was not found in book {bookId}.");
            }
        }

        var seenMeanings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var unitId in normalizedUnits.Distinct())
        {
            await ReadPagesAsync(bookId, unitId, words, seenMeanings, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ReadPagesAsync(string bookId, string? unitId, HashSet<string> words, HashSet<string> seenMeanings, CancellationToken cancellationToken)
    {
        for (var page = 1; ; page++)
        {
            var result = await _client.GetEntriesAsync(bookId, unitId, page, PageSize, cancellationToken).ConfigureAwait(false);
            foreach (var entry in result.Items)
            {

                if (seenMeanings.Add(entry.MeaningId))
                {
                    words.Add(entry.NormalizedWord);
                }
            }

            if (page >= result.TotalPage || result.Items.Count == 0)
            {
                break;
            }
        }
    }

    private static string RequireBookId(string? bookId) =>
        string.IsNullOrWhiteSpace(bookId)
            ? throw new LexarborCatalogException(BookNotFound, retryable: false, "The vocabulary book id is empty.")
            : bookId.Trim();

    private static List<string>? Normalize(IReadOnlyList<string>? unitIds)
    {
        if (unitIds is null || unitIds.Count == 0)
        {
            return null;
        }
        var trimmed = unitIds.Select(unitId => (unitId ?? string.Empty).Trim()).ToList();
        return trimmed.Any(string.IsNullOrEmpty) ? throw new ArgumentException("Unit ids must not be empty.") : trimmed;
    }
}
