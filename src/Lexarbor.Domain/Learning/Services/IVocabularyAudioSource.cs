namespace Lexarbor.Learning.Domain.Services;

/// <summary>Read-only pronunciation projection; signed URLs must never be persisted.</summary>
public sealed record VocabAudio(string Locale, string Status, string? Url, DateTimeOffset? ExpiresAt);

public interface IVocabularyAudioSource
{
    Task<VocabAudio> GetAsync(string wordId, string bookId, string meaningId, string locale,
        CancellationToken cancellationToken = default);
}
