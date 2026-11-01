using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

/// <summary>Consumes a successful preparation; no catalog, queue or storage writes.</summary>
public interface IPronunciationSpeechClient
{
    // Executors must validate the captured versions and persist BeginCall inside
    // beforeDispatch, then finish that transaction before returning. False denies
    // dispatch. Local waiting/rejection never invokes it. No automatic retries.
    Task<PronunciationSpeechResult> GenerateAsync(PronunciationPreparationResult preparation,
        CancellationToken cancellationToken = default,
        Func<PronunciationSpeechDispatch, CancellationToken, Task<bool>>? beforeDispatch = null);
}
