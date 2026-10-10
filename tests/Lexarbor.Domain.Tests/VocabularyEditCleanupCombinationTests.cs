using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Lexarbor.Domain.Tests.VocabularyCleanupConcurrencyTests;
using static Lexarbor.Domain.Tests.VocabularyCleanupTests;

namespace Lexarbor.Domain.Tests;

/// <summary>
/// Combination coverage for the W/M full-replacement edits and the C cleanup
/// commands on one shared dataset and transaction path, per the parent model in
/// #96 ("end-to-end scenario 6": a meaning edit followed by cleanup deletes the
/// current matching row; cleanup first makes the later edit 404). Every test
/// asserts the terminal state directly — no revival of deleted IDs, no partial
/// writes, disabled-book references keep shared words, historical orphans stay
/// untouched, and `PRAGMA foreign_key_check` stays empty.
/// </summary>
public class VocabularyEditCleanupCombinationTests
{
    private static VocabularyMeaningEditService Meanings(VocabularyDbContext db) =>
        new(new VocabularyRepository(db), new VocabularyBookRepository(db), new VocabularyMeaningRepository(db),
            new VocabularyMeaningUnitRepository(db), new UnitOfWork(db));
    private static VocabularyWordEditService Words(VocabularyDbContext db) =>
        new(new VocabularyRepository(db), new VocabularyWordEditRepository(db), new VocabularyMeaningRepository(db),
            new VocabularyMeaningUnitRepository(db), new UnitOfWork(db));

    [Theory]
    [InlineData("removeMeaning", true, false)]
    [InlineData("removeWords", false, true)]
    [InlineData("clear", false, true)]
    [InlineData("delete", false, true)]
    public async Task MeaningEditThenCleanup_DeletesCurrentEditedRow_AndKeepsOtherBooksAndOrphans(string action, bool keepsSiblingMeaning, bool removesExclusiveWord)
    {
        await WithFileAsync(async options =>
        {
            await using var db = new VocabularyDbContext(options);
            var historicalBefore = JsonSerializer.Serialize(await db.Vocabularies.AsNoTracking()
                .SingleAsync(v => v.Id == "historical", TestContext.Current.CancellationToken));
            // M first: a full replacement of one of A's meanings on the shared word.
            await Meanings(db).ReplaceAsync("A", "shared", "a1", " V. ", "edited-meaning", " edited example ", TestContext.Current.CancellationToken);
            var edited = await db.VocabularyMeanings.AsNoTracking().SingleAsync(m => m.Id == "a1", TestContext.Current.CancellationToken);
            Assert.Equal("v.", edited.PartOfSpeech);
            Assert.Equal("edited-meaning", edited.Meaning);
            Assert.Equal("edited example", edited.Example);

            // C second: the preview resolves R against the edited row, then the
            // commit deletes that current row inside its own transaction.
            var preview = await Service(db).PreviewAsync("A", Selection(action), TestContext.Current.CancellationToken);
            Assert.Equal(action == "removeMeaning" ? 1 : 2, preview.AffectedWordCount);
            Assert.Equal(action == "removeMeaning" ? 1 : 3, preview.MeaningCount);
            Assert.Equal(action == "removeMeaning" ? 0 : 1, preview.OrphanWordCount);
            await Service(db).CommitAsync("A", Selection(action), TestContext.Current.CancellationToken);

            await using var verify = new VocabularyDbContext(options);
            // The edited row is gone; the id is never revived and nothing is half-written.
            Assert.False(await verify.VocabularyMeanings.AnyAsync(m => m.Id == "a1", TestContext.Current.CancellationToken));
            Assert.Equal(keepsSiblingMeaning, await verify.VocabularyMeanings.AnyAsync(m => m.Id == "a2", TestContext.Current.CancellationToken));
            // The disabled book B keeps its own rows byte-for-byte, so the shared
            // word survives; the exclusive A word loses its only meaning and goes.
            Assert.True(await verify.Vocabularies.AnyAsync(v => v.Id == "shared", TestContext.Current.CancellationToken));
            Assert.Equal(!removesExclusiveWord, await verify.Vocabularies.AnyAsync(v => v.Id == "only-a", TestContext.Current.CancellationToken));
            Assert.True(await verify.Vocabularies.AnyAsync(v => v.Id == "only-b", TestContext.Current.CancellationToken));
            Assert.True(await verify.VocabularyMeanings.AnyAsync(m => m.Id == "b1", TestContext.Current.CancellationToken));
            Assert.True(await verify.VocabularyMeanings.AnyAsync(m => m.Id == "b-only", TestContext.Current.CancellationToken));
            Assert.Equal(historicalBefore, JsonSerializer.Serialize(await verify.Vocabularies.AsNoTracking()
                .SingleAsync(v => v.Id == "historical", TestContext.Current.CancellationToken)));
            Assert.Equal(action != "delete", await verify.VocabularyBooks.AnyAsync(b => b.Id == "A", TestContext.Current.CancellationToken));
            await ForeignKeysAsync(verify);
        });
    }

