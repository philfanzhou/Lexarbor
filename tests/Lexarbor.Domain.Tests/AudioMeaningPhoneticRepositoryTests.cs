using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class AudioMeaningPhoneticRepositoryTests
{
    private static CancellationToken Token => AudioTestDatabase.Token;

    [Theory]
    [InlineData(AudioLocale.EnGb)]
    [InlineData(AudioLocale.EnUs)]
    public async Task ReadAndUpload_UseSameSpecificInputAndRejectChangesBeforePublication(AudioLocale locale)
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open();
        await db.Database.MigrateAsync(Token); await AudioTestDatabase.SeedAsync(db);
        var meaning = await db.VocabularyMeanings.SingleAsync(row => row.Id == "meaning", Token);
        meaning.PhoneticUk = " /uk/ "; meaning.PhoneticUs = "[us]";
        db.VocabularyMeanings.Add(new() { Id = "second", VocabularyId = "word", BookId = "book", Meaning = "another fruit" });
        db.AudioAssets.AddRange(AudioTestDatabase.Asset("default"), AudioTestDatabase.Asset("own"));
        db.VocabularyAudioBindings.AddRange(
            AudioTestDatabase.Binding("default", locale, null, AudioTestDatabase.Revision(locale)),
            AudioTestDatabase.Binding("own", locale, "meaning", AudioTestDatabase.Revision(locale, true)));
        await db.SaveChangesAsync(Token);
        var reads = new AudioReadRepository(db); var uploads = new AudioUploadRepository(db, new ManualClock());
        var before = await reads.GetSnapshotAsync("word", "meaning", Token);
        var selected = AudioSelectionService.Select(before, locale);
        Assert.Equal(locale == AudioLocale.EnGb ? " /uk/ " : "[us]", selected.Input.Phonetic);
        Assert.Equal("own", selected.OwnBinding!.Asset.Id);
        Assert.Equal("own", selected.EffectiveBinding!.Asset.Id);
        var request = Request(locale, selected.SourceRevision, "default");
        Assert.Equal(AudioReservationDisposition.Conflict, (await uploads.ReserveAsync(request, "owner", 1, 100, Token)).Disposition);
        request = request with { ExpectedAssetId = "own" };
        var reservation = await uploads.ReserveAsync(request, "owner", 1, 100, Token);
        Assert.Equal(AudioReservationDisposition.Created, reservation.Disposition);
        var upload = Assert.IsType<AudioStoredUpload>(reservation.Upload);
        Assert.True(await uploads.BeginTransferAsync(request.IdempotencyKey, upload.Operation.UploadLease, 2, Token));
        var published = Assert.IsType<AudioUploadOperation>(await uploads.CompleteAsync(request.IdempotencyKey, upload.Operation.UploadLease, 3, Token));
        Assert.Equal(200, published.Receipt!.StatusCode);
        Assert.Equal(selected.SourceRevision, published.Receipt.SourceRevision);
        var otherLocale = locale == AudioLocale.EnGb ? AudioLocale.EnUs : AudioLocale.EnGb;
        var otherRevision = AudioSelectionService.Select(before, otherLocale).SourceRevision;
        var secondRevision = AudioSelectionService.Select(await reads.GetSnapshotAsync("word", "second", Token), locale).SourceRevision;
        var defaultRevision = AudioSelectionService.Select(await reads.GetSnapshotAsync("word", null, Token), locale).SourceRevision;
        var replacing = Request(locale, selected.SourceRevision, upload.Asset.Id);
        var pending = Assert.IsType<AudioStoredUpload>((await uploads.ReserveAsync(replacing, "owner", 4, 100, Token)).Upload);
        Assert.True(await uploads.BeginTransferAsync(replacing.IdempotencyKey, pending.Operation.UploadLease, 5, Token));
        if (locale == AudioLocale.EnGb)
            await db.VocabularyMeanings.Where(row => row.Id == "meaning").ExecuteUpdateAsync(set => set.SetProperty(row => row.PhoneticUk, "changed-specific"), Token);
        else
            await db.VocabularyMeanings.Where(row => row.Id == "meaning").ExecuteUpdateAsync(set => set.SetProperty(row => row.PhoneticUs, "changed-specific"), Token);
        var after = await reads.GetSnapshotAsync("word", "meaning", Token);
        var changed = AudioSelectionService.Select(after, locale);
        Assert.Equal("changed-specific", changed.Input.Phonetic);
        Assert.NotEqual(selected.SourceRevision, changed.SourceRevision);
        Assert.Equal(otherRevision, AudioSelectionService.Select(after, otherLocale).SourceRevision);
        Assert.Equal(secondRevision, AudioSelectionService.Select(await reads.GetSnapshotAsync("word", "second", Token), locale).SourceRevision);
        Assert.Equal(defaultRevision, AudioSelectionService.Select(await reads.GetSnapshotAsync("word", null, Token), locale).SourceRevision);
        Assert.Equal(changed.SourceRevision, AudioSelectionService.Select(await reads.GetPublicSnapshotAsync("book", "word", "meaning", Token), locale).SourceRevision);
        var page = await reads.SearchTargetsAsync(new(null, locale, null, 1, 20), Token);
        Assert.Equal(changed.SourceRevision, page.Items.Single(item => item.MeaningId == "meaning").Selection.SourceRevision);
        var rejected = Assert.IsType<AudioUploadOperation>(await uploads.CompleteAsync(replacing.IdempotencyKey, pending.Operation.UploadLease, 6, Token));
        Assert.Equal(409, rejected.Receipt!.StatusCode);
        Assert.Equal(upload.Asset.Id, changed.OwnBinding!.Asset.Id);
        Assert.Null((await uploads.GetAsync(replacing.IdempotencyKey, Token))!.Asset.FirstPublishedAtUnixMs);
        Assert.Equal(AudioReservationDisposition.Conflict,
            (await uploads.ReserveAsync(replacing with { IdempotencyKey = Guid.NewGuid().ToString() }, "owner", 7, 100, Token)).Disposition);
    }

    [Fact]
    public async Task InheritedTarget_UsesOwnConditionEvenWhenItPlaysDefault()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open();
        await db.Database.MigrateAsync(Token); await AudioTestDatabase.SeedAsync(db);
        db.AudioAssets.AddRange(AudioTestDatabase.Asset("default"), AudioTestDatabase.Asset("legacy"));
        db.VocabularyAudioBindings.AddRange(
            AudioTestDatabase.Binding("default", AudioLocale.EnGb, null, AudioTestDatabase.Revision(AudioLocale.EnGb)),
            AudioTestDatabase.Binding("legacy", AudioLocale.EnGb, "meaning", AudioTestDatabase.Revision(AudioLocale.EnGb, true)));
        await db.SaveChangesAsync(Token);
        var reads = new AudioReadRepository(db); var uploads = new AudioUploadRepository(db, new ManualClock());
        var selected = AudioSelectionService.Select(await reads.GetSnapshotAsync("word", "meaning", Token), AudioLocale.EnGb);
        Assert.Equal("legacy", selected.OwnBinding!.Asset.Id); Assert.Equal("default", selected.EffectiveBinding!.Asset.Id);
        var request = Request(AudioLocale.EnGb, selected.SourceRevision, "default");
        Assert.Equal(AudioReservationDisposition.Conflict, (await uploads.ReserveAsync(request, "owner", 1, 100, Token)).Disposition);
        var reserved = await uploads.ReserveAsync(request with { ExpectedAssetId = "legacy" }, "owner", 1, 100, Token);
        Assert.Equal(AudioReservationDisposition.Created, reserved.Disposition);
        Assert.Equal("legacy", reserved.Upload!.Operation.ExpectedAssetId);
    }

    [Fact]
    public async Task MeaningMappingAndExhaustiveDistractorQuery_PreserveBothSpecificPhonetics()
    {
        using var file = new AudioTestDatabase(); await using var db = file.Open();
        await db.Database.MigrateAsync(Token); await AudioTestDatabase.SeedAsync(db);
        var meanings = new VocabularyMeaningRepository(db);
        var meaning = Assert.IsType<VocabularyMeaningModel>(await meanings.GetByIdAsync("other-meaning"));
        meaning.PhoneticUk = "other-uk"; meaning.PhoneticUs = "other-us";
        var phonetics = new MeaningPhoneticRepository(db, new AudioInvalidationRepository(db), new EnrichmentTaskRepository(db));
        var edits = new List<MeaningPhoneticEdit>();
        foreach (var locale in new[] { AudioLocale.EnGb, AudioLocale.EnUs })
            edits.Add(new("book", "other", "other-meaning", locale, true, locale == AudioLocale.EnGb ? "other-uk" : "other-us",
                await phonetics.ReadAsync("book", "other", "other-meaning", locale, Token)));
        await new MeaningPhoneticEditService(phonetics, new UnitOfWork(db)).ApplyAsync(edits, new("config", "config", "config"), 1, Token);
        db.ChangeTracker.Clear();
        // There is only one eligible word, so requesting two forces the
        // exhaustive SQL projection rather than accepting the sampled window.
        var distractor = Assert.Single(await meanings.GetRandomDistinctVocabularyExceptAsync("book", "word", "fruit", 2));
        Assert.Equal("other-meaning", distractor.Id);
        Assert.Equal("other-uk", distractor.PhoneticUk); Assert.Equal("other-us", distractor.PhoneticUs);
        var catalog = Assert.Single(await new MeaningSubmissionRepository(db)
            .GetCatalogMeaningDistractorsAsync("apple", "fruit", 2, Token));
        Assert.Equal("other-meaning", catalog.Id);
        Assert.Equal("other-uk", catalog.PhoneticUk); Assert.Equal("other-us", catalog.PhoneticUs);
        await new VocabularyMeaningEditService(new VocabularyRepository(db), new VocabularyBookRepository(db), meanings,
            new VocabularyMeaningUnitRepository(db), new UnitOfWork(db)).ReplaceAsync("book", "other", "other-meaning", null, "other", "example", Token);
        var reread = Assert.IsType<VocabularyMeaningModel>(await meanings.GetByIdAsync("other-meaning"));
        Assert.Equal("other-uk", reread.PhoneticUk); Assert.Equal("other-us", reread.PhoneticUs);
    }

    private static AudioUploadRequest Request(AudioLocale locale, string revision, string? expected)
        => new(Guid.NewGuid().ToString(), AudioTestDatabase.Hash, "word", "meaning", locale, revision, expected,
            AudioTestDatabase.Hash, 128, "test-audio");

    private sealed class ManualClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(0);
    }
}
