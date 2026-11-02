using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Host.Configuration;
using Lexarbor.Service;

namespace Lexarbor.Host;

public sealed class MeaningPhoneticWorkVersionsProvider(EnrichmentRuntimeConfiguration runtime)
    : IMeaningPhoneticWorkVersionsProvider
{
    public MeaningPhoneticWorkVersions Capture()
    {
        var snapshot = runtime.Current;
        string Version(EnrichmentStep step, AudioLocale locale) =>
            EnrichmentTargetExecutor.ConfigurationVersion(new("", "specific", locale, step), snapshot);
        return new(Version(EnrichmentStep.Prepare, AudioLocale.EnGb),
            Version(EnrichmentStep.Audio, AudioLocale.EnGb), Version(EnrichmentStep.Audio, AudioLocale.EnUs));
    }
}