    [Theory]
    [InlineData("removeMeaning")]
    [InlineData("removeWords")]
    [InlineData("clear")]
    [InlineData("delete")]
    public async Task CleanupThenMeaningEdit_Returns404_KeepsOwnershipRulesAndWritesNothing(string action)
    {
        await WithFileAsync(async options =>
        {
            await using var db = new VocabularyDbContext(options);
            await Service(db).CommitAsync("A", Selection(action), TestContext.Current.CancellationToken);
            var after = await StateAsync(db);

            // The removed meaning is gone; editing it by its old id returns 404
            // with no revival and no partial write. Whole-scope actions also
            // removed the exclusive word together with its last meaning.
            await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
                Meanings(db).ReplaceAsync("A", "shared", "a1", null, "revived", null, TestContext.Current.CancellationToken));
            if (action != "removeMeaning")
                await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
                    Meanings(db).ReplaceAsync("A", "only-a", "a-only", null, "revived", null, TestContext.Current.CancellationToken));

            // The surviving state keeps its existing 404/409 shape: a meaning
            // that exists but belongs to another book is still an ownership 409
            // (the book itself only disappears under delete, which is 404 first).
            if (action != "delete")
                await Assert.ThrowsAsync<ConflictException>(() =>
                    Meanings(db).ReplaceAsync("A", "shared", "b1", null, "revived", null, TestContext.Current.CancellationToken));
            else
                await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
                    Meanings(db).ReplaceAsync("A", "shared", "b1", null, "revived", null, TestContext.Current.CancellationToken));

