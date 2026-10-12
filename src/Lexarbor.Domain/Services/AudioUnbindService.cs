using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Domain.Services;

public sealed class AudioUnbindService(IAudioUploadRepository repository)
{
    public Task<AudioUnbindDisposition> UnbindAsync(string wordId, string? meaningId, AudioLocale locale,
        string expectedAssetId, CancellationToken cancellationToken)
        => repository.UnbindAsync(wordId, meaningId, locale, expectedAssetId, cancellationToken);
}
