using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Moq;
using Ruoyu.Study.Vocabulary.Contract.Protos;
using Ruoyu.Study.Vocabulary.Domain.Models;
using Ruoyu.Study.Vocabulary.Domain.Repositories;
using Ruoyu.Study.Vocabulary.Domain.Services;
using Xunit;

namespace Ruoyu.Study.Vocabulary.Service.Tests;

public class VocabularyBookServiceImplTests
{
    private readonly Mock<IVocabularyBookRepository> _mockBookRepo;
    private readonly Mock<IVocabularyMeaningRepository> _mockMeaningRepo;
    private readonly Mock<IVocabularyRepository> _mockVocabRepo;
    private readonly Mock<IUnitOfWork> _mockUow;
    private readonly VocabularyBookDomainService _domainService;
    private readonly VocabularyBookServiceImpl _service;

    public VocabularyBookServiceImplTests()
    {
        _mockBookRepo = new Mock<IVocabularyBookRepository>();
        _mockMeaningRepo = new Mock<IVocabularyMeaningRepository>();
        _mockVocabRepo = new Mock<IVocabularyRepository>();
        _mockUow = new Mock<IUnitOfWork>();

        _domainService = new VocabularyBookDomainService(
            _mockBookRepo.Object,
            _mockMeaningRepo.Object,
            _mockVocabRepo.Object,
            _mockUow.Object);

        _service = new VocabularyBookServiceImpl(_domainService);
    }

    private static ServerCallContext CreateContext() => TestServerCallContextImpl.Create();

    // ==================== Add ====================

    [Fact]
    public async Task Add_WithEmptyBookName_ThrowsInvalidArgument()
    {
        var request = new VocabularyBookDto { BookName = "" };

        var act = async () => await _service.Add(request, CreateContext());

        var ex = (await act.Should().ThrowAsync<RpcException>()).Subject.Single();
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Status.Detail.Should().Contain("BookName is required");
    }

