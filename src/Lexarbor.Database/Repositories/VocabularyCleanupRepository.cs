using System.Data.Common;
using System.Text.Json;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Mapster;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Lexarbor.Database.Repositories;

public sealed class VocabularyCleanupRepository(VocabularyDbContext context) : IVocabularyCleanupRepository
{
    public async Task<T> ReadSnapshotAsync<T>(Func<Task<T>> read, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var transaction = ((SqliteConnection)context.Database.GetDbConnection()).BeginTransaction(deferred: true);
            await using var enlisted = await context.Database.UseTransactionAsync(transaction, cancellationToken);
            var result = await read();
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally { await context.Database.CloseConnectionAsync(); }
    }

    public async Task<VocabularyBookModel?> GetBookAsync(string bookId, CancellationToken cancellationToken)
        => (await context.VocabularyBooks.AsNoTracking().SingleOrDefaultAsync(b => b.Id == bookId, cancellationToken))?.Adapt<VocabularyBookModel>();
    public Task<bool> WordExistsAsync(string wordId, CancellationToken cancellationToken)
        => context.Vocabularies.AnyAsync(v => v.Id == wordId, cancellationToken);
    public async Task<VocabularyMeaningModel?> GetMeaningAsync(string meaningId, CancellationToken cancellationToken)
        => (await context.VocabularyMeanings.AsNoTracking().SingleOrDefaultAsync(m => m.Id == meaningId, cancellationToken))?.Adapt<VocabularyMeaningModel>();
    public Task<int> CountSelectedWordsAsync(string bookId, IReadOnlyList<string> wordIds, CancellationToken cancellationToken)
        => context.VocabularyMeanings.Where(m => m.BookId == bookId && wordIds.Contains(m.VocabularyId))
            .Select(m => m.VocabularyId).Distinct().CountAsync(cancellationToken);

    // This predicate is the sole definition of R for preview and commit. The
    // identifiers below are code constants; every external value is a parameter.
    private static string Predicate(VocabularyCleanupSelection selection, string alias = "m") =>
        $"{alias}.book_id=@book" + (selection.Action switch
        {
            "removeMeaning" => $" AND {alias}.vocabulary_id=@word AND {alias}.id=@meaning",
            "removeWords" => $" AND {alias}.vocabulary_id IN (SELECT value FROM json_each(@words))",
            _ => ""
        });

    private DbCommand Command(string sql, string bookId, VocabularyCleanupSelection selection)
    {
        var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = context.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = sql;
        command.Parameters.Add(new SqliteParameter("@book", bookId));
        if (selection.Action == "removeMeaning")
        {
            command.Parameters.Add(new SqliteParameter("@word", selection.WordId));
            command.Parameters.Add(new SqliteParameter("@meaning", selection.MeaningId));
        }
        if (selection.Action == "removeWords") command.Parameters.Add(new SqliteParameter("@words", JsonSerializer.Serialize(selection.WordIds)));
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
        var parameters = command.Parameters.Cast<DbParameter>().Select(p => new SqliteParameter(p.ParameterName, p.Value)).ToArray();
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
        return new VocabularyCleanupCounts(words, meanings, orphans);
    }

    public async Task<VocabularyCleanupResult> DeleteAsync(string bookId, VocabularyCleanupSelection selection)
    {
        const string table = "temp.lexarbor_cleanup_affected";
        try
        {
            await ExecuteAsync($"CREATE TEMP TABLE lexarbor_cleanup_affected(id TEXT PRIMARY KEY)", bookId, selection);
            var affected = await ExecuteAsync($"INSERT INTO {table} SELECT DISTINCT m.vocabulary_id FROM vocabulary_meaning m WHERE {Predicate(selection)}", bookId, selection);
            var deletedMeanings = await ExecuteAsync($"DELETE FROM vocabulary_meaning AS m WHERE {Predicate(selection)}", bookId, selection);
            var deletedWords = await ExecuteAsync($"""
                DELETE FROM vocabulary WHERE id IN (SELECT id FROM {table})
                AND NOT EXISTS (SELECT 1 FROM vocabulary_meaning m WHERE m.vocabulary_id=vocabulary.id)
                """, bookId, selection);
            var deletedBook = selection.Action == "delete" &&
                await ExecuteAsync("DELETE FROM vocabulary_book WHERE id=@book", bookId, selection) == 1;
            return new VocabularyCleanupResult(bookId, selection.Action, affected, deletedMeanings, deletedWords, deletedBook);
        }
        finally
        {
            try { await ExecuteAsync($"DROP TABLE IF EXISTS {table}", bookId, selection); }
            finally { context.ChangeTracker.Clear(); }
        }
    }
}
