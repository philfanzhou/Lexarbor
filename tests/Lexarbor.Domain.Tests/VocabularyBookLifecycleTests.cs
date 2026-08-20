using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Lexarbor.Domain.Tests;

public class VocabularyBookLifecycleTests : TestBase
{
    private readonly VocabularyBookDomainService _service;

    public VocabularyBookLifecycleTests()
    {
        _service = new VocabularyBookDomainService(_bookRepository, _unitOfWork);
    }

    [Fact]
    public async Task AddOrUpdateAsync_UnknownNonEmptyId_ThrowsResourceNotFound()
    {
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => _service.AddOrUpdateAsync(
            new VocabularyBookModel
            {
                Id = "missing-book",
                BookName = "Missing",
                Status = true
            }));

        Assert.Equal(0, await _dbContext.VocabularyBooks.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteAsync_UnknownBook_ThrowsResourceNotFound()
    {
        await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => _service.DeleteAsync("missing-book"));
    }

    [Fact]
    public async Task DeleteAsync_UsedBook_ThrowsConflict()
    {
        var book = await CreateBookAsync();
        var word = new VocabularyModel
        {
            Id = Guid.NewGuid().ToString(),
            Word = "apple"
        };
        await _vocabularyRepository.AddAsync(word);
        await _meaningRepository.AddAsync(new VocabularyMeaningModel
        {
            Id = Guid.NewGuid().ToString(),
            VocabularyId = word.Id,
            BookId = book.Id,
            Meaning = "fruit"
        });
        await _unitOfWork.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<ConflictException>(
            () => _service.DeleteAsync(book.Id));

        Assert.Contains("Disable it instead", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, await _dbContext.VocabularyBooks.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteAsync_EmptyBook_RemovesBook()
    {
        var book = await CreateBookAsync();

        await _service.DeleteAsync(book.Id);

        Assert.Equal(0, await _dbContext.VocabularyBooks.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetAllAsync_ExcludesDisabledBooks()
    {
        var activeBook = await CreateBookAsync();
        _ = await CreateBookAsync(status: false);

        var books = await _service.GetAllAsync();

        Assert.Single(books);
        Assert.Equal(activeBook.Id, books[0].Id);
    }

    [Fact]
    public async Task SearchAsync_IncludesDisabledBooksForAdmin()
    {
        _ = await CreateBookAsync();
        var disabledBook = await CreateBookAsync(status: false);

        var (books, totalCount) = await _service.SearchAsync(string.Empty, 1, 20);

        Assert.Equal(2, totalCount);
        Assert.Contains(books, book => book.Id == disabledBook.Id);
    }

    [Fact]
    public async Task GetWordsAsync_UnknownBook_ThrowsResourceNotFound()
    {
        await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => _service.GetWordsAsync("missing-book"));
    }

    [Fact]
    public async Task SearchAsync_SizeOneHundred_ReturnsAtMostOneHundredItems()
    {
        for (var index = 0; index < 105; index++)
        {
            await CreateBookAsync();
        }

        var (items, totalCount) = await _service.SearchAsync(string.Empty, 1, 100);

        Assert.Equal(105, totalCount);
        Assert.Equal(100, items.Count);
    }
}
