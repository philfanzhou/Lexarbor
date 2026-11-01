using System.Text.Json;
using Lexarbor.Database.Entities;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed class EnrichmentSchedulerRepository(VocabularyDbContext context, IEnrichmentTaskRepository? tasks = null) : IEnrichmentSchedulerRepository
{
    private readonly UnitOfWork _writer = new(context);

    public Task<IReadOnlyList<EnrichmentTargetKey>> DiscoverAsync(int batchSize, IReadOnlyList<string> recentWordIds, CancellationToken token)
    {
        if (batchSize is < 1 or > 1000 || recentWordIds.Count > 256) throw new ArgumentOutOfRangeException(nameof(batchSize));
        return _writer.ExecuteAdmittedTransactionAsync<IReadOnlyList<EnrichmentTargetKey>>(async () =>
        {
            // Each lane is independently bounded. New arrivals cannot move the
            // historical high-water marks, including when notifications overflow.
            var wordCursor = await CursorAsync("words"); var meaningCursor = await CursorAsync("meanings");
            if (wordCursor.UpperBound.Length == 0)
                wordCursor.UpperBound = await context.Vocabularies.OrderByDescending(w => w.Id).Select(w => w.Id).FirstOrDefaultAsync() ?? "";
            if (meaningCursor.UpperBound.Length == 0)
                meaningCursor.UpperBound = await context.VocabularyMeanings.OrderByDescending(m => m.Id).Select(m => m.Id).FirstOrDefaultAsync() ?? "";
            var words = await context.Vocabularies.AsNoTracking().Where(w => w.Id.CompareTo(wordCursor.Cursor) > 0 && w.Id.CompareTo(wordCursor.UpperBound) <= 0)
                .OrderBy(w => w.Id).Select(w => w.Id).Take(batchSize).ToArrayAsync();
            var meanings = await context.VocabularyMeanings.AsNoTracking().Where(m => m.Id.CompareTo(meaningCursor.Cursor) > 0 && m.Id.CompareTo(meaningCursor.UpperBound) <= 0)
                .OrderBy(m => m.Id).Select(m => m.Id).Take(batchSize).ToArrayAsync();
            wordCursor.Cursor = words.LastOrDefault() ?? "";
            meaningCursor.Cursor = meanings.LastOrDefault() ?? "";
            if (words.Length == 0) wordCursor.UpperBound = "";
            if (meanings.Length == 0) meaningCursor.UpperBound = "";
            var newest = await context.Vocabularies.FromSqlRaw("SELECT * FROM vocabulary ORDER BY rowid DESC LIMIT {0}", batchSize)
                .AsNoTracking().Select(w => w.Id).ToArrayAsync();
            var newestMeanings = await context.VocabularyMeanings.FromSqlRaw("SELECT * FROM vocabulary_meaning ORDER BY rowid DESC LIMIT {0}", batchSize)
                .AsNoTracking().Select(m => m.Id).ToArrayAsync();
            var notifiedMeanings = await context.VocabularyMeanings.AsNoTracking().Where(m => recentWordIds.Contains(m.VocabularyId))
                .OrderBy(m => m.Id).Select(m => m.Id).Take(batchSize).ToArrayAsync();
            var result = new HashSet<EnrichmentTargetKey>();
            // Notification/new lanes are considered first, with an unconditional
            // reserved historical page in every sweep.
            foreach (var id in recentWordIds.Concat(newest).Concat(words).Distinct(StringComparer.Ordinal))
                await DefaultAsync(id, result);
            foreach (var id in notifiedMeanings.Concat(newestMeanings).Concat(meanings).Distinct(StringComparer.Ordinal))
                await SpecificAsync(id, result);
            await context.SaveChangesAsync();
            context.Entry(wordCursor).State = EntityState.Detached; context.Entry(meaningCursor).State = EntityState.Detached;
            return result.ToArray();
        }, token);
    }

    private async Task<EnrichmentDiscoveryEntity> CursorAsync(string id)
    {
        var row = await context.EnrichmentDiscovery.SingleOrDefaultAsync(r => r.Id == id);
        if (row != null) return row;
        row = new() { Id = id }; context.EnrichmentDiscovery.Add(row); return row;
    }

    private async Task DefaultAsync(string id, HashSet<EnrichmentTargetKey> result)
    {
        var word = await context.Vocabularies.AsNoTracking().SingleOrDefaultAsync(w => w.Id == id);
        if (word == null) return;
        var positions = context.VocabularyMeaningUnits.Where(p => context.VocabularyMeanings.Any(m => m.Id == p.MeaningId && m.VocabularyId == id));
        var phraseOnly = await positions.AnyAsync() && !await positions.AnyAsync(p => p.EntryKind != "phrase");
        var missingPos = await context.VocabularyMeanings.AnyAsync(m => m.VocabularyId == id && (m.PartOfSpeech == null || m.PartOfSpeech.Trim() == "")
            && !context.VocabularyMeaningUnits.Any(p => p.MeaningId == m.Id && p.EntryKind == "phrase"));
        foreach (var locale in new[] { AudioLocale.EnGb, AudioLocale.EnUs })
        {
            if (!phraseOnly && (string.IsNullOrWhiteSpace(locale == AudioLocale.EnGb ? word.PhoneticUk : word.PhoneticUs) || missingPos))
                result.Add(new(id, null, locale, EnrichmentStep.Fields));
            if (!await context.VocabularyAudioBindings.AnyAsync(b => b.WordId == id && b.MeaningId == null && b.Locale == locale))
                result.Add(new(id, null, locale, EnrichmentStep.Audio));
        }
    }

    private async Task SpecificAsync(string id, HashSet<EnrichmentTargetKey> result)
    {
        var meaning = await context.VocabularyMeanings.AsNoTracking().SingleOrDefaultAsync(m => m.Id == id);
        if (meaning == null) return;
        foreach (var locale in new[] { AudioLocale.EnGb, AudioLocale.EnUs })
            if (!string.IsNullOrWhiteSpace(locale == AudioLocale.EnGb ? meaning.PhoneticUk : meaning.PhoneticUs)
                && !await context.VocabularyAudioBindings.AnyAsync(b => b.WordId == meaning.VocabularyId && b.MeaningId == id && b.Locale == locale))
                result.Add(new(meaning.VocabularyId, id, locale, EnrichmentStep.Audio));
    }

    public async Task<IReadOnlyList<string>> DueAsync(int limit, long now, CancellationToken token)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        var candidates = from target in context.EnrichmentTargets.AsNoTracking()
                         join round in context.EnrichmentRounds.AsNoTracking() on new { TargetId = target.Id, Number = target.CurrentRound }
                             equals new { round.TargetId, round.Number }
                         where !target.Deleted && round.ConsumedFailures < round.FailureBudget
                             && (round.State == EnrichmentState.Pending || round.State == EnrichmentState.Waiting
                                 || round.State == EnrichmentState.Running && round.LeaseExpiresAtUnixMs <= now)
                             && (round.NextAttemptAtUnixMs == null || round.NextAttemptAtUnixMs <= now)
                         orderby round.UpdatedAtUnixMs, target.Id
                         select new { target.Id, target.WordId, round.UpdatedAtUnixMs, round.CreatedAtUnixMs };
        var newestWords = await context.Vocabularies.FromSqlRaw("SELECT * FROM vocabulary ORDER BY rowid DESC LIMIT {0}", limit)
            .AsNoTracking().Select(w => w.Id).ToArrayAsync(token);
        var newestMeanings = await context.VocabularyMeanings.FromSqlRaw("SELECT * FROM vocabulary_meaning ORDER BY rowid DESC LIMIT {0}", limit)
            .AsNoTracking().Select(m => m.VocabularyId).ToArrayAsync(token);
        var recentWords = newestWords.Concat(newestMeanings).Distinct(StringComparer.Ordinal).ToArray();
        var recent = await candidates.Where(c => recentWords.Contains(c.WordId)).OrderByDescending(c => c.CreatedAtUnixMs)
            .ThenBy(c => c.Id).Select(c => c.Id).Take(limit / 2).ToArrayAsync(token);
        var historical = await candidates.Where(c => !recent.Contains(c.Id)).OrderBy(c => c.UpdatedAtUnixMs)
            .ThenBy(c => c.Id).Select(c => c.Id).Take(limit - recent.Length).ToArrayAsync(token);
        return recent.Concat(historical).ToArray();
    }

    public Task<EnrichmentServiceAdmission> EnterServiceAsync(string serviceId, string configurationVersion,
        bool probe, long now, long expiresAt, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(serviceId) || serviceId.Length > 128 || string.IsNullOrWhiteSpace(configurationVersion)
            || configurationVersion.Length > 128 || now < 0 || expiresAt <= now) throw new ArgumentException("Invalid enrichment service admission.");
        return _writer.ExecuteAdmittedTransactionAsync(async () =>
        {
            var row = await context.EnrichmentServices.SingleOrDefaultAsync(r => r.Id == serviceId);
            if (row == null)
            { row = new() { Id = serviceId, ConfigurationVersion = configurationVersion }; context.EnrichmentServices.Add(row); }
            else if (row.ConfigurationVersion != configurationVersion)
            {
                // A relevant durable configuration correction may release a
                // stopped service; it never rewrites any target/round budget.
                row.ConfigurationVersion = configurationVersion; row.ConsecutiveFailures = 0;
                row.Paused = false; row.Blocked = false; row.PauseSeconds = 0;
                row.ThrottleSeconds = 0; row.NextAttemptAtUnixMs = 0; row.LeaseId = null; row.LeaseExpiresAtUnixMs = null;
            }
            var reason = row.Blocked ? EnrichmentWaitReason.ConfigurationMissing
                : row.Paused && !probe ? EnrichmentWaitReason.ServicePaused
                : row.NextAttemptAtUnixMs > now || row.LeaseExpiresAtUnixMs > now ? EnrichmentWaitReason.Throttled
                : probe && !row.Paused ? EnrichmentWaitReason.ServicePaused : (EnrichmentWaitReason?)null;
            string? lease = null;
            if (reason == null)
            {
                lease = Guid.NewGuid().ToString(); row.LeaseId = lease; row.LeaseExpiresAtUnixMs = expiresAt;
                if (probe)
                {
                    // A crashed probe remains an explicit unknown outcome. Its
                    // expired lease permits exactly one later replacement.
                    await context.EnrichmentServiceProbes.Where(p => p.ServiceId == serviceId && p.FinishedAtUnixMs == null)
                        .ExecuteUpdateAsync(set => set.SetProperty(p => p.Result, "Unknown").SetProperty(p => p.FinishedAtUnixMs, now));
                    context.EnrichmentServiceProbes.Add(new() { Id = lease, ServiceId = serviceId, StartedAtUnixMs = now, RequestMade = true });
                }
            }
            await context.SaveChangesAsync(); context.ChangeTracker.Clear();
            return new EnrichmentServiceAdmission(serviceId, configurationVersion, lease, probe, reason,
                Math.Max(now + 1000, row.NextAttemptAtUnixMs));
        }, token);
    }

    public Task<long> FinishServiceAsync(EnrichmentServiceAdmission admission, string outcome, string? errorCode,
        bool requestMade, long now, long? retryAfterAt, EnrichmentServicePolicy policy, CancellationToken token,
        EnrichmentCallCompletion? completion = null) =>
        _writer.ExecuteAdmittedTransactionAsync(async () =>
        {
            var row = await context.EnrichmentServices.SingleOrDefaultAsync(r => r.Id == admission.ServiceId);
            if (row == null || admission.LeaseId == null || row.LeaseId != admission.LeaseId || row.ConfigurationVersion != admission.ConfigurationVersion)
            { context.ChangeTracker.Clear(); return now; }
            row.LeaseId = null; row.LeaseExpiresAtUnixMs = null;
            if (admission.Probe)
                await context.EnrichmentServiceProbes.Where(p => p.Id == admission.LeaseId).ExecuteUpdateAsync(set =>
                    set.SetProperty(p => p.Result, outcome).SetProperty(p => p.RequestMade, requestMade).SetProperty(p => p.FinishedAtUnixMs, now));
            if (requestMade)
            {
                if (outcome == "Throttled")
                {
                    row.ConsecutiveFailures = 0;
                    row.ThrottleSeconds = Math.Min(policy.MaxThrottleSeconds,
                        Math.Max(policy.ThrottleSeconds, row.ThrottleSeconds * 2));
                    row.NextAttemptAtUnixMs = Math.Max(now + row.ThrottleSeconds * 1000L, retryAfterAt ?? 0);
                    if (row.Paused) row.NextAttemptAtUnixMs = Math.Max(row.NextAttemptAtUnixMs, now + row.PauseSeconds * 1000L);
                }
                else if (outcome == "ServiceFailure")
                {
                    row.ConsecutiveFailures++;
                    if (admission.Probe || row.ConsecutiveFailures >= policy.FailureThreshold)
                    {
                        row.Paused = true;
                        row.PauseSeconds = admission.Probe ? Math.Min(policy.MaxPauseSeconds, Math.Max(policy.PauseSeconds, row.PauseSeconds * 2)) : policy.PauseSeconds;
                        row.NextAttemptAtUnixMs = now + row.PauseSeconds * 1000L;
                    }
                }
                else
                {
                    row.ConsecutiveFailures = 0;
                    if (errorCode is "credentials_rejected" or "balance_required") row.Blocked = true;
                    if (admission.Probe)
                    {
                        row.Paused = false; row.PauseSeconds = 0;
                    }
                    row.ThrottleSeconds /= 2;
                    row.NextAttemptAtUnixMs = now + row.ThrottleSeconds * 1000L;
                }
            }
            // Local validation/cancellation is not a successful service probe.
            if (admission.Probe && !requestMade) row.NextAttemptAtUnixMs = now + Math.Max(policy.PauseSeconds, row.PauseSeconds) * 1000L;
            if (completion != null)
            {
                if (tasks == null || admission.Probe || !requestMade) throw new InvalidOperationException("A catalog call completion requires the scoped task repository.");
                var next = completion.Result == EnrichmentCallResult.Throttled
                    ? Math.Max(completion.NextAttemptAtUnixMs ?? now + 1000, row.NextAttemptAtUnixMs) : completion.NextAttemptAtUnixMs;
                await tasks.FinishCallInTransactionAsync(completion.Guard, completion.CallId, completion.Result,
                    completion.ErrorCode, now, next, CancellationToken.None);
            }
            await context.SaveChangesAsync(); context.ChangeTracker.Clear();
            return row.NextAttemptAtUnixMs;
        }, token);

    public async Task<EnrichmentServiceStatus?> ServiceAsync(string serviceId, CancellationToken token)
    {
        var r = await context.EnrichmentServices.AsNoTracking().SingleOrDefaultAsync(r => r.Id == serviceId, token);
        return r == null ? null : new(r.Id, r.ConfigurationVersion, r.ConsecutiveFailures, r.ThrottleSeconds, r.Paused,
            r.Blocked, r.PauseSeconds, r.NextAttemptAtUnixMs, r.LeaseId, r.LeaseExpiresAtUnixMs);
    }
    public async Task<IReadOnlyList<EnrichmentServiceProbe>> ProbesAsync(string serviceId, CancellationToken token) =>
        await context.EnrichmentServiceProbes.AsNoTracking().Where(p => p.ServiceId == serviceId).OrderBy(p => p.StartedAtUnixMs)
            .Select(p => new EnrichmentServiceProbe(p.Id, p.ServiceId, p.Result, p.RequestMade, p.StartedAtUnixMs, p.FinishedAtUnixMs)).ToArrayAsync(token);

    public async Task<EnrichmentPlan?> ProbePlanAsync(AudioLocale locale, string textConfigurationVersion, CancellationToken token)
    {
        var plans = await (from target in context.EnrichmentTargets.AsNoTracking()
                           join round in context.EnrichmentRounds.AsNoTracking() on target.Id equals round.TargetId
                           where target.Step == EnrichmentStep.Audio && target.Locale == locale && round.PlanJson != null
                           orderby round.UpdatedAtUnixMs descending
                           select round.PlanJson).Take(16).ToArrayAsync(token);
        return plans.Select(json => JsonSerializer.Deserialize<EnrichmentPlan>(json!))
            .FirstOrDefault(plan => plan?.Preparation?.ConfigurationVersion == textConfigurationVersion);
    }
}
