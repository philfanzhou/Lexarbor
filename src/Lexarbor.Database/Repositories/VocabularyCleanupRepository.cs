using System.Data.Common;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Lexarbor.Database.Repositories;

public sealed class VocabularyCleanupRepository(VocabularyDbContext context) : IVocabularyCleanupRepository
{
    public async Task<T> ReadSnapshotAsync<T>(Func<Task<T>> read, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var transaction = await ReadSnapshot.BeginAsync(context, cancellationToken);
            var result = await read();
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally { await context.Database.CloseConnectionAsync(); }
    }

    public async Task<VocabularyBook?> GetBookAsync(string bookId, CancellationToken cancellationToken)
        => (await context.VocabularyBooks.AsNoTracking().SingleOrDefaultAsync(b => b.Id == bookId, cancellationToken))?.Adapt<VocabularyBook>();
    public Task<bool> WordExistsAsync(string wordId, CancellationToken cancellationToken)
        => context.Vocabularies.AnyAsync(v => v.Id == wordId, cancellationToken);
    public async Task<VocabularyMeaning?> GetMeaningAsync(string meaningId, CancellationToken cancellationToken)
        => (await context.VocabularyMeanings.AsNoTracking().SingleOrDefaultAsync(m => m.Id == meaningId, cancellationToken))?.Adapt<VocabularyMeaning>();
    public Task<int> CountSelectedWordsAsync(string bookId, IReadOnlyList<string> wordIds, CancellationToken cancellationToken)
        => context.VocabularyMeanings.Where(m => m.BookId == bookId && wordIds.Contains(m.VocabularyId))
            .Select(m => m.VocabularyId).Distinct().CountAsync(cancellationToken);

    // This predicate is the sole definition of R for preview and commit. The
    // identifiers below are code constants; every external value is a parameter.
    private static string Predicate(VocabularyCleanupSelection selection, string alias = "m") =>
        $"{alias}.book_id=@book" + (selection.Action switch
        {
            "removeMeaning" => $" AND {alias}.vocabulary_id=@word AND {alias}.id=@meaning",
            "removeWords" => $" AND {alias}.vocabulary_id = ANY(@words)",
            _ => ""
        });

    private DbCommand Command(string sql, string bookId, VocabularyCleanupSelection selection)
    {
        var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = context.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = sql;
        command.Parameters.Add(new NpgsqlParameter("@book", bookId));
        if (selection.Action == "removeMeaning")
        {
            command.Parameters.Add(new NpgsqlParameter("@word", selection.WordId));
            command.Parameters.Add(new NpgsqlParameter("@meaning", selection.MeaningId));
        }
        if (selection.Action == "removeWords") command.Parameters.Add(new NpgsqlParameter("@words", (selection.WordIds ?? []).ToArray()));
        return command;
    }

