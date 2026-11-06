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
}
