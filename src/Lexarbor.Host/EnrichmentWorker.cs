using System.Threading.Channels;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Host.Configuration;
using Lexarbor.Service;

namespace Lexarbor.Host;

public sealed class EnrichmentNotifications : IEnrichmentChangeNotifier
{
    private readonly Channel<string> _words = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
    { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });
    private readonly Channel<EnrichmentTargetKey> _audioTargets = Channel.CreateBounded<EnrichmentTargetKey>(new BoundedChannelOptions(256)
    { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });
    public void Notify(IReadOnlyList<string> wordIds, IReadOnlyList<EnrichmentTargetKey> audioTargets)
    {
        foreach (var target in audioTargets.Take(256)) _audioTargets.Writer.TryWrite(target);
        // Enqueue the target before waking the worker through the word channel.
        Notify(wordIds);
    }
    public void Notify(IReadOnlyList<string> wordIds)
    {
        if (wordIds.Count == 0) _words.Writer.TryWrite("");
        foreach (var id in wordIds.Take(256)) _words.Writer.TryWrite(id);
    }
    public IReadOnlyList<string> Drain()
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 256 && _words.Reader.TryRead(out var word); i++)
            if (word.Length > 0) result.Add(word);
        return result.ToArray();
    }
    public Task WaitAsync(CancellationToken token) => _words.Reader.WaitToReadAsync(token).AsTask();
    public IReadOnlyList<EnrichmentTargetKey> DrainAudioTargets()
    {
        var result = new HashSet<EnrichmentTargetKey>();
        for (var i = 0; i < 256 && _audioTargets.Reader.TryRead(out var target); i++) result.Add(target);
        return result.ToArray();
    }
}

