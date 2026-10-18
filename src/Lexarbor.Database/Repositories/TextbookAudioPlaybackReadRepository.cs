using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed class TextbookAudioPlaybackReadRepository(VocabularyDbContext context) : ITextbookAudioPlaybackReadRepository
{
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