    [Fact]
    public async Task Add_WithValidInput_ReturnsSuccess()
    {
        var request = new VocabularyBookDto
        {
            BookName = "Test Book",
            Category = "math",
            EducationLevel = "primary",
            Grade = "1"
        };

        _mockBookRepo.Setup(x => x.GetByIdAsync(It.IsAny<string>())).ReturnsAsync((VocabularyBookModel?)null);

        var response = await _service.Add(request, CreateContext());

        response.Success.Should().BeTrue();
        response.ErrorMessage.Should().BeEmpty();
        _mockBookRepo.Verify(x => x.AddAsync(It.IsAny<VocabularyBookModel>()), Times.Once);
        _mockUow.Verify(x => x.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task Add_WhenDomainThrows_ThrowsInternal()
    {
        var request = new VocabularyBookDto { BookName = "Test" };

        // AddOrUpdateAsync 对空 Id 的实体不会调用 GetByIdAsync，直接走 Add 路径
        _mockBookRepo.Setup(x => x.AddAsync(It.IsAny<VocabularyBookModel>()))
            .ThrowsAsync(new InvalidOperationException("db error"));

        var act = async () => await _service.Add(request, CreateContext());

        var ex = (await act.Should().ThrowAsync<RpcException>()).Subject.Single();
        ex.StatusCode.Should().Be(StatusCode.Internal);
    }

    // ==================== Update ====================

    [Fact]
    public async Task Update_WithEmptyId_ThrowsInvalidArgument()
    {
        var request = new VocabularyBookDto { Id = "", BookName = "Test" };

        var act = async () => await _service.Update(request, CreateContext());

        var ex = (await act.Should().ThrowAsync<RpcException>()).Subject.Single();
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Status.Detail.Should().Contain("Id is required");
    }

    [Fact]
    public async Task Update_WithValidInput_ReturnsSuccess()
    {
        var existingBook = new VocabularyBookModel { Id = "book-1", BookName = "Old Name" };
        _mockBookRepo.Setup(x => x.GetByIdAsync("book-1")).ReturnsAsync(existingBook);

        var request = new VocabularyBookDto { Id = "book-1", BookName = "New Name" };

        var response = await _service.Update(request, CreateContext());

        response.Success.Should().BeTrue();
        _mockBookRepo.Verify(x => x.UpdateAsync(It.IsAny<VocabularyBookModel>()), Times.Once);
    }

    // ==================== Get ====================

    [Fact]
    public async Task Get_WithEmptyId_ThrowsInvalidArgument()
    {
        var request = new IdRequest { Id = "" };

        var act = async () => await _service.Get(request, CreateContext());

        var ex = (await act.Should().ThrowAsync<RpcException>()).Subject.Single();
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task Get_WhenNotFound_ThrowsInternal()
    {
        // HR-03: NotFound 被 try-catch 吞为 Internal
        _mockBookRepo.Setup(x => x.GetByIdAsync("nonexistent")).ReturnsAsync((VocabularyBookModel?)null);

        var request = new IdRequest { Id = "nonexistent" };

        var act = async () => await _service.Get(request, CreateContext());

        var ex = (await act.Should().ThrowAsync<RpcException>()).Subject.Single();
        ex.StatusCode.Should().Be(StatusCode.Internal);
    }

    [Fact]
    public async Task Get_WhenFound_ReturnsDto()
    {
        var book = new VocabularyBookModel
        {
            Id = "book-1",
            BookName = "Test Book",
            Category = "math",
            EducationLevel = "primary",
            Grade = "1",
            Description = "",
            Publisher = "",
            IconUrl = ""
        };
        _mockBookRepo.Setup(x => x.GetByIdAsync("book-1")).ReturnsAsync(book);

        var request = new IdRequest { Id = "book-1" };

        var response = await _service.Get(request, CreateContext());

        response.Id.Should().Be("book-1");
        response.BookName.Should().Be("Test Book");
        response.Category.Should().Be("math");
    }

    // ==================== Search ====================

    [Fact]
    public async Task Search_WithDefaultPaging_UsesDefaults()
    {
        var books = new List<VocabularyBookModel>
        {
            new() { Id = "b1", BookName = "Book 1", DisplayOrder = 1, Description = "", Publisher = "", EducationLevel = "", Grade = "", Category = "", IconUrl = "" }
        };
        _mockBookRepo.Setup(x => x.GetAllAsync()).ReturnsAsync(books);

        var request = new SearchBookRequest { Keyword = "", Page = 0, Size = 0 };

        var response = await _service.Search(request, CreateContext());

        response.Items.Should().HaveCount(1);
        response.TotalCount.Should().Be(1);
        response.TotalPage.Should().Be(1);
    }

    [Fact]
    public async Task Search_WithKeyword_FiltersResults()
    {
        var books = new List<VocabularyBookModel>
        {
            new() { Id = "b1", BookName = "Math Book", DisplayOrder = 1, Description = "", Publisher = "", EducationLevel = "", Grade = "", Category = "", IconUrl = "" },
            new() { Id = "b2", BookName = "English Book", DisplayOrder = 2, Description = "", Publisher = "", EducationLevel = "", Grade = "", Category = "", IconUrl = "" }
        };
        _mockBookRepo.Setup(x => x.GetAllAsync()).ReturnsAsync(books);

        var request = new SearchBookRequest { Keyword = "Math", Page = 1, Size = 10 };

        var response = await _service.Search(request, CreateContext());

        response.Items.Should().HaveCount(1);
        response.Items[0].BookName.Should().Be("Math Book");
    }

    [Fact]
    public async Task Search_WithMultiplePages_ComputesTotalPages()
    {
        var books = new List<VocabularyBookModel>();
        for (int i = 1; i <= 25; i++)
        {
            books.Add(new VocabularyBookModel { Id = $"b{i}", BookName = $"Book {i}", DisplayOrder = i, Description = "", Publisher = "", EducationLevel = "", Grade = "", Category = "", IconUrl = "" });
        }
        _mockBookRepo.Setup(x => x.GetAllAsync()).ReturnsAsync(books);

        var request = new SearchBookRequest { Keyword = "", Page = 1, Size = 10 };

        var response = await _service.Search(request, CreateContext());

        response.TotalCount.Should().Be(25);
        response.TotalPage.Should().Be(3);
        response.Items.Should().HaveCount(10); // 第一页 10 条
    }

    // ==================== GetByCategory ====================

    [Fact]
    public async Task GetByCategory_WithGrade_FiltersByCategoryAndGrade()
    {
        var books = new List<VocabularyBookModel>
        {
            new() { Id = "b1", BookName = "Book 1", Category = "math", Grade = "1", DisplayOrder = 1, Description = "", Publisher = "", EducationLevel = "", IconUrl = "" },
            new() { Id = "b2", BookName = "Book 2", Category = "math", Grade = "2", DisplayOrder = 2, Description = "", Publisher = "", EducationLevel = "", IconUrl = "" },
            new() { Id = "b3", BookName = "Book 3", Category = "english", Grade = "1", DisplayOrder = 3, Description = "", Publisher = "", EducationLevel = "", IconUrl = "" }
        };
        _mockBookRepo.Setup(x => x.GetAllAsync()).ReturnsAsync(books);

        var request = new GetByCategoryRequest { Category = "math", Grade = "1" };

        var response = await _service.GetByCategory(request, CreateContext());

        response.Books.Should().HaveCount(1);
        response.Books[0].Id.Should().Be("b1");
    }

    [Fact]
    public async Task GetByCategory_WithoutGrade_FiltersByCategoryOnly()
    {
        var books = new List<VocabularyBookModel>
        {
            new() { Id = "b1", BookName = "Book 1", Category = "math", Grade = "1", DisplayOrder = 1, Description = "", Publisher = "", EducationLevel = "", IconUrl = "" },
            new() { Id = "b2", BookName = "Book 2", Category = "math", Grade = "2", DisplayOrder = 2, Description = "", Publisher = "", EducationLevel = "", IconUrl = "" }
        };
        _mockBookRepo.Setup(x => x.GetAllAsync()).ReturnsAsync(books);

        var request = new GetByCategoryRequest { Category = "math" };

        var response = await _service.GetByCategory(request, CreateContext());

        response.Books.Should().HaveCount(2);
    }

    // ==================== GetAll ====================

    [Fact]
    public async Task GetAll_ReturnsAllBooks()
    {
        var books = new List<VocabularyBookModel>
        {
            new() { Id = "b1", BookName = "Book 1", Description = "", Publisher = "", EducationLevel = "", Grade = "", Category = "", IconUrl = "" },
            new() { Id = "b2", BookName = "Book 2", Description = "", Publisher = "", EducationLevel = "", Grade = "", Category = "", IconUrl = "" }
        };
        _mockBookRepo.Setup(x => x.GetAllAsync()).ReturnsAsync(books);

        var response = await _service.GetAll(new Empty(), CreateContext());

        response.Books.Should().HaveCount(2);
    }

    // ==================== GetAllCategories ====================

    [Fact]
    public async Task GetAllCategories_ReturnsDistinctCategories()
    {
        var books = new List<VocabularyBookModel>
        {
            new() { Id = "b1", Category = "math" },
            new() { Id = "b2", Category = "english" },
            new() { Id = "b3", Category = "math" },
            new() { Id = "b4", Category = "" }
        };
        _mockBookRepo.Setup(x => x.GetAllAsync()).ReturnsAsync(books);

        var response = await _service.GetAllCategories(new Empty(), CreateContext());

        response.Items.Should().HaveCount(2);
        response.Items.Should().Contain("math");
        response.Items.Should().Contain("english");
    }

    // ==================== GetAllEducationLevels ====================

    [Fact]
    public async Task GetAllEducationLevels_ReturnsDistinctLevels()
    {
        var books = new List<VocabularyBookModel>
        {
            new() { Id = "b1", EducationLevel = "primary" },
            new() { Id = "b2", EducationLevel = "secondary" },
            new() { Id = "b3", EducationLevel = "primary" }
        };
        _mockBookRepo.Setup(x => x.GetAllAsync()).ReturnsAsync(books);

        var response = await _service.GetAllEducationLevels(new Empty(), CreateContext());

        response.Items.Should().HaveCount(2);
        response.Items.Should().Contain("primary");
        response.Items.Should().Contain("secondary");
    }

    // ==================== GetAllGrades ====================

    [Fact]
    public async Task GetAllGrades_ReturnsDistinctGrades()
    {
        var books = new List<VocabularyBookModel>
        {
            new() { Id = "b1", Grade = "1" },
            new() { Id = "b2", Grade = "2" },
            new() { Id = "b3", Grade = "1" }
        };
        _mockBookRepo.Setup(x => x.GetAllAsync()).ReturnsAsync(books);

        var response = await _service.GetAllGrades(new Empty(), CreateContext());

        response.Items.Should().HaveCount(2);
        response.Items.Should().Contain("1");
        response.Items.Should().Contain("2");
    }

    // ==================== GetGradesByEducationLevel ====================

    [Fact]
    public async Task GetGradesByEducationLevel_ReturnsGradesForLevel()
    {
        var books = new List<VocabularyBookModel>
        {
            new() { Id = "b1", EducationLevel = "primary", Grade = "1" },
            new() { Id = "b2", EducationLevel = "primary", Grade = "2" },
            new() { Id = "b3", EducationLevel = "secondary", Grade = "7" }
        };
        _mockBookRepo.Setup(x => x.GetAllAsync()).ReturnsAsync(books);

        var request = new StringRequest { Value = "primary" };

        var response = await _service.GetGradesByEducationLevel(request, CreateContext());

        response.Items.Should().HaveCount(2);
        response.Items.Should().Contain("1");
        response.Items.Should().Contain("2");
        response.Items.Should().NotContain("7");
    }

    // ==================== GetBookWords ====================

    [Fact]
    public async Task GetBookWords_WithEmptyId_ThrowsInvalidArgument()
    {
        var request = new IdRequest { Id = "" };

        var act = async () => await _service.GetBookWords(request, CreateContext());

        var ex = (await act.Should().ThrowAsync<RpcException>()).Subject.Single();
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
        ex.Status.Detail.Should().Contain("BookId is required");
    }

    [Fact]
    public async Task GetBookWords_WithValidId_ReturnsEmptyList()
    {
        // 当前实现 GetWordsAsync 返回空列表（HumanReview HR-01）
        _mockMeaningRepo.Setup(x => x.GetByBookAndVocabularyIdAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(new List<VocabularyMeaningModel>());

        var request = new IdRequest { Id = "book-1" };

        var response = await _service.GetBookWords(request, CreateContext());

        response.Words.Should().BeEmpty();
    }

    // ==================== Delete ====================

    [Fact]
    public async Task Delete_WithEmptyId_ThrowsInvalidArgument()
    {
        var request = new IdRequest { Id = "" };

        var act = async () => await _service.Delete(request, CreateContext());

        var ex = (await act.Should().ThrowAsync<RpcException>()).Subject.Single();
        ex.StatusCode.Should().Be(StatusCode.InvalidArgument);
    }

    [Fact]
    public async Task Delete_WithValidId_ReturnsSuccess()
    {
        var request = new IdRequest { Id = "book-1" };

        var response = await _service.Delete(request, CreateContext());

        response.Success.Should().BeTrue();
        _mockBookRepo.Verify(x => x.DeleteAsync("book-1"), Times.Once);
        _mockUow.Verify(x => x.SaveChangesAsync(), Times.Once);
    }

    [Fact]
    public async Task Delete_WhenDomainThrows_ThrowsInternal()
    {
        _mockBookRepo.Setup(x => x.DeleteAsync(It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("db error"));

        var request = new IdRequest { Id = "book-1" };

        var act = async () => await _service.Delete(request, CreateContext());

        var ex = (await act.Should().ThrowAsync<RpcException>()).Subject.Single();
        ex.StatusCode.Should().Be(StatusCode.Internal);
    }
}