    private async Task<int> ScalarAsync(string sql, string bookId, VocabularyCleanupSelection selection, CancellationToken cancellationToken)
    {
        await using var command = Command(sql, bookId, selection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }
    private async Task<int> ExecuteAsync(string sql, string bookId, VocabularyCleanupSelection selection)
    {
        // Execute through EF for normal command instrumentation and failure
        // injection; use fresh parameters for each command.
        await using var command = Command(sql, bookId, selection);
        var parameters = command.Parameters.Cast<DbParameter>().Select(p => new NpgsqlParameter(p.ParameterName, p.Value)).ToArray();
        return await context.Database.ExecuteSqlRawAsync(sql, parameters.Cast<object>());
    }

    public async Task<VocabularyCleanupCounts> CountAsync(string bookId, VocabularyCleanupSelection selection, CancellationToken cancellationToken)
    {
        var predicate = Predicate(selection);
        var meanings = await ScalarAsync($"SELECT count(*) FROM vocabulary_meaning m WHERE {predicate}", bookId, selection, cancellationToken);
        var words = await ScalarAsync($"SELECT count(DISTINCT m.vocabulary_id) FROM vocabulary_meaning m WHERE {predicate}", bookId, selection, cancellationToken);
        var orphans = await ScalarAsync($"""
            SELECT count(*) FROM (SELECT DISTINCT m.vocabulary_id FROM vocabulary_meaning m WHERE {predicate}) a
            WHERE NOT EXISTS (SELECT 1 FROM vocabulary_meaning m WHERE m.vocabulary_id=a.vocabulary_id AND NOT ({predicate}))
            """, bookId, selection, cancellationToken);
        var retained = await ScalarAsync($"""
            SELECT count(*) FROM (SELECT DISTINCT m.vocabulary_id FROM vocabulary_meaning m WHERE {predicate}) a
            WHERE NOT EXISTS (SELECT 1 FROM vocabulary_meaning m WHERE m.vocabulary_id=a.vocabulary_id AND NOT ({predicate}))
            AND (EXISTS (SELECT 1 FROM vocabulary_relation r WHERE r.source_word_id=a.vocabulary_id)
              OR EXISTS (SELECT 1 FROM vocabulary_relation r WHERE r.target_word_id=a.vocabulary_id))
            """, bookId, selection, cancellationToken);
        return new VocabularyCleanupCounts(words, meanings, orphans, retained);
    }

    public async Task<VocabularyCleanupResult> DeleteAsync(string bookId, VocabularyCleanupSelection selection)
    {
        const string table = "pg_temp.lexarbor_cleanup_affected";
        try
        {
            // The table lives only inside this write transaction: a rollback
            // removes it with everything else, and a commit drops it. An
            // explicit DROP in a finally block would run after a failed
            // statement, when PostgreSQL refuses every further command in the
            // transaction, and replace the original failure with that refusal.
            await ExecuteAsync("CREATE TEMP TABLE lexarbor_cleanup_affected(id text COLLATE \"C\" PRIMARY KEY) ON COMMIT DROP", bookId, selection);
            var affected = await ExecuteAsync($"INSERT INTO {table} SELECT DISTINCT m.vocabulary_id FROM vocabulary_meaning m WHERE {Predicate(selection)}", bookId, selection);
            var deletedMeanings = await ExecuteAsync($"DELETE FROM vocabulary_meaning AS m WHERE {Predicate(selection)}", bookId, selection);
            var retained = await ScalarAsync($"""
                SELECT count(*) FROM vocabulary WHERE id IN (SELECT id FROM {table})
                AND NOT EXISTS (SELECT 1 FROM vocabulary_meaning m WHERE m.vocabulary_id=vocabulary.id)
                AND (EXISTS (SELECT 1 FROM vocabulary_relation r WHERE r.source_word_id=vocabulary.id)
                  OR EXISTS (SELECT 1 FROM vocabulary_relation r WHERE r.target_word_id=vocabulary.id))
                """, bookId, selection, CancellationToken.None);
            var deletedWords = await ExecuteAsync($"""
                DELETE FROM vocabulary WHERE id IN (SELECT id FROM {table})
                AND NOT EXISTS (SELECT 1 FROM vocabulary_meaning m WHERE m.vocabulary_id=vocabulary.id)
                AND NOT EXISTS (SELECT 1 FROM vocabulary_relation r WHERE r.source_word_id=vocabulary.id)
                AND NOT EXISTS (SELECT 1 FROM vocabulary_relation r WHERE r.target_word_id=vocabulary.id)
                """, bookId, selection);
            var deletedBook = selection.Action == "delete" &&
                await ExecuteAsync("DELETE FROM vocabulary_book WHERE id=@book", bookId, selection) == 1;
            await ExecuteAsync($"DROP TABLE {table}", bookId, selection);
            return new VocabularyCleanupResult(bookId, selection.Action, affected, deletedMeanings, deletedWords, deletedBook, retained);
        }
        finally
        {
            context.ChangeTracker.Clear();
        }
    }
}
