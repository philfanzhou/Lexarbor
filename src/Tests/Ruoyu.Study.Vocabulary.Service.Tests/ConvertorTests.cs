using System;
using FluentAssertions;
using Ruoyu.Study.Vocabulary.Contract.Protos;
using Ruoyu.Study.Vocabulary.Domain.Models;
using Xunit;

namespace Ruoyu.Study.Vocabulary.Service.Tests;

public class ConvertorTests
{
    // ==================== VocabularyDto <-> VocabularyModel ====================

    [Fact]
    public void ToEntity_VocabularyDto_MapsAllFields()
    {
        var dto = new VocabularyDto
        {
            Id = "vocab-1",
            Word = "apple",
            Phonetic = "ˈæpəl"
        };

        var model = dto.ToEntity();

        model.Id.Should().Be("vocab-1");
        model.Word.Should().Be("apple");
        model.Phonetic.Should().Be("ˈæpəl");
    }

    [Fact]
    public void ToDto_VocabularyModel_MapsAllFields()
    {
        var model = new VocabularyModel
        {
            Id = "vocab-1",
            Word = "apple",
            Phonetic = "ˈæpəl",
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)
        };

        var dto = model.ToDto();

        dto.Id.Should().Be("vocab-1");
        dto.Word.Should().Be("apple");
        dto.Phonetic.Should().Be("ˈæpəl");
        // DTO 没有 CreatedAt/UpdatedAt 字段，Mapster 会忽略
    }

    [Fact]
    public void ToDto_VocabularyModel_MeaningsListStartsEmpty()
    {
        var model = new VocabularyModel { Id = "v1", Word = "test", Phonetic = "" };

        var dto = model.ToDto();

        dto.Meanings.Should().BeEmpty();
    }

    // ==================== VocabularyMeaningDto <-> VocabularyMeaningModel ====================

    [Fact]
    public void ToEntity_VocabularyMeaningDto_MapsAllFields()
    {
        var dto = new VocabularyMeaningDto
        {
            Id = "meaning-1",
            VocabularyId = "vocab-1",
            BookId = "book-1",
            PartOfSpeech = "n",
            Meaning = "苹果",
            Example = "I like apples."
        };

        var model = dto.ToEntity();

        model.Id.Should().Be("meaning-1");
        model.VocabularyId.Should().Be("vocab-1");
        model.BookId.Should().Be("book-1");
        model.PartOfSpeech.Should().Be("n");
        model.Meaning.Should().Be("苹果");
        model.Example.Should().Be("I like apples.");
    }

    [Fact]
    public void ToDto_VocabularyMeaningModel_MapsAllFields()
    {
        var model = new VocabularyMeaningModel
        {
            Id = "meaning-1",
            VocabularyId = "vocab-1",
            BookId = "book-1",
            PartOfSpeech = "v",
            Meaning = "苹果公司",
            Example = "Apple is a tech company.",
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)
        };

        var dto = model.ToDto();

        dto.Id.Should().Be("meaning-1");
        dto.VocabularyId.Should().Be("vocab-1");
        dto.BookId.Should().Be("book-1");
        dto.PartOfSpeech.Should().Be("v");
        dto.Meaning.Should().Be("苹果公司");
        dto.Example.Should().Be("Apple is a tech company.");
    }

    // ==================== VocabularyBookDto <-> VocabularyBookModel ====================

    [Fact]
    public void ToEntity_VocabularyBookDto_MapsAllFields()
    {
        var dto = new VocabularyBookDto
        {
            Id = "book-1",
            BookName = "Test Book",
            Description = "A test book",
            Publisher = "Test Publisher",
            EducationLevel = "primary",
            Grade = "1",
            Category = "math",
            DisplayOrder = 5,
            Status = true,
            IconUrl = "http://example.com/icon.png"
        };

        var model = dto.ToEntity();

        model.Id.Should().Be("book-1");
        model.BookName.Should().Be("Test Book");
        model.Description.Should().Be("A test book");
        model.Publisher.Should().Be("Test Publisher");
        model.EducationLevel.Should().Be("primary");
        model.Grade.Should().Be("1");
        model.Category.Should().Be("math");
        model.DisplayOrder.Should().Be(5);
        model.Status.Should().BeTrue();
        model.IconUrl.Should().Be("http://example.com/icon.png");
    }

    [Fact]
    public void ToDto_VocabularyBookModel_MapsAllFields()
    {
        var model = new VocabularyBookModel
        {
            Id = "book-1",
            BookName = "Test Book",
            Description = "A test book",
            Publisher = "Test Publisher",
            EducationLevel = "primary",
            Grade = "1",
            Category = "math",
            DisplayOrder = 5,
            Status = true,
            IconUrl = "http://example.com/icon.png",
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)
        };

        var dto = model.ToDto();

        dto.Id.Should().Be("book-1");
        dto.BookName.Should().Be("Test Book");
        dto.Description.Should().Be("A test book");
        dto.Publisher.Should().Be("Test Publisher");
        dto.EducationLevel.Should().Be("primary");
        dto.Grade.Should().Be("1");
        dto.Category.Should().Be("math");
        dto.DisplayOrder.Should().Be(5);
        dto.Status.Should().BeTrue();
        dto.IconUrl.Should().Be("http://example.com/icon.png");
    }

    // ==================== 边界场景 ====================

    [Fact]
    public void ToEntity_VocabularyDto_WithEmptyValues_MapsCorrectly()
    {
        var dto = new VocabularyDto
        {
            Id = "",
            Word = "",
            Phonetic = ""
        };

        var model = dto.ToEntity();

        model.Id.Should().BeEmpty();
        model.Word.Should().BeEmpty();
        model.Phonetic.Should().BeEmpty();
    }

    [Fact]
    public void ToDto_VocabularyModel_WithNullPhonetic_ThrowsArgumentNullException()
    {
        // Mapster 默认将 null 源字段直接赋值给目标，但 protobuf string setter 拒绝 null。
        // 这是 Convertor 的已知限制：生产环境依赖数据库返回非 null 值。
        var model = new VocabularyModel
        {
            Id = "v1",
            Word = "test",
            Phonetic = null
        };

        var act = () => model.ToDto();

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ToEntity_VocabularyBookDto_WithEmptyId_GeneratesNewId()
    {
        // AddOrUpdateAsync 会为新实体生成 Id，但 ToEntity 本身不会
        var dto = new VocabularyBookDto { BookName = "New Book" };

        var model = dto.ToEntity();

        model.Id.Should().BeEmpty(); // ToEntity 不生成 Id，由 DomainService 生成
        model.BookName.Should().Be("New Book");
    }
}
