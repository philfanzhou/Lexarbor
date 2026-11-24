using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Host.Integration;
using Lexarbor.Host.Configuration;
using Microsoft.AspNetCore.Hosting;
using Lexarbor.Learning.Domain.Services;
using Lexarbor.Learning.Domain.Repositories;
using Lexarbor.Learning.Service;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lexarbor.Service.Tests;

public sealed class VocabularyPhotoTraceTests : IAsyncDisposable
{
    private const string VisionKey = "synthetic-vision-test-key";
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 5, 6];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"lexarbor-photo-traces-{Guid.NewGuid():N}");
    private readonly List<IAsyncDisposable> _hosts = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var host in Enumerable.Reverse(_hosts)) await host.DisposeAsync();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task StoreKeepsOnlyTheNewestRecordsAndTheirPhotos()
    {
        var store = new VocabularyPhotoTraceStore(_directory, NullLogger<VocabularyPhotoTraceStore>.Instance);
        var start = new DateTimeOffset(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(_directory).FullName, "notes.txt"), "kept");

        for (var index = 0; index < VocabularyPhotoTraceStore.Capacity + 2; index++)
            await store.SaveAsync(Record(start.AddSeconds(index), index % 2 == 0 ? Jpeg : Png));

        var records = Directory.GetFiles(_directory, "*.json").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(VocabularyPhotoTraceStore.Capacity, records.Length);
        Assert.StartsWith("20261123T080002000Z-", records[0]);
        Assert.Equal(VocabularyPhotoTraceStore.Capacity,
            Directory.GetFiles(_directory, "*.jpg").Length + Directory.GetFiles(_directory, "*.png").Length);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        Assert.True(File.Exists(Path.Combine(_directory, "notes.txt")));
    }

    [Fact]
    public async Task StoreKeepsAnUnreadableAnswerAsTextAndNoPhotoForAnUnknownFormat()
    {
        var store = new VocabularyPhotoTraceStore(_directory, NullLogger<VocabularyPhotoTraceStore>.Instance);
        var recognition = new VocabularyPhotoTrace { RecognitionStatus = 502, RecognitionResponse = "<html>bad gateway</html>"u8.ToArray() };

        await store.SaveAsync(Record(DateTimeOffset.UtcNow, "GIF89a"u8.ToArray(), recognition));

        var trace = ReadSingleTrace();
        Assert.Equal(JsonValueKind.Null, trace.GetProperty("photo").GetProperty("file").ValueKind);
        Assert.Equal(6, trace.GetProperty("photo").GetProperty("bytes").GetInt32());
        Assert.Equal(502, trace.GetProperty("visionModel").GetProperty("status").GetInt32());
        Assert.Equal("<html>bad gateway</html>", trace.GetProperty("visionModel").GetProperty("response").GetString());
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task StoreSwallowsAnUnwritableDirectory()
    {
        // A file where the directory should be cannot be written to, whoever runs the test.
        await File.WriteAllTextAsync(_directory, "occupied", Ct);
        try
        {
            var store = new VocabularyPhotoTraceStore(_directory, NullLogger<VocabularyPhotoTraceStore>.Instance);
            await store.SaveAsync(Record(DateTimeOffset.UtcNow, Jpeg));
        }
        finally
        {
            File.Delete(_directory);
        }
    }

    [Fact]
    public async Task SuccessfulImportKeepsThePhotoTheRawAnswerAndTheCandidates()
    {
        var visionModel = new VisionAnswer(HttpStatusCode.OK,
            Completion("""{"entries":[{"word":"apple","partOfSpeech":"n.","meaning":"苹果"}]}"""));
        var (client, root) = await StudentAsync(visionModel);

        var response = await PostPhotoAsync(client, root, Jpeg);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var candidates = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement
            .GetProperty("data").GetProperty("candidates");
        Assert.Equal("apple", candidates[0].GetProperty("word").GetString());

        var text = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(_directory, "*.json")), Ct);
        Assert.DoesNotContain(VisionKey, text);
        var trace = JsonDocument.Parse(text).RootElement;
        Assert.Equal("ok", trace.GetProperty("outcome").GetString());
        Assert.Equal(root.Split('/')[^1], trace.GetProperty("studentId").GetString());
        Assert.Equal(200, trace.GetProperty("visionModel").GetProperty("status").GetInt32());
        Assert.True(JsonElement.DeepEquals(JsonDocument.Parse(visionModel.Body).RootElement, trace.GetProperty("visionModel").GetProperty("response")));
        Assert.True(JsonElement.DeepEquals(candidates, trace.GetProperty("candidates")));
        Assert.True(trace.GetProperty("durationMs").GetProperty("recognition").GetInt64() >= 0);
        var photo = trace.GetProperty("photo");
        Assert.Equal("image/jpeg", photo.GetProperty("contentType").GetString());
        Assert.Equal(Jpeg, await File.ReadAllBytesAsync(Path.Combine(_directory, photo.GetProperty("file").GetString()!), Ct));
    }

    [Fact]
    public async Task AnswerOutsideTheContractIsKeptWithTheUnavailableOutcome()
    {
        var (client, root) = await StudentAsync(new VisionAnswer(HttpStatusCode.OK, """{"pages":[]}"""));

        var response = await PostPhotoAsync(client, root, Png);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var trace = ReadSingleTrace();
        Assert.Equal("unavailable", trace.GetProperty("outcome").GetString());
        Assert.Equal("The vision model returned an invalid transcription.", trace.GetProperty("error").GetString());
        Assert.Equal(0, trace.GetProperty("visionModel").GetProperty("response").GetProperty("pages").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, trace.GetProperty("candidates").ValueKind);
        Assert.EndsWith(".png", trace.GetProperty("photo").GetProperty("file").GetString());
    }

    [Fact]
    public async Task RefusalKeepsTheStatusAndItsBody()
    {
        var (client, root) = await StudentAsync(new VisionAnswer(HttpStatusCode.BadRequest, """{"error":{"code":"invalid_image"}}"""));

        var response = await PostPhotoAsync(client, root, Jpeg);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var trace = ReadSingleTrace();
        Assert.Equal("invalid", trace.GetProperty("outcome").GetString());
        Assert.Equal(400, trace.GetProperty("visionModel").GetProperty("status").GetInt32());
        Assert.Equal("invalid_image", trace.GetProperty("visionModel").GetProperty("response").GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task RequestThatNeverReachesVisionLeavesNoTrace()
    {
        var visionModel = new VisionAnswer(HttpStatusCode.OK, Completion("""{"entries":[]}"""));
        var (client, root) = await StudentAsync(visionModel);
        var unknown = root[..(root.LastIndexOf('/') + 1)] + Guid.NewGuid();

        Assert.Equal(HttpStatusCode.NotFound, (await PostPhotoAsync(client, unknown, Jpeg)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostPhotoAsync(client, root, "GIF89a"u8.ToArray())).StatusCode);

        Assert.Equal(0, visionModel.Calls);
        Assert.False(Directory.Exists(_directory) && Directory.EnumerateFileSystemEntries(_directory).Any());
    }

    [Fact]
    public async Task ImportAnswersNormallyWhenTheTraceCannotBeWritten()
    {
        var (client, root) = await StudentAsync(new VisionAnswer(HttpStatusCode.OK, Completion("""{"entries":[]}""")));
        await File.WriteAllTextAsync(_directory, "occupied", Ct);
        try
        {
            var response = await PostPhotoAsync(client, root, Jpeg);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            File.Delete(_directory);
        }
    }

    private static VocabularyPhotoTraceRecord Record(DateTimeOffset at, byte[] photo, VocabularyPhotoTrace? recognition = null) =>
        new(at, Guid.NewGuid(), photo, "image/jpeg", "ok", null, 12, recognition ?? new VocabularyPhotoTrace(), []);

    private JsonElement ReadSingleTrace() =>
        JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(_directory, "*.json")))).RootElement;

    private static async Task<HttpResponseMessage> PostPhotoAsync(HttpClient client, string root, byte[] photo)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(photo);
        file.Headers.ContentType = new MediaTypeHeaderValue(photo[0] == 0x89 ? "image/png" : "image/jpeg");
        form.Add(file, "file", "photo");
        return await client.PostAsync($"{root}/notebook/photo", form, Ct);
    }

    private async Task<(HttpClient Client, string Root)> StudentAsync(VisionAnswer visionModel)
    {
        var factory = new VocabularyWebApplicationFactory();
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Enrichment:Text:Endpoint"] = "https://vision.test/v1",
                ["Enrichment:Text:Model"] = "vision-fixture",
                ["Enrichment:Text:ApiKey"] = VisionKey
            })).ConfigureTestServices(services =>
        {
            services.AddHttpClient(VisionPhotoRecognizer.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new VisionHandler(visionModel));
            services.AddSingleton<IVocabularyPhotoCatalog>(new EmptyCatalog());
            services.AddSingleton(new VocabularyPhotoTraceStore(_directory, NullLogger<VocabularyPhotoTraceStore>.Instance));
        }));
        _hosts.Add(factory);
        _hosts.Add(host);
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", await host.SeedKeyAsync("photo-traces", catalog: false, learning: true));
        var student = Guid.NewGuid();
        (await client.PutAsync($"/learning/v1/learners/{student}", null, Ct)).EnsureSuccessStatusCode();
        return (client, $"/learning/v1/api/vocabulary/students/{student}");
    }

    [Theory]
    [InlineData("Models:Reasoning")]
    [InlineData("Enrichment:Text")]
    [InlineData("MeaningSubmissions:Ai")]
    public async Task DirectVisionUsesTheSharedConnectionAndPreservesSeparateSenses(string section)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [section + ":Endpoint"] = "https://vision.test/v1", [section + ":Model"] = "vision-fixture",
            [section + ":ApiKey"] = VisionKey, ["VocabularyPhotoImport:Enabled"] = "false",
            ["MeaningSubmissions:Ai:Model"] = section != "MeaningSubmissions:Ai" ? "unused-review-model" : "vision-fixture"
        }).Build();
        var runtime = new EnrichmentRuntimeConfiguration(config, new PersistentConfigurationFileStore(_directory));
        Assert.Equal("vision-fixture", runtime.Current.ReviewAssistant.Model);
        var answer = new VisionAnswer(HttpStatusCode.OK, Completion("""
            {"entries":[{"word":"play","partOfSpeech":"v.","meaning":"玩"},
                        {"word":"play","partOfSpeech":"n.","meaning":"戏剧"},
                        {"word":"on time","partOfSpeech":null,"meaning":"","review":"释义归属不明"}]}
            """));
        var services = new ServiceCollection();
        services.AddHttpClient(VisionPhotoRecognizer.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new VisionHandler(answer));
        using var provider = services.BuildServiceProvider();
        var recognizer = new VisionPhotoRecognizer(provider.GetRequiredService<IHttpClientFactory>(), runtime,
            new(), NullLogger<VisionPhotoRecognizer>.Instance);
        var service = new VocabularyPhotoImportService(new KnownLearner(), recognizer, new EmptyCatalog());
        var candidates = await service.PreviewAsync(Guid.NewGuid(), new VocabPhoto(Png, "image/jpeg"), Ct);
        Assert.Equal(["玩", "戏剧", ""], candidates.Select(e => e.Meaning));
        Assert.Equal("释义归属不明", candidates[2].Review);
        Assert.All(candidates, candidate => Assert.Equal(VocabPhotoCandidateState.New, candidate.State));
        Assert.Equal(1, answer.Calls);
    }

    [Fact]
    public async Task OldPhotoSwitchWithoutReasoningModelDoesNotDispatch()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["VocabularyPhotoImport:Enabled"] = "true", ["VocabularyPhotoImport:BaseUrl"] = "https://old-ocr.test"
        }).Build();
        using var lifetime = config as IDisposable;
        var runtime = new EnrichmentRuntimeConfiguration(config, new PersistentConfigurationFileStore(_directory));
        var answer = new VisionAnswer(HttpStatusCode.OK, Completion("""{"entries":[]}"""));
        var services = new ServiceCollection();
        services.AddHttpClient(VisionPhotoRecognizer.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new VisionHandler(answer));
        using var provider = services.BuildServiceProvider();
        var recognizer = new VisionPhotoRecognizer(provider.GetRequiredService<IHttpClientFactory>(), runtime,
            new(), NullLogger<VisionPhotoRecognizer>.Instance);
        await Assert.ThrowsAsync<VocabPhotoImportUnavailableException>(() => recognizer.RecognizeAsync(new VocabPhoto(Jpeg, "image/jpeg"), Ct));
        Assert.Equal(0, answer.Calls);
    }

    [Theory]
    [InlineData("{\"entries\":[]}", true)]
    [InlineData("{}", false)]
    [InlineData("{\"entries\":[{\"word\":\"example\"}]}", false)]
    [InlineData("not json", false)]
    public void VisionTranscriptionDistinguishesEmptyAndMalformedAnswers(string content, bool valid)
    {
        var body = Encoding.UTF8.GetBytes(Completion(content));
        if (valid) Assert.Empty(VisionPhotoRecognizer.Parse(body));
        else Assert.Throws<VocabPhotoImportUnavailableException>(() => VisionPhotoRecognizer.Parse(body));
    }

    private static string Completion(string content) =>
        JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content } } } });

    private sealed record VisionAnswer(HttpStatusCode Status, string Body)
    {
        public int Calls;
    }

    private sealed class VisionHandler(VisionAnswer answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://vision.test/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.True(request.Headers.Authorization?.Parameter == VisionKey);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("vision-fixture", json.RootElement.GetProperty("model").GetString());
            var image = json.RootElement.GetProperty("messages")[1].GetProperty("content")[1];
            Assert.Equal("image_url", image.GetProperty("type").GetString());
            var data = image.GetProperty("image_url").GetProperty("url").GetString();
            Assert.Contains(data, new[] { "data:image/jpeg;base64," + Convert.ToBase64String(Jpeg),
                "data:image/png;base64," + Convert.ToBase64String(Png) });

            Interlocked.Increment(ref answer.Calls);
            return new HttpResponseMessage(answer.Status)
            {
                Content = new StringContent(answer.Body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class KnownLearner : ILearnerDirectory
    {
        public Task<bool> ExistsAsync(Guid studentId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class EmptyCatalog : IVocabularyPhotoCatalog
    {
        public Task<IReadOnlyDictionary<string, IReadOnlyList<VocabCatalogMeaning>>> LookupMeaningsAsync(
            IReadOnlyList<string> words, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<VocabCatalogMeaning>>>(
                words.ToDictionary(word => word, IReadOnlyList<VocabCatalogMeaning> (_) => []));
    }
}
