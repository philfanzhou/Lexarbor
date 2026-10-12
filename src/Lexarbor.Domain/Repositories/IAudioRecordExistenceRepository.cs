namespace Lexarbor.Domain.Repositories;

/// <summary>Includes every asset and upload operation, regardless of binding or state.</summary>
public interface IAudioRecordExistenceRepository
{
    Task<bool> HasAnyAsync(CancellationToken cancellationToken);
}
