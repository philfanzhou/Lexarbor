using Lexarbor.Database.Entities;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed class TextbookAudioTrackRepository(VocabularyDbContext context) : ITextbookAudioTrackRepository
{
    // Deferred SQLite transactions keep all ownership/count/page reads in one
    // snapshot without acquiring the application's writer semaphore.
    public async Task<T> ReadSnapshotAsync<T>(Func<Task<T>> read, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
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
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            throw new StorageBusyException(
                "The vocabulary database is busy. Please retry the request.", exception);
        }
    }
    public Task<bool> BookExistsAsync(string bookId, CancellationToken cancellationToken)
        => context.VocabularyBooks.AsNoTracking().AnyAsync(b => b.Id == bookId, cancellationToken);
    public Task<string?> GetUnitBookAsync(string unitId, CancellationToken cancellationToken)
        => context.VocabularyBookUnits.AsNoTracking().Where(u => u.Id == unitId).Select(u => u.BookId).SingleOrDefaultAsync(cancellationToken);
    public async Task<TextbookAudioTrackModel?> GetAsync(string id, CancellationToken cancellationToken)
        => (await context.TextbookAudioTracks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == id, cancellationToken)) is { } entity ? ToModel(entity) : null;
    public async Task<(IReadOnlyList<TextbookAudioTrackModel> Items, int TotalCount)> ListAsync(string unitId, int page, int size, CancellationToken cancellationToken)
    {
        var query = context.TextbookAudioTracks.AsNoTracking().Where(t => t.UnitId == unitId);
        var count = await query.CountAsync(cancellationToken);
        // Calculate in long before narrowing: large valid page numbers yield an
        // empty page rather than overflowing into a different slice.
        var offset = ((long)page - 1) * size;
        var rows = offset >= count ? [] : await query.OrderBy(t => t.DisplayOrder).ThenBy(t => t.Id)
            .Skip((int)offset).Take(size).ToListAsync(cancellationToken);
        return (rows.Select(ToModel).ToArray(), count);
    }
    public Task<bool> IsPublishedAsync(string assetId, CancellationToken cancellationToken)
        => context.AudioAssets.AsNoTracking().AnyAsync(a => a.Id == assetId && a.FirstPublishedAtUnixMs != null, cancellationToken);
    public async Task AddAsync(TextbookAudioTrackModel t, CancellationToken cancellationToken)
        => await context.TextbookAudioTracks.AddAsync(new()
        {
            Id = t.Id,
            BookId = t.BookId,
            UnitId = t.UnitId,
            Title = t.Title,
            DisplayOrder = t.DisplayOrder,
            CurrentAssetId = t.CurrentAssetId,
            DurationSeconds = t.DurationSeconds,
            IsAvailable = t.IsAvailable,
            Version = t.Version
        }, cancellationToken);
    public async Task<bool> ReplaceAsync(TextbookAudioTrackModel t, string expectedVersion, CancellationToken cancellationToken)
        => await context.TextbookAudioTracks.Where(row => row.Id == t.Id && row.Version == expectedVersion)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.Title, t.Title).SetProperty(row => row.DisplayOrder, t.DisplayOrder)
                .SetProperty(row => row.IsAvailable, t.IsAvailable).SetProperty(row => row.Version, t.Version), cancellationToken) == 1;
    public async Task<bool> DeleteAsync(string id, string expectedVersion, CancellationToken cancellationToken)
        => await context.TextbookAudioTracks.Where(t => t.Id == id && t.Version == expectedVersion).ExecuteDeleteAsync(cancellationToken) == 1;
    private static TextbookAudioTrackModel ToModel(TextbookAudioTrackEntity t) => new(t.Id, t.BookId, t.UnitId, t.Title,
        t.DisplayOrder, t.CurrentAssetId, t.DurationSeconds, t.IsAvailable, t.Version);
}
