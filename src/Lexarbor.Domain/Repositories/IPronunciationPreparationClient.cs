using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

/// <summary>Prepares one explicit target/accent, without persistence or TTS.</summary>
public interface IPronunciationPreparationClient
{
    // A task executor supplies beforeDispatch to validate the captured versions
    // and persist BeginCall just before network dispatch. It must close its
    // transaction before returning. Local waiting/invalid input never invokes it;
    // false denies dispatch. The preparation-only client owns no persistence.
    Task<PronunciationPreparationResult> PrepareAsync(PronunciationPreparationRequest request,
        CancellationToken cancellationToken = default,
        Func<PronunciationPreparationDispatch, CancellationToken, Task<bool>>? beforeDispatch = null);
}
