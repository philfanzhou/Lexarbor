using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Services;

namespace Lexarbor.Learning.Service;

public sealed class LexarborScopeEntryReader : IVocabularyScopeEntryReader
{

    private const int PageSize = 100;

    private readonly ILexarborClient _client;

    public LexarborScopeEntryReader(ILexarborClient client)
    {
        _client = client;
    }

    public async Task<IReadOnlyList<VocabularyScopeEntry>> ReadOrderedScopeEntriesAsync(
        string bookId, IReadOnlyList<string>? unitIds, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bookId))
        {
            throw new VocabCatalogUnavailableException();
        }

        var book = bookId.Trim();
        List<LexarborEntry> entries;
        try
        {
            entries = await ReadAllEntriesAsync(book, cancellationToken).ConfigureAwait(false);
        }
        catch (LexarborCatalogException)
        {
            throw new VocabCatalogUnavailableException();
        }

        var ordered = OrderByScope(entries, unitIds);
        return ordered
            .Select(pair => new VocabularyScopeEntry(
                pair.Entry.WordId,
                pair.Entry.NormalizedWord,
                pair.Entry.MeaningId,
                pair.Entry.MeaningKey,
                pair.UnitId))
            .ToList();
    }

    private async Task<List<LexarborEntry>> ReadAllEntriesAsync(string bookId, CancellationToken cancellationToken)
    {
        var entries = new List<LexarborEntry>();
        for (var page = 1; ; page++)
        {
            var result = await _client.GetEntriesAsync(bookId, null, page, PageSize, cancellationToken).ConfigureAwait(false);
            entries.AddRange(result.Items);

            if (page >= result.TotalPage || result.Items.Count == 0)
            {
                break;
            }
        }

        return entries;
    }

    private static List<(LexarborEntry Entry, string? UnitId, int UnitRank, int SectionRank, int Index)> OrderByScope(
        List<LexarborEntry> entries, IReadOnlyList<string>? unitIds)
    {
        var unitOrder = unitIds is { Count: > 0 }
            ? unitIds.Select((unitId, index) => (unitId, index)).ToDictionary(pair => pair.unitId, pair => pair.index, StringComparer.Ordinal)
            : null;

        var ranked = new List<(LexarborEntry Entry, string? UnitId, int UnitRank, int SectionRank, int Index)>(entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var position = SelectPosition(entry, unitOrder);
            if (position is null)
            {

                continue;
            }

            var (unitId, unitRank, sectionRank) = position.Value;
            ranked.Add((entry, unitId, unitRank, sectionRank, index));
        }

        ranked.Sort((left, right) =>
        {
            var compare = left.UnitRank.CompareTo(right.UnitRank);
            if (compare != 0) return compare;
            compare = left.SectionRank.CompareTo(right.SectionRank);
            if (compare != 0) return compare;
            return left.Index.CompareTo(right.Index);
        });
        return ranked;
    }

    /// <summary>
    /// Select the earliest scope unit (plan order or ascending unit number), then its
    /// lowest section rank: unsectioned/other=0, A=1, B=2. Unassigned entries come last.
    /// </summary>
    private static (string? UnitId, int UnitRank, int SectionRank)? SelectPosition(
        LexarborEntry entry, Dictionary<string, int>? unitOrder)
    {
        var positions = entry.Positions;
        if (positions.Count == 0)
        {

            return unitOrder is null ? (null, int.MaxValue, SectionRank(null)) : null;
        }

        if (unitOrder is not null)
        {
            var inScope = positions
                .Where(position => unitOrder.TryGetValue(position.UnitId, out var order))
                .OrderBy(position => unitOrder[position.UnitId])
                .ThenBy(position => SectionRank(position.Section))
                .FirstOrDefault();
            return inScope is null
                ? null
                : (inScope.UnitId, unitOrder[inScope.UnitId], SectionRank(inScope.Section));
        }

        var byUnitNumber = positions
            .OrderBy(position => position.UnitNumber)
            .ThenBy(position => SectionRank(position.Section))
            .First();
        return (byUnitNumber.UnitId, byUnitNumber.UnitNumber, SectionRank(byUnitNumber.Section));
    }

    private static int SectionRank(string? section) => section switch
    {
        "A" => 1,
        "B" => 2,
        _ => 0,
    };
}

/// <summary>
/// Question generator: the Lexarbor implementation of IVocabularyQuestionGenerator. It sends only the
/// exact target and the learning mode; Lexarbor chooses the type and hides letters. Explicitly unavailable
/// targets return null (the caller retries once without unitId, then skips); every other failure throws
/// VocabCatalogUnavailableException.
/// </summary>
/// <remarks>
/// A pending notebook meaning (book ID VocabNotebook.SubmissionBookId, ADR-0027) is not learned before approval, so it
/// never reaches Lexarbor: the learning service resolves such targets to the approved meaning first, and the adapter
/// answers null for one that slips through.
/// </remarks>
public sealed class LexarborQuestionGenerator : IVocabularyQuestionGenerator
{
    private readonly ILexarborClient _client;

    public LexarborQuestionGenerator(ILexarborClient client)
    {
        _client = client;
    }

    public Task<VocabGeneratedQuestion?> CreateAsync(VocabQuestionSpec spec, CancellationToken cancellationToken = default)
    {
        var mode = LexarborLearningQuestionReader.ModeName(spec.Mode);
        if (spec.BookId == VocabNotebook.SubmissionBookId) return Task.FromResult<VocabGeneratedQuestion?>(null);

        return _client.GetLearningQuestionAsync(new LexarborLearningQuestionRequest(
            spec.WordId,
            spec.BookId,
            spec.MeaningId,
            mode,
            spec.UnitId,
            SameEntryKind: true,
            spec.Mode == VocabGenerationMode.Review ? spec.Relation : null), cancellationToken);
    }
}
