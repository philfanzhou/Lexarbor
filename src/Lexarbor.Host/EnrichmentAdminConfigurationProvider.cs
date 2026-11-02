using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Host.Configuration;
using Lexarbor.Service;

namespace Lexarbor.Host;

public sealed class EnrichmentAdminConfigurationProvider(EnrichmentRuntimeConfiguration runtime,
    IAudioUploadOperationFactory storage) : IEnrichmentAdminConfigurationProvider
{
    public async Task<EnrichmentAdminConfiguration> CaptureAsync(CancellationToken token)
    {
        var snapshot = runtime.Current;
        using var operation = await storage.AcquireAsync(token);
        string Version(EnrichmentStep step, AudioLocale locale = AudioLocale.EnGb, string? meaning = null) =>
            EnrichmentTargetExecutor.ConfigurationVersion(new("", meaning, locale, step), snapshot);
        return new(snapshot.Enrichment.FieldsEnabled, snapshot.Enrichment.AudioEnabled, operation.Settings.Enabled,
            snapshot.CanPrepare, snapshot.HasTtsConfiguration("uk"), snapshot.HasTtsConfiguration("us"), snapshot.Enrichment.FailureBudget,
            Version(EnrichmentStep.Prepare), Version(EnrichmentStep.Prepare, meaning: "specific"),
            Version(EnrichmentStep.Audio), Version(EnrichmentStep.Audio, AudioLocale.EnUs),
            EnrichmentServiceGate.Identity("text", snapshot), snapshot.TextVersion ?? "configuration.missing",
            EnrichmentServiceGate.Identity("tts", snapshot), snapshot.TtsServiceVersion ?? "configuration.missing");
    }
}
