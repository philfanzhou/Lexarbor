using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class AudioRecordExistenceRepositoryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(AudioUploadState.Reserved)]
    [InlineData(AudioUploadState.Transferring)]
    [InlineData(AudioUploadState.Succeeded)]
    [InlineData(AudioUploadState.Abandoned)]
    [InlineData(AudioUploadState.CleanupClaimed)]
    [InlineData(AudioUploadState.CleanupObserved)]
    public async Task EveryUnboundAssetAndOperationBlocksDestination(AudioUploadState? state)
    {
        using var file = new AudioTestDatabase();
        await using var db = file.Open();
        await db.Database.MigrateAsync(AudioTestDatabase.Token);
        var query = new AudioRecordExistenceRepository(db);
        Assert.False(await query.HasAnyAsync(AudioTestDatabase.Token));
        db.AudioAssets.Add(AudioTestDatabase.Asset("historical", state == AudioUploadState.Succeeded));
        if (state is not null) db.AudioUploadOperations.Add(AudioTestDatabase.Operation("historical", state.Value));
        await db.SaveChangesAsync(AudioTestDatabase.Token);
        db.ChangeTracker.Clear();
        Assert.Empty(await db.VocabularyAudioBindings.ToListAsync(AudioTestDatabase.Token));
        Assert.True(await query.HasAnyAsync(AudioTestDatabase.Token));
        Assert.Empty(db.ChangeTracker.Entries());
        if (state is not null)
        {
            // Defensive read of any operation, even if an external database writer
            // bypassed FK enforcement. The supported application never does this.
            await db.Database.OpenConnectionAsync(AudioTestDatabase.Token);
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=OFF", AudioTestDatabase.Token);
            await db.AudioAssets.ExecuteDeleteAsync(AudioTestDatabase.Token);
            Assert.True(await query.HasAnyAsync(AudioTestDatabase.Token));
            Assert.Equal(state.Value, (await db.AudioUploadOperations.SingleAsync(AudioTestDatabase.Token)).State);
        }
    }
}
