using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Service.Dtos;
using Moq;
using Xunit;

namespace Lexarbor.Service.Tests;

/// <summary>
/// Tests for the Mapster mapping extensions between POCO DTOs and domain models.
/// </summary>
public class VocabularyMappingExtensionsTests
{
    // ==================== VocabularyDto <-> VocabularyModel ====================

    [Fact]
    public void ToEntity_VocabularyDto_MapsAllFields()
    {
        var dto = new VocabularyDto
        {
            Id = "vocab-1",
            Word = "apple",
            PhoneticUk = "ˈæpəl",
            PhoneticUs = "ˈæpəl"
        };

        var model = dto.ToEntity();

        Assert.Equal("vocab-1", model.Id);
        Assert.Equal("apple", model.Word);
        Assert.Equal("ˈæpəl", model.PhoneticUk);
        Assert.Equal("ˈæpəl", model.PhoneticUs);
    }

    [Fact]
    public void ToDto_VocabularyModel_MapsAllFields()
    {
        var model = new VocabularyModel
        {
            Id = "vocab-1",
            Word = "apple",
            PhoneticUk = "ˈæpəl",
            PhoneticUs = "ˈæpəl",
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            UpdatedAt = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)
        };

        var dto = model.ToDto();

        Assert.Equal("vocab-1", dto.Id);
        Assert.Equal("apple", dto.Word);
        Assert.Equal("ˈæpəl", dto.PhoneticUk);
        Assert.Equal("ˈæpəl", dto.PhoneticUs);
    }

    [Fact]
    public void ToDto_VocabularyModel_MeaningsListStartsEmpty()
    {
        var model = new VocabularyModel
        {
            Id = "v1",
            Word = "test",
            PhoneticUk = "",
            PhoneticUs = ""
        };

        var dto = model.ToDto();

        Assert.Empty(dto.Meanings);
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

        Assert.Equal("meaning-1", model.Id);
        Assert.Equal("vocab-1", model.VocabularyId);
        Assert.Equal("book-1", model.BookId);
        Assert.Equal("n", model.PartOfSpeech);
        Assert.Equal("苹果", model.Meaning);
        Assert.Equal("I like apples.", model.Example);
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

        Assert.Equal("meaning-1", dto.Id);
        Assert.Equal("vocab-1", dto.VocabularyId);
        Assert.Equal("book-1", dto.BookId);
        Assert.Equal("v", dto.PartOfSpeech);
        Assert.Equal("苹果公司", dto.Meaning);
        Assert.Equal("Apple is a tech company.", dto.Example);
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

        Assert.Equal("book-1", model.Id);
        Assert.Equal("Test Book", model.BookName);
        Assert.Equal("A test book", model.Description);
        Assert.Equal("Test Publisher", model.Publisher);
        Assert.Equal("primary", model.EducationLevel);
        Assert.Equal("1", model.Grade);
        Assert.Equal("math", model.Category);
        Assert.Equal(5, model.DisplayOrder);
        Assert.True(model.Status);
        Assert.Equal("http://example.com/icon.png", model.IconUrl);
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

        Assert.Equal("book-1", dto.Id);
        Assert.Equal("Test Book", dto.BookName);
        Assert.Equal("A test book", dto.Description);
        Assert.Equal("Test Publisher", dto.Publisher);
        Assert.Equal("primary", dto.EducationLevel);
        Assert.Equal("1", dto.Grade);
        Assert.Equal("math", dto.Category);
        Assert.Equal(5, dto.DisplayOrder);
        Assert.True(dto.Status);
        Assert.Equal("http://example.com/icon.png", dto.IconUrl);
    }

    // ==================== Boundary scenarios ====================

    [Fact]
    public void ToEntity_VocabularyDto_WithEmptyValues_MapsCorrectly()
    {
        var dto = new VocabularyDto
        {
            Id = "",
            Word = "",
            PhoneticUk = "",
            PhoneticUs = ""
        };

        var model = dto.ToEntity();

        Assert.Empty(model.Id);
        Assert.Empty(model.Word);
        // These two are string? on the model, so compare against the empty string
        // rather than Assert.Empty, which would need a null-forgiving operator.
        Assert.Equal(string.Empty, model.PhoneticUk);
        Assert.Equal(string.Empty, model.PhoneticUs);
    }

    [Fact]
    public void ToEntity_VocabularyBookDto_WithEmptyId_GeneratesNewId()
    {
        var dto = new VocabularyBookDto { BookName = "New Book" };

        var model = dto.ToEntity();

        Assert.Empty(model.Id); // ToEntity does not generate Id, DomainService does
        Assert.Equal("New Book", model.BookName);
    }
}
