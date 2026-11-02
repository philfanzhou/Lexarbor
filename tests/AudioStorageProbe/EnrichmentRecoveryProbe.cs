using System.Data.Common;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Host;
using Lexarbor.Host.Audio;
using Lexarbor.Host.Configuration;
using Lexarbor.Host.Integration;
using Lexarbor.Service;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

// Disposable probe only. Production endpoint/model capability rules are unchanged.
internal static class EnrichmentRecoveryProbe
{
    public static async Task VerifyAsync(AudioOptions options)
    {
        // Persistent settings reject symlinked ancestors, including macOS /var.
        var root = Path.Combine(AppContext.BaseDirectory, "enrichment-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, "published.marker");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { typeof(EnrichmentRecoveryProbe).Assembly.Location, "--crash-enrichment", root, marker }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        // Drain child pipes so output cannot turn into an accidental blocking point.
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        try
        {
            var until = DateTimeOffset.UtcNow.AddSeconds(30);
            while (!File.Exists(marker) && !process.HasExited && DateTimeOffset.UtcNow < until) await Task.Delay(50);
            Check(File.Exists(marker) && !process.HasExited, "Enrichment child must commit its S3 publication before the crash point.");
            process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(output, errors);
            var targetId = await File.ReadAllTextAsync(marker);
            await using var engine = await Engine.OpenAsync(root, options);
            var before = (await engine.Tasks.GetAsync(targetId, default))!;
            var round = before.Rounds.Single(r => r.Number == before.CurrentRound);
            Check(round.ConfigurationVersion == EnrichmentTargetExecutor.ConfigurationVersion(before.Target, engine.Runtime.Current),
                "The same persisted configuration must retain its relevant version across process replacement.");
            Check(round.State == EnrichmentState.Running && round.RequestCount == 1 && round.ConsumedFailures == 0,
                "Killed execution must retain its original running round, request and zero consumed failures.");
            var call = before.Calls.Single(c => c.Service == "tts");
            Check(call.Result == EnrichmentCallResult.Started && call.UploadIdempotencyKey != null, "The lost task result must retain the original upload key.");
            var receipt = (await engine.Uploads.GetAsync(call.UploadIdempotencyKey!, default))!;
            Check(receipt.Operation.Receipt?.StatusCode == 200 && receipt.Asset.FirstPublishedAtUnixMs != null,
                "Actual conditional publication must have committed its permanent receipt before process termination.");
            var prepared = await engine.Db.EnrichmentRounds.AsNoTracking().Where(r => r.State == EnrichmentState.Succeeded).Select(r => r.PlanJson).ToArrayAsync();
            Check(prepared.Any(p => p != null), "Successful preparation must persist across process termination.");
            var recovered = (await engine.Executor.ExecuteAsync(targetId, "restart", default))!;
            Check(recovered.CurrentRound == before.CurrentRound && recovered.Rounds.Single(r => r.Number == recovered.CurrentRound).State == EnrichmentState.Succeeded,
                "Restart must recover the same round from its receipt.");
            Check(engine.Network.Calls == 0 && await engine.Db.AudioUploadOperations.CountAsync() == 1
                && await engine.Db.VocabularyAudioBindings.CountAsync() == 1,
                "Receipt recovery must not prepare, request TTS, create another upload or publish twice.");
            var saved = (await engine.Uploads.GetAsync(call.UploadIdempotencyKey!, default))!;
            Check(saved.Operation.Receipt == receipt.Operation.Receipt && saved.Asset.Id == receipt.Asset.Id,
                "Restart must preserve the original asset ID and receipt exactly.");
            using var store = new S3AudioObjectStore(Options.Create(options), TimeProvider.System);
            Check((await store.StatAsync(receipt.Asset.Location, default))?.ByteLength == receipt.Asset.ByteLength,
                "The published private S3 object must survive the actual killed process.");
            Console.WriteLine("Private S3 enrichment process killed after actual TTS/upload/publication: same-round receipt recovery with zero new chat/TTS passed.");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            await Task.WhenAll(output, errors);
            Directory.Delete(root, true);
        }
    }

    public static async Task CrashAsync(string root, string marker)
    {
        var options = new AudioOptions
        {
            Enabled = true,
            Bucket = Environment.GetEnvironmentVariable("LEXARBOR_S3_TEST_BUCKET")!,
            InternalEndpoint = Environment.GetEnvironmentVariable("LEXARBOR_S3_TEST_INTERNAL")!,
            PublicEndpoint = Environment.GetEnvironmentVariable("LEXARBOR_S3_TEST_PUBLIC")!,
            AccessKey = "synthetic-audio-access",
            SecretKey = "synthetic-audio-secret",
            OperationTimeoutSeconds = 3
        };
        await using var engine = await Engine.OpenAsync(root, options, new BeforeReceiptRecovery(marker));
        engine.Db.Vocabularies.Add(new() { Id = "enrichment-crash-word", Word = "lead" }); await engine.Db.SaveChangesAsync();
        var key = new EnrichmentTargetKey("enrichment-crash-word", null, AudioLocale.EnGb, EnrichmentStep.Audio);
        var task = (await engine.Tasks.EnsureAsync(key, EnrichmentTargetExecutor.ConfigurationVersion(key, engine.Runtime.Current), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), default))!;
        await engine.Executor.ExecuteAsync(task.Id, "crash-child", default);
        throw new InvalidOperationException("Enrichment did not stop at the expected publication crash point.");
    }

    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class BeforeReceiptRecovery(string marker) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken token = default)
        {
            if (command.CommandText.StartsWith("UPDATE \"enrichment_call\"", StringComparison.Ordinal))
            {
                using var read = command.Connection!.CreateCommand(); read.Transaction = command.Transaction;
                read.CommandText = "SELECT c.target_id FROM enrichment_call c JOIN audio_upload_operation u ON u.idempotency_key=c.upload_idempotency_key WHERE u.receipt_status_code=200 AND c.result='Started' LIMIT 1";
                if (await read.ExecuteScalarAsync(token) is string target)
                {
                    await File.WriteAllTextAsync(marker, target, token);
                    // Parent kills the real process without disposal or graceful cancellation.
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
            }
            return result;
        }
    }

    private sealed class Engine : IAsyncDisposable
    {
        public VocabularyDbContext Db { get; }
        public EnrichmentTaskRepository Tasks { get; }
        public AudioUploadRepository Uploads { get; }
        public EnrichmentRuntimeConfiguration Runtime { get; }
        public EnrichmentTargetExecutor Executor { get; }
        public SyntheticHttp Network { get; } = new();
        private readonly AudioRuntimeConfiguration _audio;
        private readonly IConfigurationRoot _configuration;
        private Engine(VocabularyDbContext db, EnrichmentRuntimeConfiguration runtime, IConfigurationRoot configuration, AudioOptions options)
        {
            Db = db; Runtime = runtime; _configuration = configuration;
            Tasks = new(db, TimeProvider.System); Uploads = new(db, TimeProvider.System);
            _audio = new(Options.Create(options), new S3AudioObjectStoreFactory(TimeProvider.System));
            Executor = new(Tasks, new EnrichmentExecutionRepository(db, Tasks, TimeProvider.System), Uploads,
                new OpenAiCompatiblePronunciationPreparationClient(Network, runtime), new PronunciationSpeechClient(Network, runtime),
                new AudioUploadOperationFactory(_audio, Uploads, TimeProvider.System), new AudioUploadAdmission(2, 8, TimeSpan.FromSeconds(3)),
                runtime, TimeProvider.System, new EnrichmentServiceGate(new EnrichmentSchedulerRepository(db, Tasks), runtime, TimeProvider.System));
        }
        public static async Task<Engine> OpenAsync(string root, AudioOptions options, params IInterceptor[] interceptors)
        {
            var store = new PersistentConfigurationFileStore(root);
            var values = new Dictionary<string, string?>
            {
                ["Enrichment:AudioEnabled"] = "true",
                ["Enrichment:Text:Endpoint"] = "https://planner.example.test/v1",
                ["Enrichment:Text:Model"] = "fixture",
                ["Enrichment:Text:ApiKey"] = "synthetic-planner",
                ["Enrichment:Tts:Provider"] = "OpenAICompatible",
                ["Enrichment:Tts:Endpoint"] = "https://api.openai.com/v1",
                ["Enrichment:Tts:Model"] = "gpt-4o-mini-tts",
                ["Enrichment:Tts:ApiKey"] = "synthetic-speech",
                ["Enrichment:Tts:Uk:Voice"] = "uk",
                ["Enrichment:Tts:Uk:Speed"] = "1"
            };
            var config = new ConfigurationBuilder().AddInMemoryCollection(values).AddJsonFile(store.FilePath, optional: true).Build();
            var runtime = new EnrichmentRuntimeConfiguration(config, store);
            if (!runtime.Current.ConfigurationReady)
            {
                var coordinator = new PersistentConfigurationCoordinator(config, store); var read = await coordinator.ReadAsync();
                var saved = await coordinator.SaveAsync(read.Revision, new Dictionary<string, JsonNode?> { ["Enrichment:AudioEnabled"] = JsonValue.Create(true) }, runtime.PrepareAsync, default);
                Check(saved.Status == ConfigurationSaveStatus.Success, "The disposable probe must persist actual configuration versions.");
            }
            var db = new VocabularyDbContext(new DbContextOptionsBuilder<VocabularyDbContext>().UseSqlite($"Data Source={Path.Combine(root, "probe.db")};Pooling=False").AddInterceptors(interceptors).Options);
            await db.Database.MigrateAsync();
            return new(db, runtime, config, options);
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); _audio.Dispose(); Network.Dispose(); (_configuration as IDisposable)?.Dispose(); }
    }
    private sealed class SyntheticHttp : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            if (request.RequestUri!.AbsolutePath == "/v1/chat/completions")
            {
                var plan = new
                {
                    certain = true,
                    entryKind = "word",
                    classificationBasis = "Synthetic recovery probe",
                    spokenText = "lead",
                    pronunciationHint = "Use British English.",
                    requiredPhonetic = (string?)null,
                    phonetic = (string?)null,
                    partsOfSpeech = Array.Empty<object>(),
                    pronunciationControl = "instructions",
                    speechPhonetic = (string?)null
                };
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { choices = new[] { new { finish_reason = "stop", message = new { content = JsonSerializer.Serialize(plan) } } } }) };
            }
            Check(request.RequestUri.Host == "api.openai.com" && request.RequestUri.AbsolutePath == "/v1/audio/speech", "Only the supported synthetic speech request is permitted.");
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures/tone.mp3"), token)) };
        }
    }
}

internal sealed class LostDeleteResult(IAudioObjectStore inner) : IAudioObjectStore
{
    public async Task RemoveAsync(AudioObjectLocation location, CancellationToken token)
    { await inner.RemoveAsync(location, token); throw new IOException("Synthetic lost successful Delete response."); }
    public async Task<AudioObjectInfo?> StatAsync(AudioObjectLocation location, CancellationToken token)
    { await inner.StatAsync(location, token); throw new IOException("Synthetic lost post-Delete observation response."); }
    public Task PutAsync(AudioObjectLocation location, Stream content, long length, string type, CancellationToken token) => inner.PutAsync(location, content, length, type, token);
    public Task<AudioPlaybackLink> GetPlaybackLinkAsync(AudioObjectLocation location, TimeSpan validity, CancellationToken token) => inner.GetPlaybackLinkAsync(location, validity, token);
}
