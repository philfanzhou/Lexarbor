using System.Text.Json;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed class EnrichmentExecutionRepository(VocabularyDbContext context, IEnrichmentTaskRepository tasks,
    TimeProvider clock) : IEnrichmentExecutionRepository
{
    public async Task<EnrichmentExecutionSnapshot?> ReadAsync(EnrichmentTargetKey target, CancellationToken token)
    {
        if (context.Database.CurrentTransaction != null) return await ReadCoreAsync(target, token);
        await context.Database.OpenConnectionAsync(token);
        try
        {
            await using var transaction = ((SqliteConnection)context.Database.GetDbConnection()).BeginTransaction(deferred: true);
            await using var enlisted = await context.Database.UseTransactionAsync(transaction, token);
            var result = await ReadCoreAsync(target, token);
            await transaction.CommitAsync(token);
            return result;
        }
        finally { await context.Database.CloseConnectionAsync(); }
    }

    private async Task<EnrichmentExecutionSnapshot?> ReadCoreAsync(EnrichmentTargetKey target, CancellationToken token)
    {
        var input = await tasks.ReadInputAsync(target, token);
        if (input == null) return null;
        var word = await context.Vocabularies.AsNoTracking().SingleAsync(row => row.Id == target.WordId, token);
        var meanings = await context.VocabularyMeanings.AsNoTracking().Where(row => row.VocabularyId == target.WordId
            && (target.MeaningId == null || row.Id == target.MeaningId)).OrderBy(row => row.Id).ToArrayAsync(token);
        var ids = meanings.Select(row => row.Id).ToArray();
        var positions = await context.VocabularyMeaningUnits.AsNoTracking().Where(row => ids.Contains(row.MeaningId))
            .Select(row => new { row.MeaningId, row.EntryKind }).Distinct().ToArrayAsync(token);
        var contexts = meanings.Select(row => new PronunciationMeaningContext(
            new(row.Id, row.PartOfSpeech, row.Meaning, row.PhoneticUk, row.PhoneticUs),
            positions.Where(position => position.MeaningId == row.Id).Select(position => position.EntryKind).ToArray())).ToArray();
        var own = await context.VocabularyAudioBindings.AsNoTracking().Where(row => row.WordId == target.WordId
            && row.MeaningId == target.MeaningId && row.Locale == target.Locale).Select(row => row.AssetId).SingleOrDefaultAsync(token);
        return new(new(new(word.Id, word.Word, word.PhoneticUk, word.PhoneticUs),
            target.MeaningId == null ? null : contexts.Single().Meaning, target.Locale, input, contexts), own);
    }

    public async Task<EnrichmentFieldCommit?> CommitFieldsAsync(EnrichmentExecutionGuard guard, EnrichmentPlan plan,
        long now, CancellationToken token)
    {
        try
        {
            return await new UnitOfWork(context).ExecuteAdmittedTransactionAsync<EnrichmentFieldCommit?>(async () =>
            {
                now = Math.Max(now, clock.GetUtcNow().ToUnixTimeMilliseconds());
                var target = await context.EnrichmentTargets.AsNoTracking().SingleOrDefaultAsync(row => row.Id == guard.TargetId);
                if (target == null || target.MeaningId != null || target.Step is not (EnrichmentStep.Prepare or EnrichmentStep.Fields)
                    || plan.Preparation is not { } metadata || metadata.InputVersion != guard.Input
                    || !await tasks.ValidateInTransactionAsync(guard, now, CancellationToken.None)) return null;
                var key = new EnrichmentTargetKey(target.WordId, null, target.Locale, target.Step);
                var snapshot = await ReadCoreAsync(key, CancellationToken.None);
                if (snapshot == null || snapshot.OwnAssetId != metadata.OwnAssetId) return null;
                var conflicts = new List<string>();
                var updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(now);
                if (metadata.EntryKind != "phrase")
                {
                    var words = context.Vocabularies.Where(row => row.Id == target.WordId);
                    var phonetic = plan.Phonetic;
                    if (!string.IsNullOrWhiteSpace(phonetic))
                    {
                        if (target.Locale == AudioLocale.EnGb && string.IsNullOrWhiteSpace(snapshot.Request.Word.PhoneticUk))
                            await words.ExecuteUpdateAsync(set => set.SetProperty(row => row.PhoneticUk, phonetic));
                        if (target.Locale == AudioLocale.EnUs && string.IsNullOrWhiteSpace(snapshot.Request.Word.PhoneticUs))
                            await words.ExecuteUpdateAsync(set => set.SetProperty(row => row.PhoneticUs, phonetic));
                    }
                    foreach (var candidate in plan.PartsOfSpeech ?? [])
                    {
                        var meaningContext = snapshot.Request.Meanings.SingleOrDefault(value => value.Meaning.MeaningId == candidate.MeaningId);
                        if (meaningContext == null || !string.IsNullOrWhiteSpace(meaningContext.Meaning.PartOfSpeech)
                            || meaningContext.PositionKinds.Contains("phrase", StringComparer.Ordinal)) continue;
                        var row = await context.VocabularyMeanings.AsNoTracking().SingleAsync(value => value.Id == candidate.MeaningId);
                        var part = candidate.PartOfSpeech.Trim().ToLowerInvariant();
                        if (await context.VocabularyMeanings.AsNoTracking().AnyAsync(value => value.Id != row.Id
                            && value.VocabularyId == row.VocabularyId && value.BookId == row.BookId
                            && value.NormalizedPartOfSpeech == part && value.NormalizedMeaning == row.NormalizedMeaning))
                        { conflicts.Add(row.Id); continue; }
                        await context.VocabularyMeanings.Where(value => value.Id == row.Id).ExecuteUpdateAsync(set =>
                            set.SetProperty(value => value.PartOfSpeech, part).SetProperty(value => value.UpdatedAt, updatedAt));
                    }
                }
                var advanced = await tasks.AdvanceInputInTransactionAsync(guard, now, CancellationToken.None)
                    ?? throw new LostExecutionException();
                var current = (await ReadCoreAsync(key, CancellationToken.None))!;
                var input = AudioSelectionService.GetInput(current.Request.Word, null, target.Locale);
                var stored = plan with
                {
                    Preparation = metadata with
                    {
                        Input = input,
                        InputVersion = advanced.Input,
                        RequiredPhonetic = string.IsNullOrWhiteSpace(input.Phonetic) ? null : input.Phonetic.Trim(),
                        ConflictingMeaningIds = conflicts
                    }
                };
                var json = JsonSerializer.Serialize(stored);
                if (json.Length > 16384) throw new DomainValidationException("Prepared pronunciation plan is too large.");
                await context.EnrichmentRounds.Where(row => row.TargetId == guard.TargetId && row.Number == guard.Round)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.PlanJson, json));
                return new(advanced, stored, conflicts);
            }, token);
        }
        catch (LostExecutionException) { return null; }
    }

    private sealed class LostExecutionException : Exception;
}
