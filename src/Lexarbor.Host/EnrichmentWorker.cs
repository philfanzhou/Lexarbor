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
    private sealed record Running(bool Text, EnrichmentDispatch[] Items, Task<EnrichmentTextDispatchResult> Task);
    private readonly List<Running> _running = [];
    private readonly List<EnrichmentDispatch> _queue = [];
    private bool _preferRecovery = true;
    private readonly EnrichmentTextSchedulingPolicy _textPolicy = new();
    private long _textNextCheck;
    private bool _initialScanning;
    private bool _expansionReady = true;
    private long _destinationNextCheck;
    private EnrichmentConfigurationSnapshot? _destinationConfiguration;
    private EnrichmentConfigurationSnapshot? _dispatchConfiguration;
    private static bool NeedsText(EnrichmentDispatch item) => item.Lane is "Text" or "Expansion";
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
                    var sweepStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    await ScheduleAsync(stop.Token, discover);
                    if (discover && _initialScanning)
                    {
                        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(sweepStarted).TotalMilliseconds;
                        nextScan = clock.GetUtcNow().AddMilliseconds(Math.Clamp(elapsed * 2, 500, 5000));
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception) { logger.LogWarning("Enrichment scheduling failed; durable work will be retried."); }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                try
                {
                    // A completed batch immediately frees its lane. A slow sibling
                    // never imposes a barrier on ready audio or the next text batch.
                    var wakeups = _running.Select(r => (Task)r.Task).ToList();
                    var delayMs = observed.Enrichment.ScanIntervalSeconds * 1000L;
                    if (_initialScanning) delayMs = Math.Min(delayMs, Math.Max(1, (long)(nextScan - clock.GetUtcNow()).TotalMilliseconds));
                    if (_textNextCheck > Now) delayMs = Math.Min(delayMs, _textNextCheck - Now);
                    wakeups.Add(Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, delayMs)), clock, wait.Token));
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
        var released = false;
        foreach (var work in _running.Where(w => w.Task.IsCompleted).ToArray())
        {
            _running.Remove(work);
            try
            {
                var result = await work.Task;
                if (work.Text)
                {
                    var expansion = work.Items.Any(i => i.Step == "Expand");
                    // A non-dispatchable expansion yields to preparation too;
                    // capacity deferrals retain their turn without a busy loop.
                    _textPolicy.Complete(expansion, expansion && result.Ready.Count > 0 ? Math.Max(1, result.Requests) : result.Requests,
                        result.RequestMilliseconds);
                    released |= result.Requests > 0 || work.Items.Length == 0; // A probe may have healed the service.
                    _queue.InsertRange(0, work.Items.Where(i => result.Ready.Contains(i.Id)).Select(i => i with { Lane = "Ready" }));
                    _queue.AddRange(work.Items.Where(i => result.Deferred.Contains(i.Id)));
                    if (result.Deferred.Count > 0) _textNextCheck = Math.Max(_textNextCheck, result.NextCheckAtUnixMs);
                }
            }
            catch (OperationCanceledException) { released |= work.Text; }
            catch (Exception)
            {
                released |= work.Text; // Exception cleanup may have released a previously occupied slot.
                logger.LogWarning("An enrichment dispatch could not finish; its durable lease will recover.");
            }
        }
        if (released) _textNextCheck = 0; // Re-read shared cooldown/capacity after any owned request finishes.
    }

    private async Task<int> ScheduleAsync(CancellationToken token, bool discover)
    {
        await ReapAsync();
        var settings = runtime.Current.Enrichment;
        if (!settings.FieldsEnabled && !settings.AudioEnabled)
        { notifications.Drain(); notifications.DrainAudioTargets(); _queue.Clear(); _initialScanning = false; return 0; }
        await using var scope = scopes.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IEnrichmentSchedulerRepository>();
        var tasks = scope.ServiceProvider.GetRequiredService<IEnrichmentTaskRepository>();
        if ((_destinationNextCheck <= Now || _destinationConfiguration != runtime.Current)
            && scope.ServiceProvider.GetService<IEnrichmentDestinationRepository>() is { } destinations)
        {
            var configured = scope.ServiceProvider.GetService<MeaningSubmissionSettings>()?.BookId;
            var destination = runtime.Current.CanPrepare ? await destinations.EnsureAsync(configured, Now, token)
                : await destinations.ReadAsync(configured, token);
            _expansionReady = runtime.Current.CanPrepare && destination.State == "Ready";
            _destinationNextCheck = Now + 30_000;
            _destinationConfiguration = runtime.Current;
            if (!_expansionReady) _queue.RemoveAll(i => i.Step == "Expand");
        }
        var recentWords = notifications.Drain();
        var audioTargets = notifications.DrainAudioTargets();
        var targets = discover || recentWords.Count > 0
            ? await repository.DiscoverAsync(Math.Min(50, settings.DiscoveryBatchSize), recentWords, token) : [];
        if (discover && await repository.DiscoveryProgressAsync(token) is { } progress)
            _initialScanning = !progress.InitialCompleted;
        var urgent = new List<EnrichmentDispatch>();
        foreach (var key in audioTargets.Concat(targets).Distinct())
        {
            var ensured = await tasks.EnsureAsync(key, EnrichmentTargetExecutor.ConfigurationVersion(key, runtime.Current),
                Now, token, settings.FailureBudget);
            if (ensured != null && audioTargets.Contains(key)) urgent.Add(new(ensured.Id, key.WordId, key.Step.ToString(), "Ready", Now));
        }
        if (discover && scope.ServiceProvider.GetService<IEnrichmentAdminRetryRepository>() is { } recovery
            && scope.ServiceProvider.GetService<IEnrichmentAdminConfigurationProvider>() is { } configuration)
            await recovery.RecoverAsync(5, _running.SelectMany(r => r.Items).Select(i => i.WordId).Distinct().ToArray(),
                await configuration.CaptureAsync(token), Now, token);
        using var storage = await scope.ServiceProvider.GetRequiredService<IAudioUploadOperationFactory>().AcquireAsync(token);
        // Probes use the same bounded lanes and their own contexts. A slow
        // outage probe must not stop scheduling the other healthy service.
        if (discover && (settings.FieldsEnabled || storage.Settings.Enabled) && _running.All(w => !w.Text))
            _running.Add(new(true, [], ProbeAsync("text", token)));
        if (discover && storage.Settings.Enabled && _running.All(w => w.Text))
            _running.Add(new(false, [], ProbeAsync("tts", token)));
        var runningWords = _running.SelectMany(w => w.Items).Select(i => i.WordId).ToHashSet(StringComparer.Ordinal);
        var held = runningWords.Concat(_queue.Select(i => i.WordId)).ToHashSet(StringComparer.Ordinal);
        // Queued expansion must not hide preparation/publication for the same
        // word while waiting for its budget. Running work remains reserved.
        var excluded = runningWords.Concat(_queue.Where(i => i.Lane != "Expansion").Select(i => i.WordId)).Distinct().ToArray();
        var limit = Math.Clamp(settings.DiscoveryBatchSize, 1, 1000);
        var planned = await repository.PlanAsync(limit, Now, excluded, token, _expansionReady);
        var count = 0;
        foreach (var item in urgent.Concat(planned))
        {
            if (_queue.Count(i => i.Lane == item.Lane) >= limit) continue;
            if (!held.Add(item.WordId) && (item.Lane == "Expansion" || runningWords.Contains(item.WordId)
                || _queue.RemoveAll(i => i.WordId == item.WordId && i.Lane == "Expansion") == 0)) continue;
            _queue.Add(item); count++;
        }
        await StartWorkAsync(token);
        return count;
    }

    private async Task StartWorkAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var snapshot = runtime.Current;
        var settings = snapshot.Enrichment;
        if (_dispatchConfiguration != snapshot) { _dispatchConfiguration = snapshot; _textNextCheck = 0; }
        if (!settings.FieldsEnabled && !settings.AudioEnabled) { _queue.Clear(); return; }
        await using var scope = scopes.CreateAsyncScope();
        var batching = scope.ServiceProvider.GetService<EnrichmentTextBatchExecutor>() != null;
        if (!batching)
            for (var i = 0; i < _queue.Count; i++) if (NeedsText(_queue[i])) _queue[i] = _queue[i] with { Lane = "Ready" };
        while (_running.Count(w => !w.Text) < settings.Concurrency)
        {
            var preferred = _preferRecovery ? "Recovery" : "Ready";
            var item = _queue.FirstOrDefault(i => i.Lane == preferred) ?? _queue.FirstOrDefault(i => !NeedsText(i));
            if (item == null) break;
            _preferRecovery = !_preferRecovery;
            _queue.Remove(item);
            _running.Add(new(false, [item], ExecuteAsync(item, token)));
        }
        if (!batching || _textNextCheck > Now || !_queue.Any(NeedsText)) return;
        var available = await scope.ServiceProvider.GetRequiredService<EnrichmentServiceGate>().AvailabilityAsync("text", token);
        if (available.AvailableSlots == 0)
        {
            _textNextCheck = available.NextCheckAtUnixMs;
            return;
        }
        _textNextCheck = 0;
        var slots = available.AvailableSlots;
        while (slots > 0 && _running.Count(w => w.Text) < settings.TextConcurrency)
        {
            var preparations = _queue.Where(i => i.Lane == "Text").ToArray();
            var expansionRunning = _running.Any(w => w.Text && w.Items.Any(i => i.Step == "Expand"));
            var preparationPending = preparations.Length > 0 || _running.Any(w => w.Text && w.Items.Any(i => i.Step != "Expand"));
            var expansion = _textPolicy.CanExpand(preparationPending, expansionRunning)
                ? _queue.FirstOrDefault(i => i.Lane == "Expansion") : null;
            var first = expansion ?? preparations.FirstOrDefault();
            if (first == null) break;
            var kind = first.Step == "Expand" ? "Expand" : "Prepare";
            var size = EnrichmentTextBatchExecutor.BatchSize(kind);
            var limit = await scope.ServiceProvider.GetRequiredService<IAiRequestBatchRepository>()
                .LimitAsync(kind, snapshot.TextVersion ?? "configuration.missing", size.Initial, size.Maximum, token);
            var items = _queue.Where(i => i.Lane == first.Lane).Take(limit).ToArray();
            foreach (var item in items) _queue.Remove(item);
            _running.Add(new(true, items, PrepareAsync(items, token)));
            slots--;
        }
    }

    private async Task<EnrichmentTextDispatchResult> PrepareAsync(EnrichmentDispatch[] items, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        LogQueue(items[0].Lane, items);
        return await scope.ServiceProvider.GetRequiredService<EnrichmentTextBatchExecutor>().DispatchAsync(items.Select(i => i.Id).ToArray(), token);
    }

    private async Task<EnrichmentTextDispatchResult> ProbeAsync(string service, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<EnrichmentServiceGate>().ProbeAsync(service,
            scope.ServiceProvider.GetRequiredService<IPronunciationPreparationClient>(),
            scope.ServiceProvider.GetRequiredService<IPronunciationSpeechClient>(), token);
        return EnrichmentTextDispatchResult.Empty;
    }

    private async Task<EnrichmentTextDispatchResult> ExecuteAsync(EnrichmentDispatch item, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope();
        LogQueue(item.Lane, [item]);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = await scope.ServiceProvider.GetRequiredService<EnrichmentTargetExecutor>().ExecuteAsync(item.Id, "enrichment-worker", token);
        var state = result?.Rounds.Single(r => r.Number == result.CurrentRound).State.ToString() ?? "Missing";
        logger.LogInformation("Autonomous target execution: step {Step}, outcome {Outcome}, elapsedMs {ElapsedMs}",
            item.Step, state, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return EnrichmentTextDispatchResult.Empty;
    }

    private void LogQueue(string lane, EnrichmentDispatch[] items) => logger.LogInformation(
        "Autonomous dispatch: lane {Lane}, items {Items}, oldestQueueAgeMs {OldestQueueAgeMs}",
        lane, items.Length, Math.Max(0, Now - items.Min(i => i.QueuedAtUnixMs)));
}
