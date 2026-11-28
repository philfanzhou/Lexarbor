using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Host.Configuration;
using Lexarbor.Host.Integration;

namespace Lexarbor.Host;

public sealed class AutomaticReviewStatusProvider(EnrichmentRuntimeConfiguration runtime,
    MeaningSubmissionSettings settings, IMeaningSubmissionAutoReviewRepository repository) : IAutomaticReviewStatusProvider
{
    public async Task<AutomaticReviewStatus> ReadAsync(CancellationToken token)
    {
        var options = runtime.Current.ReviewAssistant;
        var code = PauseCode(options, settings);
        var backlog = await repository.ReadBacklogAsync(token);
        return new(options.AutoReviewEnabled, code != null, code, backlog.PendingCount,
            backlog.LastFailureCode, backlog.LastFailureAt, backlog.NextAttemptAt);
    }
    internal static string? PauseCode(ReviewAssistantOptions options, MeaningSubmissionSettings settings) =>
        !options.AutoReviewEnabled ? "disabled" : !settings.IsConfigured ? "submissions_not_configured"
        : !options.IsEnabled ? "configuration_missing" : !options.CanAutomaticallyReview ? "configuration_invalid" : null;
}
