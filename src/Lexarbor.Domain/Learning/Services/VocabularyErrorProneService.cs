using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Repositories;
using Microsoft.Extensions.Options;

namespace Lexarbor.Learning.Domain.Services;

public sealed class VocabularyErrorProneService(IVocabularyErrorProneRepository repository,
    VocabularyMemoryService memory, IOptions<VocabularyErrorProneOptions> options, TimeProvider clock)
{
    public static bool ValidPaging(int page, int size) => page >= 1 && size is >= 1 and <= 100 && (long)(page - 1) * size <= int.MaxValue;

    public async Task<VocabularyErrorProne?> GetAsync(Guid studentId, int page = 1, int size = 20, CancellationToken cancellationToken = default)
    {
        if (studentId == Guid.Empty) throw new ArgumentException("Student id is required.", nameof(studentId));
        if (!ValidPaging(page, size)) throw new ArgumentException("Paging is invalid.", nameof(page));
        cancellationToken.ThrowIfCancellationRequested();
        var now = clock.GetUtcNow().ToUniversalTime();
        var config = options.Value;
        var start = now.AddDays(-config.WindowDays);
        await using var snapshot = await repository.OpenAsync(studentId, cancellationToken).ConfigureAwait(false);
        if (!snapshot.StudentExists) return null;
        var coverage = await snapshot.ReadCoverageAsync(now, start, cancellationToken).ConfigureAwait(false);
        var rows = await snapshot.ReadCardsAsync(now, start, cancellationToken).ConfigureAwait(false);
        var eligible = new List<VocabularyErrorProneRow>();
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (memory.GetStage(new MemoryCard { StudentId = studentId, WordKey = row.WordKey, MeaningKey = row.MeaningKey, Stability = row.Stability }) == MemoryStage.Mastered) continue;
            if (HighError(row, config) || row.RecordedForgetCount >= config.MinimumForgetCount) eligible.Add(row);
        }
        var ordered = eligible.OrderByDescending(row => Rate(row.ErrorCount, row.AttemptCount))
            .ThenByDescending(row => row.ErrorCount).ThenBy(row => row.WordKey, StringComparer.Ordinal)
            .ThenBy(row => row.MeaningKey, StringComparer.Ordinal);
        var pageRows = ordered.Skip((page - 1) * size).Take(size).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        var displays = pageRows.Length == 0 ? new Dictionary<CardKey, VocabularyErrorProneDisplay>() :
            await snapshot.ReadSnapshotsAsync(pageRows.Select(row => new CardKey(row.WordKey, row.MeaningKey)).ToArray(),
                now, cancellationToken).ConfigureAwait(false);
        var items = new List<VocabularyErrorProneItem>();
        foreach (var projection in pageRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = displays.TryGetValue(new CardKey(projection.WordKey, projection.MeaningKey), out var display)
                ? projection with
                {
                    SnapshotDirection = display.Direction,
                    Prompt = display.Prompt,
                    OptionsSnapshot = display.OptionsSnapshot,
                    CorrectOptionId = display.CorrectOptionId,
                    AnswerWord = display.AnswerWord
                } : projection;
            var (word, meaning, incomplete) = ReadDisplay(row);
            var insufficient = row.Coverage.IsInsufficient(start);
            var reasons = new List<string>();
            if (HighError(row, config)) reasons.Add("high-error-rate");
            if (row.RecordedForgetCount >= config.MinimumForgetCount) reasons.Add("recorded-forgetting");
            items.Add(new(row.WordKey, row.MeaningKey, word, meaning, incomplete, row.AttemptCount, row.ErrorCount,
                Rate(row.ErrorCount, row.AttemptCount), Direction(row.EnglishAttempts, row.EnglishErrors),
                Direction(row.ChineseAttempts, row.ChineseErrors), Direction(row.FillAttempts, row.FillErrors), row.LastAnsweredAt, row.RecordedForgetCount,
                insufficient ? null : row.RecordedForgetCount, row.Coverage.StateCoverageStart, row.Coverage.AnswerCoverageStart,
                row.Coverage.KnownStateLogCount, row.Coverage.UnknownStateLogCount, insufficient, reasons)
            {
                PartOfSpeechChoice = Direction(row.PartOfSpeechAttempts, row.PartOfSpeechErrors),
                FormChoice = Direction(row.FormChoiceAttempts, row.FormChoiceErrors),
                RelationChoice = Direction(row.RelationChoiceAttempts, row.RelationChoiceErrors),
                FormFill = Direction(row.FormFillAttempts, row.FormFillErrors),
            });
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(studentId, now, start, now, page, size, eligible.Count,
            new(config.WindowDays, config.MinimumAttempts, config.ErrorRateThreshold, config.MinimumForgetCount),
            coverage.StateCoverageStart, coverage.AnswerCoverageStart, coverage.KnownStateLogCount,
            coverage.UnknownStateLogCount, coverage.IsInsufficient(start), items);
    }

    private static bool HighError(VocabularyErrorProneRow row, VocabularyErrorProneOptions config) =>
        row.AttemptCount >= config.MinimumAttempts && (double)row.ErrorCount / row.AttemptCount >= config.ErrorRateThreshold;
    private static double? Rate(long errors, long attempts) => attempts == 0 ? null : (double)errors / attempts;
    private static VocabularyErrorProneItem.Direction Direction(long attempts, long errors) => new(attempts, errors,
        attempts == 0 ? null : (double)(attempts - errors) / attempts);

    private static (string Word, string Meaning, bool Incomplete) ReadDisplay(VocabularyErrorProneRow row) =>
        VocabularyCardDisplay.Read(row.WordKey, row.MeaningKey, row.SnapshotDirection is { } direction && row.Prompt is not null
            ? new VocabularyErrorProneDisplay(direction, row.Prompt, row.OptionsSnapshot, row.CorrectOptionId, row.AnswerWord) : null);
}
