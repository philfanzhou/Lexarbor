using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Repositories;

namespace Lexarbor.Learning.Database.Repositories;

public sealed class VocabularyWordListRepository(LearningDbContext context) : IVocabularyWordListRepository
{
    public async Task<IVocabularyWordListSnapshot> OpenAsync(Guid studentId, CancellationToken cancellationToken = default)
    {
        var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        try
        {
            await context.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken).ConfigureAwait(false);
            await using var rows = await ExecuteReaderAsync(context, "SELECT id FROM learner_identity WHERE id = {0}", studentId, cancellationToken).ConfigureAwait(false);
            var exists = await rows.ReadAsync(cancellationToken).ConfigureAwait(false);
            return new Snapshot(context, transaction, studentId, exists);
        }
        catch { await transaction.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private static Task<RelationalDataReader> ExecuteReaderAsync(LearningDbContext context, string sql, Guid studentId, CancellationToken cancellationToken)
    {
        var command = context.GetService<IRawSqlCommandBuilder>().Build(sql, [studentId]);
        return command.RelationalCommand.ExecuteReaderAsync(new RelationalCommandParameterObject(
            context.GetService<IRelationalConnection>(), command.ParameterValues, null, context,
            context.GetService<IRelationalCommandDiagnosticsLogger>()), cancellationToken);
    }

    private sealed class Snapshot(LearningDbContext context, IDbContextTransaction transaction, Guid studentId, bool exists)
        : IVocabularyWordListSnapshot
    {
        public bool StudentExists => exists;

        public async Task<IReadOnlyList<VocabularyWordListCard>> ReadCardsAsync(CancellationToken cancellationToken = default)
        {
            await using var rows = await ExecuteReaderAsync(context, """
                SELECT word_key, meaning_key, stability, due_at, reps, lapses, first_learned_at, last_reviewed_at
                FROM vocab_memory_card WHERE student_id = {0}
                """, studentId, cancellationToken).ConfigureAwait(false);
            var cards = new List<VocabularyWordListCard>();
            var reader = rows.DbDataReader;
            while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
                cards.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetDouble(2),
                    reader.GetFieldValue<DateTimeOffset>(3), reader.GetInt32(4), reader.GetInt32(5),
                    reader.GetFieldValue<DateTimeOffset>(6), reader.GetFieldValue<DateTimeOffset>(7)));
            return cards;
        }

        public Task<IReadOnlyDictionary<CardKey, VocabularyErrorProneDisplay>> ReadSnapshotsAsync(IReadOnlyList<CardKey> keys,
            DateTimeOffset now, CancellationToken cancellationToken = default) =>
            VocabularyQuestionSnapshots.ReadAsync(context, studentId, keys, now, cancellationToken);

        // No commit or SaveChanges: disposing rolls back the read-only transaction on every path.
        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
