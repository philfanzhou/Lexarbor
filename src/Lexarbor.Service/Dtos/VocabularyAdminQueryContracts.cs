namespace Lexarbor.Service.Dtos;

public record VocabularyAdminBookDto(string Id, string BookName, bool Status);
public record VocabularyAdminWordDto(string Id, string Word, string? PhoneticUk, string? PhoneticUs,
    IReadOnlyList<VocabularyAdminBookDto> Books);

/// <summary>A unit a meaning of an administrative detail is assigned to.</summary>
public record VocabularyAdminUnitDto(string UnitId, int Number, string? Title);

/// <summary>
/// A meaning of an administrative detail: the fields the public meaning DTO
/// carries plus the units the meaning is assigned to, in unit-number order.
/// The public <c>/api</c> detail keeps its own DTO and does not grow this field.
/// </summary>
public record VocabularyAdminMeaningDetailDto(string Id, string VocabularyId, string BookId, string? PartOfSpeech,
    string Meaning, string? Example, IReadOnlyList<VocabularyAdminUnitDto> Units);

public sealed record VocabularyAdminDetailDto(string Id, string Word, string? PhoneticUk, string? PhoneticUs,
    IReadOnlyList<VocabularyAdminBookDto> Books, IReadOnlyList<VocabularyAdminMeaningDetailDto> Meanings)
    : VocabularyAdminWordDto(Id, Word, PhoneticUk, PhoneticUs, Books);
