using Lexarbor.Domain.Models;
using Lexarbor.Service.Dtos;
using Mapster;

namespace Lexarbor.Service;

internal static class VocabularyMappingExtensions
{
    public static VocabularyModel ToEntity(this VocabularyDto dto)
    {
        return dto.Adapt<VocabularyModel>();
    }

    public static VocabularyDto ToDto(this VocabularyModel model)
    {
        return model.Adapt<VocabularyDto>();
    }

    public static VocabularyMeaningModel ToEntity(this VocabularyMeaningDto dto)
    {
        return dto.Adapt<VocabularyMeaningModel>();
    }

    public static VocabularyMeaningDto ToDto(this VocabularyMeaningModel model)
    {
        return model.Adapt<VocabularyMeaningDto>();
    }

    public static (VocabularyModel Word, VocabularyMeaningModel Meaning, string? UnitId) ToEntities(
        this VocabularyBatchEntryDto dto,
        string bookId)
    {
        return (
            new VocabularyModel
            {
                Word = dto.Word ?? string.Empty,
                PhoneticUk = dto.PhoneticUk,
                PhoneticUs = dto.PhoneticUs
            },
            new VocabularyMeaningModel
            {
                BookId = bookId,
                PartOfSpeech = dto.PartOfSpeech,
                Meaning = dto.Meaning ?? string.Empty,
                Example = dto.Example
            },
            dto.UnitId);
    }

    public static VocabularyBookModel ToEntity(this VocabularyBookDto dto)
    {
        return dto.Adapt<VocabularyBookModel>();
    }

    public static VocabularyBookDto ToDto(this VocabularyBookModel model)
    {
        return model.Adapt<VocabularyBookDto>();
    }

    public static VocabularyBookUnitDto ToDto(this VocabularyBookUnitModel model, int meaningCount)
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
