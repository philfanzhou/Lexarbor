using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ruoyu.Study.Vocabulary.Domain.Models;
using Ruoyu.Study.Vocabulary.Domain.Services;
using Xunit;

namespace Ruoyu.Study.Vocabulary.Domain.Tests;

public class VocabularyBookDomainServiceTests : TestBase
{
    private readonly VocabularyBookDomainService _service;

    public VocabularyBookDomainServiceTests()
    {
        _service = new VocabularyBookDomainService(_bookRepository, _meaningRepository, _vocabularyRepository, _unitOfWork);
    }

    [Fact]
    public async Task AddOrUpdateAsync_NewBook_CreatesBook()
    {
        var book = new VocabularyBookModel
        {
            BookName = "初中英语词汇",
            Category = "ENGLISH",
            Grade = "初一",
            DisplayOrder = 1,
            Status = true
        };

        var result = await _service.AddOrUpdateAsync(book);

        Assert.NotEmpty(result.Id);
        Assert.Equal("初中英语词汇", result.BookName);
        Assert.Equal("ENGLISH", result.Category);
        Assert.True(result.Status);
    }

    [Fact]
    public async Task AddOrUpdateAsync_ExistingBook_UpdatesBook()
    {
        var book = new VocabularyBookModel
        {
            BookName = "初中英语词汇",
            Category = "ENGLISH",
            DisplayOrder = 1,
            Status = true
        };
        var created = await _service.AddOrUpdateAsync(book);

        var update = new VocabularyBookModel
        {
            Id = created.Id,
            BookName = "高中英语词汇",
            Category = "ENGLISH",
            DisplayOrder = 2,
            Status = false
        };
        var result = await _service.AddOrUpdateAsync(update);

        Assert.Equal(created.Id, result.Id);
        Assert.Equal("高中英语词汇", result.BookName);
        Assert.Equal(2, result.DisplayOrder);
        Assert.False(result.Status);
    }

    [Fact]
    public async Task GetAllAsync_ReturnsAllBooks()
    {
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "Book1", DisplayOrder = 1, Status = true });
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "Book2", DisplayOrder = 2, Status = true });

        var books = await _service.GetAllAsync();

        Assert.Equal(2, books.Count);
    }

    [Fact]
    public async Task GetAsync_ExistingId_ReturnsBook()
    {
        var created = await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "TestBook", DisplayOrder = 1, Status = true });

        var result = await _service.GetAsync(created.Id);

        Assert.NotNull(result);
        Assert.Equal("TestBook", result.BookName);
    }

    [Fact]
    public async Task GetAsync_NonExistingId_ReturnsNull()
    {
        var result = await _service.GetAsync("nonexistent-id");

        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteAsync_ExistingBook_DeletesBook()
    {
        var created = await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "ToDelete", DisplayOrder = 1, Status = true });

        await _service.DeleteAsync(created.Id);

        var result = await _service.GetAsync(created.Id);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetByCategoryAsync_FiltersCorrectly()
    {
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "Book1", Category = "ENGLISH", Grade = "初一", DisplayOrder = 1, Status = true });
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "Book2", Category = "CHINESE", Grade = "初一", DisplayOrder = 2, Status = true });
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "Book3", Category = "ENGLISH", Grade = "初二", DisplayOrder = 3, Status = true });

        var result = await _service.GetByCategoryAsync("ENGLISH", null);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetByCategoryAsync_WithGrade_FiltersCorrectly()
    {
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "Book1", Category = "ENGLISH", Grade = "初一", DisplayOrder = 1, Status = true });
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "Book2", Category = "ENGLISH", Grade = "初二", DisplayOrder = 2, Status = true });

        var result = await _service.GetByCategoryAsync("ENGLISH", "初一");

        Assert.Single(result);
        Assert.Equal("Book1", result[0].BookName);
    }

    [Fact]
    public async Task SearchAsync_ByKeyword_ReturnsMatchingBooks()
    {
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "初中英语", Description = "English vocabulary", DisplayOrder = 1, Status = true });
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "高中英语", Description = "Advanced English", DisplayOrder = 2, Status = true });
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "初中数学", Description = "Math problems", DisplayOrder = 3, Status = true });

        var (items, totalCount) = await _service.SearchAsync("英语", 1, 10);

        Assert.Equal(2, totalCount);
    }

    [Fact]
    public async Task GetAllCategoriesAsync_ReturnsDistinctCategories()
    {
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "B1", Category = "ENGLISH", DisplayOrder = 1, Status = true });
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "B2", Category = "ENGLISH", DisplayOrder = 2, Status = true });
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "B3", Category = "CHINESE", DisplayOrder = 3, Status = true });

        var categories = await _service.GetAllCategoriesAsync();

        Assert.Equal(2, categories.Count);
        Assert.Contains("ENGLISH", categories);
        Assert.Contains("CHINESE", categories);
    }

    [Fact]
    public async Task GetAllEducationLevelsAsync_ReturnsDistinctLevels()
    {
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "B1", EducationLevel = "初中", DisplayOrder = 1, Status = true });
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "B2", EducationLevel = "高中", DisplayOrder = 2, Status = true });

        var levels = await _service.GetAllEducationLevelsAsync();

        Assert.Equal(2, levels.Count);
    }

    [Fact]
    public async Task GetGradesByEducationLevelAsync_ReturnsCorrectGrades()
    {
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "B1", EducationLevel = "初中", Grade = "初一", DisplayOrder = 1, Status = true });
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "B2", EducationLevel = "初中", Grade = "初二", DisplayOrder = 2, Status = true });
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "B3", EducationLevel = "高中", Grade = "高一", DisplayOrder = 3, Status = true });

        var grades = await _service.GetGradesByEducationLevelAsync("初中");

        Assert.Equal(2, grades.Count);
        Assert.Contains("初一", grades);
        Assert.Contains("初二", grades);
    }
}
