using System.Net;
using System.Text.Json;
using Lexarbor.Host.Configuration;
using Lexarbor.Host.Integration;

namespace Lexarbor.Service.Tests;

public sealed class AiTextBatchClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly EnrichmentClientSettings Settings = new("https://fixture.example/v1", "fixture", "fixture-secret", 30);
    private static AiTextItem<int> Item(string id) => new(id, "{\"word\":\"fixture\"}", value => JsonDocument.Parse(value).RootElement.GetProperty("value").GetInt32());
    [Theory]
    [InlineData("{\"results\":[{\"id\":\"b\",\"result\":{\"value\":2}},{\"id\":\"a\",\"result\":{\"value\":1}}]}", "Ready", 2)]
    [InlineData("{\"results\":[{\"id\":\"a\",\"result\":{\"value\":1}}]}", "Partial", 1)]
    [InlineData("{\"results\":[{\"id\":\"a\",\"result\":{\"value\":1}},{\"id\":\"b\",\"result\":{}}]}", "Partial", 1)]
    [InlineData("{\"results\":[{\"id\":\"a\",\"result\":{\"value\":1}},{\"id\":\"a\",\"result\":{\"value\":2}}]}", "Partial", 0)]
    [InlineData("{\"results\":[{\"id\":\"foreign\",\"result\":{\"value\":1}}]}", "Invalid", 0)]
    [InlineData("{\"results\":[{\"id\":\"a\",\"result\":{\"value\":1,\"secret\":\"fixture-secret\"}}]}", "Invalid", 0)]
    [InlineData("{\"results\":", "Invalid", 0)]
    public async Task ValidatesMembersIndependentlyAndNeverUsesArrayOrder(string content, string outcome, int count)
    {
        using var http = new Factory(content);
        var result = await new AiTextBatchClient(http, TimeProvider.System).SendAsync(Settings, "rules", [Item("a"), Item("b")], Ct);
        Assert.Equal(outcome, result.Outcome); Assert.Equal(count, result.Items.Count);
        if (result.Items.ContainsKey("a")) Assert.Equal(1, result.Items["a"]);
        if (result.Items.ContainsKey("b")) Assert.Equal(2, result.Items["b"]);
        Assert.Equal(1, http.Calls);
    }
    [Fact]
    public async Task SingleFallbackUsesOriginalSchemaAndTruncationRejectsWholeResponse()
    {
        using var http = new Factory("{\"value\":7}");
        var client = new AiTextBatchClient(http, TimeProvider.System);
        Assert.Equal(7, (await client.SendAsync(Settings, "rules", [Item("a")], Ct)).Items["a"]);
        Assert.Equal("rules", http.Instructions); Assert.Equal("{\"word\":\"fixture\"}", http.Prompt);
        http.Finish = "length";
        Assert.Empty((await client.SendAsync(Settings, "rules", [Item("a")], Ct)).Items);
    }
    [Fact]
    public async Task OversizedResponseIsBoundedAndRateLimitIsNotAValidationFailure()
    {
        using var http = new Factory(new string('x', 70000));
        var client = new AiTextBatchClient(http, TimeProvider.System);
        Assert.Equal("batch.response_too_large", (await client.SendAsync(Settings, "rules", [Item("a")], Ct)).ErrorCode);
        http.Status = HttpStatusCode.TooManyRequests;
        Assert.Equal("Throttled", (await client.SendAsync(Settings, "rules", [Item("a")], Ct)).Outcome);
    }
    [Fact]
    public async Task ProviderRejectingBatchCanFallBackAndPlainText429RemainsThrottled()
    {
        using var http = new Factory("{}") { Status = HttpStatusCode.BadRequest, RawBody = "unsupported request" };
        var client = new AiTextBatchClient(http, TimeProvider.System);
        Assert.Equal("Invalid", (await client.SendAsync(Settings, "rules", [Item("a"), Item("b")], Ct)).Outcome);
        Assert.Equal("Rejected", (await client.SendAsync(Settings, "rules", [Item("a")], Ct)).Outcome);
        http.Status = HttpStatusCode.TooManyRequests;
        Assert.Equal("Throttled", (await client.SendAsync(Settings, "rules", [Item("a"), Item("b")], Ct)).Outcome);
    }

    private sealed class Factory(string content) : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls; public string Finish = "stop"; public string? Instructions, Prompt;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string? RawBody;
        private HttpClient? _client;
        public HttpClient CreateClient(string name) => _client ??= new(this, false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Instructions = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString();
            Prompt = body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
            return new(Status) { Content = RawBody != null ? new StringContent(RawBody) : JsonContent.Create(new { choices = new[] { new { finish_reason = Finish, message = new { content } } } }) };
        }
        protected override void Dispose(bool disposing) { if (disposing) _client?.Dispose(); base.Dispose(disposing); }
    }
}
