using Lexarbor.Database.Entities;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed class AudioReadRepository(VocabularyDbContext context) : IAudioReadRepository, IAudioPublicReadRepository
{
    public async Task<AudioTargetPage> SearchTargetsAsync(AudioTargetQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var transaction = ((SqliteConnection)context.Database.GetDbConnection()).BeginTransaction(deferred: true);
            await using var enlisted = await context.Database.UseTransactionAsync(transaction, cancellationToken);
            if (query.BookId != null && !await context.VocabularyBooks.AsNoTracking().AnyAsync(row => row.Id == query.BookId, cancellationToken))
                throw new ResourceNotFoundException("Vocabulary book was not found.");
            var items = new List<AudioTarget>();
            var count = 0;
            var skip = (long)(query.Page - 1) * query.Size;
            void Add(AudioReadSnapshot snapshot, AudioLocale locale)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (query.Locale != null && query.Locale != locale) return;
                var selection = AudioSelectionService.Select(snapshot, locale);
                if (query.Status != null && query.Status != selection.Status) return;
                if (count >= skip && items.Count < query.Size)
                    items.Add(new(snapshot.Word.WordId, snapshot.Meaning?.MeaningId, selection));
                count++;
            }

            // Scan bounded batches rather than materializing the whole catalogue.
            // Both phases and the total share the same deferred read transaction.
            const int batchSize = 200;
            var wordsQuery = context.Vocabularies.AsNoTracking().Where(word => query.BookId == null
                || context.VocabularyMeanings.Any(meaning => meaning.VocabularyId == word.Id && meaning.BookId == query.BookId));
            for (var offset = 0; ; offset += batchSize)
            {
                var words = await wordsQuery.OrderBy(word => word.Id).Skip(offset).Take(batchSize).ToListAsync(cancellationToken);
                if (words.Count == 0) break;
                var ids = words.Select(word => word.Id).ToList();
                var bindings = await ReadBindingsAsync(ids, [], cancellationToken);
                foreach (var word in words)
                {
                    var snapshot = new AudioReadSnapshot(new(word.Id, word.Word, word.PhoneticUk, word.PhoneticUs), null,
                        bindings.Where(binding => binding.WordId == word.Id && binding.MeaningId == null).ToList());
                    foreach (var locale in Enum.GetValues<AudioLocale>()) Add(snapshot, locale);
                }
            }
            var meaningsQuery = context.VocabularyMeanings.AsNoTracking().Where(meaning =>
                (query.BookId == null || meaning.BookId == query.BookId)
                && context.VocabularyAudioBindings.Any(binding => binding.MeaningId == meaning.Id));
            for (var offset = 0; ; offset += batchSize)
            {
                var meanings = await meaningsQuery.OrderBy(meaning => meaning.Id).Skip(offset).Take(batchSize).ToListAsync(cancellationToken);
                if (meanings.Count == 0) break;
                var ids = meanings.Select(meaning => meaning.VocabularyId).Distinct().ToList();
                var words = await context.Vocabularies.AsNoTracking().Where(word => ids.Contains(word.Id)).ToDictionaryAsync(word => word.Id, cancellationToken);
                var bindings = await ReadBindingsAsync(ids, meanings.Select(meaning => meaning.Id).ToList(), cancellationToken);
                foreach (var meaning in meanings)
                {
                    var word = words[meaning.VocabularyId];
                    var snapshot = new AudioReadSnapshot(new(word.Id, word.Word, word.PhoneticUk, word.PhoneticUs),
                        new(meaning.Id, meaning.PartOfSpeech, meaning.Meaning), bindings.Where(binding => binding.WordId == word.Id
                            && (binding.MeaningId == null || binding.MeaningId == meaning.Id)).ToList());
                    foreach (var locale in Enum.GetValues<AudioLocale>())
                        if (snapshot.Bindings.Any(binding => binding.MeaningId == meaning.Id && binding.Locale == locale))
                            Add(snapshot, locale);
                }
            }
            await transaction.CommitAsync(cancellationToken);
            return new(items, count, (int)(((long)count + query.Size - 1) / query.Size));
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private async Task<List<AudioBinding>> ReadBindingsAsync(List<string> wordIds, List<string> meaningIds, CancellationToken cancellationToken)
    {
        var rows = await (from binding in context.VocabularyAudioBindings.AsNoTracking()
                          join asset in context.AudioAssets.AsNoTracking() on binding.AssetId equals asset.Id
                          where wordIds.Contains(binding.WordId) && (binding.MeaningId == null || meaningIds.Contains(binding.MeaningId))
                          select new { binding, asset }).ToListAsync(cancellationToken);
        return rows.Select(row => ToBinding(row.binding, row.asset)).ToList();
    }

    private static AudioBinding ToBinding(VocabularyAudioBindingEntity binding, AudioAssetEntity asset)
        => new(binding.WordId, binding.MeaningId, binding.Locale,
            new AudioAsset(asset.Id, new(asset.Bucket, asset.ObjectKey), asset.Sha256,
                asset.ByteLength, asset.ContentType, asset.CreatedAtUnixMs, asset.FirstPublishedAtUnixMs), binding.SourceRevision);

    public Task<AudioReadSnapshot> GetSnapshotAsync(string wordId, string? meaningId,
        CancellationToken cancellationToken) => ReadSnapshotAsync(null, wordId, meaningId, cancellationToken);

    public Task<AudioReadSnapshot> GetPublicSnapshotAsync(string bookId, string wordId, string? meaningId,
        CancellationToken cancellationToken) => ReadSnapshotAsync(bookId, wordId, meaningId, cancellationToken);

    private async Task<AudioReadSnapshot> ReadSnapshotAsync(string? bookId, string wordId, string? meaningId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var transaction = ((SqliteConnection)context.Database.GetDbConnection()).BeginTransaction(deferred: true);
            await using var enlisted = await context.Database.UseTransactionAsync(transaction, cancellationToken);
            if (bookId != null)
            {
                var book = await context.VocabularyBooks.AsNoTracking()
                    .SingleOrDefaultAsync(row => row.Id == bookId, cancellationToken)
                    ?? throw new ResourceNotFoundException("Vocabulary book was not found.");
                if (!book.Status) throw new BusinessRuleException("Vocabulary book is disabled.");
                if (!await context.VocabularyMeanings.AsNoTracking().AnyAsync(row => row.BookId == bookId
                    && row.VocabularyId == wordId && (meaningId == null || row.Id == meaningId), cancellationToken))
                    throw new ResourceNotFoundException("Vocabulary audio target was not found.");
            }
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
                bindings.Select(row => ToBinding(row.binding, row.asset)).ToList());
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }
}
