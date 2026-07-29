using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ruoyu.Study.Vocabulary.Domain.Exceptions;
using Ruoyu.Study.Vocabulary.Domain.Models;
using Ruoyu.Study.Vocabulary.Domain.Services;
using Xunit;

namespace Ruoyu.Study.Vocabulary.Domain.Tests;

public class VocabularyBookDomainServiceAdditionalTests : TestBase
{
    private readonly VocabularyBookDomainService _service;

    public VocabularyBookDomainServiceAdditionalTests()
    {
        _service = new VocabularyBookDomainService(_bookRepository, _unitOfWork);
    }

    [Fact]
    public async Task AddOrUpdateAsync_NewBook_SetsCreatedAtAndUpdatedAt()
    {
        var book = new VocabularyBookModel
        {
            BookName = "测试词书",
            Category = "ENGLISH",
            DisplayOrder = 1,
            Status = true
        };

        var result = await _service.AddOrUpdateAsync(book);

        Assert.NotEqual(default, result.CreatedAt);
        Assert.NotEqual(default, result.UpdatedAt);
        Assert.Equal(result.CreatedAt, result.UpdatedAt);
    }

    [Fact]
    public async Task AddOrUpdateAsync_NewBook_GeneratesId()
    {
        var book = new VocabularyBookModel
        {
            BookName = "新词书",
            DisplayOrder = 1,
            Status = true
        };

        var result = await _service.AddOrUpdateAsync(book);

        Assert.NotEmpty(result.Id);
        Assert.NotEqual(Guid.Empty.ToString(), result.Id);
    }

    [Fact]
    public async Task AddOrUpdateAsync_ExistingBook_UpdatesAllFields()
    {
        var book = new VocabularyBookModel
        {
            BookName = "原始词书",
            Description = "原始描述",
            Publisher = "原始出版社",
            EducationLevel = "初中",
            Grade = "初一",
            Category = "ENGLISH",
            DisplayOrder = 1,
            Status = true,
            IconUrl = "http://old-icon.png"
        };
        var created = await _service.AddOrUpdateAsync(book);

        var update = new VocabularyBookModel
        {
            Id = created.Id,
            BookName = "更新词书",
            Description = "更新描述",
            Publisher = "更新出版社",
            EducationLevel = "高中",
            Grade = "高一",
            Category = "CHINESE",
            DisplayOrder = 5,
            Status = false,
            IconUrl = "http://new-icon.png"
        };
        var result = await _service.AddOrUpdateAsync(update);

        Assert.Equal(created.Id, result.Id);
        Assert.Equal("更新词书", result.BookName);
        Assert.Equal("更新描述", result.Description);
        Assert.Equal("更新出版社", result.Publisher);
        Assert.Equal("高中", result.EducationLevel);
        Assert.Equal("高一", result.Grade);
        Assert.Equal("CHINESE", result.Category);
        Assert.Equal(5, result.DisplayOrder);
        Assert.False(result.Status);
        Assert.Equal("http://new-icon.png", result.IconUrl);
    }

    [Fact]
    public async Task AddOrUpdateAsync_ExistingBook_UpdatesUpdatedAt()
    {
        var book = new VocabularyBookModel { BookName = "时间测试", DisplayOrder = 1, Status = true };
        var created = await _service.AddOrUpdateAsync(book);
        var originalUpdatedAt = created.UpdatedAt;

        var update = new VocabularyBookModel
        {
            Id = created.Id,
            BookName = "时间测试更新",
            DisplayOrder = 2,
            Status = true
        };
        var result = await _service.AddOrUpdateAsync(update);

        Assert.True(result.UpdatedAt >= originalUpdatedAt);
    }

    [Fact]
    public async Task DeleteAsync_NonExistingBook_ThrowsResourceNotFound()
    {
        await Assert.ThrowsAsync<ResourceNotFoundException>(
            () => _service.DeleteAsync("nonexistent-id"));
    }

