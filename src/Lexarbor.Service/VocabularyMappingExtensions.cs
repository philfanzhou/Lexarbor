using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Lexarbor.Service.Dtos;
using Mapster;

namespace Lexarbor.Service;

internal static class VocabularyMappingExtensions
{
    public static Vocabulary ToModel(this VocabularyDto dto)
    {
        return dto.Adapt<Vocabulary>();
    }

    public static VocabularyDto ToDto(this Vocabulary model)
    {
        return model.Adapt<VocabularyDto>();
    }

    public static VocabularyMeaning ToModel(this VocabularyMeaningDto dto)
    {
        return new VocabularyMeaning
        {
            Id = dto.Id,
            VocabularyId = dto.VocabularyId,
            BookId = dto.BookId,
            PartOfSpeech = dto.PartOfSpeech,
            Meaning = dto.Meaning,
            Example = dto.Example
        };
    }

    public static VocabularyMeaningDto ToDto(this VocabularyMeaning model, Vocabulary word)
    {
        var dto = model.Adapt<VocabularyMeaningDto>();
        dto.Pronunciation = AudioSelectionService.GetPronunciation(
            new(word.Id, word.Word, word.PhoneticUk, word.PhoneticUs),
            new(model.Id, model.PartOfSpeech, model.Meaning, model.PhoneticUk, model.PhoneticUs));
        return dto;
    }

    public static (Vocabulary Word, VocabularyMeaning Meaning, string? UnitId, string? Section, string? EntryKind) ToEntities(
        this VocabularyBatchEntryDto dto,
        string bookId)
    {
        return (
            new Vocabulary
            {
                Word = dto.Word ?? string.Empty,
                PhoneticUk = dto.PhoneticUk,
                PhoneticUs = dto.PhoneticUs
            },
            new VocabularyMeaning
            {
                BookId = bookId,
                PartOfSpeech = dto.PartOfSpeech,
                Meaning = dto.Meaning ?? string.Empty,
                Example = dto.Example
            },
            dto.UnitId,
            dto.Section,
            dto.EntryKind);
    }

    public static VocabularyBook ToModel(this VocabularyBookDto dto)
    {
        return dto.Adapt<VocabularyBook>();
    }

    public static VocabularyBookDto ToDto(this VocabularyBook model)
    {
        return model.Adapt<VocabularyBookDto>();
    }

    public static VocabularyBookUnitDto ToDto(this VocabularyBookUnit model, int meaningCount)
    {
        return new VocabularyBookUnitDto
        {
            Id = model.Id,
            BookId = model.BookId,
            Number = model.Number,
            Title = model.Title,
            MeaningCount = meaningCount
        };
    }
}
