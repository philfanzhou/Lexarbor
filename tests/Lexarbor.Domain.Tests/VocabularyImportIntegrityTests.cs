using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Lexarbor.Domain.Tests;

public class VocabularyImportIntegrityTests : TestBase
{
    private readonly VocabularyDomainService _service;

    public VocabularyImportIntegrityTests()
    {
        _service = new VocabularyDomainService(
            _vocabularyRepository,
            _bookRepository,
            _meaningRepository,
            _unitOfWork);
    }

    [Fact]
    public async Task AddOrUpdateAsync_TrimsAndLowercasesWord()
    {
        var book = await CreateBookAsync();

        var (word, _) = await _service.AddOrUpdateAsync(
            new VocabularyModel { Word = "  Apple  " },
            new VocabularyMeaningModel { BookId = book.Id, Meaning = " apple " });

        Assert.Equal("apple", word.Word);
        Assert.Equal(
            "apple",
            await _dbContext.Vocabularies
                .Select(item => item.Word)
                .SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddOrUpdateAsync_EquivalentWord_ReusesExistingWord()
    {
        var book = await CreateBookAsync();
        var (firstWord, _) = await _service.AddOrUpdateAsync(
            new VocabularyModel { Word = " Apple " },
            new VocabularyMeaningModel { BookId = book.Id, Meaning = "fruit" });

        var (secondWord, _) = await _service.AddOrUpdateAsync(
            new VocabularyModel { Word = "APPLE" },
            new VocabularyMeaningModel { BookId = book.Id, Meaning = "company" });

        Assert.Equal(firstWord.Id, secondWord.Id);
        Assert.Equal(1, await _dbContext.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, await _dbContext.VocabularyMeanings.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddOrUpdateAsync_EquivalentMeaning_IsIdempotent()
    {
        var book = await CreateBookAsync();
        var (_, firstMeaning) = await _service.AddOrUpdateAsync(
            new VocabularyModel { Word = "Apple" },
            new VocabularyMeaningModel
            {
                BookId = book.Id,
                PartOfSpeech = " N. ",
                Meaning = " fruit "
            });

        var (_, secondMeaning) = await _service.AddOrUpdateAsync(
            new VocabularyModel { Word = " apple " },
            new VocabularyMeaningModel
            {
                BookId = book.Id,
                PartOfSpeech = "n.",
                Meaning = "fruit"
            });

        Assert.Equal(firstMeaning.Id, secondMeaning.Id);
        Assert.Equal("n.", secondMeaning.PartOfSpeech);
        Assert.Equal("fruit", secondMeaning.Meaning);
        Assert.Equal(1, await _dbContext.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await _dbContext.VocabularyMeanings.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddOrUpdateAsync_EquivalentMeaning_UpdatesProvidedExample()
    {
        var book = await CreateBookAsync();
        await _service.AddOrUpdateAsync(
            new VocabularyModel { Word = "apple" },
            new VocabularyMeaningModel
            {
                BookId = book.Id,
                PartOfSpeech = "n.",
                Meaning = "fruit",
                Example = "Old example."
            });

        var (_, meaning) = await _service.AddOrUpdateAsync(
            new VocabularyModel { Word = "APPLE" },
            new VocabularyMeaningModel
            {
                BookId = book.Id,
                PartOfSpeech = " n. ",
                Meaning = " fruit ",
                Example = "  New example.  "
            });

        Assert.Equal("New example.", meaning.Example);
        Assert.Equal(
            "New example.",
            await _dbContext.VocabularyMeanings
                .Select(item => item.Example)
                .SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await _dbContext.VocabularyMeanings.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddOrUpdateAsync_UpdateMeaningToEquivalentValue_ThrowsConflict()
    {
        var book = await CreateBookAsync();
        var (word, _) = await _service.AddOrUpdateAsync(
            new VocabularyModel { Word = "apple" },
            new VocabularyMeaningModel
            {
                BookId = book.Id,
                PartOfSpeech = "n.",
                Meaning = "fruit"
            });
        var (_, secondMeaning) = await _service.AddOrUpdateAsync(
            new VocabularyModel { Word = "apple" },
            new VocabularyMeaningModel
            {
                BookId = book.Id,
                PartOfSpeech = "n.",
                Meaning = "company"
            });

        await Assert.ThrowsAsync<ConflictException>(() => _service.AddOrUpdateAsync(
            new VocabularyModel { Id = word.Id, Word = "apple" },
            new VocabularyMeaningModel
            {
                Id = secondMeaning.Id,
                VocabularyId = word.Id,
                BookId = book.Id,
                PartOfSpeech = "n.",
                Meaning = "fruit"
            }));

        Assert.Equal(2, await _dbContext.VocabularyMeanings.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SearchAsync_ReturnsOnlyWordsLinkedToActiveBooks()
    {
        var activeBook = await CreateBookAsync();
        var disabledBook = await CreateBookAsync();
        var (activeWord, _) = await _service.AddOrUpdateAsync(
            new VocabularyModel { Word = "activeword" },
            new VocabularyMeaningModel
            {
                BookId = activeBook.Id,
                Meaning = "active"
            });
        await _service.AddOrUpdateAsync(
            new VocabularyModel { Word = "disabledword" },
            new VocabularyMeaningModel
            {
                BookId = disabledBook.Id,
                Meaning = "disabled"
            });
        await _vocabularyRepository.AddAsync(new VocabularyModel
        {
            Id = Guid.NewGuid().ToString(),
            Word = "orphanword",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        disabledBook.Status = false;
        await _bookRepository.UpdateAsync(disabledBook);
        await _unitOfWork.SaveChangesAsync();

        var (items, totalCount) = await _service.SearchAsync("word", 1, 20);

        Assert.Equal(1, totalCount);
        Assert.Single(items);
        Assert.Equal(activeWord.Id, items[0].Id);
    }

    [Fact]
    public async Task AddOrUpdateAsync_MissingBook_ThrowsResourceNotFound()
    {
        await Assert.ThrowsAsync<ResourceNotFoundException>(() => _service.AddOrUpdateAsync(
            new VocabularyModel { Word = "apple" },
            new VocabularyMeaningModel { BookId = "missing-book", Meaning = "fruit" }));

        Assert.Equal(0, await _dbContext.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await _dbContext.VocabularyMeanings.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddOrUpdateAsync_DisabledBook_ThrowsBusinessRule()
    {
        var book = await CreateBookAsync(status: false);

        await Assert.ThrowsAsync<BusinessRuleException>(() => _service.AddOrUpdateAsync(
            new VocabularyModel { Word = "apple" },
            new VocabularyMeaningModel { BookId = book.Id, Meaning = "fruit" }));

        Assert.Equal(0, await _dbContext.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await _dbContext.VocabularyMeanings.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddOrUpdateAsync_MissingWordId_ThrowsResourceNotFound()
    {
        var book = await CreateBookAsync();

        await Assert.ThrowsAsync<ResourceNotFoundException>(() => _service.AddOrUpdateAsync(
            new VocabularyModel { Id = "missing-word", Word = "apple" },
            new VocabularyMeaningModel { BookId = book.Id, Meaning = "fruit" }));

        Assert.Equal(0, await _dbContext.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await _dbContext.VocabularyMeanings.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddOrUpdateAsync_MissingMeaningId_ThrowsResourceNotFound()
    {
        var book = await CreateBookAsync();
        var (word, _) = await _service.AddOrUpdateAsync(
            new VocabularyModel { Word = "apple" },
            new VocabularyMeaningModel { BookId = book.Id, Meaning = "fruit" });

        await Assert.ThrowsAsync<ResourceNotFoundException>(() => _service.AddOrUpdateAsync(
            new VocabularyModel { Id = word.Id, Word = "apple" },
            new VocabularyMeaningModel
            {
                Id = "missing-meaning",
                BookId = book.Id,
                Meaning = "company"
            }));

        Assert.Equal(1, await _dbContext.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, await _dbContext.VocabularyMeanings.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddOrUpdateAsync_MeaningOwnedByAnotherWord_ThrowsConflict()
    {
        var book = await CreateBookAsync();
        var (firstWord, firstMeaning) = await _service.AddOrUpdateAsync(
            new VocabularyModel { Word = "apple" },
            new VocabularyMeaningModel { BookId = book.Id, Meaning = "fruit" });
        var (secondWord, _) = await _service.AddOrUpdateAsync(
            new VocabularyModel { Word = "banana" },
            new VocabularyMeaningModel { BookId = book.Id, Meaning = "yellow fruit" });

        await Assert.ThrowsAsync<ConflictException>(() => _service.AddOrUpdateAsync(
            new VocabularyModel { Id = secondWord.Id, Word = secondWord.Word },
            new VocabularyMeaningModel
            {
                Id = firstMeaning.Id,
                BookId = book.Id,
                Meaning = "changed"
            }));

        Assert.NotEqual(firstWord.Id, secondWord.Id);
        Assert.Equal(2, await _dbContext.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, await _dbContext.VocabularyMeanings.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddOrUpdateAsync_MeaningOwnedByAnotherBook_ThrowsConflict()
    {
        var firstBook = await CreateBookAsync();
        var secondBook = await CreateBookAsync();
        var (word, meaning) = await _service.AddOrUpdateAsync(
            new VocabularyModel { Word = "apple" },
            new VocabularyMeaningModel { BookId = firstBook.Id, Meaning = "fruit" });

        await Assert.ThrowsAsync<ConflictException>(() => _service.AddOrUpdateAsync(
            new VocabularyModel { Id = word.Id, Word = word.Word },
            new VocabularyMeaningModel
            {
                Id = meaning.Id,
                BookId = secondBook.Id,
                Meaning = "changed"
            }));

        Assert.Equal(1, await _dbContext.VocabularyMeanings.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            firstBook.Id,
            await _dbContext.VocabularyMeanings
                .Select(item => item.BookId)
                .SingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetDetailAsync_DisabledBook_ThrowsBusinessRule()
    {
        var book = await CreateBookAsync();
        var (word, _) = await _service.AddOrUpdateAsync(
            new VocabularyModel { Word = "apple" },
            new VocabularyMeaningModel { BookId = book.Id, Meaning = "fruit" });

        book.Status = false;
        await _bookRepository.UpdateAsync(book);
        await _unitOfWork.SaveChangesAsync();

        await Assert.ThrowsAsync<BusinessRuleException>(
            () => _service.GetDetailAsync(word.Id, book.Id));
    }
}
