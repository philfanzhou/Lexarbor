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
        Assert.Equal("apple", word.Word);
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
        var (vocabulary, vocabularyMeaning) = Entry(word!, meaning!);

        Assert.Equal(expected, VocabularyDomainService.ValidateBatchEntry(vocabulary, vocabularyMeaning));
    }

    private VocabularyDomainService CreateService(IVocabularyMeaningRepository meaningRepository)
    {
        return new VocabularyDomainService(
            _vocabularyRepository,
            _bookRepository,
            meaningRepository,
            _unitOfWork);
    }

    private Task<int> CountMeaningsAsync(string bookId)
    {
        return _dbContext.VocabularyMeanings.CountAsync(
            meaning => meaning.BookId == bookId,
            TestContext.Current.CancellationToken);
    }

    private static (VocabularyModel Word, VocabularyMeaningModel Meaning) Entry(
        string word,
        string meaning,
        string? partOfSpeech = null,
        string? phoneticUk = null,
        string? phoneticUs = null,
        string? example = null)
    {
        return (
            new VocabularyModel { Word = word, PhoneticUk = phoneticUk, PhoneticUs = phoneticUs },
            new VocabularyMeaningModel { PartOfSpeech = partOfSpeech, Meaning = meaning, Example = example });
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
}
