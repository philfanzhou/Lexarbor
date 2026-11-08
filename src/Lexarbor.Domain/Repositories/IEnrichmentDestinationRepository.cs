namespace Lexarbor.Domain.Repositories;

public sealed record EnrichmentDestination(string? BookId, string State, bool Managed);

public interface IEnrichmentDestinationRepository
{
    Task<EnrichmentDestination> ReadAsync(string? configuredBookId, CancellationToken token);
    Task<EnrichmentDestination> EnsureAsync(string? configuredBookId, long now, CancellationToken token);
}
