using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Grpc.Core;
using Moq;
using Ruoyu.Study.Vocabulary.Contract.Protos;
using Ruoyu.Study.Vocabulary.Domain.Models;
using Ruoyu.Study.Vocabulary.Domain.Repositories;
using Ruoyu.Study.Vocabulary.Domain.Services;
using Xunit;

namespace Ruoyu.Study.Vocabulary.Service.Tests;

public class VocabularyServiceImplTests
{
    private readonly Mock<IVocabularyRepository> _mockVocabRepo;
    private readonly Mock<IVocabularyMeaningRepository> _mockMeaningRepo;
    private readonly Mock<IUnitOfWork> _mockUow;
    private readonly VocabularyDomainService _domainService;
    private readonly VocabularyServiceImpl _service;

    public VocabularyServiceImplTests()
    {
        _mockVocabRepo = new Mock<IVocabularyRepository>();
        _mockMeaningRepo = new Mock<IVocabularyMeaningRepository>();
        _mockUow = new Mock<IUnitOfWork>();

        _domainService = new VocabularyDomainService(
            _mockVocabRepo.Object,
            _mockMeaningRepo.Object,
            _mockUow.Object);

        _service = new VocabularyServiceImpl(_domainService);
    }

    private static ServerCallContext CreateContext() => TestServerCallContextImpl.Create();

    // ==================== Get ====================

    [Fact]
    public async Task Get_WithEmptyWordId_ThrowsInvalidArgument()
    {
        var request = new GetDetailRequest { WordId = "", BookId = "book-1" };

        var act = async () => await _service.Get(request, CreateContext());

        var ex = (await act.Should().ThrowAsync<RpcException>()).Subject.Single();
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Status.Detail.Should().Contain("ID is required");
    }

    [Fact]
    public async Task Get_WithEmptyBookId_ThrowsInvalidArgument()
    {
        var request = new GetDetailRequest { WordId = "word-1", BookId = "" };

        var act = async () => await _service.Get(request, CreateContext());

        var ex = (await act.Should().ThrowAsync<RpcException>()).Subject.Single();
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Status.Detail.Should().Contain("Book ID is required");
    }

    [Fact]
    public async Task Get_WithValidRequest_ReturnsDtoWithMeanings()
    {
        var wordId = "word-1";
        var bookId = "book-1";
        var word = new VocabularyModel { Id = wordId, Word = "apple", Phonetic = "ˈæpəl" };
        var meanings = new List<VocabularyMeaningModel>
        {
            new() { Id = "m1", VocabularyId = wordId, BookId = bookId, PartOfSpeech = "n", Meaning = "苹果", Example = "" },
            new() { Id = "m2", VocabularyId = wordId, BookId = bookId, PartOfSpeech = "v", Meaning = "苹果公司", Example = "" }
        };

        _mockVocabRepo.Setup(x => x.GetByIdAsync(wordId)).ReturnsAsync(word);
        _mockMeaningRepo.Setup(x => x.GetByBookAndVocabularyIdAsync(bookId, wordId)).ReturnsAsync(meanings);

        var request = new GetDetailRequest { WordId = wordId, BookId = bookId };

        var response = await _service.Get(request, CreateContext());

        response.Should().NotBeNull();
        response.Id.Should().Be(wordId);
        response.Word.Should().Be("apple");
        response.Phonetic.Should().Be("ˈæpəl");
        response.Meanings.Should().HaveCount(2);
    }

    [Fact]
    public async Task Get_WhenWordNotFound_ThrowsInternal()
    {
        var wordId = "nonexistent";
        _mockVocabRepo.Setup(x => x.GetByIdAsync(wordId)).ReturnsAsync((VocabularyModel?)null);

        var request = new GetDetailRequest { WordId = wordId, BookId = "book-1" };

        var act = async () => await _service.Get(request, CreateContext());

        var ex = (await act.Should().ThrowAsync<RpcException>()).Subject.Single();
        ex.StatusCode.Should().Be(StatusCode.Internal);
    }

    // ==================== Search ====================

    [Fact]
    public async Task Search_WithEmptyKeyword_ThrowsInvalidArgument()
    {
        var request = new SearchRequest { Keyword = "" };

        var act = async () => await _service.Search(request, CreateContext());

        var ex = (await act.Should().ThrowAsync<RpcException>()).Subject.Single();
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Status.Detail.Should().Contain("Keyword is required");
    }

    [Fact]
    public async Task Search_WithDefaultPaging_UsesDefaults()
    {
        var items = new List<VocabularyModel>
        {
            new() { Id = "w1", Word = "apple", Phonetic = "" }
        };
        _mockVocabRepo.Setup(x => x.SearchAsync("app", 1, 20)).ReturnsAsync((items, 1));

        var request = new SearchRequest { Keyword = "app", Page = 0, Size = 0 };

        var response = await _service.Search(request, CreateContext());

        response.Items.Should().HaveCount(1);
        response.TotalCount.Should().Be(1);
        response.TotalPage.Should().Be(1);
    }

