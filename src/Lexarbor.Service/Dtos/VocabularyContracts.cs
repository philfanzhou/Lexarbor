using System.Text.Json.Serialization;

namespace Lexarbor.Service.Dtos;

/// <summary>
/// Vocabulary DTO (mirrors proto3 VocabularyDto).
/// </summary>
public class VocabularyDto
{
    public string Id { get; set; } = string.Empty;
    public string Word { get; set; } = string.Empty;
    public string? PhoneticUk { get; set; }
    public string? PhoneticUs { get; set; }

    [JsonPropertyName("meanings")]
    public List<VocabularyMeaningDto> Meanings { get; set; } = new();
}

/// <summary>
/// Vocabulary meaning DTO (mirrors proto3 VocabularyMeaningDto).
/// </summary>
public class VocabularyMeaningDto
{
    public string Id { get; set; } = string.Empty;
    public string VocabularyId { get; set; } = string.Empty;
    public string BookId { get; set; } = string.Empty;
    public string? PartOfSpeech { get; set; }
    public string Meaning { get; set; } = string.Empty;
    public string? Example { get; set; }
}

/// <summary>
/// Vocabulary book DTO (mirrors proto3 VocabularyBookDto).
/// </summary>
public class VocabularyBookDto
{
    public string Id { get; set; } = string.Empty;
    public string BookName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Category { get; set; }
    public string? EducationLevel { get; set; }
    public string? Grade { get; set; }
    public string? Publisher { get; set; }

    // Nullable so that a write can tell "the client sent 0/false" apart from
    // "the client left the field out". Both carry a default that silently
    // destroys data on the replace path, so PUT rejects the omission instead of
    // writing the default over the stored value. Responses always populate them.
    public int? DisplayOrder { get; set; }
    public bool? Status { get; set; }
    public string? IconUrl { get; set; }
}

/// <summary>
/// Paginated vocabulary list response.
/// </summary>
public class VocabularyPageResponse
{
    public List<VocabularyDto> Items { get; set; } = new();
    public int TotalPage { get; set; }
    public int TotalCount { get; set; }
}

/// <summary>
/// Paginated vocabulary book list response.
/// </summary>
public class VocabularyBookPageResponse
{
    public List<VocabularyBookDto> Items { get; set; } = new();
    public int TotalPage { get; set; }
    public int TotalCount { get; set; }
}

/// <summary>
/// Vocabulary book list response.
/// </summary>
public class VocabularyBookListResponse
{
    public List<VocabularyBookDto> Books { get; set; } = new();
}

/// <summary>
/// Boolean response.
/// </summary>
public class BoolResponse
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// String list response.
/// </summary>
public class StringListResponse
{
    public List<string> Items { get; set; } = new();
}

// ==================== Request DTOs ====================

public class GetDetailRequest
{
    public string WordId { get; set; } = string.Empty;
    public string BookId { get; set; } = string.Empty;
}

public class AddOrUpdateRequest
{
    public VocabularyDto? Word { get; set; }
    public VocabularyMeaningDto? Meaning { get; set; }
}

public class SearchRequest
{
    public string Keyword { get; set; } = string.Empty;
    public int Page { get; set; }
    public int Size { get; set; }
}

public class GetQuestionRequest
{
    public string WordId { get; set; } = string.Empty;
    public string BookId { get; set; } = string.Empty;
    public bool? ChineseToEnglish { get; set; }
}

public class QuestionResponse
{
    public string Word { get; set; } = string.Empty;
    public List<OptionDto> Options { get; set; } = new();
}

public class OptionDto
{
    public string Meaning { get; set; } = string.Empty;
    public bool IsCorrect { get; set; }
}

public class SearchBookRequest
{
    public string Keyword { get; set; } = string.Empty;
    public int Page { get; set; }
    public int Size { get; set; }
}

public class GetByCategoryRequest
{
    public string Category { get; set; } = string.Empty;
    public string? Grade { get; set; }
}