            Assert.Equal(after, await StateAsync(db));
            await ForeignKeysAsync(db);
        });
    }

    [Fact]
    public async Task WordEditThenCleanup_UsesCurrentCollections_AndLastReferenceRemovalMakesLaterEdit404()
    {
        await WithFileAsync(async options =>
        {
            await using var db = new VocabularyDbContext(options);
            // W first: a full replacement of the shared fields.
            await Words(db).ReplaceAsync("shared", " Renamed ", "new-uk", null, TestContext.Current.CancellationToken);

            // C second: the counts and the deletion still follow the current
            // collections — renaming a shared field never changed membership.
            var preview = await Service(db).PreviewAsync("A", Selection("removeWords"), TestContext.Current.CancellationToken);
            Assert.Equal((2, 3, 1), (preview.AffectedWordCount, preview.MeaningCount, preview.OrphanWordCount));
            var result = await Service(db).CommitAsync("A", Selection("removeWords"), TestContext.Current.CancellationToken);
            Assert.Equal((2, 3, 1, false), (result.AffectedWordCount, result.DeletedMeaningCount, result.DeletedWordCount, result.DeletedBook));

            await using var verify = new VocabularyDbContext(options);
            // The word survives through disabled B with the edited shared fields;
            // the edit's submitted casing is the new display spelling.
            var shared = await verify.Vocabularies.AsNoTracking().SingleAsync(v => v.Id == "shared", TestContext.Current.CancellationToken);
            Assert.Equal("Renamed", shared.Word);
            Assert.Equal("new-uk", shared.PhoneticUk);
            Assert.Null(shared.PhoneticUs);
            Assert.True(await verify.Vocabularies.AnyAsync(v => v.Id == "historical", TestContext.Current.CancellationToken));
            // A later edit of the word whose last reference the cleanup removed is 404.
            await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
                Words(db).ReplaceAsync("only-a", "again", null, null, TestContext.Current.CancellationToken));
            await ForeignKeysAsync(verify);
        });
    }

    [Theory]
    [InlineData("removeWords")]
    [InlineData("clear")]
    [InlineData("delete")]
    public async Task CleanupThenWordEdit_RemovedWordsAre404_SharedWordStaysEditable(string action)
    {
        await WithFileAsync(async options =>
        {
            await using var db = new VocabularyDbContext(options);
            await Service(db).CommitAsync("A", Selection(action), TestContext.Current.CancellationToken);
            var after = await StateAsync(db);

            await Assert.ThrowsAsync<ResourceNotFoundException>(() =>
                Words(db).ReplaceAsync("only-a", "revived", null, null, TestContext.Current.CancellationToken));
            Assert.Equal(after, await StateAsync(db));

            // The word still referenced by disabled B survives and stays editable.
            await Words(db).ReplaceAsync("shared", "still-shared", null, null, TestContext.Current.CancellationToken);
            await using var verify = new VocabularyDbContext(options);
            Assert.Equal("still-shared", (await verify.Vocabularies.AsNoTracking()
                .SingleAsync(v => v.Id == "shared", TestContext.Current.CancellationToken)).Word);
            await ForeignKeysAsync(verify);
        });
    }

    [Theory]
    [InlineData(false, "meaning")]
    [InlineData(true, "meaning")]
    [InlineData(false, "word")]
    [InlineData(true, "word")]
    public async Task EditAndCleanup_OnIndependentContexts_SerializeOnCurrentDataWithConsistentEndState(bool cleanupFirst, string editKind)
    {
        await WithFileAsync(async options =>
        {
            await using var first = new VocabularyDbContext(options);
            await using var second = new VocabularyDbContext(options);
            // A stale tracked snapshot on the editing context predates the other
            // transaction, like a form opened before a concurrent cleanup.
            if (editKind == "meaning") await second.VocabularyMeanings.FindAsync(["a1"], TestContext.Current.CancellationToken);
            else await second.Vocabularies.FindAsync(["only-a"], TestContext.Current.CancellationToken);
            var selection = editKind == "meaning" ? Selection("removeMeaning") : Selection("removeWords");

            Func<Task> cleanup = () => Service(cleanupFirst ? first : second).CommitAsync("A", selection, TestContext.Current.CancellationToken);
            Func<Task> edit = async () =>
            {
                var db = cleanupFirst ? second : first;
                Func<Task> operation = () => editKind == "meaning"
                    ? Meanings(db).ReplaceAsync("A", "shared", "a1", null, "edited", "example", TestContext.Current.CancellationToken)
                    : Words(db).ReplaceAsync("only-a", "renamed", "uk", null, TestContext.Current.CancellationToken);
                // Edit first commits before the cleanup; cleanup first removes
                // the target, so the later edit is refused with 404.
                if (cleanupFirst) await Assert.ThrowsAsync<ResourceNotFoundException>(operation);
                else await operation();
            };
            await OrderedAsync(first, cleanupFirst ? cleanup : edit, cleanupFirst ? edit : cleanup);

            await using var verify = new VocabularyDbContext(options);
            // Either order ends with the same terminal state: the edited target
            // is gone (the cleanup deleted the current edited row, or the edit
            // never landed because its target had already been removed).
            if (editKind == "meaning")
            {
                Assert.False(await verify.VocabularyMeanings.AnyAsync(m => m.Id == "a1", TestContext.Current.CancellationToken));
                Assert.True(await verify.VocabularyMeanings.AnyAsync(m => m.Id == "a2", TestContext.Current.CancellationToken));
            }
            else
            {
                Assert.False(await verify.Vocabularies.AnyAsync(v => v.Id == "only-a", TestContext.Current.CancellationToken));
                Assert.False(await verify.VocabularyMeanings.AnyAsync(m => m.Id == "a-only", TestContext.Current.CancellationToken));
            }
            // No other book lost anything and no dangling reference remains.
            Assert.True(await verify.Vocabularies.AnyAsync(v => v.Id == "shared", TestContext.Current.CancellationToken));
            Assert.True(await verify.VocabularyMeanings.AnyAsync(m => m.Id == "b1", TestContext.Current.CancellationToken));
            Assert.True(await verify.Vocabularies.AnyAsync(v => v.Id == "historical", TestContext.Current.CancellationToken));
            await ForeignKeysAsync(verify);
        });
    }

    /// <summary>
    /// Holds the first operation inside its write transaction until released, so
    /// the second one demonstrably waits on the shared write lock and only runs
    /// against the committed state (mirrors the barrier in the concurrency tests).
    /// </summary>
    private static async Task OrderedAsync(VocabularyDbContext first, Func<Task> firstOperation, Func<Task> secondOperation)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var one = new UnitOfWork(first).ExecuteInTransactionAsync(async () =>
        { await firstOperation(); entered.SetResult(); await release.Task; return 0; });
        Task? two = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            two = secondOperation(); Assert.False(two.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await one; if (two != null) await two;
    }
}
