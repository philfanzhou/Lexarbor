using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

public interface IVocabularyWordEditRepository
{
    Task<Vocabulary?> GetCurrentAsync(string id);
    Task<bool> HasOtherNormalizedWordAsync(string normalizedWord, string excludedId);
    Task InvalidateRelationsAsync(string wordId, long nowUnixMs);
}
