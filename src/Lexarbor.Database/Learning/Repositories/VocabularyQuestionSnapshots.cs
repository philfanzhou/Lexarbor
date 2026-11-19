using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Lexarbor.Learning.Domain.Models;

namespace Lexarbor.Learning.Database.Repositories;

/// <summary>
/// Latest issued question per exact card key, read in one parameterized query inside the caller's transaction.
/// </summary>
internal static class VocabularyQuestionSnapshots
{
    public static async Task<IReadOnlyDictionary<CardKey, VocabularyErrorProneDisplay>> ReadAsync(LearningDbContext context,
        Guid studentId, IReadOnlyList<CardKey> keys, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var result = new Dictionary<CardKey, VocabularyErrorProneDisplay>();
        if (keys.Count == 0) return result;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var key in keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.WriteStartObject(); writer.WriteString("word_key", key.WordKey);
                writer.WriteString("meaning_key", key.MeaningKey); writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        var command = context.GetService<IRawSqlCommandBuilder>().Build("""
            SELECT DISTINCT ON (q.word_key COLLATE "C", q.meaning_key COLLATE "C")
                q.word_key, q.meaning_key, q.direction::integer, q.prompt, q.options_snapshot, q.correct_option_id, q.answer_word,
                q.word_id, q.meaning_id, q.book_id
            FROM jsonb_to_recordset(CAST({2} AS jsonb)) k(word_key text, meaning_key text)
            JOIN vocab_issued_question q ON q.student_id = {0} AND q.word_key = k.word_key AND q.meaning_key = k.meaning_key
            WHERE q.issued_at <= {1}
            ORDER BY q.word_key COLLATE "C", q.meaning_key COLLATE "C", q.issued_at DESC, q.id DESC
            """, [studentId, now, Encoding.UTF8.GetString(buffer.WrittenSpan)]);
        await using var rows = await command.RelationalCommand.ExecuteReaderAsync(new RelationalCommandParameterObject(
            context.GetService<IRelationalConnection>(), command.ParameterValues, null, context,
            context.GetService<IRelationalCommandDiagnosticsLogger>()), cancellationToken).ConfigureAwait(false);
        var reader = rows.DbDataReader;
        while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new(reader.GetString(0), reader.GetString(1)),
                new(reader.GetInt32(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetGuid(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.GetString(7), reader.GetString(8), reader.GetString(9)));
        return result;
    }
}
