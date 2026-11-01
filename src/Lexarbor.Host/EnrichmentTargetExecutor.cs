using System.Text.Json;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Host.Configuration;
using Lexarbor.Service;

namespace Lexarbor.Host;

/// <summary>One explicit target; no discovery, timers, batch API or automatic startup.</summary>
public sealed class EnrichmentTargetExecutor(IEnrichmentTaskRepository tasks, IEnrichmentExecutionRepository catalog,
    IAudioUploadRepository uploads, IPronunciationPreparationClient preparation, IPronunciationSpeechClient speech,
    IAudioUploadOperationFactory operations, AudioUploadAdmission admission, EnrichmentRuntimeConfiguration runtime,
    TimeProvider clock, EnrichmentServiceGate? services = null)
{
    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();
    // Stable relevant versions, independent of the HTTP settings ETag. Audio
    // depends on text and its own accent, never the other accent or review AI.
    public static string ConfigurationVersion(EnrichmentTargetKey key, EnrichmentConfigurationSnapshot snapshot) =>
        key.Step == EnrichmentStep.Audio
            ? (snapshot.TextVersion ?? "configuration.missing") + "." +
                ((key.Locale == AudioLocale.EnGb ? snapshot.UkTtsVersion : snapshot.UsTtsVersion) ?? "configuration.missing")
            : (snapshot.TextVersion ?? "configuration.missing") + (key.MeaningId == null && snapshot.Enrichment.FieldsEnabled ? ".fields" : ".audio");

    public async Task<EnrichmentTask?> ExecuteAsync(string targetId, string owner, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var task = await tasks.GetAsync(targetId, token);
        if (task == null || task.Deleted) return task;
        // A successful permanent receipt wins before Ensure compares ownBinding.
        // Never replace its key or make a fresh TTS request on an unknown read.
        if (task.Target.Step == EnrichmentStep.Audio)
            foreach (var call in task.Calls.Where(call => call.Round == task.CurrentRound && call.UploadIdempotencyKey != null
                && call.Result is EnrichmentCallResult.Started or EnrichmentCallResult.Succeeded))
                if (await tasks.RecoverPublishedAsync(task.Id, call.UploadIdempotencyKey!, Now, token)) return await GetAsync(task.Id, token);

        task = await RefreshAsync(task, token);
        if (task == null) return null;
        var snapshot = runtime.Current;
        var audioOnlyPreparation = task.Target.Step == EnrichmentStep.Prepare
            && (task.Target.MeaningId != null || !snapshot.Enrichment.FieldsEnabled);
        var enabled = task.Target.Step == EnrichmentStep.Audio ? snapshot.Enrichment.AudioEnabled
            : task.Target.Step == EnrichmentStep.Fields ? snapshot.Enrichment.FieldsEnabled
            : audioOnlyPreparation ? snapshot.Enrichment.AudioEnabled
            : snapshot.Enrichment.FieldsEnabled || snapshot.Enrichment.AudioEnabled;
        // Do not prepare again for ordinary existing/stale own audio.
        var currentInput = await tasks.ReadInputAsync(task.Target, token);
        var needsAudio = task.Target.Step == EnrichmentStep.Audio && currentInput?.OwnAssetId == null;
        if (enabled && (needsAudio || audioOnlyPreparation))
        {
            using var storage = await operations.AcquireAsync(token);
            if (!storage.Settings.Enabled)
            {
                var waitingGuard = await tasks.ClaimAsync(task.Id, owner, Now, Now + 900_000, token);
                return waitingGuard == null ? await GetAsync(task.Id, token)
                    : await WaitAsync(waitingGuard, EnrichmentWaitReason.StorageDisabled, 30_000, token);
            }
        }
        EnrichmentTask? preparedTask = null;
        if (enabled && (needsAudio || task.Target.Step == EnrichmentStep.Fields))
        {
            var key = task.Target with { Step = EnrichmentStep.Prepare };
            preparedTask = await tasks.EnsureAsync(key, ConfigurationVersion(key, snapshot), Now, token, snapshot.Enrichment.FailureBudget);
            if (preparedTask != null) preparedTask = await ExecuteAsync(preparedTask.Id, owner, token);
            // Our own field commit can change the source. Advance by Ensure once,
            // then claim that current round; no unbounded inline retry loop.
            task = await RefreshAsync(task, token);
            if (task == null) return null;
        }
        var guard = await tasks.ClaimAsync(task.Id, owner, Now, Now + 900_000, token);
        if (guard == null) return await GetAsync(task.Id, token);
        if (!enabled) return await WaitAsync(guard, EnrichmentWaitReason.Disabled, 30_000, token);
        if (task.Target.Step == EnrichmentStep.Audio && guard.Input.OwnAssetId != null)
        { await tasks.CompleteAsync(guard, Now, token); return await GetAsync(task.Id, token); }
        if (task.Target.Step == EnrichmentStep.Prepare) return await PrepareAsync(task, guard, token);

        var preparedRound = preparedTask?.Rounds.Single(round => round.Number == preparedTask.CurrentRound);
        if (preparedRound?.State != EnrichmentState.Succeeded || EnrichmentPreparedPlans.Restore(preparedRound.Plan) is not { } ready)
        {
            if (preparedRound?.State == EnrichmentState.Failed)
                await tasks.StopAsync(guard, "preparation.failed", Now, token);
            else return await WaitAsync(guard, EnrichmentWaitReason.ConfigurationMissing, 30_000, token);
            return await GetAsync(task.Id, token);
        }
        if (task.Target.Step == EnrichmentStep.Fields)
        {
            var fields = await catalog.ReadAsync(task.Target, token);
            if (fields == null || fields.Request.InputVersion != guard.Input) return await RefreshAsync(task, token);
            var missing = ready.EntryKind != "phrase" && (string.IsNullOrWhiteSpace(task.Target.Locale == AudioLocale.EnGb
                ? fields.Request.Word.PhoneticUk : fields.Request.Word.PhoneticUs)
                || fields.Request.Meanings.Any(value => string.IsNullOrWhiteSpace(value.Meaning.PartOfSpeech)
                    && !value.PositionKinds.Contains("phrase", StringComparer.Ordinal)));
            if (preparedRound.Plan!.Preparation!.ConflictingMeaningIds?.Count > 0)
                await tasks.StopAsync(guard, "fields.equivalent_meaning", Now, token);
            else if (missing) await tasks.StopAsync(guard, "fields.candidates_missing", Now, token);
            else await tasks.CompleteAsync(guard, Now, token);
            return await GetAsync(task.Id, token);
        }
        return await AudioAsync(task, guard, ready, token);
    }

    private async Task<EnrichmentTask?> PrepareAsync(EnrichmentTask task, EnrichmentExecutionGuard guard, CancellationToken token)
    {
        var current = await catalog.ReadAsync(task.Target, token);
        if (current == null || current.Request.InputVersion != guard.Input) return await RefreshAsync(task, token);
        var round = (await GetAsync(task.Id, token))!.Rounds.Single(value => value.Number == guard.Round);
        var stored = round.Plan;
        var open = task.Calls.SingleOrDefault(call => call.Round == guard.Round && call.Result == EnrichmentCallResult.Started);
        if (open != null)
        {
            await tasks.FinishCallAsync(guard, open.Id, EnrichmentPreparedPlans.Restore(stored) != null
                ? EnrichmentCallResult.Succeeded : EnrichmentCallResult.TransientFailure,
                stored?.Preparation != null ? null : "text.outcome_unknown", Now, stored?.Preparation != null ? null : Now + 30_000, token);
            if (stored?.Preparation == null) return await GetAsync(task.Id, token);
        }
        if (EnrichmentPreparedPlans.Restore(stored) == null)
        {
            var callId = Guid.NewGuid().ToString();
            var called = false;
            var callFinished = false;
            EnrichmentServiceAdmission? service = null;
            var result = await preparation.PrepareAsync(current.Request, token, async (dispatch, ct) =>
            {
                if (dispatch.InputVersion != guard.Input || dispatch.ConfigurationVersion != runtime.Current.TextVersion
                    || ConfigurationVersion(task.Target, runtime.Current) != round.ConfigurationVersion) return false;
                if (services != null)
                {
                    service = await services.EnterAsync("text", false, ct);
                    if (service.LeaseId == null) return false;
                }
                called = await tasks.BeginCallAsync(guard, callId, "text", null, Now, ct);
                return called;
            });
            if (service?.WaitReason is { } reason)
                return await WaitAsync(guard, reason, Math.Max(1000, service.NextAttemptAtUnixMs - Now), token);
            if (service?.LeaseId != null)
            {
                var completion = called && result.RequestMade ? Completion(guard, callId, "text", result.Outcome.ToString(), result.ErrorCode, result.RetryAfter) : null;
                var next = await services!.FinishAsync(service, result.Outcome.ToString(), result.ErrorCode, called && result.RequestMade, result.RetryAfter, token, completion);
                callFinished = completion != null;
                if (result.Outcome == PronunciationPreparationOutcome.Throttled) result = result with { RetryAfter = TimeSpan.FromMilliseconds(Math.Max(1000, next - Now)) };
            }
            if (result.Outcome != PronunciationPreparationOutcome.Ready || result.Plan == null)
            {
                await RecordAsync(guard, callId, called, "text", result.Outcome.ToString(), result.ErrorCode,
                    result.RetryAfter, token, callFinished);
                return await RefreshOrGetAsync(task, result.Outcome == PronunciationPreparationOutcome.Superseded, token);
            }
            stored = EnrichmentPreparedPlans.Store(result.Plan, current.OwnAssetId);
            if (JsonSerializer.Serialize(stored).Length > 16384)
            {
                await RecordAsync(guard, callId, called, "text", "InvalidPlan", "plan_too_large", null, token);
                return await GetAsync(task.Id, token);
            }
            if (!await tasks.SavePlanAsync(guard, stored, Now, token)) return await RefreshAsync(task, token);
            if (called && !await tasks.FinishCallAsync(guard, callId, EnrichmentCallResult.Succeeded, null, Now, null, token))
                return await RefreshAsync(task, token);
        }
        if (task.Target.MeaningId == null && runtime.Current.Enrichment.FieldsEnabled)
        {
            EnrichmentFieldCommit? committed;
            try { committed = await catalog.CommitFieldsAsync(guard, stored!, Now, token); }
            catch (DomainValidationException)
            {
                await tasks.StopAsync(guard, "fields.plan_too_large", Now, token);
                return await GetAsync(task.Id, token);
            }
            if (committed == null)
            {
                var latest = await catalog.ReadAsync(task.Target, token);
                if (latest?.Request.InputVersion == guard.Input && latest.OwnAssetId != stored!.Preparation!.OwnAssetId)
                    return await tasks.EnsureAsync(task.Target, ConfigurationVersion(task.Target, runtime.Current), Now,
                        token, runtime.Current.Enrichment.FailureBudget, Guid.NewGuid().ToString());
                return await RefreshAsync(task, token);
            }
            guard = committed.Guard;
        }
        await tasks.CompleteAsync(guard, Now, token);
        return await GetAsync(task.Id, token);
    }

    private async Task<EnrichmentTask?> AudioAsync(EnrichmentTask task, EnrichmentExecutionGuard guard,
        PreparedPronunciation prepared, CancellationToken token)
    {
        var current = await catalog.ReadAsync(task.Target, token);
        if (current == null || current.Request.InputVersion != guard.Input || prepared.InputVersion.SourceRevision != guard.Input.SourceRevision
            || prepared.ConfigurationVersion != runtime.Current.TextVersion) return await RefreshAsync(task, token);
        var round = (await GetAsync(task.Id, token))!.Rounds.Single(value => value.Number == guard.Round);
        var open = task.Calls.SingleOrDefault(call => call.Round == guard.Round && call.Result == EnrichmentCallResult.Started);
        if (open != null)
        {
            var previous = await uploads.GetAsync(open.UploadIdempotencyKey!, token);
            if (previous?.Operation.Receipt == null && previous != null)
            {
                if (previous.Operation.UploadLease.ExpiresAtUnixMs > Now) return await GetAsync(task.Id, token);
                await uploads.ExpireAsync(open.UploadIdempotencyKey!, Now, token);
                previous = await uploads.GetAsync(open.UploadIdempotencyKey!, token);
                if (previous?.Operation.Receipt == null) return await GetAsync(task.Id, token);
            }
            await tasks.FinishCallAsync(guard, open.Id, EnrichmentCallResult.TransientFailure,
                "tts.outcome_unknown", Now, Now + 30_000, token);
            return await GetAsync(task.Id, token);
        }
        prepared = prepared with { InputVersion = guard.Input };
        if (!await tasks.SavePlanAsync(guard, EnrichmentPreparedPlans.Store(prepared, current.OwnAssetId), Now, token))
            return await RefreshAsync(task, token);
        using var operation = await operations.AcquireAsync(token);
        if (!operation.Settings.Enabled) return await WaitAsync(guard, EnrichmentWaitReason.StorageDisabled, 30_000, token);
        using var permit = await admission.EnterAsync(token);
        if (permit == null) return await WaitAsync(guard, EnrichmentWaitReason.Throttled, 1000, token);
        var callId = Guid.NewGuid().ToString(); var uploadKey = Guid.NewGuid().ToString(); var called = false;
        var callFinished = false;
        EnrichmentServiceAdmission? service = null;
        var result = await speech.GenerateAsync(new(PronunciationPreparationOutcome.Ready, prepared), token, async (dispatch, ct) =>
        {
            if (dispatch.InputVersion != guard.Input || dispatch.TextConfigurationVersion != runtime.Current.TextVersion
                || dispatch.TtsConfigurationVersion != (task.Target.Locale == AudioLocale.EnGb ? runtime.Current.UkTtsVersion : runtime.Current.UsTtsVersion)
                || ConfigurationVersion(task.Target, runtime.Current) != round.ConfigurationVersion) return false;
            if (services != null)
            {
                service = await services.EnterAsync("tts", false, ct);
                if (service.LeaseId == null) return false;
            }
            called = await tasks.BeginCallAsync(guard, callId, "tts", uploadKey, Now, ct);
            return called;
        });
        if (service?.WaitReason is { } reason)
            return await WaitAsync(guard, reason, Math.Max(1000, service.NextAttemptAtUnixMs - Now), token);
        if (service?.LeaseId != null)
        {
            var completion = called && result.RequestMade ? Completion(guard, callId, "tts", result.Outcome.ToString(), result.ErrorCode, result.RetryAfter) : null;
            var next = await services!.FinishAsync(service, result.Outcome.ToString(), result.ErrorCode, called && result.RequestMade, result.RetryAfter, token, completion);
            callFinished = completion != null;
            if (result.Outcome == PronunciationSpeechOutcome.Throttled) result = result with { RetryAfter = TimeSpan.FromMilliseconds(Math.Max(1000, next - Now)) };
        }
        if (result.Outcome != PronunciationSpeechOutcome.Ready || result.Audio == null)
        {
            await RecordAsync(guard, callId, called, "tts", result.Outcome.ToString(), result.ErrorCode, result.RetryAfter, token, callFinished);
            return await RefreshOrGetAsync(task, result.Outcome == PronunciationSpeechOutcome.Superseded, token);
        }
        if (!AudioUploadContentValidation.IsValid(result.Audio, operation.Settings.MaxBytes, generated: true))
        {
            await RecordAsync(guard, callId, called, "tts", "InvalidAudio", "invalid_audio", null, token);
            return await GetAsync(task.Id, token);
        }
        var uploaded = await operation.Service.UploadAsync(new(uploadKey, task.Target.WordId, task.Target.MeaningId,
            task.Target.Locale, guard.Input.SourceRevision, guard.Input.OwnAssetId, result.Audio, "enrichment", null), token, guard);
        if (uploaded.StatusCode == 200)
        {
            await tasks.RecoverPublishedAsync(task.Id, uploadKey, Now, token);
            return await GetAsync(task.Id, token);
        }
        await RecordAsync(guard, callId, called, "audio", "ServiceFailure", "publication_failed", null, token);
        return await RefreshOrGetAsync(task, uploaded.StatusCode is 404 or 409, token);
    }

    private async Task RecordAsync(EnrichmentExecutionGuard guard, string callId, bool called, string service,
        string outcome, string? error, TimeSpan? retry, CancellationToken token, bool finished = false)
    {
        var code = service + "." + (error ?? "failed");
        if (outcome == "Superseded") return;
        if (!called)
        {
            if (outcome == "Waiting") await tasks.WaitAsync(guard, error == "disabled" ? EnrichmentWaitReason.Disabled
                : EnrichmentWaitReason.ConfigurationMissing, Now, Now + 30_000, token);
            else await tasks.StopAsync(guard, code, Now, token);
            return;
        }
        if (finished) return;
        var completion = Completion(guard, callId, service, outcome, error, retry)!;
        await tasks.FinishCallAsync(guard, callId, completion.Result, completion.ErrorCode, Now, completion.NextAttemptAtUnixMs, token);
    }

    private EnrichmentCallCompletion? Completion(EnrichmentExecutionGuard guard, string callId, string service,
        string outcome, string? error, TimeSpan? retry)
    {
        if (outcome is "Ready" or "Superseded") return null;
        var terminal = outcome is "Rejected" or "InvalidInput" or "InvalidPlan";
        var throttled = outcome == "Throttled";
        var next = Now + Math.Max(1000, (long)(retry ?? TimeSpan.FromSeconds(throttled ? runtime.Current.Enrichment.ThrottleInitialSeconds : 30)).TotalMilliseconds);
        return new(guard, callId, throttled ? EnrichmentCallResult.Throttled
            : terminal ? EnrichmentCallResult.TerminalFailure : EnrichmentCallResult.TransientFailure,
            service + "." + (error ?? "failed"), terminal ? null : next);
    }

    private Task<EnrichmentTask?> GetAsync(string id, CancellationToken token) => tasks.GetAsync(id, token);
    private Task<EnrichmentTask?> RefreshAsync(EnrichmentTask task, CancellationToken token) => tasks.EnsureAsync(task.Target,
        ConfigurationVersion(task.Target, runtime.Current), Now, token, runtime.Current.Enrichment.FailureBudget);
    private Task<EnrichmentTask?> RefreshOrGetAsync(EnrichmentTask task, bool refresh, CancellationToken token) =>
        refresh ? RefreshAsync(task, token) : GetAsync(task.Id, token);
    private async Task<EnrichmentTask?> WaitAsync(EnrichmentExecutionGuard guard, EnrichmentWaitReason reason, long delay, CancellationToken token)
    { await tasks.WaitAsync(guard, reason, Now, Now + delay, token); return await GetAsync(guard.TargetId, token); }
}
