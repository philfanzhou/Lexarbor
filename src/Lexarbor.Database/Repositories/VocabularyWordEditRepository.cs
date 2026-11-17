using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Mapster;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed class VocabularyWordEditRepository(VocabularyDbContext context) : IVocabularyWordEditRepository
{
    public async Task InvalidateRelationsAsync(string wordId, long nowUnixMs) =>
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE vocabulary_relation
            SET status='needs_review', review_reason='word_changed', version=version+1,
                updated_at_unix_ms=greatest(updated_at_unix_ms, {nowUnixMs})
            WHERE source_word_id={wordId} OR target_word_id={wordId}
            """);

    public async Task<VocabularyModel?> GetCurrentAsync(string id)
    {
        // Re-read inside the write transaction, even when this context tracked an
        // earlier version of the row before waiting for the shared write lock.
        var entity = await context.Vocabularies.AsNoTracking().SingleOrDefaultAsync(v => v.Id == id);
        return entity?.Adapt<VocabularyModel>();
    }

    public async Task<bool> HasOtherNormalizedWordAsync(string normalizedWord, string excludedId)
    {
        // The replacement service normalizes with .NET's Trim() and
        // ToLowerInvariant(): every Unicode white space and every cased
        // letter, not the ASCII-only fold of the normalized_word column.
        // Historical rows must be compared under exactly that rule, and no SQL
        // function reproduces it, so the words are streamed and compared here.
        // ToLowerInvariant keeps the number of code points and Trim only
        // removes, so a stored word with fewer code points than the normalized
        // form can never match; that bound (PostgreSQL's length() counts code
        // points) is the only filter left to the database. The existing
        // transaction covers this scan and the subsequent write, and the scan
        // stops at the first other match.
        var minimumLength = normalizedWord.EnumerateRunes().Count();
        await foreach (var word in context.Vocabularies.AsNoTracking()
                           .Where(v => v.Id != excludedId && v.Word.Length >= minimumLength)
                           .Select(v => v.Word)
                           .AsAsyncEnumerable())
        {
            if (string.Equals(word.Trim().ToLowerInvariant(), normalizedWord, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
