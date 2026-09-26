namespace Lexarbor.Service.Dtos;

public record VocabularyAdminBookDto(string Id, string BookName, bool Status);
public record VocabularyAdminWordDto(string Id, string Word, string? PhoneticUk, string? PhoneticUs,
    IReadOnlyList<VocabularyAdminBookDto> Books);
public sealed record VocabularyAdminDetailDto(string Id, string Word, string? PhoneticUk, string? PhoneticUs,
    IReadOnlyList<VocabularyAdminBookDto> Books, IReadOnlyList<VocabularyMeaningDto> Meanings)
    : VocabularyAdminWordDto(Id, Word, PhoneticUk, PhoneticUs, Books);
