using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

public interface IVocabularyWordEditRepository
{
    Task<VocabularyModel?> GetCurrentAsync(string id);
    Task<bool> HasOtherNormalizedWordAsync(string normalizedWord, string excludedId);
}
