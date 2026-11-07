using System.Security.Cryptography;
using System.Text;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Host.Configuration;
using Lexarbor.Service;

namespace Lexarbor.Host;

public sealed class EnrichmentServiceGate(IEnrichmentSchedulerRepository repository,
    EnrichmentRuntimeConfiguration runtime, TimeProvider clock, AudioUploadAdmission? uploadAdmission = null,
    ILogger<EnrichmentServiceGate>? logger = null)
{
    internal LeaseCleanup Observe(EnrichmentServiceAdmission? admission = null) => new(this) { Admission = admission };

    // Covers single-target fallback and probes as well as ordinary batch work.
    // An interrupted request remains unknown in target history, but no longer
    // occupies service capacity until its lease deadline after a graceful stop.
    internal sealed class LeaseCleanup(EnrichmentServiceGate gate) : IAsyncDisposable
    {
        public EnrichmentServiceAdmission? Admission { get; set; }
        private string _outcome = "Unknown";
        private string? _error;
        private bool _called;
        private TimeSpan? _retry;
        private bool _finished;
        public void Record(string outcome, string? error, bool called, TimeSpan? retry)
        { _outcome = outcome; _error = error; _called = called; _retry = retry; }
        public async Task<long> FinishAsync(EnrichmentCallCompletion? completion = null)
        {
            if (Admission?.LeaseId == null) return 0;
            var next = await gate.FinishAsync(Admission, _outcome, _error, _called, _retry, CancellationToken.None, completion);
            _finished = true;
            return next;
        }
        public async ValueTask DisposeAsync()
        {
            if (_finished || Admission?.LeaseId == null) return;
            try { await FinishAsync(); }
            catch (Exception) { gate.LogCleanupFailure(); }
        }
    }

    private void LogCleanupFailure() => logger?.LogWarning("Enrichment service lease cleanup failed; the lease will expire.");
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
    public Task<EnrichmentServiceAvailability> AvailabilityAsync(string service, CancellationToken token)
    {
        var snapshot = runtime.Current;
        return repository.AvailabilityAsync(Identity(service, snapshot), Version(service, snapshot),
            service == "tts" ? snapshot.Enrichment.Concurrency : snapshot.Enrichment.TextConcurrency, Now, token);
    }
    public Task<EnrichmentServiceAdmission> EnterAsync(string service, bool probe, CancellationToken token)
    {
        var snapshot = runtime.Current;
        var timeout = service == "text" ? snapshot.Enrichment.Text.TimeoutSeconds : snapshot.Enrichment.Tts.TimeoutSeconds;
        // Leave time for admission/staging and bounded database completion retries.
        var now = Now;
        return repository.EnterServiceAsync(Identity(service, snapshot), Version(service, snapshot), probe, now,
            now + (timeout + 60L) * 1000, token, service == "tts" ? Math.Clamp(snapshot.Enrichment.Concurrency, 1, 8) : snapshot.Enrichment.TextConcurrency);
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
        await using var cleanup = Observe();
        async Task<bool> Enter(CancellationToken ct)
        { admission = await EnterAsync(service, true, ct); cleanup.Admission = admission; return admission.LeaseId != null; }
        if (service == "text")
        {
            var result = await preparation.PrepareAsync(new(word, null, locale, version, []), token, (_, ct) => Enter(ct));
            cleanup.Record(result.Outcome.ToString(), result.ErrorCode, result.RequestMade, result.RetryAfter);
            if (admission?.LeaseId != null) await cleanup.FinishAsync();
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
            cleanup.Record(result.Outcome.ToString(), result.ErrorCode, result.RequestMade, result.RetryAfter);
            if (admission?.LeaseId != null) await cleanup.FinishAsync();
        }
    }
}
