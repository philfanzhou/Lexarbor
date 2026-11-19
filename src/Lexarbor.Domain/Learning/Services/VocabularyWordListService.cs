using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Repositories;

namespace Lexarbor.Learning.Domain.Services;

/// <summary>
/// Read-only word lists behind the staff vocabulary counts. Mastered words use the Vocabulary Size rule
/// (a word counts once when any of its meanings is mastered); due reviews list every card due now.
/// </summary>
public sealed class VocabularyWordListService(IVocabularyWordListRepository repository,
    VocabularyMemoryService memory, TimeProvider clock)
{
    public const int MaxQueryLength = 50;

    public static bool ValidView(string? view) => view is VocabularyWordList.MasteredView or VocabularyWordList.DueView;

    public static bool ValidQuery(string? query) => query is null || query.Trim().Length <= MaxQueryLength;

    public async Task<VocabularyWordList?> GetAsync(Guid studentId, string view, string? query, int page = 1, int size = 20,
        CancellationToken cancellationToken = default)
    {
        if (studentId == Guid.Empty) throw new ArgumentException("Student id is required.", nameof(studentId));
        if (!ValidView(view)) throw new ArgumentException("View is invalid.", nameof(view));
        if (!ValidQuery(query)) throw new ArgumentException("Query is too long.", nameof(query));
        if (!VocabularyErrorProneService.ValidPaging(page, size)) throw new ArgumentException("Paging is invalid.", nameof(page));
        var filter = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        cancellationToken.ThrowIfCancellationRequested();
        var now = clock.GetUtcNow().ToUniversalTime();
        await using var snapshot = await repository.OpenAsync(studentId, cancellationToken).ConfigureAwait(false);
        if (!snapshot.StudentExists) return null;
        var cards = await snapshot.ReadCardsAsync(cancellationToken).ConfigureAwait(false);
        var staged = new List<(VocabularyWordListCard Card, MemoryStage Stage)>(cards.Count);
        foreach (var card in cards)
        {
            cancellationToken.ThrowIfCancellationRequested();
            staged.Add((card, memory.GetStage(new MemoryCardModel
                { StudentId = studentId, WordKey = card.WordKey, MeaningKey = card.MeaningKey, Stability = card.Stability })));
        }
        var masteredWords = staged.GroupBy(entry => entry.Card.WordKey, StringComparer.Ordinal)
            .Where(group => group.Any(entry => entry.Stage == MemoryStage.Mastered)).ToArray();
        var dueCards = staged.Where(entry => entry.Card.DueAt <= now).ToArray();
        bool Matches(string wordKey) => filter is null || wordKey.Contains(filter, StringComparison.OrdinalIgnoreCase);

        IReadOnlyList<(VocabularyWordListCard Card, MemoryStage Stage)[]> ordered = view == VocabularyWordList.MasteredView
            ? masteredWords.Where(group => Matches(group.Key))
                .OrderByDescending(group => group.Where(entry => entry.Stage == MemoryStage.Mastered).Max(entry => entry.Card.LastReviewedAt))
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(entry => entry.Stage)
                    .ThenBy(entry => entry.Card.MeaningKey, StringComparer.Ordinal).ToArray())
                .ToArray()
            : dueCards.Where(entry => Matches(entry.Card.WordKey))
                .OrderBy(entry => entry.Card.DueAt).ThenBy(entry => entry.Card.WordKey, StringComparer.Ordinal)
                .ThenBy(entry => entry.Card.MeaningKey, StringComparer.Ordinal)
                .Select(entry => new[] { entry }).ToArray();
        var pageRows = ordered.Skip((page - 1) * size).Take(size).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var keys = pageRows.SelectMany(row => row).Select(entry => new CardKey(entry.Card.WordKey, entry.Card.MeaningKey)).ToArray();
        var displays = keys.Length == 0 ? new Dictionary<CardKey, VocabularyErrorProneDisplay>()
            : await snapshot.ReadSnapshotsAsync(keys, now, cancellationToken).ConfigureAwait(false);
        var items = new List<VocabularyWordListItem>(pageRows.Length);
        foreach (var row in pageRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? word = null;
            var meanings = new List<VocabularyWordListItem.MeaningEntry>(row.Length);
            foreach (var (card, stage) in row)
            {
                displays.TryGetValue(new CardKey(card.WordKey, card.MeaningKey), out var display);
                var (text, meaning, incomplete) = VocabularyCardDisplay.Read(card.WordKey, card.MeaningKey, display);
                if (!incomplete) word ??= text;
                var direction = (VocabQuestionDirection)(display?.Direction ?? 0);
                meanings.Add(new(card.MeaningKey, meaning, incomplete, stage.ToString().ToLowerInvariant(), card.DueAt,
                    card.Reps, card.Lapses, card.FirstLearnedAt, card.LastReviewedAt)
                {
                    LatestLinkedQuestionType = direction.IsLinked() ? direction.ToString() : null,
                });
            }
            items.Add(new(row[0].Card.WordKey, word ?? row[0].Card.WordKey, word is null, meanings));
        }
        return new(studentId, now, view, filter, page, size, ordered.Count,
            new(masteredWords.Length, dueCards.Length), items);
    }
}
