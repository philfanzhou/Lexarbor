using System.Security.Cryptography;
using System.Text;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Host.Configuration;
using Lexarbor.Service;

namespace Lexarbor.Host;

public sealed class EnrichmentServiceGate(IEnrichmentSchedulerRepository repository,
    EnrichmentRuntimeConfiguration runtime, TimeProvider clock, AudioUploadAdmission? uploadAdmission = null)
{
    public static string Identity(string slot, string endpoint, string provider)
    {
        var normalized = Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri)
            ? uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/')
            : endpoint.Trim().TrimEnd('/');
        return slot + "." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(provider.Trim().ToLowerInvariant() + "\n" + normalized)));
    }
    public static string Identity(string service, EnrichmentConfigurationSnapshot snapshot) => service == "text"
        ? Identity("enrichment.text", snapshot.Enrichment.Text.Endpoint, "OpenAICompatible")
        : Identity("enrichment.tts", snapshot.Enrichment.Tts.Endpoint, snapshot.Enrichment.Provider);
    private static string Version(string service, EnrichmentConfigurationSnapshot snapshot) =>
        (service == "text" ? snapshot.TextVersion : snapshot.TtsServiceVersion) ?? "configuration.missing";
    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();
    public Task<EnrichmentServiceAdmission> EnterAsync(string service, bool probe, CancellationToken token)
    {
        var snapshot = runtime.Current;
        return repository.EnterServiceAsync(Identity(service, snapshot), Version(service, snapshot), probe, Now, Now + 360_000, token);
    }
    public Task<long> FinishAsync(EnrichmentServiceAdmission admission, string outcome, string? error,
        bool called, TimeSpan? retryAfter, CancellationToken token, EnrichmentCallCompletion? completion = null)
    {
        var s = runtime.Current.Enrichment;
        return repository.FinishServiceAsync(admission, outcome, error, called, Now,
            retryAfter == null ? null : Now + Math.Max(0, (long)retryAfter.Value.TotalMilliseconds),
            new(s.ServiceFailureThreshold, s.ServicePauseSeconds, s.MaxServicePauseSeconds, s.ThrottleInitialSeconds, s.ThrottleMaxSeconds), token, completion);
    }

    // Independent recovery work, even when all catalog targets are exhausted.
    // Synthetic content is discarded and never authorizes fields or publication.
    public async Task ProbeAsync(string service, IPronunciationPreparationClient preparation,
        IPronunciationSpeechClient speech, CancellationToken token)
    {
        var snapshot = runtime.Current;
        if (!snapshot.CanPrepare || service == "tts" && !snapshot.Enrichment.AudioEnabled) return;
        var status = await repository.ServiceAsync(Identity(service, snapshot), token);
        if (status?.Paused != true || status.Blocked || status.NextAttemptAtUnixMs > Now || status.LeaseExpiresAtUnixMs > Now) return;
        var locale = snapshot.HasTtsConfiguration("uk") ? AudioLocale.EnGb : AudioLocale.EnUs;
        if (service == "tts" && !snapshot.HasTtsConfiguration(locale == AudioLocale.EnGb ? "uk" : "us")) return;
        var word = new AudioWordInput("enrichment-service-probe", "example", null, null);
        var input = AudioSelectionService.GetInput(word, null, locale);
        var revision = AudioSelectionService.ComputeRevision(input);
        var version = new EnrichmentInputVersion(revision, revision, null);
        EnrichmentServiceAdmission? admission = null;
        async Task<bool> Enter(CancellationToken ct)
        { admission = await EnterAsync(service, true, ct); return admission.LeaseId != null; }
        if (service == "text")
        {
            var result = await preparation.PrepareAsync(new(word, null, locale, version, []), token, (_, ct) => Enter(ct));
            if (admission?.LeaseId != null) await FinishAsync(admission, result.Outcome.ToString(), result.ErrorCode, result.RequestMade, result.RetryAfter, token);
        }
        else
        {
            // Reuse a previously validated preparation, including for exhausted
            // targets. The probe cannot complete or publish that target.
            var stored = await repository.ProbePlanAsync(locale, snapshot.TextVersion!, token);
            if (stored == null)
            {
                var other = locale == AudioLocale.EnGb ? AudioLocale.EnUs : AudioLocale.EnGb;
                if (snapshot.HasTtsConfiguration(other == AudioLocale.EnGb ? "uk" : "us"))
                    stored = await repository.ProbePlanAsync(other, snapshot.TextVersion!, token);
            }
            var plan = EnrichmentPreparedPlans.Restore(stored);
            if (plan == null) return;
            using var permit = uploadAdmission == null ? null : await uploadAdmission.EnterAsync(token);
            if (uploadAdmission != null && permit == null) return;
            var result = await speech.GenerateAsync(new(PronunciationPreparationOutcome.Ready, plan), token, (_, ct) => Enter(ct));
            if (admission?.LeaseId != null) await FinishAsync(admission, result.Outcome.ToString(), result.ErrorCode, result.RequestMade, result.RetryAfter, token);
        }
    }
}
