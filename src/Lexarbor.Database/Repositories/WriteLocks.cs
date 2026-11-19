using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

internal static class WriteLocks
{
    // Structural mutations can cascade into words, positions, audio and enrichment.
    // ROW EXCLUSIVE participants run concurrently; SHARE ROW EXCLUSIVE waits for them.
    internal static Task CatalogAsync(VocabularyDbContext db, CancellationToken token) =>
        db.Database.ExecuteSqlRawAsync("LOCK TABLE vocabulary IN SHARE ROW EXCLUSIVE MODE", token);

    private static Task EnterCatalogAsync(VocabularyDbContext db, CancellationToken token) =>
        db.Database.ExecuteSqlRawAsync("LOCK TABLE vocabulary IN ROW EXCLUSIVE MODE", token);

    internal static async Task BookAsync(VocabularyDbContext db, string bookId, CancellationToken token)
    {
        await EnterCatalogAsync(db, token);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM vocabulary_book WHERE id = {bookId} FOR UPDATE", token);
    }

    internal static Task KeyAsync(VocabularyDbContext db, string key, CancellationToken token) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", token);

    internal static async Task SubmissionReviewsAsync(VocabularyDbContext db, CancellationToken token)
    {
        await EnterCatalogAsync(db, token);
        await KeyAsync(db, "meaning-submission:review-queue", token);
    }

    internal static async Task WordsAsync(VocabularyDbContext db, string? wordId,
        IReadOnlyCollection<string> normalizedSpellings, CancellationToken token)
    {
        await EnterCatalogAsync(db, token);
        if (normalizedSpellings.Count == 0)
        {
            if (!string.IsNullOrEmpty(wordId)) await KeyAsync(db, "word:" + wordId, token);
            return;
        }

        // Lock missing spellings before looking up their ids. A waiter sees its predecessor's
        // committed insert at READ COMMITTED. All batches take spelling keys, then word keys,
        // in the same order; no connection or lock is kept between requests.
        var spellings = normalizedSpellings.ToArray();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT pg_advisory_xact_lock(k) FROM (
                SELECT DISTINCT hashtextextended('spelling:' || spelling, 0) AS k
                FROM unnest({spellings}::text[]) AS spelling ORDER BY k
            ) AS keys
            """, token);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT pg_advisory_xact_lock(k) FROM (
                SELECT DISTINCT hashtextextended('word:' || id, 0) AS k FROM vocabulary
                WHERE id = {wordId} OR normalized_word = ANY({spellings}) ORDER BY k
            ) AS keys
            """, token);
    }

    internal static async Task WordSetAsync(VocabularyDbContext db, IReadOnlyCollection<string> wordIds, CancellationToken token)
    {
        await EnterCatalogAsync(db, token);
        var ids = wordIds.ToArray();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT pg_advisory_xact_lock(k) FROM (
                SELECT DISTINCT hashtextextended('word:' || id, 0) AS k
                FROM unnest({ids}::text[]) AS id ORDER BY k
            ) AS keys
            """, token);
    }

    internal static async Task MeaningAsync(VocabularyDbContext db, string meaningId, CancellationToken token)
    {
        await EnterCatalogAsync(db, token);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT pg_advisory_xact_lock(hashtextextended('word:' || vocabulary_id, 0))
            FROM vocabulary_meaning WHERE id = {meaningId}
            """, token);
    }

    internal static async Task AudioAsync(VocabularyDbContext db, string key, string? wordId, CancellationToken token)
    {
        await EnterCatalogAsync(db, token);
        await KeyAsync(db, "upload:" + key, token);
        if (wordId is not null) await KeyAsync(db, "word:" + wordId, token);
        else await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT pg_advisory_xact_lock(hashtextextended('word:' || word_id, 0))
            FROM audio_upload_operation WHERE idempotency_key = {key}
            """, token);
    }

    internal static async Task EnrichmentTargetAsync(VocabularyDbContext db, string id, CancellationToken token)
    {
        await EnterCatalogAsync(db, token);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT pg_advisory_xact_lock(hashtextextended('word:' || word_id, 0))
            FROM enrichment_target WHERE id = {id}
            """, token);
    }
}
