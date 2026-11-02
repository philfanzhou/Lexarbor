using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

public interface IEnrichmentAdminConfigurationProvider
{
    Task<EnrichmentAdminConfiguration> CaptureAsync(CancellationToken token);
}
public interface IEnrichmentAdminRepository
{
    Task<EnrichmentAdminPage<EnrichmentAdminTask>> ListAsync(EnrichmentAdminFilter filter,
        EnrichmentAdminConfiguration configuration, long now, CancellationToken token);
    Task<EnrichmentAdminDetail?> DetailAsync(string id, int historyPage, int historySize,
        EnrichmentAdminConfiguration configuration, long now, CancellationToken token);
    Task<EnrichmentAdminSummary> SummaryAsync(EnrichmentAdminFilter filter,
        EnrichmentAdminConfiguration configuration, long now, CancellationToken token);
}
public interface IEnrichmentAdminRetryRepository
{
    // expectedRound, replay and missing/applicable checks join the SAME writer
    // as round creation and dependent preparation invalidation. No HTTP precheck.
    Task<EnrichmentRetryResult> RetryAsync(string id, int expectedRound, string retryKey,
        EnrichmentAdminConfiguration configuration, long now, CancellationToken token);
}