    [Fact]
    public async Task SearchAsync_NoKeyword_ReturnsAllBooks()
    {
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "词书A", DisplayOrder = 1, Status = true });
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "词书B", DisplayOrder = 2, Status = true });
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "词书C", DisplayOrder = 3, Status = true });

        var (items, totalCount) = await _service.SearchAsync(null, 1, 10);

        Assert.Equal(3, totalCount);
        Assert.Equal(3, items.Count);
    }

    [Fact]
    public async Task SearchAsync_EmptyKeyword_ReturnsAllBooks()
    {
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "词书X", DisplayOrder = 1, Status = true });

        var (items, totalCount) = await _service.SearchAsync("", 1, 10);

        Assert.Equal(1, totalCount);
    }

    [Fact]
    public async Task SearchAsync_ByDescription_ReturnsMatchingBooks()
    {
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "词书1", Description = "数学专用词汇", DisplayOrder = 1, Status = true });
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "词书2", Description = "英语专用词汇", DisplayOrder = 2, Status = true });

        var (items, totalCount) = await _service.SearchAsync("数学", 1, 10);

        Assert.Equal(1, totalCount);
        Assert.Equal("词书1", items[0].BookName);
    }

    [Fact]
    public async Task SearchAsync_Pagination_Works()
    {
        for (int i = 0; i < 15; i++)
        {
            await _service.AddOrUpdateAsync(new VocabularyBookModel
            {
                BookName = $"词书{i}",
                DisplayOrder = i,
                Status = true
            });
        }

        var (page1, total1) = await _service.SearchAsync(null, 1, 10);
        var (page2, total2) = await _service.SearchAsync(null, 2, 10);

        Assert.Equal(15, total1);
        Assert.Equal(10, page1.Count);
        Assert.Equal(5, page2.Count);
    }

    [Fact]
    public async Task SearchAsync_OrdersByDisplayOrder()
    {
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "第三", DisplayOrder = 3, Status = true });
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "第一", DisplayOrder = 1, Status = true });
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "第二", DisplayOrder = 2, Status = true });

        var (items, _) = await _service.SearchAsync(null, 1, 10);

        Assert.Equal("第一", items[0].BookName);
        Assert.Equal("第二", items[1].BookName);
        Assert.Equal("第三", items[2].BookName);
    }

    [Fact]
    public async Task GetByCategoryAsync_EmptyCategory_ReturnsAllBooks()
    {
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "B1", Category = "ENGLISH", DisplayOrder = 1, Status = true });
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "B2", Category = "CHINESE", DisplayOrder = 2, Status = true });

        var result = await _service.GetByCategoryAsync("", null);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetByCategoryAsync_OrdersByDisplayOrder()
    {
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "C3", Category = "ENGLISH", DisplayOrder = 3, Status = true });
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "C1", Category = "ENGLISH", DisplayOrder = 1, Status = true });
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "C2", Category = "ENGLISH", DisplayOrder = 2, Status = true });

        var result = await _service.GetByCategoryAsync("ENGLISH", null);

        Assert.Equal("C1", result[0].BookName);
        Assert.Equal("C2", result[1].BookName);
        Assert.Equal("C3", result[2].BookName);
    }

    [Fact]
    public async Task GetAllGradesAsync_ReturnsDistinctGrades()
    {
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "B1", Grade = "初一", DisplayOrder = 1, Status = true });
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "B2", Grade = "初一", DisplayOrder = 2, Status = true });
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "B3", Grade = "初二", DisplayOrder = 3, Status = true });

        var grades = await _service.GetAllGradesAsync();

        Assert.Equal(2, grades.Count);
        Assert.Contains("初一", grades);
        Assert.Contains("初二", grades);
    }

    [Fact]
    public async Task GetAllGradesAsync_ExcludesEmptyGrades()
    {
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "B1", Grade = "初一", DisplayOrder = 1, Status = true });
        await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "B2", Grade = "", DisplayOrder = 2, Status = true });

        var grades = await _service.GetAllGradesAsync();

        Assert.Single(grades);
        Assert.Contains("初一", grades);
    }

    [Fact]
    public async Task GetAsync_AfterDelete_ReturnsNull()
    {
        var created = await _service.AddOrUpdateAsync(new VocabularyBookModel { BookName = "临时词书", DisplayOrder = 1, Status = true });
        await _service.DeleteAsync(created.Id);

        var result = await _service.GetAsync(created.Id);

        Assert.Null(result);
    }

    [Fact]
    public async Task AddOrUpdateAsync_MultipleBooks_AllCreated()
    {
        for (int i = 0; i < 5; i++)
        {
            await _service.AddOrUpdateAsync(new VocabularyBookModel
            {
                BookName = $"词书{i}",
                DisplayOrder = i,
                Status = true
            });
        }

        var all = await _service.GetAllAsync();
        Assert.Equal(5, all.Count);
    }
}