    [Fact]
    public async Task Search_WithCustomPaging_ReturnsCorrectTotalPages()
    {
        var items = new List<VocabularyModel>
        {
            new() { Id = "w1", Word = "apple", Phonetic = "" }
        };
        _mockVocabRepo.Setup(x => x.SearchAsync("app", 2, 10)).ReturnsAsync((items, 25));

        var request = new SearchRequest { Keyword = "app", Page = 2, Size = 10 };

        var response = await _service.Search(request, CreateContext());

        response.TotalCount.Should().Be(25);
        response.TotalPage.Should().Be(3); // ceil(25/10) = 3
    }

    [Fact]
    public async Task Search_WhenDomainThrows_ThrowsInternal()
    {
        _mockVocabRepo.Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>()))
            .ThrowsAsync(new InvalidOperationException("db error"));

        var request = new SearchRequest { Keyword = "test" };

        var act = async () => await _service.Search(request, CreateContext());

        var ex = (await act.Should().ThrowAsync<RpcException>()).Subject.Single();
        ex.StatusCode.Should().Be(StatusCode.Internal);
    }

    // ==================== AddOrUpdate ====================

    [Fact]
    public async Task AddOrUpdate_WithNullWord_ThrowsInvalidArgument()
    {
        var request = new AddOrUpdateRequest
        {
            Word = null,
            Meaning = new VocabularyMeaningDto { Meaning = "test" }
        };

        var act = async () => await _service.AddOrUpdate(request, CreateContext());

        var ex = (await act.Should().ThrowAsync<RpcException>()).Subject.Single();
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task AddOrUpdate_WithNullMeaning_ThrowsInvalidArgument()
    {
        var request = new AddOrUpdateRequest
        {
            Word = new VocabularyDto { Word = "test" },
            Meaning = null
        };

        var act = async () => await _service.AddOrUpdate(request, CreateContext());

        var ex = (await act.Should().ThrowAsync<RpcException>()).Subject.Single();
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task AddOrUpdate_WithValidInput_ReturnsSuccess()
    {
        var request = new AddOrUpdateRequest
        {
            Word = new VocabularyDto { Word = "apple" },
            Meaning = new VocabularyMeaningDto { Meaning = "苹果", PartOfSpeech = "n" }
        };

        _mockVocabRepo.Setup(x => x.GetByWordAsync("apple")).ReturnsAsync((VocabularyModel?)null);
        _mockMeaningRepo.Setup(x => x.GetByIdAsync(It.IsAny<string>())).ReturnsAsync((VocabularyMeaningModel?)null);

        var response = await _service.AddOrUpdate(request, CreateContext());

        response.Success.Should().BeTrue();
        response.ErrorMessage.Should().BeEmpty();
        _mockVocabRepo.Verify(x => x.AddAsync(It.IsAny<VocabularyModel>()), Times.Once);
        _mockMeaningRepo.Verify(x => x.AddAsync(It.IsAny<VocabularyMeaningModel>()), Times.Once);
        _mockUow.Verify(x => x.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task AddOrUpdate_WhenDomainThrows_ThrowsInternal()
    {
        var request = new AddOrUpdateRequest
        {
            Word = new VocabularyDto { Word = "apple" },
            Meaning = new VocabularyMeaningDto { Meaning = "苹果" }
        };

        _mockVocabRepo.Setup(x => x.GetByWordAsync("apple"))
            .ThrowsAsync(new InvalidOperationException("db error"));

        var act = async () => await _service.AddOrUpdate(request, CreateContext());

        var ex = (await act.Should().ThrowAsync<RpcException>()).Subject.Single();
        ex.StatusCode.Should().Be(StatusCode.Internal);
    }

    // ==================== GetQuestion ====================

    [Fact]
    public async Task GetQuestion_WithEmptyWordId_ThrowsInvalidArgument()
    {
        var request = new GetQuestionRequest { WordId = "", BookId = "book-1" };

        var act = async () => await _service.GetQuestion(request, CreateContext());

        var ex = (await act.Should().ThrowAsync<RpcException>()).Subject.Single();
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task GetQuestion_WithEmptyBookId_ThrowsInvalidArgument()
    {
        var request = new GetQuestionRequest { WordId = "word-1", BookId = "" };

        var act = async () => await _service.GetQuestion(request, CreateContext());

        var ex = (await act.Should().ThrowAsync<RpcException>()).Subject.Single();
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task GetQuestion_WhenNoMeanings_ThrowsInternal()
    {
        // HR-03: NotFound 被 try-catch 吞为 Internal
        var wordId = "word-1";
        var bookId = "book-1";
        var word = new VocabularyModel { Id = wordId, Word = "apple", Phonetic = "" };

        _mockVocabRepo.Setup(x => x.GetByIdAsync(wordId)).ReturnsAsync(word);
        _mockMeaningRepo.Setup(x => x.GetByBookAndVocabularyIdAsync(bookId, wordId))
            .ReturnsAsync(new List<VocabularyMeaningModel>());

        var request = new GetQuestionRequest { WordId = wordId, BookId = bookId, ChineseToEnglish = true };

        var act = async () => await _service.GetQuestion(request, CreateContext());

        var ex = (await act.Should().ThrowAsync<RpcException>()).Subject.Single();
        ex.StatusCode.Should().Be(StatusCode.Internal);
    }

    [Fact]
    public async Task GetQuestion_WithChineseToEnglish_ReturnsChineseQuestion()
    {
        var wordId = "word-1";
        var bookId = "book-1";
        var word = new VocabularyModel { Id = wordId, Word = "apple", Phonetic = "" };
        var meanings = new List<VocabularyMeaningModel>
        {
            new() { Id = "m1", VocabularyId = wordId, BookId = bookId, Meaning = "苹果", PartOfSpeech = "", Example = "" }
        };
        var distractors = new List<VocabularyModel>
        {
            new() { Id = "w2", Word = "banana", Phonetic = "" },
            new() { Id = "w3", Word = "orange", Phonetic = "" },
            new() { Id = "w4", Word = "grape", Phonetic = "" }
        };

        _mockVocabRepo.Setup(x => x.GetByIdAsync(wordId)).ReturnsAsync(word);
        _mockMeaningRepo.Setup(x => x.GetByBookAndVocabularyIdAsync(bookId, wordId)).ReturnsAsync(meanings);
        _mockVocabRepo.Setup(x => x.GetRandomExceptAsync(wordId, 3)).ReturnsAsync(distractors);

        var request = new GetQuestionRequest { WordId = wordId, BookId = bookId, ChineseToEnglish = true };

        var response = await _service.GetQuestion(request, CreateContext());

        response.Word.Should().Be("苹果"); // 中文题目
        response.Options.Should().HaveCount(4); // 1 正确 + 3 干扰
        response.Options.Should().Contain(o => o.Meaning == "apple" && o.IsCorrect);
        response.Options.Should().Contain(o => o.Meaning == "banana" && !o.IsCorrect);
    }

    [Fact]
    public async Task GetQuestion_WithEnglishToChinese_ReturnsEnglishQuestion()
    {
        // ChineseToEnglish=false 时随机选择方向，需同时 mock 两种 distractor 路径
        var wordId = "word-1";
        var bookId = "book-1";
        var word = new VocabularyModel { Id = wordId, Word = "apple", Phonetic = "" };
        var meanings = new List<VocabularyMeaningModel>
        {
            new() { Id = "m1", VocabularyId = wordId, BookId = bookId, Meaning = "苹果", PartOfSpeech = "", Example = "" }
        };
        var distractorWords = new List<VocabularyModel>
        {
            new() { Id = "w2", Word = "banana", Phonetic = "" },
            new() { Id = "w3", Word = "orange", Phonetic = "" },
            new() { Id = "w4", Word = "grape", Phonetic = "" }
        };
        var distractorMeanings = new List<VocabularyMeaningModel>
        {
            new() { Id = "m2", Meaning = "香蕉", PartOfSpeech = "", Example = "" },
            new() { Id = "m3", Meaning = "橙子", PartOfSpeech = "", Example = "" },
            new() { Id = "m4", Meaning = "葡萄", PartOfSpeech = "", Example = "" }
        };

        _mockVocabRepo.Setup(x => x.GetByIdAsync(wordId)).ReturnsAsync(word);
        _mockMeaningRepo.Setup(x => x.GetByBookAndVocabularyIdAsync(bookId, wordId)).ReturnsAsync(meanings);
        _mockVocabRepo.Setup(x => x.GetRandomExceptAsync(wordId, 3)).ReturnsAsync(distractorWords);
        _mockMeaningRepo.Setup(x => x.GetRandomExceptAsync(wordId, bookId, 3)).ReturnsAsync(distractorMeanings);

        var request = new GetQuestionRequest { WordId = wordId, BookId = bookId, ChineseToEnglish = false };

        // 由于随机性，只验证不抛异常且返回有效结构
        var response = await _service.GetQuestion(request, CreateContext());

        response.Should().NotBeNull();
        response.Options.Should().HaveCount(4);
        response.Options.Should().Contain(o => o.IsCorrect);
    }

    [Fact]
    public async Task GetQuestion_WhenDomainThrows_ThrowsInternal()
    {
        var wordId = "word-1";
        var bookId = "book-1";

        _mockVocabRepo.Setup(x => x.GetByIdAsync(wordId))
            .ThrowsAsync(new InvalidOperationException("db error"));

        var request = new GetQuestionRequest { WordId = wordId, BookId = bookId, ChineseToEnglish = true };

        var act = async () => await _service.GetQuestion(request, CreateContext());

        var ex = (await act.Should().ThrowAsync<RpcException>()).Subject.Single();
        ex.StatusCode.Should().Be(StatusCode.Internal);
    }
}
