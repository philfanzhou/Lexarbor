using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Mapster;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed class VocabularyWordEditRepository(VocabularyDbContext context) : IVocabularyWordEditRepository
{
    public async Task<VocabularyModel?> GetCurrentAsync(string id)
    {
        // Re-read inside the write transaction, even when this context tracked an
        // earlier version of the row before waiting for the shared write lock.
        var entity = await context.Vocabularies.AsNoTracking().SingleOrDefaultAsync(v => v.Id == id);
        return entity?.Adapt<VocabularyModel>();
    }

    public Task<bool> HasOtherNormalizedWordAsync(string normalizedWord, string excludedId)
        => context.Vocabularies.AnyAsync(v => v.NormalizedWord == normalizedWord && v.Id != excludedId);
}
