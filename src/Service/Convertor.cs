using Mapster;
using Ruoyu.Study.Vocabulary.Contract.Protos;
using Ruoyu.Study.Vocabulary.Domain.Models;

namespace Ruoyu.Study.Vocabulary.Service;

internal static class Convertor
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

    public static VocabularyBookModel ToEntity(this VocabularyBookDto dto)
    {
        return dto.Adapt<VocabularyBookModel>();
    }

    public static VocabularyBookDto ToDto(this VocabularyBookModel model)
    {
        return model.Adapt<VocabularyBookDto>();
    }
}