using System.Security.Cryptography;
using System.Text.Json;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed class VocabularyExpansionRepository(VocabularyDbContext context) : IVocabularyExpansionRepository
{
    public async Task<VocabularyExpansionInput?> ReadAsync(EnrichmentTargetKey target, CancellationToken token)
    {
        if (target.Step != EnrichmentStep.Expand || target.Locale != null || target.MeaningId == null) return null;
        // One bounded sense per task. Generated targets cannot feed a recursive expansion loop.
        var input = await (from meaning in context.VocabularyMeanings.AsNoTracking()
                           join word in context.Vocabularies.AsNoTracking() on meaning.VocabularyId equals word.Id
                           where word.Id == target.WordId && meaning.Id == target.MeaningId
                               && !context.EnrichmentGeneratedWords.Any(g => g.WordId == word.Id)
                               && !context.VocabularyMeaningUnits.Any(p => p.MeaningId == meaning.Id && p.EntryKind == "phrase")
                           select new { WordId = word.Id, MeaningId = meaning.Id, word.Word, meaning.PartOfSpeech, meaning.Meaning, meaning.Example })
            .SingleOrDefaultAsync(token);
        if (input == null) return null;
        var revision = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { Policy = "lexarbor.expansion.v1", input.WordId, input.MeaningId, Word = input.Word.Trim().ToLowerInvariant(), input.PartOfSpeech, input.Meaning, input.Example })));
        return new(input.WordId, input.MeaningId, input.Word, input.PartOfSpeech, input.Meaning, input.Example, new(revision, revision, null));
    }

    public async Task MarkGeneratedAsync(string wordId, string taskId, CancellationToken token)
    {
        if (!UnitOfWork.OwnsWriteContext(context) || context.Database.CurrentTransaction == null)
            throw new InvalidOperationException("Generated word provenance requires the catalog transaction.");
        context.EnrichmentGeneratedWords.Add(new() { WordId = wordId, TaskId = taskId });
        await context.SaveChangesAsync(token);
    }

    public async Task<VocabularyCatalogReference?> FindExistingMeaningAsync(VocabularyExpansionCandidate candidate, CancellationToken token)
    {
        var spelling = CatalogKeys.Of(candidate.Word);
        var definition = CatalogKeys.Of(candidate.Meaning);
        var labels = VocabularyPartOfSpeech.FromLabel(candidate.PartOfSpeech)?.Aliases.ToArray() ?? [CatalogKeys.Of(candidate.PartOfSpeech)];
        var matches = await (from word in context.Vocabularies.AsNoTracking()
                             join meaning in context.VocabularyMeanings.AsNoTracking() on word.Id equals meaning.VocabularyId
                             join book in context.VocabularyBooks.AsNoTracking() on meaning.BookId equals book.Id
                             where word.NormalizedWord == spelling && book.Status && meaning.NormalizedMeaning.ToLower() == definition
                                 && EF.Parameter(labels).Contains(meaning.NormalizedPartOfSpeech)
                             select new VocabularyCatalogReference(word.Id, book.Id, meaning.Id)).Take(2).ToArrayAsync(token);
        return matches.Length == 1 ? matches[0] : null;
    }

    public async Task<VocabularyRelationMeaningPair?> ReadHistoricalMeaningPairAsync(VocabularyRelationModel relation, CancellationToken token)
    {
        if (relation is not { Status: "confirmed", OriginKind: "generated", Version: 1, OriginReference: { } taskId })
            return null;
        var receipt = await (from task in context.EnrichmentTargets.AsNoTracking()
                             join round in context.EnrichmentRounds.AsNoTracking() on task.Id equals round.TargetId
                             join word in context.Vocabularies.AsNoTracking() on relation.TargetWordId equals word.Id
                             where task.Id == taskId && task.WordId == relation.SourceWordId && !task.Deleted
                                 && task.Step == EnrichmentStep.Expand && task.MeaningId != null
                                 && round.State == EnrichmentState.Succeeded && round.PlanJson != null
                                 && round.CreatedAtUnixMs <= relation.CreatedAtUnixMs && relation.CreatedAtUnixMs <= round.UpdatedAtUnixMs
                             select new { task.MeaningId, round.SourceRevision, round.PlanJson, word.NormalizedWord })
            .Take(2).ToArrayAsync(token);
        if (receipt.Length != 1) return null;
        var proof = receipt[0];
        var source = await ReadAsync(new(relation.SourceWordId, proof.MeaningId, null, EnrichmentStep.Expand), token);
        if (source == null || source.Version.SourceRevision != proof.SourceRevision) return null;
        var plan = JsonSerializer.Deserialize<EnrichmentPlan>(proof.PlanJson!)?.Expansion;
        if (plan == null || !VocabularyExpansionValidation.Valid(plan)) return null;
        var candidate = plan.Candidates.SingleOrDefault(item => item.Type == relation.Type && CatalogKeys.Of(item.Word) == proof.NormalizedWord
            && item.UsageNote.Trim() == relation.UsageNote);
        if (candidate == null) return null;
        var target = await FindExistingMeaningAsync(candidate, token);
        return target?.WordId == relation.TargetWordId ? new(source.MeaningId, target.MeaningId) : null;
    }
}
