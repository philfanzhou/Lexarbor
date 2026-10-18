using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Host.Audio;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Lexarbor.Service.Tests;

public sealed class AudioStorageTests
{
    internal static AudioOptions ValidOptions() => new()
    {
        Enabled = true,
        InternalEndpoint = "http://127.0.0.1:1",
        PublicEndpoint = "https://audio.example.test/s3/",
        AccessKey = "synthetic-access",
        SecretKey = "synthetic-secret"
    };
    internal static AudioObjectLocation Location() => new("lexarbor-audio", $"audio/{Guid.NewGuid():N}.mp3");

    [Fact]
    public async Task OldConfigurationStartsAndDisabledStoreDoesNotUseNetwork()
    {
        using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live", TestContext.Current.CancellationToken)).StatusCode);
        var store = factory.Services.GetRequiredService<IAudioObjectStore>();
        var error = await Assert.ThrowsAsync<AudioStorageException>(() => store.StatAsync(Location(), TestContext.Current.CancellationToken));
        Assert.Equal("audio.storage.disabled", error.Code);
    }

    [Fact]
    public async Task InvalidEnabledConfigurationRejectsStartupWithSafeDiagnostic()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audio:Enabled"] = "true",
            ["Audio:SecretKey"] = "synthetic-secret-marker"
        });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddAudioStorage(builder.Configuration);
        using var host = builder.Build();
        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));
        Assert.Contains(AudioOptions.ConfigurationFailure, error.Message);
        Assert.DoesNotContain("synthetic-secret-marker", error.ToString());
    }

    [Theory]
    [InlineData("Enabled", "synthetic-invalid-marker")]
    [InlineData("PlaybackTtlSeconds", "synthetic-invalid-marker")]
    [InlineData("MaxUploadBytes", "synthetic-invalid-marker")]
    [InlineData("MaxTextbookUploadBytes", "synthetic-invalid-marker")]
    [InlineData("MaxTextbookDurationSeconds", "synthetic-invalid-marker")]
    [InlineData("OperationTimeoutSeconds", "2147483648")]
    [InlineData("MaxUploadBytes", "9223372036854775808")]
    public async Task InvalidConfigurationTypesRejectStartupWithoutEchoingValues(string property, string value)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audio:Enabled"] = "true",
            [$"Audio:{property}"] = value
        });
        using var logs = new RecordingLogs();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AddAudioStorage(builder.Configuration);
        using var host = builder.Build();

        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Equal(AudioOptions.ConfigurationFailure, error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(value, error.ToString());
        Assert.Contains(logs.Entries, entry => entry.Contains(AudioOptions.ConfigurationFailure, StringComparison.Ordinal));
        Assert.All(logs.Entries, entry => Assert.DoesNotContain(value, entry));
    }

    [Fact]
    public void MissingAudioConfigurationUsesSixtySecondTimeout()
    {
        var services = new ServiceCollection();
        services.AddAudioStorage(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        Assert.Equal(60, provider.GetRequiredService<IOptions<AudioOptions>>().Value.OperationTimeoutSeconds);
    }

    [Theory]
    [InlineData(null, 60)]
    [InlineData("17", 17)]
    public void HostConfigurationUsesDefaultTimeoutAndHonorsOverrides(string? value, int expected)
    {
        var configuration = new Dictionary<string, string?>();
        if (value is not null)
            configuration["Audio:OperationTimeoutSeconds"] = value;
        using var factory = new VocabularyWebApplicationFactory("Testing", true, extraConfiguration: configuration);

        Assert.Equal(expected, factory.Services.GetRequiredService<IOptions<AudioOptions>>().Value.OperationTimeoutSeconds);
    }

    [Fact]
    public async Task OfflineStorageDoesNotAffectExistingHostApis()
    {
        using var factory = new VocabularyWebApplicationFactory("Testing", true, extraConfiguration: new Dictionary<string, string?>
        {
            ["Audio:Enabled"] = "true",
            ["Audio:InternalEndpoint"] = "http://127.0.0.1:1",
            ["Audio:PublicEndpoint"] = "https://audio.example.test/",
            ["Audio:AccessKey"] = "synthetic-access",
            ["Audio:SecretKey"] = "synthetic-secret",
            ["Audio:OperationTimeoutSeconds"] = "1"
        });
        using var client = factory.CreateClient();
        var store = factory.Services.GetRequiredService<IAudioObjectStore>();
        var error = await Assert.ThrowsAsync<AudioStorageException>(() => store.StatAsync(Location(), TestContext.Current.CancellationToken));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("synthetic-secret", error.ToString());
        foreach (var route in new[] { "/health/live", "/health/ready", "/api/vocabulary-books/all" })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(route, TestContext.Current.CancellationToken)).StatusCode);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            var now = DateTimeOffset.UtcNow;
            database.VocabularyBooks.Add(new VocabularyBookEntity
            {
                Id = "offline-book",
                BookName = "Offline audio book",
                Status = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            for (var index = 0; index < 4; index++)
            {
                database.Vocabularies.Add(new VocabularyEntity
                {
                    Id = $"offline-word-{index}",
                    Word = $"offline-word-{index}",
                    CreatedAt = now,
                    UpdatedAt = now
                });
                database.VocabularyMeanings.Add(new VocabularyMeaningEntity
                {
                    Id = $"offline-meaning-{index}",
                    VocabularyId = $"offline-word-{index}",
                    BookId = "offline-book",
                    Meaning = $"Offline meaning {index}",
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
            await database.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        using var quiz = await client.PostAsJsonAsync("/api/vocabulary/question",
            new { wordId = "offline-word-0", bookId = "offline-book", chineseToEnglish = true },
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, quiz.StatusCode);
        using var document = JsonDocument.Parse(await quiz.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.True(document.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(4, document.RootElement.GetProperty("data").GetProperty("options").GetArrayLength());
    }

    [Theory]
    [InlineData("InternalEndpoint", "http://user:password@example.test/")]
    [InlineData("InternalEndpoint", "http://example.test/prefix/")]
    [InlineData("PublicEndpoint", "https://example.test/s3/?secret=value")]
    [InlineData("PublicEndpoint", "https://example.test/s3/../")]
    [InlineData("PublicEndpoint", "ftp://example.test/")]
    [InlineData("KeyPrefix", "../")]
    [InlineData("Bucket", "pictures.bucket")]
    [InlineData("Region", "region?secret")]
    [InlineData("PlaybackTtlSeconds", "59")]
    [InlineData("PlaybackTtlSeconds", "3601")]
    [InlineData("MaxUploadBytes", "0")]
    [InlineData("MaxUploadBytes", "20971521")]
    [InlineData("MaxConcurrentUploads", "0")]
    [InlineData("MaxConcurrentUploads", "9")]
    [InlineData("MaxQueuedUploads", "-1")]
    [InlineData("MaxQueuedUploads", "9")]
    [InlineData("OperationTimeoutSeconds", "0")]
    [InlineData("OperationTimeoutSeconds", "121")]
    [InlineData("RecoveryBatchSize", "0")]
    [InlineData("RecoveryBatchSize", "101")]
    [InlineData("RecoveryIntervalSeconds", "9")]
    [InlineData("RecoveryIntervalSeconds", "3601")]
    [InlineData("RecoveryLeaseSeconds", "30")]
    [InlineData("RecoveryLeaseSeconds", "901")]
    [InlineData("RecoveryRetrySeconds", "9")]
    [InlineData("RecoveryRetrySeconds", "3601")]
    [InlineData("RecoveryMaxRetrySeconds", "59")]
    [InlineData("RecoveryMaxRetrySeconds", "86401")]
    [InlineData("RecoveryRecheckSeconds", "3599")]
    [InlineData("RecoveryRecheckSeconds", "604801")]
    public void ConfigurationBoundariesAreRejected(string property, string value)
    {
        var options = ValidOptions();
        var member = typeof(AudioOptions).GetProperty(property)!;
        member.SetValue(options, Convert.ChangeType(value, member.PropertyType));
        Assert.False(options.IsValid());
    }

    [Fact]
    public void SupportedLimitsAndDisabledEmptyConfigurationAreValid()
    {
        Assert.True(new AudioOptions().IsValid());
        var options = ValidOptions();
        options.PlaybackTtlSeconds = 60; options.MaxUploadBytes = 1;
        options.MaxConcurrentUploads = 1; options.MaxQueuedUploads = 0;
        Assert.True(options.IsValid());
        options.PlaybackTtlSeconds = 3600; options.MaxUploadBytes = 20 * 1024 * 1024;
        options.MaxConcurrentUploads = 8; options.MaxQueuedUploads = 8;
        Assert.True(options.IsValid());
    }

    [Theory]
    [InlineData("pictures", "audio/0123456789abcdef0123456789abcdef.mp3")]
    [InlineData("lexarbor-audio", "audio/../pictures/file.mp3")]
    [InlineData("lexarbor-audio", "audio/file.mp3")]
    [InlineData("lexarbor-audio", "other/0123456789abcdef0123456789abcdef.mp3")]
    public async Task AllOperationsRejectForeignLocationsBeforeNetwork(string bucket, string key)
    {
        using var store = new S3AudioObjectStore(Options.Create(ValidOptions()), TimeProvider.System);
        var location = new AudioObjectLocation(bucket, key);
        using var content = new MemoryStream([1]);
        await Assert.ThrowsAsync<AudioStorageException>(() => store.PutAsync(location, content, 1, "audio/mpeg", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<AudioStorageException>(() => store.StatAsync(location, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<AudioStorageException>(() => store.RemoveAsync(location, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<AudioStorageException>(() => store.GetPlaybackLinkAsync(location, TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SigningUsesPublicHostAndPrefixWithoutNetworkAndHonorsCancellation()
    {
        using var store = new S3AudioObjectStore(Options.Create(ValidOptions()), TimeProvider.System);
        var link = await store.GetPlaybackLinkAsync(Location(), TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        var uri = new Uri(link.Url);
        Assert.Equal("audio.example.test", uri.Host);
        Assert.StartsWith("/s3/lexarbor-audio/audio/", uri.AbsolutePath);
        Assert.Contains("X-Amz-Expires=60", uri.Query);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.StatAsync(Location(), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RemoveAsync(Location(), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetPlaybackLinkAsync(Location(), TimeSpan.FromSeconds(60), cancelled.Token));
        using var content = new MemoryStream([1]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PutAsync(Location(), content, 1, "audio/mpeg", cancelled.Token));
        await Assert.ThrowsAsync<AudioStorageException>(() => store.GetPlaybackLinkAsync(Location(), TimeSpan.FromSeconds(59), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<AudioStorageException>(() => store.PutAsync(Location(), content, 0, "audio/mpeg", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<AudioStorageException>(() => store.PutAsync(Location(), content, 1, "image/png", TestContext.Current.CancellationToken));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StalledRequestsRespectTimeoutAndInFlightCancellation(bool cancelCaller)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var options = ValidOptions();
        options.InternalEndpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
        options.OperationTimeoutSeconds = 1;
        using var store = new S3AudioObjectStore(Options.Create(options), TimeProvider.System);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pending = store.StatAsync(Location(), caller.Token);
        using var socket = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        // The peer accepted the actual HTTP request but never replies.
        if (cancelCaller)
        {
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        else
        {
            var error = await Assert.ThrowsAsync<AudioStorageException>(() => pending);
            Assert.Equal("audio.storage.timeout", error.Code);
            Assert.Null(error.InnerException);
            Assert.DoesNotContain(options.InternalEndpoint, error.ToString());
            Assert.DoesNotContain(options.SecretKey, error.ToString());
        }
    }

    [Theory]
    [InlineData(2, 4, AudioUploadPurpose.Vocabulary)]
    [InlineData(2, 4, AudioUploadPurpose.Textbook)]
    [InlineData(4, 2, AudioUploadPurpose.Vocabulary)]
    [InlineData(4, 2, AudioUploadPurpose.Textbook)]
    public async Task UploadPurposeUsesExactCapturedLimit_InclusiveAndIndependent(long wordLimit, long textbookLimit, AudioUploadPurpose purpose)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var options = ValidOptions(); options.MaxUploadBytes = wordLimit; options.MaxTextbookUploadBytes = textbookLimit;
        options.InternalEndpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
        using var store = new S3AudioObjectStore(Options.Create(options), TimeProvider.System);
        // Mutating the source options cannot change this store's captured policy.
        options.MaxUploadBytes = 1; options.MaxTextbookUploadBytes = 1;
        var limit = purpose == AudioUploadPurpose.Vocabulary ? wordLimit : textbookLimit;
        using var input = new MemoryStream(new byte[8]); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        // A cancelled valid call gets past input validation. No provider request is needed for this boundary assertion.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PutAsync(Location(), input, limit, "audio/mpeg", purpose, cancelled.Token));
        var error = await Assert.ThrowsAsync<AudioStorageException>(() => store.PutAsync(Location(), input, limit + 1, "audio/mpeg", purpose, cancelled.Token));
        Assert.Equal("audio.storage.invalid_input", error.Code); Assert.False(listener.Pending());
        if (purpose == AudioUploadPurpose.Vocabulary)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.PutAsync(Location(), input, wordLimit, "audio/mpeg", cancelled.Token));
            var legacy = await Assert.ThrowsAsync<AudioStorageException>(() => store.PutAsync(Location(), input, wordLimit + 1, "audio/mpeg", cancelled.Token));
            Assert.Equal("audio.storage.invalid_input", legacy.Code); Assert.False(listener.Pending());
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public async Task UnknownUploadPurposeIsRejectedBeforeNetwork(int purpose)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var options = ValidOptions(); options.InternalEndpoint = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
        using var store = new S3AudioObjectStore(Options.Create(options), TimeProvider.System); using var input = new MemoryStream([1]);
        var error = await Assert.ThrowsAsync<AudioStorageException>(() => store.PutAsync(Location(), input, 1, "audio/mpeg", (AudioUploadPurpose)purpose, TestContext.Current.CancellationToken));
        Assert.Equal("audio.storage.invalid_input", error.Code); Assert.Null(error.InnerException); Assert.False(listener.Pending());
    }

    private sealed class RecordingLogs : ILoggerProvider
    {
        public List<string> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Recorder(this);
        public void Dispose() { }

        private sealed class Recorder(RecordingLogs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => owner.Entries.Add(formatter(state, exception) + exception?.ToString());
        }
    }

}
