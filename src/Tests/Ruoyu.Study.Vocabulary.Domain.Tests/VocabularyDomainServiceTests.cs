using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ruoyu.Study.Vocabulary.Domain.Models;
using Ruoyu.Study.Vocabulary.Domain.Services;
using Xunit;

namespace Ruoyu.Study.Vocabulary.Domain.Tests;

public class VocabularyDomainServiceTests : TestBase
{
    private readonly VocabularyDomainService _service;

    public VocabularyDomainServiceTests()
    {
        _service = new VocabularyDomainService(_vocabularyRepository, _meaningRepository, _unitOfWork);
    }

    [Fact]
    public async Task AddOrUpdateAsync_NewWord_CreatesWordAndMeaning()
    {
        var vocabulary = new VocabularyModel { Word = "apple", Phonetic = "/ˈæp.əl/" };
        var meaning = new VocabularyMeaningModel
        {
            BookId = "book-1",
            PartOfSpeech = "n.",
            Meaning = "苹果",
            Example = "I eat an apple."
        };

        var (word, resultMeaning) = await _service.AddOrUpdateAsync(vocabulary, meaning);

        Assert.NotNull(word);
        Assert.NotEmpty(word.Id);
        Assert.Equal("apple", word.Word);
        Assert.Equal("/ˈæp.əl/", word.Phonetic);
        Assert.NotNull(resultMeaning);
        Assert.Equal(word.Id, resultMeaning.VocabularyId);
        Assert.Equal("n.", resultMeaning.PartOfSpeech);
        Assert.Equal("苹果", resultMeaning.Meaning);
    }

    [Fact]
    public async Task AddOrUpdateAsync_ExistingWord_UpdatesWordAndAddsNewMeaning()
    {
        var vocabulary = new VocabularyModel { Word = "apple", Phonetic = "/ˈæp.əl/" };
        var meaning1 = new VocabularyMeaningModel
        {
            BookId = "book-1",
            PartOfSpeech = "n.",
            Meaning = "苹果"
        };
        var (existingWord, _) = await _service.AddOrUpdateAsync(vocabulary, meaning1);

        var updatedVocabulary = new VocabularyModel { Id = existingWord.Id, Word = "apple", Phonetic = "/ˈæp.əl/ (updated)" };
        var meaning2 = new VocabularyMeaningModel
        {
            BookId = "book-1",
            PartOfSpeech = "v.",
            Meaning = "提供苹果"
        };
        var (word, resultMeaning) = await _service.AddOrUpdateAsync(updatedVocabulary, meaning2);

        Assert.Equal(existingWord.Id, word.Id);
        Assert.Equal("/ˈæp.əl/ (updated)", word.Phonetic);
        Assert.Equal("v.", resultMeaning.PartOfSpeech);
    }

    [Fact]
    public async Task AddOrUpdateAsync_ExistingMeaning_UpdatesMeaning()
    {
        var vocabulary = new VocabularyModel { Word = "book", Phonetic = "/bʊk/" };
        var meaning = new VocabularyMeaningModel
        {
            BookId = "book-1",
            PartOfSpeech = "n.",
            Meaning = "书"
        };
        var (_, createdMeaning) = await _service.AddOrUpdateAsync(vocabulary, meaning);

        var updateMeaning = new VocabularyMeaningModel
        {
            Id = createdMeaning.Id,
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
        var vocabulary = new VocabularyModel { Word = "test", Phonetic = "/test/" };
        var meaning = new VocabularyMeaningModel
        {
            BookId = "book-1",
            PartOfSpeech = "n.",
            Meaning = "测试"
        };
        var (createdWord, _) = await _service.AddOrUpdateAsync(vocabulary, meaning);

        var (word, meanings) = await _service.GetDetailAsync(createdWord.Id, "book-1");

        Assert.NotNull(word);
        Assert.Equal("test", word.Word);
        Assert.Single(meanings);
        Assert.Equal("测试", meanings[0].Meaning);
    }

    [Fact]
    public async Task GetDetailAsync_NonExistingWord_ThrowsKeyNotFoundException()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _service.GetDetailAsync("nonexistent-id", "book-1"));
    }

    [Fact]
    public async Task SearchAsync_ByKeyword_ReturnsMatchingWords()
    {
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
