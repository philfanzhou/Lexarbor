using Lexarbor.Database.Entities;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed class TextbookAudioPlaybackReadRepository(VocabularyDbContext context) : ITextbookAudioPlaybackReadRepository
{
    public Task<TextbookAudioTrackPage> GetPublicPageAsync(string bookId, string unitId, int page, int size,
        CancellationToken cancellationToken)
        => new TextbookAudioTrackRepository(context).ReadSnapshotAsync(async () =>
        {
            await VerifyPublicUnitAsync(bookId, unitId, cancellationToken);
            var query = PublicTracks(bookId, unitId);
            var count = await query.CountAsync(cancellationToken);
            var offset = ((long)page - 1) * size;
            var rows = offset >= count ? [] : await query.OrderBy(t => t.DisplayOrder).ThenBy(t => t.Id)
                .Skip((int)offset).Take(size).ToListAsync(cancellationToken);
            return new TextbookAudioTrackPage(rows.Select(t => new TextbookAudioTrackModel(t.Id, t.BookId, t.UnitId,
                t.Title, t.DisplayOrder, t.CurrentAssetId, t.DurationSeconds, t.IsAvailable, t.Version)).ToArray(),
                count, (int)Math.Ceiling(count / (double)size));
        }, cancellationToken);

    public Task<TextbookAudioPlaybackSnapshot> GetPublicSnapshotAsync(string bookId, string unitId, string trackId,
        CancellationToken cancellationToken)
        => new TextbookAudioTrackRepository(context).ReadSnapshotAsync(async () =>
        {
            await VerifyPublicUnitAsync(bookId, unitId, cancellationToken);
            var row = await (from track in PublicTracks(bookId, unitId)
                             join asset in context.AudioAssets.AsNoTracking() on track.CurrentAssetId equals asset.Id
                             where track.Id == trackId
                             select new { track.Id, Asset = asset }).SingleOrDefaultAsync(cancellationToken)
                ?? throw new ResourceNotFoundException("Textbook audio track was not found.");
            var a = row.Asset;
            return new TextbookAudioPlaybackSnapshot(row.Id, new(a.Id, new(a.Bucket, a.ObjectKey), a.Sha256,
                a.ByteLength, a.ContentType, a.CreatedAtUnixMs, a.FirstPublishedAtUnixMs));
        }, cancellationToken);

    private IQueryable<TextbookAudioTrackEntity> PublicTracks(string bookId, string unitId)
        => context.TextbookAudioTracks.AsNoTracking().Where(t => t.BookId == bookId && t.UnitId == unitId && t.IsAvailable
            && context.AudioAssets.Any(a => a.Id == t.CurrentAssetId && a.FirstPublishedAtUnixMs != null));

    private async Task VerifyPublicUnitAsync(string bookId, string unitId, CancellationToken token)
    {
        var book = await context.VocabularyBooks.AsNoTracking().SingleOrDefaultAsync(b => b.Id == bookId, token)
            ?? throw new ResourceNotFoundException("Vocabulary book was not found.");
        if (!book.Status) throw new BusinessRuleException("Vocabulary book is disabled.");
        if (!await context.VocabularyBookUnits.AsNoTracking().AnyAsync(u => u.Id == unitId && u.BookId == bookId, token))
            throw new ResourceNotFoundException("Vocabulary book unit was not found.");
    }

    public Task<TextbookAudioPlaybackSnapshot> GetAdminSnapshotAsync(string bookId, string unitId, string trackId,
        CancellationToken cancellationToken)
        // The catalogue helper owns the deferred SQLite transaction and maps busy/locked
        // across connection open, begin, every read and commit. Nothing lazy escapes it.
        => new TextbookAudioTrackRepository(context).ReadSnapshotAsync(async () =>
        {
            if (!await context.VocabularyBooks.AsNoTracking().AnyAsync(b => b.Id == bookId, cancellationToken))
                throw new ResourceNotFoundException("Vocabulary book was not found.");
            var owner = await context.VocabularyBookUnits.AsNoTracking().Where(u => u.Id == unitId)
                .Select(u => u.BookId).SingleOrDefaultAsync(cancellationToken)
                ?? throw new ResourceNotFoundException("Vocabulary book unit was not found.");
            if (owner != bookId) throw new ConflictException("Vocabulary book unit does not belong to the requested vocabulary book.");
            var track = await context.TextbookAudioTracks.AsNoTracking().SingleOrDefaultAsync(t => t.Id == trackId, cancellationToken)
                ?? throw new ResourceNotFoundException("Textbook audio track was not found.");
            if (track.BookId != bookId || track.UnitId != unitId)
                throw new ConflictException("Textbook audio track does not belong to the requested book and unit.");
            var asset = await context.AudioAssets.AsNoTracking()
                .SingleOrDefaultAsync(a => a.Id == track.CurrentAssetId && a.FirstPublishedAtUnixMs != null, cancellationToken)
                ?? throw new ResourceNotFoundException("Textbook audio track has no published current file.");
            return new TextbookAudioPlaybackSnapshot(track.Id, new(asset.Id, new(asset.Bucket, asset.ObjectKey), asset.Sha256,
                asset.ByteLength, asset.ContentType, asset.CreatedAtUnixMs, asset.FirstPublishedAtUnixMs));
        }, cancellationToken);
}
