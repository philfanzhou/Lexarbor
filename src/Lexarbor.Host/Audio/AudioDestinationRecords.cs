using Lexarbor.Domain.Repositories;

namespace Lexarbor.Host.Audio;

public interface IAudioDestinationRecords
{
    Task<bool> HasAnyAsync(CancellationToken cancellationToken);
}

public sealed class AudioDestinationRecords(IServiceScopeFactory scopes) : IAudioDestinationRecords
{
    public async Task<bool> HasAnyAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IAudioRecordExistenceRepository>().HasAnyAsync(cancellationToken);
    }
}
