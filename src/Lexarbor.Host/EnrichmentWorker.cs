using System.Threading.Channels;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Host.Configuration;
using Lexarbor.Service;

namespace Lexarbor.Host;

public sealed class EnrichmentNotifications : IEnrichmentChangeNotifier
{
    private readonly Channel<string> _words = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
    { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });
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
}

public sealed class EnrichmentWorker(IServiceScopeFactory scopes, EnrichmentRuntimeConfiguration runtime,
    EnrichmentNotifications notifications, TimeProvider clock, ILogger<EnrichmentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Program finishes configuration and database startup before RunAsync
        // starts hosted services. Provider availability cannot block readiness.
        await Task.Yield();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var observed = runtime.Current;
                try { await RunBatchAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception) { logger.LogWarning("Enrichment batch failed; durable work will be retried."); }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                try
                {
                    await await Task.WhenAny(
                        Task.Delay(TimeSpan.FromSeconds(observed.Enrichment.ScanIntervalSeconds), clock, wait.Token),
                        runtime.WaitForChangeAsync(observed, wait.Token), notifications.WaitAsync(wait.Token));
                }
                finally { await wait.CancelAsync(); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task<int> RunBatchAsync(CancellationToken token)
    {
        var settings = runtime.Current.Enrichment;
        if (!settings.FieldsEnabled && !settings.AudioEnabled)
        { notifications.Drain(); return 0; }
        string[] due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IEnrichmentSchedulerRepository>();
            var tasks = scope.ServiceProvider.GetRequiredService<IEnrichmentTaskRepository>();
            var targets = await repository.DiscoverAsync(settings.DiscoveryBatchSize, notifications.Drain(), token);
            foreach (var key in targets)
                await tasks.EnsureAsync(key, EnrichmentTargetExecutor.ConfigurationVersion(key, runtime.Current),
                    clock.GetUtcNow().ToUnixTimeMilliseconds(), token, runtime.Current.Enrichment.FailureBudget);
            var gate = scope.ServiceProvider.GetRequiredService<EnrichmentServiceGate>();
            var preparation = scope.ServiceProvider.GetRequiredService<IPronunciationPreparationClient>();
            var speech = scope.ServiceProvider.GetRequiredService<IPronunciationSpeechClient>();
            using var storage = await scope.ServiceProvider.GetRequiredService<IAudioUploadOperationFactory>().AcquireAsync(token);
            if (settings.FieldsEnabled || storage.Settings.Enabled) await gate.ProbeAsync("text", preparation, speech, token);
            if (storage.Settings.Enabled) await gate.ProbeAsync("tts", preparation, speech, token);
            due = (await repository.DueAsync(Math.Max(2, Math.Max(settings.DiscoveryBatchSize, settings.Concurrency)),
                clock.GetUtcNow().ToUnixTimeMilliseconds(), token)).ToArray();
        }
        // Only this bounded slice exists in memory. Each target gets its own
        // scoped context; no provider IO occurs inside a discovery transaction.
        for (var start = 0; start < due.Length; start += settings.Concurrency)
            await Task.WhenAll(due.Skip(start).Take(settings.Concurrency).Select(async id =>
            {
                await using var scope = scopes.CreateAsyncScope();
                try
                {
                    await scope.ServiceProvider.GetRequiredService<EnrichmentTargetExecutor>()
                        .ExecuteAsync(id, "enrichment-worker", token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception) { logger.LogWarning("An enrichment target could not finish; its durable lease will recover."); }
            }));
        return due.Length;
    }
}