public sealed class EnrichmentWorker(IServiceScopeFactory scopes, EnrichmentRuntimeConfiguration runtime,
    EnrichmentNotifications notifications, TimeProvider clock, ILogger<EnrichmentWorker> logger) : BackgroundService
{
    private sealed record Running(bool Text, EnrichmentDispatch[] Items, Task<IReadOnlyList<string>> Task);
    private readonly List<Running> _running = [];
    private readonly List<EnrichmentDispatch> _queue = [];
    private bool _preferRecovery = true;
    private bool _preferExpand;
    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var nextScan = DateTimeOffset.MinValue;
        EnrichmentConfigurationSnapshot? scanned = null;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var observed = runtime.Current;
                try
                {
                    var discover = scanned != observed || clock.GetUtcNow() >= nextScan;
                    if (discover) { nextScan = clock.GetUtcNow().AddSeconds(observed.Enrichment.ScanIntervalSeconds); scanned = observed; }
                    await ScheduleAsync(stop.Token, discover);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception) { logger.LogWarning("Enrichment scheduling failed; durable work will be retried."); }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                try
                {
                    // A completed batch immediately frees its lane. A slow sibling
                    // never imposes a barrier on ready audio or the next text batch.
                    var wakeups = _running.Select(r => (Task)r.Task).ToList();
                    wakeups.Add(Task.Delay(TimeSpan.FromSeconds(observed.Enrichment.ScanIntervalSeconds), clock, wait.Token));
                    wakeups.Add(runtime.WaitForChangeAsync(observed, wait.Token));
                    wakeups.Add(notifications.WaitAsync(wait.Token));
                    await Task.WhenAny(wakeups);
                }
                finally { await wait.CancelAsync(); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally { await StopWorkAsync(stop); }
    }

    // A bounded single sweep for integration checks; the hosted loop refills lanes
    // on every completion instead of waiting for this entire sweep to finish.
    public async Task<int> RunBatchAsync(CancellationToken token, bool discover = true)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            var count = await ScheduleAsync(stop.Token, discover);
            while (_running.Count > 0 || _queue.Count > 0)
            {
                if (_running.Count > 0) await Task.WhenAny(_running.Select(r => r.Task));
                await ReapAsync();
                await StartWorkAsync(stop.Token);
                if (_running.Count == 0) break; // A shared cooldown may leave queued text deferred.
            }
            return count;
        }
        finally { await StopWorkAsync(stop); }
    }

    private async Task StopWorkAsync(CancellationTokenSource stop)
    {
        await stop.CancelAsync();
        try { await Task.WhenAll(_running.Select(r => r.Task)); } catch (Exception) { /* Observed below, without payloads. */ }
        await ReapAsync();
        _queue.Clear();
    }

    private async Task ReapAsync()
    {
        foreach (var work in _running.Where(w => w.Task.IsCompleted).ToArray())
        {
            _running.Remove(work);
            try
            {
                var selected = await work.Task;
                if (work.Text)
                    _queue.InsertRange(0, work.Items.Where(i => selected.Contains(i.Id)).Select(i => i with { Lane = "Ready" }));
            }
            catch (OperationCanceledException) { }
            catch (Exception) { logger.LogWarning("An enrichment dispatch could not finish; its durable lease will recover."); }
        }
    }

    private async Task<int> ScheduleAsync(CancellationToken token, bool discover)
    {
        await ReapAsync();
        var settings = runtime.Current.Enrichment;
        if (!settings.FieldsEnabled && !settings.AudioEnabled)
        { notifications.Drain(); notifications.DrainAudioTargets(); _queue.Clear(); return 0; }
        await using var scope = scopes.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IEnrichmentSchedulerRepository>();
        var tasks = scope.ServiceProvider.GetRequiredService<IEnrichmentTaskRepository>();
        var recentWords = notifications.Drain();
        var audioTargets = notifications.DrainAudioTargets();
        var targets = discover || recentWords.Count > 0
            ? await repository.DiscoverAsync(settings.DiscoveryBatchSize, recentWords, token) : [];
        var urgent = new List<EnrichmentDispatch>();
        foreach (var key in audioTargets.Concat(targets).Distinct())
        {
            var ensured = await tasks.EnsureAsync(key, EnrichmentTargetExecutor.ConfigurationVersion(key, runtime.Current),
                Now, token, settings.FailureBudget);
            if (ensured != null && audioTargets.Contains(key)) urgent.Add(new(ensured.Id, key.WordId, key.Step.ToString(), "Ready", Now));
        }
        using var storage = await scope.ServiceProvider.GetRequiredService<IAudioUploadOperationFactory>().AcquireAsync(token);
        // Probes use the same bounded lanes and their own contexts. A slow
        // outage probe must not stop scheduling the other healthy service.
        if (discover && (settings.FieldsEnabled || storage.Settings.Enabled) && _running.All(w => !w.Text))
            _running.Add(new(true, [], ProbeAsync("text", token)));
        if (discover && storage.Settings.Enabled && _running.All(w => w.Text))
            _running.Add(new(false, [], ProbeAsync("tts", token)));
        var held = _running.SelectMany(w => w.Items).Concat(_queue).Select(i => i.WordId).ToHashSet(StringComparer.Ordinal);
        var limit = Math.Clamp(settings.DiscoveryBatchSize, 1, 1000);
        var planned = await repository.PlanAsync(limit, Now, held.ToArray(), token);
        var count = 0;
        foreach (var item in urgent.Concat(planned))
        {
            if (_queue.Count(i => i.Lane == item.Lane) >= limit || !held.Add(item.WordId)) continue;
            _queue.Add(item); count++;
        }
        await StartWorkAsync(token);
        return count;
    }

    private async Task StartWorkAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var settings = runtime.Current.Enrichment;
        if (!settings.FieldsEnabled && !settings.AudioEnabled) { _queue.Clear(); return; }
        await using var scope = scopes.CreateAsyncScope();
        var batching = scope.ServiceProvider.GetService<EnrichmentTextBatchExecutor>() != null;
        if (!batching)
            for (var i = 0; i < _queue.Count; i++) if (_queue[i].Lane == "Text") _queue[i] = _queue[i] with { Lane = "Ready" };
        while (_running.Count(w => !w.Text) < settings.Concurrency)
        {
            var preferred = _preferRecovery ? "Recovery" : "Ready";
            var item = _queue.FirstOrDefault(i => i.Lane == preferred) ?? _queue.FirstOrDefault(i => i.Lane != "Text");
            if (item == null) break;
            _preferRecovery = !_preferRecovery;
            _queue.Remove(item);
            _running.Add(new(false, [item], ExecuteAsync(item, token)));
        }
        var snapshot = runtime.Current;
        var status = snapshot.CanPrepare ? await scope.ServiceProvider.GetRequiredService<IEnrichmentSchedulerRepository>()
            .ServiceAsync(EnrichmentServiceGate.Identity("text", snapshot), token) : null;
        var cooling = status != null && status.ConfigurationVersion == snapshot.TextVersion
            && (status.Paused || status.Blocked || status.NextAttemptAtUnixMs > Now);
        while (batching && !cooling && _running.Count(w => w.Text) < settings.TextConcurrency)
        {
            var candidates = _queue.Where(i => i.Lane == "Text").ToArray();
            if (candidates.Length == 0) break;
            var first = candidates.FirstOrDefault(i => (i.Step == "Expand") == _preferExpand) ?? candidates[0];
            _preferExpand = first.Step != "Expand";
            var kind = first.Step == "Expand" ? "Expand" : "Prepare";
            var limit = await scope.ServiceProvider.GetRequiredService<IAiRequestBatchRepository>()
                .LimitAsync(kind, runtime.Current.TextVersion ?? "configuration.missing", kind == "Expand" ? 1 : 6, token);
            var items = candidates.Where(i => (i.Step == "Expand") == (first.Step == "Expand")).Take(limit).ToArray();
            foreach (var item in items) _queue.Remove(item);
            _running.Add(new(true, items, PrepareAsync(items, token)));
        }
    }

    private async Task<IReadOnlyList<string>> PrepareAsync(EnrichmentDispatch[] items, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        LogQueue("Text", items);
        return await scope.ServiceProvider.GetRequiredService<EnrichmentTextBatchExecutor>().PrepareAsync(items.Select(i => i.Id).ToArray(), token);
    }

    private async Task<IReadOnlyList<string>> ProbeAsync(string service, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<EnrichmentServiceGate>().ProbeAsync(service,
            scope.ServiceProvider.GetRequiredService<IPronunciationPreparationClient>(),
            scope.ServiceProvider.GetRequiredService<IPronunciationSpeechClient>(), token);
        return [];
    }

    private async Task<IReadOnlyList<string>> ExecuteAsync(EnrichmentDispatch item, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        LogQueue(item.Lane, [item]);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = await scope.ServiceProvider.GetRequiredService<EnrichmentTargetExecutor>().ExecuteAsync(item.Id, "enrichment-worker", token);
        var state = result?.Rounds.Single(r => r.Number == result.CurrentRound).State.ToString() ?? "Missing";
        logger.LogInformation("Autonomous target execution: step {Step}, outcome {Outcome}, elapsedMs {ElapsedMs}",
            item.Step, state, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return [];
    }

    private void LogQueue(string lane, EnrichmentDispatch[] items) => logger.LogInformation(
        "Autonomous dispatch: lane {Lane}, items {Items}, oldestQueueAgeMs {OldestQueueAgeMs}",
        lane, items.Length, Math.Max(0, Now - items.Min(i => i.QueuedAtUnixMs)));
}
