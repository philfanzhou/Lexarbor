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
/// One unit of an enabled book, as <c>GET /api/vocabulary-books/{bookId}/units</c>
/// reports it. The counts are per distinct meaning and word, matching the
/// administrative unit-content counts.
/// </summary>
public record VocabularyPublicUnitDto(string Id, int Number, string? Title, int WordCount, int MeaningCount);

/// <summary>Unit list response of the anonymous book-browse endpoint.</summary>
public class VocabularyPublicUnitListResponse
{
    public List<VocabularyPublicUnitDto> Units { get; set; } = new();
}

/// <summary>
/// One place an entry holds inside the requested scope; <c>Section</c> and
/// <c>EntryKind</c> are null when the place is unsectioned or unclassified.
/// </summary>
public record VocabularyPublicEntryPositionDto(string UnitId, int UnitNumber, string? Section, string? EntryKind);

/// <summary>
/// One meaning as <c>GET /api/vocabulary-books/{bookId}/entries</c> reports
/// it. <c>NormalizedWord</c> and <c>MeaningKey</c> are the server's
/// <c>lower(trim(...))</c> comparison keys and should be treated as opaque.
/// </summary>
public record VocabularyPublicEntryDto(
    string WordId,
    string Word,
    string NormalizedWord,
    string? PhoneticUk,
    string? PhoneticUs,
    string MeaningId,
    string? PartOfSpeech,
    string Meaning,
    string MeaningKey,
    string? Example,
    List<VocabularyPublicEntryPositionDto> Positions);

/// <summary>
/// One page of entries: <c>TotalCount</c> counts meanings in the scope and
/// <c>WordCount</c> the distinct words behind them.
/// </summary>
public class VocabularyPublicEntryPageResponse
{
    public List<VocabularyPublicEntryDto> Items { get; set; } = new();
    public int TotalPage { get; set; }
    public int TotalCount { get; set; }
    public int WordCount { get; set; }
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

/// <summary>
/// Body of <c>POST /admin/vocabulary/batch</c>. ADR-005 defines its semantics.
/// </summary>
public class VocabularyBatchImportRequest
{
    public string? BookId { get; set; }
    public List<VocabularyBatchEntryDto?>? Entries { get; set; }
}

/// <summary>
/// One row of a batch import. Blank optional fields count as absent.
/// </summary>
public class VocabularyBatchEntryDto
{
    public string? Word { get; set; }
    public string? PhoneticUk { get; set; }
    public string? PhoneticUs { get; set; }
    public string? PartOfSpeech { get; set; }
    public string? Meaning { get; set; }
    public string? Example { get; set; }

    /// <summary>
    /// Optional unit of <c>bookId</c> the resolved meaning is assigned to. Must
    /// name an existing unit of that book; units are never created here.
    /// </summary>
    public string? UnitId { get; set; }

    /// <summary>
    /// Optional section of that unit the assignment sits in: exactly <c>A</c>
    /// or <c>B</c> after trimming, case significant. Requires <c>unitId</c>; a
    /// blank value is no section.
    /// </summary>
    public string? Section { get; set; }

    /// <summary>
    /// Optional classification of the entry at that place: exactly <c>word</c>
    /// or <c>phrase</c> after trimming, case significant — <c>Word</c> is
    /// invalid. Requires <c>unitId</c>, because the kind is a property of an
    /// assignment's position; a blank value is unclassified, and nothing is
    /// ever inferred from the entry's text. A <c>phrase</c> entry must leave
    /// <c>phoneticUk</c>, <c>phoneticUs</c> and <c>partOfSpeech</c> blank.
    /// </summary>
    public string? EntryKind { get; set; }
}

/// <summary>
/// Counts returned by a successful batch import; <c>total = created + reused</c>.
/// </summary>
public class VocabularyBatchImportResponse
{
    public int Total { get; set; }
    public int Created { get; set; }
    public int Reused { get; set; }
}

/// <summary>
/// Why one entry of a rejected batch is invalid; <c>index</c> is its zero-based
/// position in <c>entries</c>.
/// </summary>
public class VocabularyBatchEntryError
{
    public int Index { get; set; }
    public string Message { get; set; } = string.Empty;
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
