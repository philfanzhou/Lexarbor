using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Repositories;

using Lexarbor.Learning.Domain.Calendar;

namespace Lexarbor.Learning.Domain.Services;

public sealed class VocabularyOverviewService(
    IVocabularyOverviewRepository repository,
    IVocabularyScopeEntryReader scopeReader,
    VocabularyMemoryService memoryService,
    TimeProvider clock)
{
    public async Task<VocabularyOverview?> GetAsync(
        Guid studentId, int tzOffsetMinutes = 0, CancellationToken cancellationToken = default)
    {
        if (studentId == Guid.Empty) throw new ArgumentException("Student id is required.", nameof(studentId));
        if (tzOffsetMinutes is < -720 or > 840)
            throw new ArgumentOutOfRangeException(nameof(tzOffsetMinutes));
        tzOffsetMinutes = LearningDay.OffsetMinutes;
        cancellationToken.ThrowIfCancellationRequested();
        var now = clock.GetUtcNow().ToUniversalTime();
        await using var snapshot = await repository.OpenAsync(studentId, cancellationToken).ConfigureAwait(false);
        if (!snapshot.StudentExists) return null;
        var plan = snapshot.ActivePlan;
        IReadOnlyList<VocabularyPlanScopeEntry> entries = plan is null
            ? []
            : await VocabularyPlanScopeEntry.ReadAsync(scopeReader, plan.Segments, cancellationToken).ConfigureAwait(false);
        var cards = await snapshot.ReadCardsAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var keys = new HashSet<CardKey>();
        var stages = new Dictionary<string, MemoryStage>(StringComparer.Ordinal);
        var due = 0;
        foreach (var card in cards)
        {
            cancellationToken.ThrowIfCancellationRequested();
            keys.Add(new CardKey(card.WordKey, card.MeaningKey));
            if (card.DueAt <= now) due++;
            var stage = memoryService.GetStage(card);
            if (!stages.TryGetValue(card.WordKey, out var previous) || stage > previous)
                stages[card.WordKey] = stage;
        }
        var scopeWords = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (_, _, entry) in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var learned = keys.Contains(new CardKey(entry.NormalizedWord, entry.MeaningKey));
            scopeWords.TryGetValue(entry.NormalizedWord, out var previous);
            scopeWords[entry.NormalizedWord] = previous || learned;
        }
        var newWords = 0;
        foreach (var learned in scopeWords.Values) if (!learned) newWords++;
        var learning = 0;
        var familiar = 0;
        var mastered = 0;
        foreach (var stage in stages.Values)
        {
            switch (stage)
            {
                case MemoryStage.Learning: learning++; break;
                case MemoryStage.Familiar: familiar++; break;
                case MemoryStage.Mastered: mastered++; break;
            }
        }
        return new VocabularyOverview(studentId, now,
            DateOnly.FromDateTime(now.AddMinutes(tzOffsetMinutes).DateTime), tzOffsetMinutes,
            mastered, new VocabularyOverviewStages(newWords, learning, familiar, mastered), due, plan);
    }
}
