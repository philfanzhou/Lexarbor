using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Services;

namespace Lexarbor.Learning.Service;

public sealed class LexarborAudioSource(ILexarborClient client) : IVocabularyAudioSource
{
    public async Task<VocabAudio> GetAsync(string wordId, string bookId, string meaningId, string locale,
        CancellationToken cancellationToken = default)
    {
        // A notebook meaning under review has no catalog word yet, so it has no pronunciation (ADR-0027).
        if (bookId == VocabNotebook.SubmissionBookId) throw new VocabAudioUnavailableException();
        try
        {
            var item = await client.GetAudioAsync(wordId, bookId, meaningId, locale, cancellationToken).ConfigureAwait(false);
            return new VocabAudio(item.Locale, item.Status, item.Url, item.ExpiresAt);
        }
        catch (LexarborCatalogException)
        {
            throw new VocabAudioUnavailableException();
        }
    }
}
