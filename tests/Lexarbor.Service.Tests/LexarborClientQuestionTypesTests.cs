using System.Net;
using System.Text;
using System.Text.Json;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Services;
using Lexarbor.Learning.Service;
using Microsoft.Extensions.Options;

namespace Lexarbor.Service.Tests;

public sealed class LexarborClientQuestionTypesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Choice = """
        {"success":true,"data":{"type":"ChineseToEnglish","prompt":"苹果","options":[
        {"text":"apple","isCorrect":true},{"text":"pear","isCorrect":false},{"text":"plum","isCorrect":false},{"text":"grape","isCorrect":false}]}}
        """;

    [Fact]
    public async Task TypesAreSentOnlyWhenPresentAndBindTheReply()
    {
        var handler = new Capture(Choice);
        var client = new CatalogClient(new HttpClient(handler), Options.Create(new CatalogOptions { BaseUrl = "https://catalog.test" }));

        var question = await client.GetLearningQuestionAsync(Request(["ChineseToEnglish"]), Ct);
        Assert.Equal(VocabQuestionDirection.ChineseToEnglish, question!.Direction);
        using (var body = JsonDocument.Parse(handler.Body!))
            Assert.Equal("[\"ChineseToEnglish\"]", body.RootElement.GetProperty("types").GetRawText());

        await client.GetLearningQuestionAsync(Request(null), Ct);
        using (var body = JsonDocument.Parse(handler.Body!))
            Assert.False(body.RootElement.TryGetProperty("types", out _));

        // A reply outside the requested types breaks the provider contract.
        await Assert.ThrowsAsync<VocabCatalogUnavailableException>(() => client.GetLearningQuestionAsync(Request(["FillLetters"]), Ct));
    }

    private static CatalogLearningQuestionRequest Request(IReadOnlyList<string>? types) =>
        new("apple", "book", "apple-m", "review", "one", SameEntryKind: true, Types: types);

    private sealed class Capture(string reply) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }
}
