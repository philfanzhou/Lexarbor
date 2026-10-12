using System.Data.Common;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;


namespace Lexarbor.Domain.Tests;

public sealed class AudioPublicReadRepositoryTests
{
    private static CancellationToken Ct => AudioTestDatabase.Token;

    [Fact]
    public async Task ConcurrentDisableUnlinkAndEditAfterBookRead_ReturnOriginalAuthorizedSnapshot()
    {
        using var file = new AudioTestDatabase();
        await using (var seed = file.Open())
        {
            await seed.Database.MigrateAsync(Ct); await AudioTestDatabase.SeedAsync(seed);
            seed.AudioAssets.Add(AudioTestDatabase.Asset("original"));
            seed.VocabularyAudioBindings.Add(AudioTestDatabase.Binding("original", AudioLocale.EnGb, null, AudioTestDatabase.Revision(AudioLocale.EnGb)));
            await seed.SaveChangesAsync(Ct); await seed.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL", Ct);
        }
        var interceptor = new AfterBookRead(async () =>
        {
            await using var writer = file.Open();
            await writer.Database.ExecuteSqlRawAsync("UPDATE vocabulary_book SET status=0 WHERE id='book'; DELETE FROM vocabulary_meaning WHERE id='meaning'; UPDATE vocabulary SET phonetic_uk='new' WHERE id='word'", Ct);
        });
        await using var db = file.Open(interceptor);
        var repository = new AudioReadRepository(db);
        var snapshot = await repository.GetPublicSnapshotAsync("book", "word", "meaning", Ct);
        Assert.True(interceptor.Fired); Assert.Equal("meaning", snapshot.Meaning!.MeaningId);
        Assert.Equal("uk", snapshot.Word.PhoneticUk); Assert.Equal("original", Assert.Single(snapshot.Bindings).Asset.Id);
        Assert.Null(db.Database.CurrentTransaction);
        await Assert.ThrowsAsync<BusinessRuleException>(() => repository.GetPublicSnapshotAsync("book", "word", "meaning", Ct));
        await db.Database.ExecuteSqlRawAsync("UPDATE vocabulary_book SET status=1 WHERE id='book'", Ct);
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => repository.GetPublicSnapshotAsync("book", "word", null, Ct));
    }

    [Fact]
    public async Task CancellationAfterAuthorizationRead_ReleasesSnapshotAndPreservesData()
    {
        using var file = new AudioTestDatabase();
        await using (var seed = file.Open()) { await seed.Database.MigrateAsync(Ct); await AudioTestDatabase.SeedAsync(seed); }
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var db = file.Open(new AfterBookRead(() => { cancel.Cancel(); return Task.CompletedTask; }));
        var repository = new AudioReadRepository(db);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => repository.GetPublicSnapshotAsync("book", "word", "meaning", cancel.Token));
        Assert.Null(db.Database.CurrentTransaction);
        var snapshot = await repository.GetPublicSnapshotAsync("book", "word", "meaning", Ct);
        Assert.Equal("meaning", snapshot.Meaning!.MeaningId);
        Assert.Equal(2, await db.VocabularyMeanings.CountAsync(Ct)); Assert.Empty(db.ChangeTracker.Entries());
    }

    private sealed class AfterBookRead(Func<Task> action) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.Contains("FROM \"vocabulary_book\"", StringComparison.Ordinal))
            {
                Fired = true; await action();
            }
            return result;
        }
    }
}
