using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed class AudioReadRepository(VocabularyDbContext context) : IAudioReadRepository
{
    public async Task<AudioReadSnapshot> GetSnapshotAsync(string wordId, string? meaningId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var transaction = ((SqliteConnection)context.Database.GetDbConnection()).BeginTransaction(deferred: true);
            await using var enlisted = await context.Database.UseTransactionAsync(transaction, cancellationToken);
            // No tracking: an edit or import on this scoped context must never
            // make later audio reads reuse an older word/meaning instance.
            var word = await context.Vocabularies.AsNoTracking().SingleOrDefaultAsync(row => row.Id == wordId, cancellationToken)
                ?? throw new ResourceNotFoundException("Vocabulary word was not found.");
            AudioMeaningInput? meaning = null;
            if (meaningId != null)
            {
                var row = await context.VocabularyMeanings.AsNoTracking()
                    .SingleOrDefaultAsync(row => row.Id == meaningId && row.VocabularyId == wordId, cancellationToken)
                    ?? throw new ResourceNotFoundException("Vocabulary meaning was not found.");
                meaning = new(row.Id, row.PartOfSpeech, row.Meaning);
            }
            var bindings = await (from binding in context.VocabularyAudioBindings.AsNoTracking()
                                  join asset in context.AudioAssets.AsNoTracking() on binding.AssetId equals asset.Id
                                  where binding.WordId == wordId && (binding.MeaningId == null || binding.MeaningId == meaningId)
                                  select new { binding, asset }).ToListAsync(cancellationToken);
            var result = new AudioReadSnapshot(new(word.Id, word.Word, word.PhoneticUk, word.PhoneticUs), meaning,
                bindings.Select(row => new AudioBinding(row.binding.WordId, row.binding.MeaningId, row.binding.Locale,
                    new AudioAsset(row.asset.Id, new(row.asset.Bucket, row.asset.ObjectKey), row.asset.Sha256,
                        row.asset.ByteLength, row.asset.ContentType, row.asset.CreatedAtUnixMs, row.asset.FirstPublishedAtUnixMs),
                    row.binding.SourceRevision)).ToList());
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }
}
