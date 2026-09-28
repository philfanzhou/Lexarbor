using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Lexarbor.Domain.Tests;

/// <summary>
/// The batch import semantics fixed by ADR-005. The scenario numbers refer to
/// the end-to-end scenarios in that decision's issue (#72).
/// </summary>
public class VocabularyBatchImportTests : TestBase
{
    private readonly VocabularyDomainService _service;

    public VocabularyBatchImportTests()
    {
        _service = CreateService(_meaningRepository);
    }

    // Scenario 1.
    [Fact]
    public async Task ImportBatchAsync_EmptyBook_CreatesEveryEntry()
    {
        var book = await CreateBookAsync();

        var result = await _service.ImportBatchAsync(
            book.Id,
            [Entry("apple", "苹果"), Entry("banana", "香蕉"), Entry("cherry", "樱桃")]);

        Assert.Equal(new VocabularyBatchImportResult(3, 3, 0), result);
        Assert.Equal(3, await _dbContext.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, await CountMeaningsAsync(book.Id));
    }

    // Scenario 2.
    [Fact]
    public async Task ImportBatchAsync_ResubmittedBatch_CreatesNothing()
    {
        var book = await CreateBookAsync();
        await _service.ImportBatchAsync(
            book.Id,
            [Entry("apple", "苹果"), Entry("banana", "香蕉"), Entry("cherry", "樱桃")]);

        var result = await _service.ImportBatchAsync(
            book.Id,
            [Entry("apple", "苹果"), Entry("banana", "香蕉"), Entry("cherry", "樱桃")]);

        Assert.Equal(new VocabularyBatchImportResult(3, 0, 3), result);
        Assert.Equal(3, await _dbContext.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, await CountMeaningsAsync(book.Id));
    }

    // Scenario 3: a later entry matches the meaning an earlier one in the same
    // batch has just written.
    [Fact]
    public async Task ImportBatchAsync_EquivalentEntriesInOneBatch_StoreOneMeaning()
    {
        var book = await CreateBookAsync();

        var result = await _service.ImportBatchAsync(
            book.Id,
            [
                Entry("Apple", "苹果", partOfSpeech: "n."),
                Entry(" apple ", "苹果", partOfSpeech: "N."),
                Entry("APPLE", " 苹果 ", partOfSpeech: " n. ")
            ]);

        Assert.Equal(new VocabularyBatchImportResult(3, 1, 2), result);
        var word = Assert.Single(await _dbContext.Vocabularies.ToListAsync(TestContext.Current.CancellationToken));
        // One row for all three spellings, and the first entry's casing is the
        // stored display spelling.
        Assert.Equal("Apple", word.Word);
        Assert.Equal(1, await CountMeaningsAsync(book.Id));
    }

