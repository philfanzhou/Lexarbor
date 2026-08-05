using System;
using System.Threading.Tasks;
using Ruoyu.Study.Vocabulary.Domain.Exceptions;
using Ruoyu.Study.Vocabulary.Domain.Models;
using Ruoyu.Study.Vocabulary.Domain.Services;
using Xunit;

namespace Ruoyu.Study.Vocabulary.Domain.Tests;

public class VocabularyDomainServiceTests : TestBase
{
    private readonly VocabularyDomainService _service;

    public VocabularyDomainServiceTests()
    {
        _service = new VocabularyDomainService(
            _vocabularyRepository,
            _bookRepository,
            _meaningRepository,
            _unitOfWork);
    }

    [Fact]
    public async Task AddOrUpdateAsync_NewWord_CreatesWordAndMeaning()
    {
        var book = await CreateBookAsync();
        var vocabulary = new VocabularyModel
        {
            Word = "apple",
            PhoneticUk = "/ˈæp.əl/",
            PhoneticUs = "/ˈæp.əl/"
        };
        var meaning = new VocabularyMeaningModel
        {
            BookId = book.Id,
            PartOfSpeech = "n.",
            Meaning = "苹果",
            Example = "I eat an apple."
        };

        var (word, resultMeaning) = await _service.AddOrUpdateAsync(vocabulary, meaning);

        Assert.NotNull(word);
        Assert.NotEmpty(word.Id);
        Assert.Equal("apple", word.Word);
        Assert.Equal("/ˈæp.əl/", word.PhoneticUk);
        Assert.Equal("/ˈæp.əl/", word.PhoneticUs);
        Assert.NotNull(resultMeaning);
        Assert.Equal(word.Id, resultMeaning.VocabularyId);
        Assert.Equal("n.", resultMeaning.PartOfSpeech);
        Assert.Equal("苹果", resultMeaning.Meaning);
    }

    [Fact]
    public async Task AddOrUpdateAsync_ExistingWord_UpdatesWordAndAddsNewMeaning()
    {
        var book = await CreateBookAsync();
        var vocabulary = new VocabularyModel
        {
            Word = "apple",
            PhoneticUk = "/ˈæp.əl/",
            PhoneticUs = "/ˈæp.əl/"
        };
        var meaning1 = new VocabularyMeaningModel
        {
            BookId = book.Id,
            PartOfSpeech = "n.",
            Meaning = "苹果"
        };
        var (existingWord, _) = await _service.AddOrUpdateAsync(vocabulary, meaning1);

        var updatedVocabulary = new VocabularyModel
        {
            Id = existingWord.Id,
            Word = "apple",
            PhoneticUk = "/ˈæp.əl/ updated/",
            PhoneticUs = "/ˈæp.əl/ updated/"
        };
        var meaning2 = new VocabularyMeaningModel
        {
            BookId = book.Id,
            PartOfSpeech = "v.",
            Meaning = "提供苹果"
        };
        var (word, resultMeaning) = await _service.AddOrUpdateAsync(updatedVocabulary, meaning2);

        Assert.Equal(existingWord.Id, word.Id);
        Assert.Equal("/ˈæp.əl/ updated/", word.PhoneticUk);
        Assert.Equal("/ˈæp.əl/ updated/", word.PhoneticUs);
        Assert.Equal("v.", resultMeaning.PartOfSpeech);
    }

    [Fact]
    public async Task AddOrUpdateAsync_ExistingMeaning_UpdatesMeaning()
    {
        var book = await CreateBookAsync();
        var vocabulary = new VocabularyModel
        {
            Word = "book",
            PhoneticUk = "/bʊk/",
            PhoneticUs = "/bʊk/"
        };
        var meaning = new VocabularyMeaningModel
        {
            BookId = book.Id,
            PartOfSpeech = "n.",
            Meaning = "书"
        };
        var (_, createdMeaning) = await _service.AddOrUpdateAsync(vocabulary, meaning);

        var updateMeaning = new VocabularyMeaningModel
        {
            Id = createdMeaning.Id,
            BookId = book.Id,
            PartOfSpeech = "n.",
            Meaning = "书本",
            Example = "I read a book."
        };
        var (_, resultMeaning) = await _service.AddOrUpdateAsync(
            new VocabularyModel { Id = vocabulary.Id, Word = "book" }, updateMeaning);

        Assert.Equal("书本", resultMeaning.Meaning);
        Assert.Equal("I read a book.", resultMeaning.Example);
    }

    [Fact]
    public async Task GetDetailAsync_ExistingWord_ReturnsWordAndMeanings()
    {
        var book = await CreateBookAsync();
        var vocabulary = new VocabularyModel
        {
            Word = "test",
            PhoneticUk = "/test/",
            PhoneticUs = "/test/"
        };
        var meaning = new VocabularyMeaningModel
        {
            BookId = book.Id,
            PartOfSpeech = "n.",
            Meaning = "测试"
        };
        var (createdWord, _) = await _service.AddOrUpdateAsync(vocabulary, meaning);

        var (word, meanings) = await _service.GetDetailAsync(createdWord.Id, book.Id);

        Assert.NotNull(word);
        Assert.Equal("test", word.Word);
        Assert.Single(meanings);
        Assert.Equal("测试", meanings[0].Meaning);
    }

    [Fact]
    public async Task GetDetailAsync_NonExistingWord_ThrowsResourceNotFoundException()
    {
        var book = await CreateBookAsync();

        await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => _service.GetDetailAsync("nonexistent-id", book.Id));
    }

    [Fact]
    public async Task SearchAsync_ByKeyword_ReturnsMatchingWords()
    {
        _ = await CreateBookAsync(id: "b1");
        await _service.AddOrUpdateAsync(
            new VocabularyModel { Word = "apple" },
            new VocabularyMeaningModel { BookId = "b1", Meaning = "苹果" });
        await _service.AddOrUpdateAsync(
            new VocabularyModel { Word = "application" },
            new VocabularyMeaningModel { BookId = "b1", Meaning = "应用" });
        await _service.AddOrUpdateAsync(
            new VocabularyModel { Word = "banana" },
            new VocabularyMeaningModel { BookId = "b1", Meaning = "香蕉" });

        var (items, totalCount) = await _service.SearchAsync("app", 1, 10);

        Assert.Equal(2, totalCount);
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public async Task SearchAsync_NoKeyword_ReturnsAllWords()
    {
        _ = await CreateBookAsync(id: "b1");
        await _service.AddOrUpdateAsync(
            new VocabularyModel { Word = "apple" },
            new VocabularyMeaningModel { BookId = "b1", Meaning = "苹果" });
        await _service.AddOrUpdateAsync(
            new VocabularyModel { Word = "banana" },
            new VocabularyMeaningModel { BookId = "b1", Meaning = "香蕉" });

        var (items, totalCount) = await _service.SearchAsync(null, 1, 10);

        Assert.Equal(2, totalCount);
    }

    [Fact]
    public async Task SearchAsync_Pagination_Works()
    {
        _ = await CreateBookAsync(id: "b1");
        for (int i = 0; i < 15; i++)
        {
            await _service.AddOrUpdateAsync(
                new VocabularyModel { Word = $"word{i}" },
                new VocabularyMeaningModel { BookId = "b1", Meaning = $"词{i}" });
        }

        var (page1, total1) = await _service.SearchAsync(null, 1, 10);
        var (page2, total2) = await _service.SearchAsync(null, 2, 10);

        Assert.Equal(15, total1);
        Assert.Equal(10, page1.Count);
        Assert.Equal(5, page2.Count);
    }
}
