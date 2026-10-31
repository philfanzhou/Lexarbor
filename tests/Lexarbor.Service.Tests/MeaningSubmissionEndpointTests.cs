using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Lexarbor.Service.Tests;

public sealed class MeaningSubmissionEndpointTests : IAsyncLifetime
{
    private const string Issuer = "urn:test:student";
    private const string Subject = "test.student";
    private const string Audience = "urn:lexarbor:integration";
    private const string KeyId = "student-1";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly RSA _key = RSA.Create(2048);
    private readonly string _keyDirectory = Directory.CreateTempSubdirectory("lexarbor-integration-").FullName;
    private VocabularyWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public async ValueTask InitializeAsync()
    {
        var publicKeyPath = Path.Combine(_keyDirectory, "student.pub.pem");
        await File.WriteAllTextAsync(publicKeyPath, _key.ExportSubjectPublicKeyInfoPem(), Ct);
        _factory = new VocabularyWebApplicationFactory("Testing", true, extraConfiguration: new Dictionary<string, string?>
        {
            ["MeaningSubmissions:BookId"] = "supplementary",
            ["MeaningSubmissions:Integration:Issuer"] = Issuer,
            ["MeaningSubmissions:Integration:Subject"] = Subject,
            ["MeaningSubmissions:Integration:PublicKeys:0:KeyId"] = KeyId,
            ["MeaningSubmissions:Integration:PublicKeys:0:PublicKeyPath"] = publicKeyPath
        });
        _client = _factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        db.VocabularyBooks.AddRange(
            new() { Id = "supplementary", BookName = "Supplementary", Status = true },
            new() { Id = "main", BookName = "Main", Status = true });
        foreach (var (id, word, meaning) in new[] { ("w1", "apple", "苹果"), ("w2", "river", "河流"), ("w3", "cloud", "云"), ("w4", "stone", "石头") })
        {
            db.Vocabularies.Add(new() { Id = id, Word = word });
            db.VocabularyMeanings.Add(new() { Id = "m" + id, VocabularyId = id, BookId = "main", Meaning = meaning });
        }

        await db.SaveChangesAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        _key.Dispose();
        Directory.Delete(_keyDirectory, recursive: true);
    }

    private string Token(string scope = "meaning-submissions", int lifetimeSeconds = 60, string subject = Subject, string kid = KeyId)
    {
        var issued = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var token = new JwtSecurityToken(Issuer, Audience,
            [new Claim("sub", subject), new Claim("scope", scope),
                new Claim("iat", issued.ToString(), ClaimValueTypes.Integer64)],
            DateTimeOffset.FromUnixTimeSeconds(issued).UtcDateTime,
            DateTimeOffset.FromUnixTimeSeconds(issued + lifetimeSeconds).UtcDateTime,
            new SigningCredentials(new RsaSecurityKey(_key) { KeyId = kid }, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task<HttpResponseMessage> Post(string path, object body, string? bearer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (bearer != null) request.Headers.Authorization = new("Bearer", bearer);
        return await _client.SendAsync(request, Ct);
    }

    private static async Task<JsonElement> Data(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return json.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task IntegrationRoutes_RequireAValidServiceTokenWithTheScope()
    {
        var body = new { word = "serendipity", meaning = "意外发现" };
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post("/integration/meaning-submissions", body, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Post("/integration/meaning-submissions", body, _factory.CreateToken("admin"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Post("/integration/meaning-submissions", body, Token(lifetimeSeconds: 120))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Post("/integration/meaning-submissions", body, Token(subject: "someone.else"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Post("/integration/meaning-submissions", body, Token(kid: "unknown"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Post("/integration/meaning-submissions", body, Token(scope: "other"))).StatusCode);

        using var adminRequest = new HttpRequestMessage(HttpMethod.Get, "/admin/meaning-submissions");
        adminRequest.Headers.Authorization = new("Bearer", Token());
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(adminRequest, Ct)).StatusCode);

        var unknown = await Post("/integration/unknown", body, Token());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task SubmitReviewAndLookup_RoundTrip()
    {
        var created = await Data(await Post("/integration/meaning-submissions",
            new { word = "serendipity", partOfSpeech = "n.", meaning = "意外发现" }, Token()));
        var id = created.GetProperty("id").GetString()!;
        Assert.Equal("pending", created.GetProperty("status").GetString());

        var question = await Data(await Post($"/integration/meaning-submissions/{id}/question",
            new { chineseToEnglish = false }, Token()));
        Assert.Equal("serendipity", question.GetProperty("word").GetString());
        Assert.Equal(4, question.GetProperty("options").GetArrayLength());

        using var admin = new HttpRequestMessage(HttpMethod.Get, "/admin/meaning-submissions?status=pending");
        admin.Headers.Authorization = new("Bearer", _factory.CreateToken("admin"));
        var page = await Data(await _client.SendAsync(admin, Ct));
        Assert.True(page.GetProperty("configured").GetBoolean());
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal(Subject, item.GetProperty("source").GetString());

        var approved = await Data(await Post($"/admin/meaning-submissions/{id}/approve",
            new { word = "serendipity", partOfSpeech = "n.", meaning = "意外发现" }, _factory.CreateToken("admin")));
        Assert.Equal("approved", approved.GetProperty("status").GetString());
        Assert.True(approved.GetProperty("definitionKept").GetBoolean());

        var lookup = await Data(await Post("/integration/meaning-submissions/lookup", new { ids = new[] { id, "missing" } }, Token()));
        var found = Assert.Single(lookup.GetProperty("items").EnumerateArray());
        Assert.Equal("supplementary", found.GetProperty("resultBookId").GetString());

        using var search = new HttpRequestMessage(HttpMethod.Get, "/integration/vocabulary/search?keyword=Seren");
        search.Headers.Authorization = new("Bearer", Token());
        var matches = await Data(await _client.SendAsync(search, Ct));
        var match = Assert.Single(matches.GetProperty("items").EnumerateArray());
        Assert.Equal("supplementary", match.GetProperty("bookId").GetString());
        Assert.Equal("意外发现", match.GetProperty("meaningKey").GetString());

        var advice = await Post($"/admin/meaning-submissions/{id}/advice", new { }, _factory.CreateToken("admin"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, advice.StatusCode);
    }
}