    [Fact]
    public async Task ImportBatchAsync_LaterEntries_OverwritePhoneticsAndExampleButBlanksDoNot()
    {
        var book = await CreateBookAsync();

        await _service.ImportBatchAsync(
            book.Id,
            [
                Entry("apple", "苹果", phoneticUk: "/uk-1/", phoneticUs: "/us-1/", example: "first"),
                Entry("apple", "苹果", phoneticUk: "/uk-2/", phoneticUs: "  ", example: "second"),
                Entry("apple", "苹果", phoneticUk: "", phoneticUs: null, example: " \t ")
            ]);

        var word = Assert.Single(await _dbContext.Vocabularies.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal("/uk-2/", word.PhoneticUk);
        Assert.Equal("/us-1/", word.PhoneticUs);
        var meaning = Assert.Single(await _dbContext.VocabularyMeanings.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal("second", meaning.Example);
    }

    [Fact]
    public async Task ImportBatchAsync_BlankOptionalFields_DoNotClearStoredValues()
    {
        var book = await CreateBookAsync();
        await _service.ImportBatchAsync(
            book.Id,
            [Entry("apple", "苹果", phoneticUk: "/uk/", phoneticUs: "/us/", example: "kept")]);

        var result = await _service.ImportBatchAsync(
            book.Id,
            [Entry("apple", "苹果", phoneticUk: " ", phoneticUs: "", example: "")]);

        Assert.Equal(new VocabularyBatchImportResult(1, 0, 1), result);
        var word = Assert.Single(await _dbContext.Vocabularies.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal("/uk/", word.PhoneticUk);
        Assert.Equal("/us/", word.PhoneticUs);
        var meaning = Assert.Single(await _dbContext.VocabularyMeanings.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal("kept", meaning.Example);
    }

    [Fact]
    public async Task ImportBatchAsync_SharedWord_ReusesItAcrossBooks()
    {
        var firstBook = await CreateBookAsync();
        var secondBook = await CreateBookAsync();
        await _service.ImportBatchAsync(firstBook.Id, [Entry("apple", "苹果")]);

        var result = await _service.ImportBatchAsync(secondBook.Id, [Entry("apple", "苹果")]);

        // The meaning belongs to the book, so it is new; the word row is shared.
        Assert.Equal(new VocabularyBatchImportResult(1, 1, 0), result);
        Assert.Equal(1, await _dbContext.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await CountMeaningsAsync(secondBook.Id));
    }

    [Fact]
    public async Task ImportBatchAsync_EntriesWithUnits_SameMeaningInTwoUnits()
    {
        var book = await CreateBookAsync();
        var unit2 = await CreateUnitAsync(book.Id, 2);
        var unit6 = await CreateUnitAsync(book.Id, 6);

        var first = await _service.ImportBatchAsync(
            book.Id,
            [Entry("apple", "苹果", unitId: unit2.Id), Entry("apple", "苹果", unitId: unit6.Id)]);

        // The second entry reuses the meaning the first one wrote; each entry
        // still assigns it to its own unit, and the counts stay about meanings.
        Assert.Equal(new VocabularyBatchImportResult(2, 1, 1), first);
        Assert.Equal(1, await _dbContext.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await CountMeaningsAsync(book.Id));
        Assert.Equal(2, await _dbContext.VocabularyMeaningUnits.CountAsync(TestContext.Current.CancellationToken));

        // Resubmitting the batch writes nothing: no meaning, no assignment.
        var second = await _service.ImportBatchAsync(
            book.Id,
            [Entry("apple", "苹果", unitId: unit2.Id), Entry("apple", "苹果", unitId: unit6.Id)]);
        Assert.Equal(new VocabularyBatchImportResult(2, 0, 2), second);
        Assert.Equal(1, await CountMeaningsAsync(book.Id));
        Assert.Equal(2, await _dbContext.VocabularyMeaningUnits.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ImportBatchAsync_ReusedStoredMeaning_GetsTheEntryUnitAssignment()
    {
        var book = await CreateBookAsync();
        var unit = await CreateUnitAsync(book.Id, 1);
        await _service.ImportBatchAsync(book.Id, [Entry("apple", "苹果")]);

        // The meaning already exists, so the entry counts as reused; the
        // assignment to the entry's unit is still written.
        var result = await _service.ImportBatchAsync(
            book.Id,
            [Entry("apple", "苹果", unitId: unit.Id)]);

        Assert.Equal(new VocabularyBatchImportResult(1, 0, 1), result);
        Assert.Equal(1, await CountMeaningsAsync(book.Id));
        var membership = Assert.Single(
            await _dbContext.VocabularyMeaningUnits.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(unit.Id, membership.UnitId);
    }

    [Fact]
    public async Task ImportBatchAsync_UnknownOrCrossBookUnitId_ReportsEveryEntryAndWritesNothing()
    {
        var book = await CreateBookAsync();
        var otherBook = await CreateBookAsync();
        var otherBookUnit = await CreateUnitAsync(otherBook.Id, 1);

        var exception = await Assert.ThrowsAsync<BatchEntryValidationException>(() => _service.ImportBatchAsync(
            book.Id,
            [
                Entry("apple", "苹果"),
                Entry("banana", "香蕉", unitId: $"missing-{Guid.NewGuid():N}"),
                Entry("cherry", "樱桃", unitId: otherBookUnit.Id),
                Entry("durian", "榴莲", unitId: " ")
            ]));

        Assert.Equal("2 entries are invalid.", exception.Message);
        Assert.Equal(
            [(1, "Unit was not found in the requested vocabulary book."),
             (2, "Unit was not found in the requested vocabulary book.")],
            exception.EntryErrors);
        Assert.Equal(0, await _dbContext.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await CountMeaningsAsync(book.Id));
        Assert.Equal(0, await _dbContext.VocabularyMeaningUnits.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ImportBatchAsync_UnitRemovedBehindTheCheck_RollsBackThroughForeignKeys()
    {
        var book = await CreateBookAsync();
        var unit = await CreateUnitAsync(book.Id, 1);
        // Remove the unit the way an outside writer would, then let the
        // validation read still report it: this is the window between the
        // batch's unit check and its membership write.
        _dbContext.VocabularyBookUnits.Remove(
            await _dbContext.VocabularyBookUnits.SingleAsync(
                stored => stored.Id == unit.Id, TestContext.Current.CancellationToken));
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var service = new VocabularyDomainService(
            _vocabularyRepository,
            _bookRepository,
            _meaningRepository,
            new UnitReportingDeletedUnit(_bookUnitRepository, unit.Id),
            _meaningUnitRepository,
            _unitOfWork);

        await Assert.ThrowsAsync<ConflictException>(() => service.ImportBatchAsync(
            book.Id,
            [Entry("apple", "苹果", unitId: unit.Id)]));

        Assert.Equal(0, await _dbContext.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await CountMeaningsAsync(book.Id));
        Assert.Equal(0, await _dbContext.VocabularyMeaningUnits.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ImportBatchAsync_ConcurrentIdenticalBatches_LeaveOneMeaningAndAssignment()
    {
        var book = await CreateBookAsync();
        var unit = await CreateUnitAsync(book.Id, 1);
        var entries = new List<(VocabularyModel, VocabularyMeaningModel, string?)>
        {
            Entry("apple", "苹果", unitId: unit.Id)
        };

        // The process-wide write lock serializes the two batches, so one
        // creates the meaning and its assignment and the other matches both.
        var results = await Task.WhenAll(
            _service.ImportBatchAsync(book.Id, entries),
            _service.ImportBatchAsync(book.Id, entries));

        Assert.Equal(
            [new VocabularyBatchImportResult(1, 0, 1), new VocabularyBatchImportResult(1, 1, 0)],
            results.OrderBy(result => result.Created).ToList());
        Assert.Equal(1, await CountMeaningsAsync(book.Id));
        Assert.Equal(1, await _dbContext.VocabularyMeaningUnits.CountAsync(TestContext.Current.CancellationToken));
    }

    // Scenario 7: the third entry hits the equivalent-meaning unique index,
    // and the two entries already written in the same transaction go with it.
    [Fact]
    public async Task ImportBatchAsync_ConstraintViolationMidBatch_RollsBackEveryEntry()
    {
        var book = await CreateBookAsync();
        var service = CreateService(new NoEquivalentMeaningRepository(_meaningRepository));

        await Assert.ThrowsAsync<ConflictException>(() => service.ImportBatchAsync(
            book.Id,
            [Entry("apple", "苹果"), Entry("banana", "香蕉"), Entry("apple", "苹果")]));

        Assert.Equal(0, await _dbContext.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await CountMeaningsAsync(book.Id));
    }

    [Fact]
    public async Task ImportBatchAsync_UnknownBook_ThrowsNotFoundAndWritesNothing()
    {
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => _service.ImportBatchAsync(
            $"missing-{Guid.NewGuid():N}",
            [Entry("apple", "苹果")]));

        Assert.Equal(0, await _dbContext.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
    }

    // Scenario 6. Rejected even when every entry would only match an existing
    // meaning: the check is on the book, before any entry is looked at.
    [Fact]
    public async Task ImportBatchAsync_DisabledBook_ThrowsBusinessRuleAndWritesNothing()
    {
        var book = await CreateBookAsync(status: false);

        var exception = await Assert.ThrowsAsync<BusinessRuleException>(() => _service.ImportBatchAsync(
            book.Id,
            [Entry("apple", "苹果")]));

        Assert.Equal("New meanings cannot be added to a disabled vocabulary book.", exception.Message);
        Assert.Equal(0, await _dbContext.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ImportBatchAsync_InvalidBatch_IsRejectedBeforeAnyWrite()
    {
        var book = await CreateBookAsync();

        await Assert.ThrowsAsync<DomainValidationException>(() => _service.ImportBatchAsync(" ", [Entry("apple", "苹果")]));
        await Assert.ThrowsAsync<DomainValidationException>(() => _service.ImportBatchAsync(book.Id, []));
        await Assert.ThrowsAsync<DomainValidationException>(() => _service.ImportBatchAsync(
            book.Id,
            Enumerable.Range(0, VocabularyDomainService.MaxBatchEntries + 1)
                .Select(index => Entry($"word{index}", "meaning"))
                .ToList()));
        // A valid entry ahead of the invalid one is not written.
        await Assert.ThrowsAsync<DomainValidationException>(() => _service.ImportBatchAsync(
            book.Id,
            [Entry("apple", "苹果"), Entry("banana", " ")]));

        Assert.Equal(0, await _dbContext.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("apple", "苹果", null)]
    [InlineData(" ", "苹果", "Word is required.")]
    [InlineData("apple", "", "Meaning is required.")]
    [InlineData(null, null, "Word and meaning are required.")]
    public void ValidateBatchEntry_ReportsMissingRequiredFields(string? word, string? meaning, string? expected)
    {
        var (vocabulary, vocabularyMeaning, _) = Entry(word!, meaning!);

        Assert.Equal(expected, VocabularyDomainService.ValidateBatchEntry(vocabulary, vocabularyMeaning));
    }

    private VocabularyDomainService CreateService(IVocabularyMeaningRepository meaningRepository)
    {
        return new VocabularyDomainService(
            _vocabularyRepository,
            _bookRepository,
            meaningRepository,
            _bookUnitRepository,
            _meaningUnitRepository,
            _unitOfWork);
    }

    private Task<int> CountMeaningsAsync(string bookId)
    {
        return _dbContext.VocabularyMeanings.CountAsync(
            meaning => meaning.BookId == bookId,
            TestContext.Current.CancellationToken);
    }

    private async Task<VocabularyBookUnitModel> CreateUnitAsync(string bookId, int number)
    {
        var unitService = new VocabularyBookUnitDomainService(
            _bookRepository,
            _meaningRepository,
            _bookUnitRepository,
            _meaningUnitRepository,
            _unitOfWork);
        return await unitService.CreateAsync(bookId, number, null);
    }

    private static (VocabularyModel Word, VocabularyMeaningModel Meaning, string? UnitId) Entry(
        string word,
        string meaning,
        string? partOfSpeech = null,
        string? phoneticUk = null,
        string? phoneticUs = null,
        string? example = null,
        string? unitId = null)
    {
        return (
            new VocabularyModel { Word = word, PhoneticUk = phoneticUk, PhoneticUs = phoneticUs },
            new VocabularyMeaningModel { PartOfSpeech = partOfSpeech, Meaning = meaning, Example = example },
            unitId);
    }

    /// <summary>
    /// Never finds an equivalent meaning, so a repeated entry is inserted a
    /// second time and the database's unique index has to reject it.
    /// </summary>
    private sealed class NoEquivalentMeaningRepository(IVocabularyMeaningRepository inner)
        : IVocabularyMeaningRepository
    {
        public Task<VocabularyMeaningModel?> GetEquivalentAsync(
            string vocabularyId,
            string bookId,
            string normalizedPartOfSpeech,
            string meaning) => Task.FromResult<VocabularyMeaningModel?>(null);

        public Task<VocabularyMeaningModel?> GetByIdAsync(string id) => inner.GetByIdAsync(id);
        public Task<List<VocabularyMeaningModel>> GetByVocabularyIdAsync(string vocabularyId) =>
            inner.GetByVocabularyIdAsync(vocabularyId);
        public Task<List<VocabularyMeaningModel>> GetByBookIdAsync(string bookId) => inner.GetByBookIdAsync(bookId);
        public Task<List<VocabularyMeaningModel>> GetByBookAndVocabularyIdAsync(string bookId, string vocabularyId) =>
            inner.GetByBookAndVocabularyIdAsync(bookId, vocabularyId);
        public Task<List<VocabularyMeaningModel>> GetRandomDistinctVocabularyExceptAsync(
            string bookId,
            string excludeVocabularyId,
            string excludeMeaning,
            int count) =>
            inner.GetRandomDistinctVocabularyExceptAsync(bookId, excludeVocabularyId, excludeMeaning, count);
        public Task AddAsync(VocabularyMeaningModel model) => inner.AddAsync(model);
        public Task UpdateAsync(VocabularyMeaningModel model) => inner.UpdateAsync(model);
        public Task DeleteAsync(string id) => inner.DeleteAsync(id);
        public Task DeleteByVocabularyIdAsync(string vocabularyId) => inner.DeleteByVocabularyIdAsync(vocabularyId);
    }

    /// <summary>
    /// Reports a unit that is no longer stored, so a batch's unit check passes
    /// for a unit the membership's foreign keys then have to reject.
    /// </summary>
    private sealed class UnitReportingDeletedUnit(
        IVocabularyBookUnitRepository inner,
        string deletedUnitId) : IVocabularyBookUnitRepository
    {
        public async Task<List<VocabularyBookUnitModel>> GetByBookIdAsync(string bookId)
        {
            var units = await inner.GetByBookIdAsync(bookId);
            units.Add(new VocabularyBookUnitModel
            {
                Id = deletedUnitId,
                BookId = bookId,
                Number = int.MaxValue
            });
            return units;
        }

        public Task<VocabularyBookUnitModel?> GetByIdAsync(string id) => inner.GetByIdAsync(id);
        public Task<Dictionary<string, int>> GetAssignmentCountsByBookIdAsync(string bookId) =>
            inner.GetAssignmentCountsByBookIdAsync(bookId);
        public Task AddAsync(VocabularyBookUnitModel model) => inner.AddAsync(model);
        public Task UpdateAsync(VocabularyBookUnitModel model) => inner.UpdateAsync(model);
        public Task DeleteAsync(string id) => inner.DeleteAsync(id);
    }
}
