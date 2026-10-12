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

}
