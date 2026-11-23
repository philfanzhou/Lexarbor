using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Services;
using Lexarbor.Learning.Service;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lexarbor.Service.Tests;

public sealed class VocabularyPhotoTraceTests : IAsyncDisposable
{
    private const string StructaDocKey = "structadoc-trace-test-key";
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
        Assert.Equal(502, trace.GetProperty("structaDoc").GetProperty("status").GetInt32());
        Assert.Equal("<html>bad gateway</html>", trace.GetProperty("structaDoc").GetProperty("response").GetString());
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
        var structaDoc = new StructaDocAnswer(HttpStatusCode.OK,
            """{"blocks":[{"type":"text","content":"apple n. 苹果","contentFormat":"plain","extra":"kept"}]}""");
        var (client, root) = await StudentAsync(structaDoc);

        var response = await PostPhotoAsync(client, root, Jpeg);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var candidates = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement
            .GetProperty("data").GetProperty("candidates");
        Assert.Equal("apple", candidates[0].GetProperty("word").GetString());

        var text = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(_directory, "*.json")), Ct);
        Assert.DoesNotContain(StructaDocKey, text);
        var trace = JsonDocument.Parse(text).RootElement;
        Assert.Equal("ok", trace.GetProperty("outcome").GetString());
        Assert.Equal(root.Split('/')[^1], trace.GetProperty("studentId").GetString());
        Assert.Equal(200, trace.GetProperty("structaDoc").GetProperty("status").GetInt32());
        Assert.Equal("kept", trace.GetProperty("structaDoc").GetProperty("response").GetProperty("blocks")[0].GetProperty("extra").GetString());
        Assert.True(JsonElement.DeepEquals(candidates, trace.GetProperty("candidates")));
        Assert.True(trace.GetProperty("durationMs").GetProperty("recognition").GetInt64() >= 0);
        var photo = trace.GetProperty("photo");
        Assert.Equal("image/jpeg", photo.GetProperty("contentType").GetString());
        Assert.Equal(Jpeg, await File.ReadAllBytesAsync(Path.Combine(_directory, photo.GetProperty("file").GetString()!), Ct));
    }

    [Fact]
    public async Task AnswerOutsideTheContractIsKeptWithTheUnavailableOutcome()
    {
        var (client, root) = await StudentAsync(new StructaDocAnswer(HttpStatusCode.OK, """{"pages":[]}"""));

        var response = await PostPhotoAsync(client, root, Png);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var trace = ReadSingleTrace();
        Assert.Equal("unavailable", trace.GetProperty("outcome").GetString());
        Assert.Equal("StructaDoc answered without blocks.", trace.GetProperty("error").GetString());
        Assert.Equal(0, trace.GetProperty("structaDoc").GetProperty("response").GetProperty("pages").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, trace.GetProperty("candidates").ValueKind);
        Assert.EndsWith(".png", trace.GetProperty("photo").GetProperty("file").GetString());
    }

    [Fact]
    public async Task RefusalKeepsTheStatusAndItsBody()
    {
        var (client, root) = await StudentAsync(new StructaDocAnswer(HttpStatusCode.BadRequest, """{"code":"IMAGE_UNREADABLE"}"""));

        var response = await PostPhotoAsync(client, root, Jpeg);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var trace = ReadSingleTrace();
        Assert.Equal("invalid", trace.GetProperty("outcome").GetString());
        Assert.Equal(400, trace.GetProperty("structaDoc").GetProperty("status").GetInt32());
        Assert.Equal("IMAGE_UNREADABLE", trace.GetProperty("structaDoc").GetProperty("response").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ImportAnswersNormallyWhenTheTraceCannotBeWritten()
    {
        var (client, root) = await StudentAsync(new StructaDocAnswer(HttpStatusCode.OK, """{"blocks":[]}"""));
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

    private async Task<(HttpClient Client, string Root)> StudentAsync(StructaDocAnswer structaDoc)
    {
        var options = VocabularyPhotoImportOptions.Read(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["VocabularyPhotoImport:Enabled"] = "true",
            ["VocabularyPhotoImport:BaseUrl"] = "http://structadoc.test",
            ["VocabularyPhotoImport:ApiKey"] = StructaDocKey,
        }).Build());
        var factory = new VocabularyWebApplicationFactory();
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton(options);
            services.AddHttpClient(StructaDocPhotoRecognizer.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new StructaDocHandler(structaDoc));
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

    private sealed record StructaDocAnswer(HttpStatusCode Status, string Body);

    private sealed class StructaDocHandler(StructaDocAnswer answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/api/v1/image/recognize", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(answer.Status)
            {
                Content = new StringContent(answer.Body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class EmptyCatalog : IVocabularyPhotoCatalog
    {
        public Task<IReadOnlyDictionary<string, IReadOnlyList<VocabCatalogMeaning>>> LookupMeaningsAsync(
            IReadOnlyList<string> words, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<VocabCatalogMeaning>>>(
                words.ToDictionary(word => word, IReadOnlyList<VocabCatalogMeaning> (_) => []));
    }
}
