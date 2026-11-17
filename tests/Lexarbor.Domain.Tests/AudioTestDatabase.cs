using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Lexarbor.Tests.Shared;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Lexarbor.Domain.Tests;

internal sealed class AudioTestDatabase : IDisposable
{
    private readonly PostgreSqlTestDatabase _database = new();
    public string ConnectionString => _database.ConnectionString;
    public static CancellationToken Token => TestContext.Current.CancellationToken;
    public const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public VocabularyDbContext Open(params IInterceptor[] interceptors) => _database.Open(interceptors);

    public DbContextOptionsBuilder<VocabularyDbContext> Options() => PostgreSqlTestServer.Options(ConnectionString);

    public static async Task SeedAsync(VocabularyDbContext db)
    {
        db.VocabularyBooks.Add(new() { Id = "book", BookName = "Audio test", Status = true });
        db.Vocabularies.AddRange(new VocabularyEntity { Id = "word", Word = "Apple", PhoneticUk = "uk", PhoneticUs = "us" },
            new VocabularyEntity { Id = "other", Word = "other" });
        await db.SaveChangesAsync(Token);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO vocabulary_meaning (id, vocabulary_id, book_id, part_of_speech, meaning, created_at, updated_at)
            VALUES ('meaning', 'word', 'book', 'n.', 'fruit', '0001-01-01 00:00:00+00:00', '0001-01-01 00:00:00+00:00'),
                   ('other-meaning', 'other', 'book', NULL, 'other', '0001-01-01 00:00:00+00:00', '0001-01-01 00:00:00+00:00');
            """, Token);
        db.VocabularyBookUnits.Add(new() { Id = "unit", BookId = "book", Number = 1 });
        db.VocabularyMeaningUnits.Add(new() { UnitId = "unit", MeaningId = "meaning", BookId = "book" });
        await db.SaveChangesAsync(Token);
        db.ChangeTracker.Clear();
    }

    public static AudioAssetEntity Asset(string id, bool published = true) => new()
    {
        Id = id,
        Bucket = "test-audio",
        ObjectKey = $"audio/{id}.mp3",
        Sha256 = Hash,
        ByteLength = 128,
        CreatedAtUnixMs = 1,
        FirstPublishedAtUnixMs = published ? 2 : null
    };

    public static VocabularyAudioBindingEntity Binding(string id, AudioLocale locale, string? meaningId, string revision) => new()
    {
        Id = id,
        WordId = "word",
        MeaningId = meaningId,
        Locale = locale,
        AssetId = id,
        SourceRevision = revision
    };

    public static AudioUploadOperationEntity Operation(string assetId, AudioUploadState state) => new()
    {
        IdempotencyKey = Guid.NewGuid().ToString(),
        AssetId = assetId,
        WordId = "word",
        MeaningId = "meaning",
        Locale = AudioLocale.EnGb,
        SourceRevision = Hash,
        RequestFingerprint = Hash,
        State = state,
        CreatedAtUnixMs = 1,
        UpdatedAtUnixMs = 2,
        UploadLeaseOwner = "test-owner",
        UploadLeaseFence = 1,
        UploadLeaseExpiresAtUnixMs = 100,
        ReceiptStatusCode = state == AudioUploadState.Succeeded ? 200 : state is AudioUploadState.Reserved or AudioUploadState.Transferring ? null : 503,
        ReceiptAssetId = state == AudioUploadState.Succeeded ? assetId : null,
        ReceiptSourceRevision = state == AudioUploadState.Succeeded ? Hash : null,
        ReceiptErrorCode = state is AudioUploadState.Reserved or AudioUploadState.Transferring or AudioUploadState.Succeeded ? null : "audio.upload_failed",
        CompletedAtUnixMs = state is AudioUploadState.Reserved or AudioUploadState.Transferring ? null : 2,
        CleanupLeaseOwner = state == AudioUploadState.CleanupClaimed ? "test-cleaner" : null,
        CleanupLeaseFence = state == AudioUploadState.CleanupClaimed ? 1 : 0,
        CleanupLeaseExpiresAtUnixMs = state == AudioUploadState.CleanupClaimed ? 200 : null,
        CleanupReturnState = state == AudioUploadState.CleanupClaimed ? AudioUploadState.Abandoned : null,
        CleanupNextAttemptAtUnixMs = 300,
        CleanupAttempts = 0
    };

    public static string Revision(AudioLocale locale, bool meaning = false) => AudioSelectionService.ComputeRevision(
        AudioSelectionService.GetInput(new("word", "Apple", "uk", "us"), meaning ? new("meaning", "n.", "fruit") : null, locale));

    public async Task<object?[]> RowsAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(Token);
        var rows = new List<object?>();
        while (await reader.ReadAsync(Token))
        {
            var values = new object[reader.FieldCount]; reader.GetValues(values);
            rows.Add(values.Select(value => value is DBNull ? null : value).ToArray());
        }
        return rows.ToArray();
    }

    public void Dispose() => _database.Dispose();
}
