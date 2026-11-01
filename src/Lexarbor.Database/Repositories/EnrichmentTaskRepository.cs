using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lexarbor.Database.Entities;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed class EnrichmentTaskRepository(VocabularyDbContext context, TimeProvider? timeProvider = null)
    : IEnrichmentTaskRepository
{
    private readonly UnitOfWork _unitOfWork = new(context);
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly HashSet<(Guid Transaction, EnrichmentExecutionGuard Guard)> _validated = [];
    private IQueryable<EnrichmentTargetEntity> Targets => context.EnrichmentTargets.AsNoTracking();
    private IQueryable<EnrichmentRoundEntity> Rounds => context.EnrichmentRounds.AsNoTracking();

    public Task<EnrichmentTask?> GetAsync(string targetId, CancellationToken cancellationToken) =>
        ReadAsync(() => GetCoreAsync(targetId, cancellationToken), cancellationToken);

    public Task<EnrichmentInputVersion?> ReadInputAsync(EnrichmentTargetKey target, CancellationToken cancellationToken) =>
        ReadAsync(() => InputAsync(target, cancellationToken), cancellationToken);

    public Task<EnrichmentTask?> EnsureAsync(EnrichmentTargetKey target, string configurationVersion, long now,
        CancellationToken cancellationToken, int failureBudget = 5, string? retryKey = null)
    {
        ValidateOptions(target, configurationVersion, failureBudget, retryKey);
        return WriteAsync(() => EnsureCoreAsync(target, configurationVersion, Now(now), failureBudget, retryKey, false), cancellationToken);
    }

    public Task<EnrichmentTask?> InvalidateInTransactionAsync(EnrichmentTargetKey target, string configurationVersion,
        long now, CancellationToken cancellationToken, int failureBudget = 5)
    {
        RequireTransaction(cancellationToken);
        ValidateOptions(target, configurationVersion, failureBudget, null);
        return EnsureCoreAsync(target, configurationVersion, Now(now), failureBudget, null, true);
    }

    private async Task<EnrichmentTask?> EnsureCoreAsync(EnrichmentTargetKey key, string config, long now, int budget,
        string? retryKey, bool invalidate)
    {
        var target = await Targets.SingleOrDefaultAsync(row => row.WordId == key.WordId && row.MeaningId == key.MeaningId
            && row.Locale == key.Locale && row.Step == key.Step);
        if (target?.Deleted == true) return await GetCoreAsync(target.Id, CancellationToken.None);
        var input = await InputAsync(key, CancellationToken.None);
        if (input == null)
        {
            if (target == null) return null;
            await CancelAsync(target, now);
            return await GetCoreAsync(target.Id, CancellationToken.None);
        }
        EnrichmentRoundEntity? previous = null;
        if (target != null)
        {
            previous = await Rounds.SingleAsync(row => row.TargetId == target.Id && row.Number == target.CurrentRound);
            var missingEnvelope = key.Step == EnrichmentStep.Prepare && previous.State == EnrichmentState.Succeeded
                && (previous.PlanJson == null || JsonSerializer.Deserialize<EnrichmentPlan>(previous.PlanJson)?.Preparation == null);
            if (!invalidate && !missingEnvelope && previous.State == EnrichmentState.Succeeded && Version(previous) == input
                && (key.Step != EnrichmentStep.Prepare || previous.ConfigurationVersion == config))
                return await GetCoreAsync(target.Id, CancellationToken.None);
            if (retryKey != null && await Rounds.AnyAsync(row => row.TargetId == target.Id && row.RetryKey == retryKey))
                return await GetCoreAsync(target.Id, CancellationToken.None);
            if (!invalidate && !missingEnvelope && retryKey == null && Version(previous) == input && previous.ConfigurationVersion == config && previous.State != EnrichmentState.Cancelled)
                return await GetCoreAsync(target.Id, CancellationToken.None);
            if (previous.State is EnrichmentState.Pending or EnrichmentState.Running or EnrichmentState.Waiting)
                await RoundQuery(target.Id, target.CurrentRound).ExecuteUpdateAsync(set => set
                    .SetProperty(row => row.State, EnrichmentState.Superseded).SetProperty(row => row.LeaseOwner, (string?)null)
                    .SetProperty(row => row.LeaseExpiresAtUnixMs, (long?)null).SetProperty(row => row.UpdatedAtUnixMs, now));
            await context.EnrichmentTargets.Where(row => row.Id == target.Id).ExecuteUpdateAsync(set => set
                .SetProperty(row => row.CurrentRound, row => row.CurrentRound + 1).SetProperty(row => row.Epoch, row => row.Epoch + 1));
            target.CurrentRound++; target.Epoch++;
        }
        else
        {
            target = new()
            {
                Id = Guid.NewGuid().ToString(),
                WordId = key.WordId,
                MeaningId = key.MeaningId,
                Locale = key.Locale,
                Step = key.Step,
                CurrentRound = 1,
                Epoch = 1
            };
            context.EnrichmentTargets.Add(target);
        }
        var round = new EnrichmentRoundEntity
        {
            TargetId = target.Id,
            Number = target.CurrentRound,
            State = EnrichmentState.Pending,
            InputRevision = input.Revision,
            SourceRevision = input.SourceRevision,
            OwnAssetId = input.OwnAssetId,
            ConfigurationVersion = config,
            RetryKey = retryKey,
            FailureBudget = budget,
            CreatedAtUnixMs = now,
            UpdatedAtUnixMs = now
        };
        context.EnrichmentRounds.Add(round);
        try { await context.SaveChangesAsync(); }
        finally { context.Entry(target).State = EntityState.Detached; context.Entry(round).State = EntityState.Detached; }
        return await GetCoreAsync(target.Id, CancellationToken.None);
    }

    public Task<EnrichmentExecutionGuard?> ClaimAsync(string targetId, string owner, long now, long expiresAt,
        CancellationToken cancellationToken)
    {
        Bounded(owner, 128); ValidateExpiry(now, expiresAt);
        return WriteAsync<EnrichmentExecutionGuard?>(async () =>
        {
            now = Now(now);
            if (expiresAt <= now) return null;
            var target = await Targets.SingleOrDefaultAsync(row => row.Id == targetId && !row.Deleted);
            if (target == null) return null;
            var round = await Rounds.SingleAsync(row => row.TargetId == targetId && row.Number == target.CurrentRound);
            if (round.State is not (EnrichmentState.Pending or EnrichmentState.Waiting or EnrichmentState.Running)
                || round.ConsumedFailures >= round.FailureBudget || round.NextAttemptAtUnixMs > now
                || round.State == EnrichmentState.Running && round.LeaseExpiresAtUnixMs > now) return null;
            var input = await InputAsync(Key(target), CancellationToken.None);
            if (input == null) { await CancelAsync(target, now); return null; }
            if (input != Version(round)) return null;
            var reclaimed = round.State == EnrichmentState.Running;
            var changed = await RoundQuery(targetId, target.CurrentRound).Where(row => row.LeaseFence == round.LeaseFence)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.State, EnrichmentState.Running)
                    .SetProperty(row => row.LeaseOwner, owner).SetProperty(row => row.LeaseFence, row => row.LeaseFence + 1)
                    .SetProperty(row => row.LeaseExpiresAtUnixMs, expiresAt).SetProperty(row => row.NextAttemptAtUnixMs, (long?)null)
                    .SetProperty(row => row.WaitReason, reclaimed ? EnrichmentWaitReason.LeaseExpired : null)
                    .SetProperty(row => row.UpdatedAtUnixMs, now));
            if (changed == 1 && reclaimed) await AddWaitAsync(targetId, target.CurrentRound, EnrichmentWaitReason.LeaseExpired, null, now, now);
            return changed == 1 ? new(targetId, target.CurrentRound, target.Epoch, owner, round.LeaseFence + 1, input) : null;
        }, cancellationToken);
    }

    public Task<bool> RenewAsync(EnrichmentExecutionGuard guard, long now, long expiresAt, CancellationToken cancellationToken)
    {
        ValidateExpiry(now, expiresAt);
        return WriteAsync(async () => await ValidAsync(guard, now) && expiresAt > Now(now)
            && await Owned(guard, Now(now)).Where(row => row.LeaseExpiresAtUnixMs < expiresAt).ExecuteUpdateAsync(set => set
                .SetProperty(row => row.LeaseExpiresAtUnixMs, expiresAt).SetProperty(row => row.UpdatedAtUnixMs, Now(now))) == 1, cancellationToken);
    }

    public Task<bool> SavePlanAsync(EnrichmentExecutionGuard guard, EnrichmentPlan plan, long now, CancellationToken cancellationToken)
    {
        Bounded(plan.SpokenText, 4096); Optional(plan.PronunciationHint, 2048); Optional(plan.Phonetic, 512);
        if (plan.InferredEntryKind is not (null or "word" or "phrase")) throw new ArgumentException("Enrichment entry kind is invalid.", nameof(plan));
        if (plan.Preparation is { } preparation)
        {
            Bounded(preparation.ConfigurationVersion, 128); Bounded(preparation.ClassificationBasis, 512);
            Bounded(preparation.Input.Word, 4096); Optional(preparation.Input.Phonetic, 512);
            Optional(preparation.RequiredPhonetic, 512); Optional(preparation.SpeechPhonetic, 512);
            if (preparation.InputVersion != guard.Input || preparation.InputVersion.SourceRevision != AudioSelectionService.ComputeRevision(preparation.Input)
                || preparation.EntryKind is not ("word" or "phrase") || !Enum.IsDefined(preparation.Control)
                || preparation.ConflictingMeaningIds?.Count > 100)
                throw new ArgumentException("Enrichment preparation is invalid.", nameof(plan));
        }
        if (plan.PartsOfSpeech != null)
        {
            if (plan.PartsOfSpeech.Count > 100 || plan.PartsOfSpeech.Select(value => value.MeaningId).Distinct(StringComparer.Ordinal).Count() != plan.PartsOfSpeech.Count)
                throw new ArgumentException("Enrichment part-of-speech candidates are invalid.", nameof(plan));
            foreach (var candidate in plan.PartsOfSpeech) { Bounded(candidate.MeaningId, 128); Bounded(candidate.PartOfSpeech, 128); }
        }
        var json = JsonSerializer.Serialize(plan);
        if (json.Length > 16384) throw new ArgumentException("Enrichment plan is too large.", nameof(plan));
        return WriteAsync(async () => await ValidAsync(guard, now) && await Owned(guard, Now(now))
            .Where(row => row.PlanJson == null || row.PlanJson == json).ExecuteUpdateAsync(set => set
                .SetProperty(row => row.PlanJson, json).SetProperty(row => row.UpdatedAtUnixMs, Now(now))) == 1, cancellationToken);
    }

    public Task<bool> BeginCallAsync(EnrichmentExecutionGuard guard, string callId, string service,
        string? uploadIdempotencyKey, long now, CancellationToken cancellationToken)
    {
        Uuid(callId); if (uploadIdempotencyKey != null) Uuid(uploadIdempotencyKey);
        if (service is not ("text" or "tts")) throw new ArgumentException("Enrichment service is invalid.", nameof(service));
        return WriteAsync(async () =>
        {
            now = Now(now);
            if (!await ValidAsync(guard, now) || await context.EnrichmentCalls.AnyAsync(row => row.Id == callId || uploadIdempotencyKey != null && row.UploadIdempotencyKey == uploadIdempotencyKey
                || row.TargetId == guard.TargetId && row.Round == guard.Round && row.Result == EnrichmentCallResult.Started)) return false;
            var call = new EnrichmentCallEntity
            {
                Id = callId,
                TargetId = guard.TargetId,
                Round = guard.Round,
                Service = service,
                Result = EnrichmentCallResult.Started,
                UploadIdempotencyKey = uploadIdempotencyKey,
                StartedAtUnixMs = now
            };
            context.EnrichmentCalls.Add(call);
            try { await context.SaveChangesAsync(); }
            finally { context.Entry(call).State = EntityState.Detached; }
            await Owned(guard, now).ExecuteUpdateAsync(set => set.SetProperty(row => row.RequestCount, row => row.RequestCount + 1)
                .SetProperty(row => row.UpdatedAtUnixMs, now));
            return true;
        }, cancellationToken);
    }

    public Task<bool> FinishCallAsync(EnrichmentExecutionGuard guard, string callId, EnrichmentCallResult result,
        string? errorCode, long now, long? nextAttemptAt, CancellationToken cancellationToken) =>
        WriteAsync(() => FinishCallCoreAsync(guard, callId, result, errorCode, now, nextAttemptAt), cancellationToken);

    public Task<bool> FinishCallInTransactionAsync(EnrichmentExecutionGuard guard, string callId, EnrichmentCallResult result,
        string? errorCode, long now, long? nextAttemptAt, CancellationToken cancellationToken)
    {
        RequireTransaction(cancellationToken);
        return FinishCallCoreAsync(guard, callId, result, errorCode, now, nextAttemptAt);
    }

    private async Task<bool> FinishCallCoreAsync(EnrichmentExecutionGuard guard, string callId, EnrichmentCallResult result,
        string? errorCode, long now, long? nextAttemptAt)
    {
        if (result == EnrichmentCallResult.Started || !Enum.IsDefined(result)) throw new ArgumentOutOfRangeException(nameof(result));
        if (result == EnrichmentCallResult.Succeeded)
        {
            if (errorCode != null || nextAttemptAt != null) throw new ArgumentException("Successful calls have no retry or error.");
        }
        else SafeCode(errorCode);
        if (result is EnrichmentCallResult.Throttled or EnrichmentCallResult.TransientFailure)
        {
            if (nextAttemptAt == null) throw new ArgumentException("A retry time is required.", nameof(nextAttemptAt));
            ValidateExpiry(now, nextAttemptAt.Value);
        }
        now = Now(now);
        if (!await ValidAsync(guard, now)) return false;
        var changed = await context.EnrichmentCalls.Where(row => row.Id == callId && row.TargetId == guard.TargetId
            && row.Round == guard.Round && row.Result == EnrichmentCallResult.Started).ExecuteUpdateAsync(set => set
                .SetProperty(row => row.Result, result).SetProperty(row => row.ErrorCode, errorCode)
                .SetProperty(row => row.FinishedAtUnixMs, now));
        if (changed == 0) return false;
        if (result == EnrichmentCallResult.Succeeded) return true;
        var round = await Owned(guard, now).SingleAsync();
        var consumed = round.ConsumedFailures + (result == EnrichmentCallResult.Throttled ? 0 : 1);
        var failed = result == EnrichmentCallResult.TerminalFailure || consumed >= round.FailureBudget;
        await Owned(guard, now).ExecuteUpdateAsync(set => set.SetProperty(row => row.ConsumedFailures, consumed)
            .SetProperty(row => row.State, failed ? EnrichmentState.Failed : EnrichmentState.Waiting)
            .SetProperty(row => row.WaitReason, failed ? null : result == EnrichmentCallResult.Throttled ? EnrichmentWaitReason.Throttled : EnrichmentWaitReason.Retry)
            .SetProperty(row => row.ErrorCode, errorCode).SetProperty(row => row.NextAttemptAtUnixMs, failed ? null : nextAttemptAt)
            .SetProperty(row => row.LeaseOwner, (string?)null).SetProperty(row => row.LeaseExpiresAtUnixMs, (long?)null)
            .SetProperty(row => row.UpdatedAtUnixMs, now));
        if (!failed) await AddWaitAsync(guard.TargetId, guard.Round,
            result == EnrichmentCallResult.Throttled ? EnrichmentWaitReason.Throttled : EnrichmentWaitReason.Retry,
            errorCode, now, Math.Max(now, nextAttemptAt!.Value));
        return true;
    }

    public Task<bool> WaitAsync(EnrichmentExecutionGuard guard, EnrichmentWaitReason reason, long now,
        long nextAttemptAt, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(reason) || reason is EnrichmentWaitReason.Retry or EnrichmentWaitReason.LeaseExpired)
            throw new ArgumentOutOfRangeException(nameof(reason));
        ValidateExpiry(now, nextAttemptAt);
        return WriteAsync(async () =>
        {
            now = Now(now);
            if (!await ValidAsync(guard, now) || await HasOpenCallAsync(guard)) return false;
            await Owned(guard, now).ExecuteUpdateAsync(set => set.SetProperty(row => row.State, EnrichmentState.Waiting)
                .SetProperty(row => row.WaitReason, reason).SetProperty(row => row.NextAttemptAtUnixMs, nextAttemptAt)
                .SetProperty(row => row.LeaseOwner, (string?)null).SetProperty(row => row.LeaseExpiresAtUnixMs, (long?)null)
                .SetProperty(row => row.UpdatedAtUnixMs, now));
            await AddWaitAsync(guard.TargetId, guard.Round, reason, null, now, Math.Max(now, nextAttemptAt));
            return true;
        }, cancellationToken);
    }

    public Task<bool> RecoverPublishedAsync(string targetId, string uploadIdempotencyKey, long now, CancellationToken cancellationToken)
    {
        Uuid(uploadIdempotencyKey);
        return WriteAsync(async () =>
        {
            now = Now(now);
            var target = await Targets.SingleOrDefaultAsync(row => row.Id == targetId && !row.Deleted && row.Step == EnrichmentStep.Audio);
            if (target == null) return false;
            var round = await RoundQuery(targetId, target.CurrentRound).SingleAsync();
            if (round.State is not (EnrichmentState.Pending or EnrichmentState.Running or EnrichmentState.Waiting or EnrichmentState.Succeeded)) return false;
            var input = await InputAsync(Key(target), CancellationToken.None);
            if (input == null || input.Revision != round.InputRevision || input.SourceRevision != round.SourceRevision) return false;
            var upload = await context.AudioUploadOperations.AsNoTracking().SingleOrDefaultAsync(row => row.IdempotencyKey == uploadIdempotencyKey
                && row.WordId == target.WordId && row.MeaningId == target.MeaningId && row.Locale == target.Locale
                && row.SourceRevision == round.SourceRevision && row.State == AudioUploadState.Succeeded
                && (round.State == EnrichmentState.Succeeded || row.ExpectedAssetId == round.OwnAssetId)
                && row.ReceiptAssetId == input.OwnAssetId && row.ReceiptSourceRevision == round.SourceRevision && row.ReceiptStatusCode == 200);
            if (upload == null) return false;
            var call = await context.EnrichmentCalls.AsNoTracking().SingleOrDefaultAsync(row => row.TargetId == targetId
                && row.Round == target.CurrentRound && row.UploadIdempotencyKey == uploadIdempotencyKey);
            if (call == null || call.Result is not (EnrichmentCallResult.Started or EnrichmentCallResult.Succeeded)) return false;
            await context.EnrichmentCalls.Where(row => row.Id == call.Id).ExecuteUpdateAsync(set => set
                .SetProperty(row => row.Result, EnrichmentCallResult.Succeeded).SetProperty(row => row.ErrorCode, (string?)null)
                .SetProperty(row => row.FinishedAtUnixMs, Math.Max(call.StartedAtUnixMs, upload.CompletedAtUnixMs!.Value)));
            await RoundQuery(targetId, target.CurrentRound).ExecuteUpdateAsync(set => set.SetProperty(row => row.State, EnrichmentState.Succeeded)
                .SetProperty(row => row.OwnAssetId, input.OwnAssetId).SetProperty(row => row.LeaseOwner, (string?)null)
                .SetProperty(row => row.LeaseExpiresAtUnixMs, (long?)null).SetProperty(row => row.NextAttemptAtUnixMs, (long?)null)
                .SetProperty(row => row.WaitReason, (EnrichmentWaitReason?)null).SetProperty(row => row.ErrorCode, (string?)null)
                .SetProperty(row => row.UpdatedAtUnixMs, now));
            return true;
        }, cancellationToken);
    }

    public Task<bool> StopAsync(EnrichmentExecutionGuard guard, string errorCode, long now, CancellationToken cancellationToken)
    {
        SafeCode(errorCode);
        return WriteAsync(async () => await ValidAsync(guard, now) && !await HasOpenCallAsync(guard)
            && await Owned(guard, Now(now)).ExecuteUpdateAsync(set => set.SetProperty(row => row.State, EnrichmentState.Failed)
                .SetProperty(row => row.ErrorCode, errorCode).SetProperty(row => row.WaitReason, (EnrichmentWaitReason?)null)
                .SetProperty(row => row.NextAttemptAtUnixMs, (long?)null).SetProperty(row => row.LeaseOwner, (string?)null)
                .SetProperty(row => row.LeaseExpiresAtUnixMs, (long?)null).SetProperty(row => row.UpdatedAtUnixMs, Now(now))) == 1, cancellationToken);
    }

    public Task<bool> CompleteAsync(EnrichmentExecutionGuard guard, long now, CancellationToken cancellationToken) =>
        WriteAsync(async () => await ValidAsync(guard, now) && !await HasOpenCallAsync(guard)
            && await Owned(guard, Now(now)).ExecuteUpdateAsync(set => set.SetProperty(row => row.State, EnrichmentState.Succeeded)
                .SetProperty(row => row.WaitReason, (EnrichmentWaitReason?)null).SetProperty(row => row.ErrorCode, (string?)null)
                .SetProperty(row => row.NextAttemptAtUnixMs, (long?)null).SetProperty(row => row.LeaseOwner, (string?)null)
                .SetProperty(row => row.LeaseExpiresAtUnixMs, (long?)null).SetProperty(row => row.UpdatedAtUnixMs, Now(now))) == 1, cancellationToken);

    public async Task<bool> ValidateInTransactionAsync(EnrichmentExecutionGuard guard, long now, CancellationToken cancellationToken)
    {
        RequireTransaction(cancellationToken);
        var valid = await ValidAsync(guard, now);
        if (valid) _validated.Add((context.Database.CurrentTransaction!.TransactionId, guard));
        return valid;
    }

    public async Task<EnrichmentExecutionGuard?> AdvanceInputInTransactionAsync(EnrichmentExecutionGuard guard, long now,
        CancellationToken cancellationToken)
    {
        RequireTransaction(cancellationToken);
        now = Now(now);
        if (!_validated.Remove((context.Database.CurrentTransaction!.TransactionId, guard)) || !await Owned(guard, now).AnyAsync()) return null;
        var target = await Targets.SingleAsync(row => row.Id == guard.TargetId);
        var input = await InputAsync(Key(target), CancellationToken.None);
        if (input == null) return null;
        await Owned(guard, now).ExecuteUpdateAsync(set => set.SetProperty(row => row.InputRevision, input.Revision)
            .SetProperty(row => row.SourceRevision, input.SourceRevision).SetProperty(row => row.OwnAssetId, input.OwnAssetId)
            .SetProperty(row => row.UpdatedAtUnixMs, now));
        return guard with { Input = input };
    }

    private async Task<bool> ValidAsync(EnrichmentExecutionGuard guard, long now)
    {
        now = Now(now);
        if (!await Owned(guard, now).AnyAsync()) return false;
        var target = await Targets.SingleAsync(row => row.Id == guard.TargetId);
        return await InputAsync(Key(target), CancellationToken.None) == guard.Input;
    }

    private IQueryable<EnrichmentRoundEntity> Owned(EnrichmentExecutionGuard guard, long now) => Rounds.Where(row =>
        row.TargetId == guard.TargetId && row.Number == guard.Round && row.State == EnrichmentState.Running
        && row.LeaseOwner == guard.Owner && row.LeaseFence == guard.Fence && row.LeaseExpiresAtUnixMs > now
        && row.InputRevision == guard.Input.Revision && row.SourceRevision == guard.Input.SourceRevision && row.OwnAssetId == guard.Input.OwnAssetId
        && context.EnrichmentTargets.Any(target => target.Id == guard.TargetId && !target.Deleted
            && target.CurrentRound == guard.Round && target.Epoch == guard.Epoch));

    private IQueryable<EnrichmentRoundEntity> RoundQuery(string id, int round) => Rounds.Where(row => row.TargetId == id && row.Number == round);
    private Task<bool> HasOpenCallAsync(EnrichmentExecutionGuard guard) => context.EnrichmentCalls.AnyAsync(row =>
        row.TargetId == guard.TargetId && row.Round == guard.Round && row.Result == EnrichmentCallResult.Started);

    private async Task<EnrichmentInputVersion?> InputAsync(EnrichmentTargetKey key, CancellationToken token)
    {
        var word = await context.Vocabularies.AsNoTracking().SingleOrDefaultAsync(row => row.Id == key.WordId, token);
        if (word == null) return null;
        AudioMeaningInput? meaning = null;
        if (key.MeaningId != null)
        {
            var row = await context.VocabularyMeanings.AsNoTracking().SingleOrDefaultAsync(row => row.Id == key.MeaningId && row.VocabularyId == key.WordId, token);
            if (row == null || key.Step == EnrichmentStep.Fields
                || string.IsNullOrWhiteSpace(key.Locale == AudioLocale.EnGb ? row.PhoneticUk : row.PhoneticUs)) return null;
            meaning = new(row.Id, row.PartOfSpeech, row.Meaning, row.PhoneticUk, row.PhoneticUs);
        }
        var source = AudioSelectionService.ComputeRevision(AudioSelectionService.GetInput(new(word.Id, word.Word, word.PhoneticUk, word.PhoneticUs), meaning, key.Locale));
        var own = key.Step != EnrichmentStep.Audio ? null : await context.VocabularyAudioBindings.AsNoTracking().Where(row => row.WordId == key.WordId
            && row.MeaningId == key.MeaningId && row.Locale == key.Locale).Select(row => row.AssetId).SingleOrDefaultAsync(token);
        // Preparation/field plans also depend on explicit position classification
        // and current POS, not whitespace in spelling. Audio uses the shared v1
        // input revision. No secret/config fingerprint is included here.
        object? fields = null;
        if (key.Step != EnrichmentStep.Audio)
        {
            var meanings = await context.VocabularyMeanings.AsNoTracking().Where(row => row.VocabularyId == key.WordId
                && (key.MeaningId == null || row.Id == key.MeaningId)).OrderBy(row => row.Id)
                .Select(row => new { row.Id, row.PartOfSpeech, row.Meaning }).ToArrayAsync(token);
            var positions = await context.VocabularyMeaningUnits.AsNoTracking().Where(row =>
                context.VocabularyMeanings.Any(meaningRow => meaningRow.Id == row.MeaningId && meaningRow.VocabularyId == key.WordId)
                && (key.MeaningId == null || row.MeaningId == key.MeaningId))
                .Select(row => new { row.MeaningId, row.EntryKind }).Distinct().ToArrayAsync(token);
            fields = meanings.Select(row => new
            {
                row.Id,
                row.PartOfSpeech,
                row.Meaning,
                Kinds = positions.Where(position => position.MeaningId == row.Id).Select(position => position.EntryKind).OrderBy(kind => kind, StringComparer.Ordinal).ToArray()
            }).ToArray();
        }
        var epoch = key.MeaningId == null ? 0 : await context.MeaningPhoneticEpochs.AsNoTracking().Where(row =>
            row.WordId == key.WordId && row.MeaningId == key.MeaningId && row.Locale == key.Locale)
            .Select(row => (long?)row.Epoch).SingleOrDefaultAsync(token) ?? 0;
        // Keep untouched pre-upgrade rounds byte-for-byte compatible. A real
        // edit adds the independent durable epoch, including A -> B -> A.
        object?[] version = epoch == 0 ? ["lexarbor.enrichment.input.v1", source, key.Step.ToString(), fields]
            : ["lexarbor.enrichment.input.v2", source, key.Step.ToString(), fields, epoch];
        var revision = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(version)));
        return new(revision, source, own);
    }

    private async Task CancelAsync(EnrichmentTargetEntity target, long now)
    {
        // Non-applicable (e.g. cleared specific phonetic) is not a deleted ID.
        await context.EnrichmentTargets.Where(row => row.Id == target.Id).ExecuteUpdateAsync(set => set.SetProperty(row => row.Epoch, row => row.Epoch + 1));
        await Rounds.Where(row => row.TargetId == target.Id && (row.State == EnrichmentState.Pending || row.State == EnrichmentState.Running || row.State == EnrichmentState.Waiting))
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.State, EnrichmentState.Cancelled).SetProperty(row => row.LeaseOwner, (string?)null)
                .SetProperty(row => row.LeaseExpiresAtUnixMs, (long?)null).SetProperty(row => row.UpdatedAtUnixMs, now));
    }

    private async Task<EnrichmentTask?> GetCoreAsync(string id, CancellationToken token)
    {
        var target = await Targets.SingleOrDefaultAsync(row => row.Id == id, token);
        if (target == null) return null;
        var rounds = await Rounds.Where(row => row.TargetId == id).OrderBy(row => row.Number).ToListAsync(token);
        var calls = await context.EnrichmentCalls.AsNoTracking().Where(row => row.TargetId == id).OrderBy(row => row.StartedAtUnixMs).ThenBy(row => row.Id).ToListAsync(token);
        var waits = await context.EnrichmentWaits.AsNoTracking().Where(row => row.TargetId == id)
            .OrderBy(row => row.RecordedAtUnixMs).ThenBy(row => row.Id).ToListAsync(token);
        return new(target.Id, Key(target), target.Epoch, target.Deleted, target.CurrentRound, rounds.Select(row => new EnrichmentRound(
            row.Number, row.State, Version(row), row.ConfigurationVersion, row.FailureBudget, row.RequestCount, row.ConsumedFailures,
            row.PlanJson == null ? null : JsonSerializer.Deserialize<EnrichmentPlan>(row.PlanJson), row.WaitReason, row.ErrorCode,
            row.NextAttemptAtUnixMs, row.LeaseOwner, row.LeaseFence, row.LeaseExpiresAtUnixMs, row.CreatedAtUnixMs, row.UpdatedAtUnixMs)).ToArray(),
            calls.Select(row => new EnrichmentCall(row.Id, row.Round, row.Service, row.Result, row.UploadIdempotencyKey,
                row.ErrorCode, row.StartedAtUnixMs, row.FinishedAtUnixMs)).ToArray(),
            waits.Select(row => new EnrichmentWait(row.Round, row.Reason, row.ErrorCode, row.RecordedAtUnixMs, row.NextAttemptAtUnixMs)).ToArray());
    }

    private async Task AddWaitAsync(string targetId, int round, EnrichmentWaitReason reason, string? code, long now, long next)
    {
        var wait = new EnrichmentWaitEntity
        {
            Id = Guid.NewGuid().ToString(),
            TargetId = targetId,
            Round = round,
            Reason = reason,
            ErrorCode = code,
            RecordedAtUnixMs = now,
            NextAttemptAtUnixMs = next
        };
        context.EnrichmentWaits.Add(wait);
        try { await context.SaveChangesAsync(); }
        finally { context.Entry(wait).State = EntityState.Detached; }
    }

    private async Task<T> ReadAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        if (context.Database.CurrentTransaction != null) return await action();
        await context.Database.OpenConnectionAsync(token);
        try
        {
            await using var transaction = ((SqliteConnection)context.Database.GetDbConnection()).BeginTransaction(deferred: true);
            await using var enlisted = await context.Database.UseTransactionAsync(transaction, token);
            var result = await action(); await transaction.CommitAsync(token); return result;
        }
        finally { await context.Database.CloseConnectionAsync(); }
    }

    private Task<T> WriteAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        if (UnitOfWork.IsWriteInProgress || context.Database.CurrentTransaction != null)
            throw new InvalidOperationException("Use the enrichment transaction methods inside the actual writer transaction.");
        return _unitOfWork.ExecuteAdmittedTransactionAsync(action, token);
    }
    private void RequireTransaction(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!UnitOfWork.OwnsWriteContext(context) || context.Database.CurrentTransaction == null)
            throw new InvalidOperationException("Enrichment authorization requires the caller's serialized writer transaction on the same context.");
    }
    private long Now(long supplied)
    {
        if (supplied < 0) throw new ArgumentOutOfRangeException(nameof(supplied));
        return Math.Max(supplied, _clock.GetUtcNow().ToUnixTimeMilliseconds());
    }
    private static EnrichmentTargetKey Key(EnrichmentTargetEntity target) => new(target.WordId, target.MeaningId, target.Locale, target.Step);
    private static EnrichmentInputVersion Version(EnrichmentRoundEntity row) => new(row.InputRevision, row.SourceRevision, row.OwnAssetId);
    private static void ValidateExpiry(long now, long expiresAt)
    { if (now < 0 || expiresAt <= now) throw new ArgumentOutOfRangeException(nameof(expiresAt)); }
    private static void Bounded(string value, int maximum)
    { if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl)) throw new ArgumentException("Enrichment value is invalid."); }
    private static void Optional(string? value, int maximum) { if (value != null) Bounded(value, maximum); }
    private static void SafeCode(string? value)
    { if (value == null || !Regex.IsMatch(value, "^[a-z][a-z0-9_.]{0,95}$")) throw new ArgumentException("A safe enrichment error code is required."); }
    private static void Uuid(string value)
    { if (!Guid.TryParseExact(value, "D", out var id) || id.ToString() != value) throw new ArgumentException("A canonical UUID is required."); }
    private static void ValidateOptions(EnrichmentTargetKey key, string version, int budget, string? retryKey)
    {
        Bounded(key.WordId, 128); if (key.MeaningId != null) Bounded(key.MeaningId, 128);
        _ = key.Locale.ToCode(); if (!Enum.IsDefined(key.Step)) throw new ArgumentOutOfRangeException(nameof(key));
        Bounded(version, 128); if (budget is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(budget));
        if (retryKey != null) Uuid(retryKey);
    }
}
