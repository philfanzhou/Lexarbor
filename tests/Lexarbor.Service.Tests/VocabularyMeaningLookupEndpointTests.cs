using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lexarbor.Service.Tests;

public sealed class VocabularyMeaningLookupEndpointTests
{
    private const string Lookup = "/api/vocabulary/meanings/lookup";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LookupReturnsEveryEnabledSenseWithItsOwnPartOfSpeechInInputOrder()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        await client.UseCatalogKeyAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            db.VocabularyBooks.AddRange(
                new VocabularyBookEntity { Id = "core", BookName = "Core", Status = true },
                new VocabularyBookEntity { Id = "supplement", BookName = "Supplement", Status = true },
                new VocabularyBookEntity { Id = "disabled", BookName = "Disabled", Status = false });
            db.EnrichmentDestinations.Add(new() { Id = "expansion", BookId = "supplement" });
            db.Vocabularies.AddRange(Word("cut", "Cut"), Word("cut-legacy", "CUT"), Word("cutting", "cutting"),
                Word("look-after", "look after"), Word("hidden", "hidden"));
            db.VocabularyMeanings.AddRange(
                Meaning("cut-noun", "cut", "core", "  CUT É  ", "n."),
                Meaning("cut-verb", "cut", "core", "  CUT É  ", "v."),
                Meaning("cut-extra", "cut-legacy", "supplement", "Remove", "vt."),
                Meaning("cut-hidden", "cut", "disabled", "Hidden", "adj."),
                Meaning("prefix", "cutting", "core", "Sharp", "adj."),
                Meaning("phrase", "look-after", "core", "Take care of"),
                Meaning("phrase-extra", "look-after", "supplement", "Watch over", ""),
                Meaning("hidden-m", "hidden", "disabled", "Unseen", "adj."));
            await db.SaveChangesAsync(Ct);
        }

        var words = new[] { "  CuT  ", "missing", "LOOK after ", "cut", "  CuT  ", "hidden" };
        var items = await ReadItems(await client.PostAsJsonAsync(Lookup, new { words }, Ct));
        Assert.Equal(words, items.Select(item => item.Word));
        Assert.Collection(items[0].Meanings,
            noun => Assert.Equal(new("cut", "Cut", "cut", "core", "Core", "cut-noun", "n.", "  CUT É  ", "cut É"), noun),
            verb => Assert.Equal(new("cut", "Cut", "cut", "core", "Core", "cut-verb", "v.", "  CUT É  ", "cut É"), verb),
            extra => Assert.Equal(new("cut-legacy", "CUT", "cut", "supplement", "Supplement", "cut-extra", "vt.", "Remove", "remove"), extra));
        Assert.Empty(items[1].Meanings);
        Assert.Collection(items[2].Meanings,
            phrase => Assert.Equal(new("look-after", "look after", "look after", "core", "Core", "phrase", null, "Take care of", "take care of"), phrase),
            extra => Assert.Equal(new("look-after", "look after", "look after", "supplement", "Supplement", "phrase-extra", null, "Watch over", "watch over"), extra));
        Assert.Equal(items[0].Meanings, items[3].Meanings);
        Assert.Equal(items[0].Meanings, items[4].Meanings);
        Assert.Empty(items[5].Meanings);
    }

    [Fact]
    public async Task LookupUsesOneReadForTheWholeBatchAndUsesTheBusinessFailureResponse()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        var probe = new LookupProbe();
        await using var original = factory.Services.GetRequiredService<IDbContextFactory<VocabularyDbContext>>().CreateDbContext();
        var connectionString = original.Database.GetConnectionString();
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<VocabularyDbContext>();
            services.AddScoped(_ => new VocabularyDbContext(new DbContextOptionsBuilder<VocabularyDbContext>()
                .UseNpgsql(connectionString).AddInterceptors(probe).Options));
        }));
        using var client = configured.CreateClient();
        await client.UseCatalogKeyAsync(configured);

        var words = Enumerable.Range(0, 99).Select(index => $"missing-{index}").Append("missing-0").ToArray();
        var items = await ReadItems(await client.PostAsJsonAsync(Lookup, new { words }, Ct));
        Assert.Equal(100, items.Length);
        Assert.StartsWith("SELECT", Assert.Single(probe.Commands).TrimStart(), StringComparison.OrdinalIgnoreCase);

        probe.Fail = true;
        using var response = await client.PostAsJsonAsync(Lookup, new { words }, Ct);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task LookupRequiresCatalogScopeBeforeValidatingBody()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        await Expect(await client.PostAsync(Lookup, Json("{"), Ct), HttpStatusCode.Unauthorized);
        client.DefaultRequestHeaders.Add(BusinessApiAuthorization.Header, "invalid");
        await Expect(await client.PostAsync(Lookup, Json("{"), Ct), HttpStatusCode.Unauthorized);
        client.DefaultRequestHeaders.Remove(BusinessApiAuthorization.Header);
        client.DefaultRequestHeaders.Add(BusinessApiAuthorization.Header, await factory.SeedKeyAsync(catalog: false, meaningSubmissions: true));
        await Expect(await client.PostAsync(Lookup, Json("{"), Ct), HttpStatusCode.Forbidden);
        client.DefaultRequestHeaders.Remove(BusinessApiAuthorization.Header);
        await client.UseCatalogKeyAsync(factory);

        foreach (var body in new[] { "{", "{}", "{\"words\":[]}", "{\"words\":[\"bank\",\" \\t\"]}", "{\"words\":[null]}", "{\"words\":[42]}" })
            await Expect(await client.PostAsync(Lookup, Json(body), Ct), HttpStatusCode.BadRequest);
        await Expect(await client.PostAsJsonAsync(Lookup, new { words = Enumerable.Repeat("bank", 101) }, Ct), HttpStatusCode.BadRequest);

        using var scope = factory.Services.CreateScope();
        var keys = scope.ServiceProvider.GetRequiredService<BusinessApiKeyRepository>();
        var catalog = Assert.Single(await keys.ListAsync(Ct), key => key.Catalog);
        Assert.True(await keys.RevokeAsync(catalog.Id, 2, Ct));
        await Expect(await client.PostAsJsonAsync(Lookup, new { words = new[] { "bank" } }, Ct), HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LookupEnforcesBodyLimitWithOrWithoutContentLength(bool kestrel)
    {
        await using var factory = new VocabularyWebApplicationFactory();
        if (kestrel)
        {
            factory.UseKestrel(0);
            factory.StartServer();
        }
        using var client = ((WebApplicationFactory<Program>)factory).CreateClient();
        await client.UseCatalogKeyAsync(factory);
        var limit = (int)VocabularyEndpoints.MaxBatchRequestBytes;
        Assert.Single(await ReadItems(await client.PostAsync(Lookup, Json("{\"words\":[\"missing\"]}".PadRight(limit)), Ct)));
        await Expect(await client.PostAsync(Lookup, Json(new string(' ', limit + 1)), Ct), HttpStatusCode.RequestEntityTooLarge);
        using var chunked = new HttpRequestMessage(HttpMethod.Post, Lookup)
        {
            Content = new StreamContent(new ChunkedBody(limit + 1))
        };
        chunked.Content.Headers.ContentType = new("application/json");
        chunked.Headers.TransferEncodingChunked = true;
        await Expect(await client.SendAsync(chunked, Ct), HttpStatusCode.RequestEntityTooLarge);
    }

    private static VocabularyEntity Word(string id, string spelling) => new() { Id = id, Word = spelling };

    private static VocabularyMeaningEntity Meaning(string id, string word, string book, string text, string? partOfSpeech = null) =>
        new() { Id = id, VocabularyId = word, BookId = book, Meaning = text, PartOfSpeech = partOfSpeech };

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");

    private static async Task<VocabularyMeaningLookup[]> ReadItems(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.True(json.RootElement.GetProperty("success").GetBoolean());
            var items = json.RootElement.GetProperty("data").GetProperty("items");
            Assert.All(items.EnumerateArray().SelectMany(item => item.GetProperty("meanings").EnumerateArray()),
                meaning => Assert.True(meaning.TryGetProperty("partOfSpeech", out _)));
            return Assert.IsType<VocabularyMeaningLookup[]>(items.Deserialize<VocabularyMeaningLookup[]>(JsonSerializerOptions.Web));
        }
    }

    private static async Task Expect(HttpResponseMessage response, HttpStatusCode status)
    {
        using (response)
        {
            Assert.Equal(status, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        }
    }

    private sealed class LookupProbe : DbCommandInterceptor
    {
        public bool Fail { get; set; }
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("vocabulary_meaning AS", StringComparison.Ordinal))
            {
                Commands.Add(command.CommandText);
                if (Fail) throw new InvalidOperationException("Synthetic lookup failure.");
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class ChunkedBody(int bytes) : MemoryStream(Enumerable.Repeat((byte)' ', bytes).ToArray())
    {
        public override bool CanSeek => false;
    }
}
