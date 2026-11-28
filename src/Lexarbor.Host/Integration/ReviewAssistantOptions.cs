using Lexarbor.Host.Configuration;

namespace Lexarbor.Host.Integration;

public sealed class ReviewAssistantOptions
{
    public string? Endpoint { get; init; }
    public string? Model { get; init; }
    public string? ApiKey { get; init; }
    public int TimeoutSeconds { get; init; } = EnrichmentClientSettings.DefaultTimeoutSeconds;
    public bool AutoReviewEnabled { get; init; }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(Model);
    public bool CanAutomaticallyReview => AutoReviewEnabled && IsEnabled && Model!.Length <= 200;
}
