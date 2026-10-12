using System.Data.Common;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class AudioReadRepositoryTests
{
    private static CancellationToken Token => AudioTestDatabase.Token;

    private static async Task BindAllAsync(VocabularyDbContext db)
    {
        foreach (var locale in Enum.GetValues<AudioLocale>())
            foreach (var meaning in new[] { false, true })
            {
                var id = $"{locale}-{meaning}";
                db.AudioAssets.Add(AudioTestDatabase.Asset(id));
                db.VocabularyAudioBindings.Add(AudioTestDatabase.Binding(id, locale, meaning ? "meaning" : null, AudioTestDatabase.Revision(locale, meaning)));
            }
        await db.SaveChangesAsync(Token); db.ChangeTracker.Clear();
    }

    [Theory]
    [InlineData("uk", true, false, true, false)]
    [InlineData("us", false, true, false, true)]
    [InlineData("word", true, true, true, true)]
    [InlineData("pos", false, false, true, true)]
    [InlineData("meaning", false, false, true, true)]
    [InlineData("example", false, false, false, false)]
    [InlineData("positions", false, false, false, false)]
    [InlineData("book-disabled", false, false, false, false)]
    [InlineData("timestamp", false, false, false, false)]
    public async Task ActualWrites_DeriveOnlyAffectedRevisions(string change, bool defaultGb, bool defaultUs, bool overrideGb, bool overrideUs)
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open();
        await db.Database.MigrateAsync(Token); await AudioTestDatabase.SeedAsync(db); await BindAllAsync(db);
        // Keep tracked instances intentionally: the read repository must ignore
        // them after services and direct SQL change the same scoped database.
        _ = await db.Vocabularies.SingleAsync(row => row.Id == "word", Token);
        _ = await db.VocabularyMeanings.SingleAsync(row => row.Id == "meaning", Token);
        var words = new VocabularyRepository(db); var meanings = new VocabularyMeaningRepository(db);
        var books = new VocabularyBookRepository(db); var units = new VocabularyBookUnitRepository(db);
        var positions = new VocabularyMeaningUnitRepository(db); var uow = new UnitOfWork(db);
        var wordEdit = new VocabularyWordEditService(words, new VocabularyWordEditRepository(db), meanings, positions, uow);
        var meaningEdit = new VocabularyMeaningEditService(words, books, meanings, positions, uow);
        switch (change)
        {
            case "uk": await wordEdit.ReplaceAsync("word", "Apple", "new-uk", "us", Token); break;
            case "us": await wordEdit.ReplaceAsync("word", "Apple", "uk", "new-us", Token); break;
            case "word": await wordEdit.ReplaceAsync("word", "APPLE", "uk", "us", Token); break;
            case "pos": await meaningEdit.ReplaceAsync("book", "word", "meaning", "v.", "fruit", null, Token); break;
            case "meaning": await meaningEdit.ReplaceAsync("book", "word", "meaning", "n.", "new fruit", null, Token); break;
            case "example": await meaningEdit.ReplaceAsync("book", "word", "meaning", "n.", "fruit", "new example", Token); break;
            case "positions": await db.Database.ExecuteSqlRawAsync("DELETE FROM vocabulary_meaning_unit; INSERT INTO vocabulary_meaning_unit VALUES ('unit','meaning','book','A','phrase')", Token); break;
            case "book-disabled": await db.Database.ExecuteSqlRawAsync("UPDATE vocabulary_book SET status=0 WHERE id='book'", Token); break;
            case "timestamp": await db.Database.ExecuteSqlRawAsync("UPDATE vocabulary SET updated_at='2026-10-01'; UPDATE vocabulary_meaning SET updated_at='2026-10-01'", Token); break;
        }
        var repository = new AudioReadRepository(db);
        var defaults = await repository.GetSnapshotAsync("word", null, Token);
        var overrides = await repository.GetSnapshotAsync("word", "meaning", Token);
        AssertStatus(defaults, AudioLocale.EnGb, defaultGb); AssertStatus(defaults, AudioLocale.EnUs, defaultUs);
        AssertStatus(overrides, AudioLocale.EnGb, overrideGb); AssertStatus(overrides, AudioLocale.EnUs, overrideUs);
        Assert.Equal(4, await db.VocabularyAudioBindings.CountAsync(Token));
        Assert.Equal(4, await db.AudioAssets.CountAsync(Token));
    }

    [Fact]
    public async Task BatchImport_PhoneticMergeIsSeenByDefaultAndOverrideWithoutWriteHooks()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open();
        await db.Database.MigrateAsync(Token); await AudioTestDatabase.SeedAsync(db); await BindAllAsync(db);
        var domain = new VocabularyDomainService(new VocabularyRepository(db), new VocabularyBookRepository(db),
            new VocabularyMeaningRepository(db), new VocabularyBookUnitRepository(db), new VocabularyMeaningUnitRepository(db), new UnitOfWork(db));
        var result = await domain.ImportBatchAsync("book", [(new() { Word = "Apple", PhoneticUk = "import-uk" },
            new() { PartOfSpeech = "n.", Meaning = "fruit" }, null, null, null)]);
        Assert.Equal(1, result.Reused);
        var repository = new AudioReadRepository(db);
        foreach (var meaning in new string?[] { null, "meaning" })
        {
            var snapshot = await repository.GetSnapshotAsync("word", meaning, Token);
            AssertStatus(snapshot, AudioLocale.EnGb, true); AssertStatus(snapshot, AudioLocale.EnUs, false);
        }
    }

    [Fact]
    public async Task MissingTargetsWrongOwnershipAndCancelledRead_LeaveDataUntouched()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open();
        await db.Database.MigrateAsync(Token); await AudioTestDatabase.SeedAsync(db); await BindAllAsync(db);
        var repository = new AudioReadRepository(db);
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => repository.GetSnapshotAsync("missing", null, Token));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => repository.GetSnapshotAsync("word", "missing", Token));
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => repository.GetSnapshotAsync("word", "other-meaning", Token));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.GetSnapshotAsync("word", null, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new VocabularyWordEditService(new VocabularyRepository(db),
            new VocabularyWordEditRepository(db), new VocabularyMeaningRepository(db), new VocabularyMeaningUnitRepository(db), new UnitOfWork(db))
            .ReplaceAsync("word", "changed", "changed", "changed", cancelled.Token));
        Assert.Equal("Apple", (await repository.GetSnapshotAsync("word", null, Token)).Word.Word);
        Assert.Equal(4, await db.VocabularyAudioBindings.CountAsync(Token));
    }

    [Fact]
    public async Task ConcurrentCommitBetweenWordAndBindings_ReturnsOneReadSnapshot()
    {
        using var file = new AudioTestDatabase();
        await using (var seed = file.Open())
        {
            await seed.Database.MigrateAsync(Token); await AudioTestDatabase.SeedAsync(seed); await BindAllAsync(seed);
            await seed.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL", Token);
        }
        var interceptor = new ChangeAfterWordRead(file);
        await using var db = file.Open(interceptor);
        var repository = new AudioReadRepository(db);
        var first = await repository.GetSnapshotAsync("word", "meaning", Token);
        Assert.True(interceptor.Changed); Assert.Equal("uk", first.Word.PhoneticUk);
        AssertStatus(first, AudioLocale.EnGb, false);
        var second = await repository.GetSnapshotAsync("word", "meaning", Token);
        Assert.Equal("concurrent-uk", second.Word.PhoneticUk); AssertStatus(second, AudioLocale.EnGb, false);
        Assert.NotEqual(AudioSelectionService.Select(first, AudioLocale.EnGb).SourceRevision, AudioSelectionService.Select(second, AudioLocale.EnGb).SourceRevision);
    }

    [Fact]
    public async Task TargetPage_ConcurrentCommitKeepsInputsBindingsAndCountInOneSnapshot()
    {
        using var file = new AudioTestDatabase();
        await using (var seed = file.Open())
        {
            await seed.Database.MigrateAsync(Token); await AudioTestDatabase.SeedAsync(seed); await BindAllAsync(seed);
            await seed.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL", Token);
        }
        var interceptor = new ChangeAfterWordRead(file);
        await using var db = file.Open(interceptor);
        var repository = new AudioReadRepository(db);
        var query = new AudioTargetQuery(null, AudioLocale.EnGb, AudioContentStatus.Ready, 1, 20);
        var first = await repository.SearchTargetsAsync(query, Token);
        Assert.True(interceptor.Changed);
        Assert.Equal(2, first.TotalCount);
        Assert.All(first.Items, item => Assert.Equal("uk", item.Selection.Input.Phonetic));
        var second = await repository.SearchTargetsAsync(query, Token);
        Assert.Equal(1, second.TotalCount);
        Assert.Equal("meaning", second.Items[0].MeaningId);
        Assert.Equal("concurrent-uk", second.Items[0].Selection.Input.Phonetic);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.SearchTargetsAsync(query, cancelled.Token));
        Assert.Equal(4, await db.VocabularyAudioBindings.CountAsync(Token));
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task TargetPage_CancelledBetweenQueries_ReleasesSnapshotAndDoesNotWrite()
    {
        using var file = new AudioTestDatabase();
        await using (var seed = file.Open())
        {
            await seed.Database.MigrateAsync(Token); await AudioTestDatabase.SeedAsync(seed); await BindAllAsync(seed);
        }
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await using var db = file.Open(new CancelAfterWordRead(cancelled));
        var repository = new AudioReadRepository(db);
        var query = new AudioTargetQuery(null, null, null, 1, 20);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.SearchTargetsAsync(query, cancelled.Token));
        Assert.Equal(6, (await repository.SearchTargetsAsync(query, Token)).TotalCount);
        Assert.Equal(4, await db.AudioAssets.CountAsync(Token));
        Assert.Equal(4, await db.VocabularyAudioBindings.CountAsync(Token));
        Assert.Equal(0, await db.AudioUploadOperations.CountAsync(Token));
        Assert.Null(db.Database.CurrentTransaction);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    private sealed class CancelAfterWordRead(CancellationTokenSource cancelled) : DbCommandInterceptor
    {
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM \"vocabulary\" AS", StringComparison.Ordinal)) cancelled.Cancel();
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task CleanupSql_CascadesOnlyBindingsAndKeepsResourceRecoveryRecords()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open();
        await db.Database.MigrateAsync(Token); await AudioTestDatabase.SeedAsync(db); await BindAllAsync(db);
        var operation = AudioTestDatabase.Operation("EnGb-True", AudioUploadState.Succeeded);
        db.AudioUploadOperations.Add(operation); await db.SaveChangesAsync(Token);
        var service = new VocabularyCleanupService(new VocabularyCleanupRepository(db), new UnitOfWork(db));
        var result = await service.CommitAsync("book", new("removeMeaning", "word", "meaning"), Token);
        Assert.Equal((1, 1), (result.DeletedMeaningCount, result.DeletedWordCount));
        Assert.Equal(0, await db.VocabularyAudioBindings.CountAsync(Token));
        Assert.Equal(4, await db.AudioAssets.CountAsync(Token)); Assert.Equal(1, await db.AudioUploadOperations.CountAsync(Token));
        Assert.Equal(operation.IdempotencyKey, (await db.AudioUploadOperations.AsNoTracking().SingleAsync(Token)).IdempotencyKey);
        Assert.True(await db.Vocabularies.AnyAsync(row => row.Id == "other", Token));
    }

    private static void AssertStatus(AudioReadSnapshot snapshot, AudioLocale locale, bool stale)
    {
        var result = AudioSelectionService.Select(snapshot, locale);
        Assert.Equal(stale ? AudioContentStatus.Stale : AudioContentStatus.Ready, result.Status);
        Assert.NotNull(result.EffectiveBinding);
    }

    private sealed class ChangeAfterWordRead(AudioTestDatabase file) : DbCommandInterceptor
    {
        public bool Changed { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!Changed && command.CommandText.Contains("FROM \"vocabulary\" AS", StringComparison.Ordinal))
            {
                Changed = true;
                await using var writer = file.Open();
                await using var transaction = await writer.Database.BeginTransactionAsync(cancellationToken);
                await writer.Database.ExecuteSqlRawAsync("UPDATE vocabulary SET phonetic_uk='concurrent-uk' WHERE id='word'", cancellationToken);
                var revision = AudioSelectionService.ComputeRevision(AudioSelectionService.GetInput(new("word", "Apple", "concurrent-uk", "us"), new("meaning", "n.", "fruit"), AudioLocale.EnGb));
                await writer.Database.ExecuteSqlInterpolatedAsync($"UPDATE vocabulary_audio_binding SET source_revision={revision} WHERE id='EnGb-True'", cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            return result;
        }
    }
}
