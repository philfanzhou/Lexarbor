using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Mapster;
using Microsoft.Data.Sqlite;
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

    public async Task<bool> HasOtherNormalizedWordAsync(string normalizedWord, string excludedId)
    {
        // SQLite lower/trim only handle ASCII case and ordinary spaces. Historical
        // rows must use exactly the same normalization as the replacement service.
        var connection = (SqliteConnection)context.Database.GetDbConnection();
        connection.CreateFunction<string, string>("lexarbor_edit_normalize_word",
            word => word.Trim().ToLowerInvariant(), isDeterministic: true);
        // EXISTS stops at the first other match without materializing vocabulary
        // rows. The existing transaction covers this scan and the subsequent write.
        return await context.Database.SqlQuery<int>($"""
            SELECT EXISTS (
                SELECT 1 FROM vocabulary
                WHERE id <> {excludedId}
                  AND lexarbor_edit_normalize_word(word) = {normalizedWord}
            ) AS Value
            """).SingleAsync() != 0;
    }
}
