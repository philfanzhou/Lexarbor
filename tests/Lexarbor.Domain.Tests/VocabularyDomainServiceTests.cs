using System;
using System.Threading.Tasks;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Xunit;

namespace Lexarbor.Domain.Tests;

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

    // Words are always stored lower-cased, and instr() -- what string.Contains
    // used to translate to -- compares bytes, so before this every mixed-case
    // keyword returned an empty page that was indistinguishable from "no such
    // word". LIKE folds ASCII case, which is the folding used everywhere else
    // in this codebase.
    [Theory]
    [InlineData("apple")]
    [InlineData("Apple")]
    [InlineData("APPLE")]
    [InlineData("aPpLe")]
    public async Task SearchAsync_KeywordCaseDoesNotMatter(string keyword)
    {
        _ = await CreateBookAsync(id: "b1");
        await _service.AddOrUpdateAsync(
            new VocabularyModel { Word = "apple" },
            new VocabularyMeaningModel { BookId = "b1", Meaning = "苹果" });
        await _service.AddOrUpdateAsync(
            new VocabularyModel { Word = "banana" },
            new VocabularyMeaningModel { BookId = "b1", Meaning = "香蕉" });

        var (items, totalCount) = await _service.SearchAsync(keyword, 1, 10);

        Assert.Equal(1, totalCount);
        Assert.Equal("apple", Assert.Single(items).Word);
    }

    // instr() had no wildcards, so moving to LIKE could have widened a search
    // silently. "%" would match every word and "a_ple" would match "apple".
    [Theory]
    [InlineData("%")]
    [InlineData("a_ple")]
    public async Task SearchAsync_LikeWildcardsInKeywordAreLiteral(string keyword)
    {
        _ = await CreateBookAsync(id: "b1");
        await _service.AddOrUpdateAsync(
            new VocabularyModel { Word = "apple" },
            new VocabularyMeaningModel { BookId = "b1", Meaning = "苹果" });
        await _service.AddOrUpdateAsync(
            new VocabularyModel { Word = "banana" },
            new VocabularyMeaningModel { BookId = "b1", Meaning = "香蕉" });

        var (items, totalCount) = await _service.SearchAsync(keyword, 1, 10);

        Assert.Equal(0, totalCount);
        Assert.Empty(items);
    }

    [Fact]
    public async Task SearchAsync_EscapeCharacterInKeywordIsLiteral()
    {
        _ = await CreateBookAsync(id: "b1");
        await _service.AddOrUpdateAsync(
            new VocabularyModel { Word = @"back\slash" },
            new VocabularyMeaningModel { BookId = "b1", Meaning = "反斜杠" });
        await _service.AddOrUpdateAsync(
            new VocabularyModel { Word = "banana" },
            new VocabularyMeaningModel { BookId = "b1", Meaning = "香蕉" });

        // The keyword is the ESCAPE character itself, so it has to be doubled
        // before it reaches SQLite or the pattern ends in a dangling escape.
        var (items, totalCount) = await _service.SearchAsync(@"\", 1, 10);

        Assert.Equal(1, totalCount);
        Assert.Equal(@"back\slash", Assert.Single(items).Word);
    }

    [Fact]
    public async Task SearchAsync_NonAsciiCaseIsNotFolded()
    {
        _ = await CreateBookAsync(id: "b1");
        await _service.AddOrUpdateAsync(
            new VocabularyModel { Word = "café" },
            new VocabularyMeaningModel { BookId = "b1", Meaning = "咖啡馆" });

        // SQLite folds ASCII only -- its LIKE and its lower() agree on that, and
        // neither touches É. Pinned rather than fixed: case-folding the rest of
        // Unicode needs FTS5 or an ICU build, which is a larger decision than
        // this change. Stated here so the boundary is a known one.
        Assert.Equal(1, (await _service.SearchAsync("café", 1, 10)).TotalCount);
        Assert.Equal(1, (await _service.SearchAsync("CAF", 1, 10)).TotalCount);
        Assert.Equal(0, (await _service.SearchAsync("CAFÉ", 1, 10)).TotalCount);
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
