using Lexarbor.Database.Entities;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed class MeaningPhoneticRepository(VocabularyDbContext context, IAudioInvalidationRepository invalidations,
    IEnrichmentTaskRepository tasks) : IMeaningPhoneticRepository
{
    public async Task<MeaningPhoneticSnapshot> ReadAsync(string bookId, string wordId, string meaningId,
        AudioLocale locale, CancellationToken token)
    {
        _ = locale.ToCode();
        var meaning = await context.VocabularyMeanings.AsNoTracking().SingleOrDefaultAsync(row => row.Id == meaningId, token)
            ?? throw new ResourceNotFoundException("Vocabulary meaning was not found.");
        if (meaning.BookId != bookId || meaning.VocabularyId != wordId)
            throw new ConflictException("Vocabulary meaning does not belong to the requested book and word.");
        var word = await context.Vocabularies.AsNoTracking().SingleAsync(row => row.Id == wordId, token);
        var revision = AudioSelectionService.ComputeRevision(AudioSelectionService.GetInput(
            new(word.Id, word.Word, word.PhoneticUk, word.PhoneticUs),
            new(meaning.Id, meaning.PartOfSpeech, meaning.Meaning, meaning.PhoneticUk, meaning.PhoneticUs), locale));
        var own = await context.VocabularyAudioBindings.AsNoTracking().Where(row => row.WordId == wordId
            && row.MeaningId == meaningId && row.Locale == locale).Select(row => row.AssetId).SingleOrDefaultAsync(token);
        var epoch = await context.MeaningPhoneticEpochs.AsNoTracking().Where(row => row.MeaningId == meaningId
            && row.WordId == wordId && row.Locale == locale).Select(row => (long?)row.Epoch).SingleOrDefaultAsync(token) ?? 0;
        return new(locale == AudioLocale.EnGb ? meaning.PhoneticUk : meaning.PhoneticUs, epoch, revision, own);
    }

    public async Task<MeaningPhoneticEditResult> UpdateInTransactionAsync(MeaningPhoneticEdit edit,
        MeaningPhoneticWorkVersions configuration, long now, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!UnitOfWork.OwnsWriteContext(context) || context.Database.CurrentTransaction == null)
            throw new InvalidOperationException("Specific phonetic edits require the caller's serialized writer transaction on the same context.");
        if (now < 0) throw new ArgumentOutOfRangeException(nameof(now));
        ArgumentNullException.ThrowIfNull(configuration);
        foreach (var version in new[] { configuration.Preparation, configuration.UkAudio, configuration.UsAudio })
            if (version != null && (string.IsNullOrWhiteSpace(version) || version.Length > 128 || version.Any(char.IsControl)))
                throw new ArgumentException("Enrichment configuration version is invalid.", nameof(configuration));
        var before = await ReadAsync(edit.BookId, edit.WordId, edit.MeaningId, edit.Locale, token);
        if (!edit.IsProvided) return new(false, before);
        if (before.Epoch != edit.Expected.Epoch || before.SourceRevision != edit.Expected.SourceRevision || before.OwnAssetId != edit.Expected.OwnAssetId)
            throw new ConflictException("Specific phonetic target changed. Read it again before editing.");
        var value = string.IsNullOrWhiteSpace(edit.Value) ? null : edit.Value.Trim();
        if (value?.Length > 512 || value?.Any(char.IsControl) == true)
            throw new DomainValidationException("Specific phonetic must be at most 512 characters without control characters.");
        if (AudioSelectionService.NormalizePhonetic(before.Value) == AudioSelectionService.NormalizePhonetic(value))
            return new(false, before);
        var epoch = checked(before.Epoch + 1);
        // Capture deletion authority while the exact old binding is still live.
        if (before.OwnAssetId != null && !await invalidations.AuthorizeInTransactionAsync(
                new(edit.WordId, edit.MeaningId, edit.Locale, before.OwnAssetId, epoch), now, token))
            throw new ConflictException("Specific audio could not be invalidated safely.");
        if (before.Epoch == 0)
        {
            var row = new MeaningPhoneticEpochEntity { MeaningId = edit.MeaningId, WordId = edit.WordId, Locale = edit.Locale, Epoch = epoch };
            context.MeaningPhoneticEpochs.Add(row);
            try { await context.SaveChangesAsync(token); }
            finally { context.Entry(row).State = EntityState.Detached; }
        }
        else if (await context.MeaningPhoneticEpochs.Where(row => row.MeaningId == edit.MeaningId && row.WordId == edit.WordId
                     && row.Locale == edit.Locale && row.Epoch == before.Epoch)
                     .ExecuteUpdateAsync(set => set.SetProperty(row => row.Epoch, epoch), token) != 1)
            throw new ConflictException("Specific phonetic target changed. Read it again before editing.");
        var meanings = context.VocabularyMeanings.Where(row => row.Id == edit.MeaningId && row.VocabularyId == edit.WordId && row.BookId == edit.BookId);
        var updatedAt = DateTimeOffset.FromUnixTimeMilliseconds(now);
        if (edit.Locale == AudioLocale.EnGb)
            await meanings.ExecuteUpdateAsync(set => set.SetProperty(row => row.PhoneticUk, value).SetProperty(row => row.UpdatedAt, updatedAt), token);
        else
            await meanings.ExecuteUpdateAsync(set => set.SetProperty(row => row.PhoneticUs, value).SetProperty(row => row.UpdatedAt, updatedAt), token);
        await context.VocabularyAudioBindings.Where(row => row.WordId == edit.WordId && row.MeaningId == edit.MeaningId
            && row.Locale == edit.Locale && row.AssetId == before.OwnAssetId).ExecuteDeleteAsync(token);
        // Both a saved pronunciation plan and an in-flight audio call become
        // obsolete. Empty specific values cancel both, never enqueue a default.
        foreach (var step in new[] { EnrichmentStep.Prepare, EnrichmentStep.Audio })
        {
            var version = step == EnrichmentStep.Prepare ? configuration.Preparation
                : edit.Locale == AudioLocale.EnGb ? configuration.UkAudio : configuration.UsAudio;
            // Missing configuration must never postpone the edit. Its stable
            // marker is replaced by the relevant durable version when ready.
            await tasks.InvalidateInTransactionAsync(new(edit.WordId, edit.MeaningId, edit.Locale, step),
                version ?? "configuration.missing", now, token);
        }
        return new(true, await ReadAsync(edit.BookId, edit.WordId, edit.MeaningId, edit.Locale, token));
    }
}
