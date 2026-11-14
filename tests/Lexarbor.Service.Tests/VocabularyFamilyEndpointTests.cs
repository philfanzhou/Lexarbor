using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lexarbor.Service.Tests;

public sealed class VocabularyFamilyEndpointTests
{
    private const string Lookup = "/api/vocabulary/relations/lookup";
    private const string Single = "/api/vocabulary/cut/relations";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SingleAndBatchAgreeOnOrderingDirectionAndEnabledMeanings()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        await client.UseCatalogKeyAsync(factory);
        await Seed(factory.Services);

        var single = await Data(await client.GetAsync(Single, Ct));
        Assert.Equal(new[] { "self", "incoming", "derived", "empty" },
            single.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("relationId").GetString()));
        var rows = single.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal("outgoing", rows[0].GetProperty("direction").GetString());
        Assert.Equal("incoming", rows[1].GetProperty("direction").GetString());
        Assert.Equal("saw", rows[1].GetProperty("word").GetProperty("wordId").GetString());
        Assert.Equal("cut", rows[0].GetProperty("word").GetProperty("normalizedWord").GetString());
        Assert.Equal("generated", rows[2].GetProperty("originKind").GetString());
        Assert.False(rows[2].TryGetProperty("sourceMeaningId", out _));
        Assert.False(rows[2].TryGetProperty("targetMeaningId", out _));
        var meaning = Assert.Single(rows[2].GetProperty("meanings").EnumerateArray());
        Assert.Equal("fruit É", meaning.GetProperty("meaningKey").GetString());
        Assert.Equal("yes", meaning.GetProperty("bookId").GetString());
        Assert.Empty(rows[3].GetProperty("meanings").EnumerateArray());
        var incoming = await Data(await client.GetAsync("/api/vocabulary/cutter/relations", Ct));
        var reverse = Assert.Single(incoming.GetProperty("items").EnumerateArray());
        Assert.Equal("incoming", reverse.GetProperty("direction").GetString());
        Assert.Equal("cut-meaning", Assert.Single(reverse.GetProperty("meanings").EnumerateArray()).GetProperty("meaningId").GetString());

        var batch = await Data(await client.PostAsJsonAsync(Lookup, new { wordIds = new[] { "cut", "missing", "saw", "cut", "isolate" } }, Ct));
        var results = batch.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(new[] { "cut", "missing", "saw", "cut", "isolate" }, results.Select(row => row.GetProperty("wordId").GetString()));
        Assert.True(JsonElement.DeepEquals(single.GetProperty("items"), results[0].GetProperty("items")));
        Assert.True(JsonElement.DeepEquals(results[0], results[3]));
        Assert.False(results[1].GetProperty("found").GetBoolean());
        Assert.Empty(results[1].GetProperty("items").EnumerateArray());
        Assert.True(results[4].GetProperty("found").GetBoolean());
        Assert.Empty(results[4].GetProperty("items").EnumerateArray());
        await Expect(await client.GetAsync("/api/vocabulary/missing/relations", Ct), HttpStatusCode.NotFound);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        await db.VocabularyBooks.Where(book => book.Id == "yes").ExecuteUpdateAsync(set => set.SetProperty(book => book.Status, false), Ct);
        var disabled = await Data(await client.GetAsync(Single, Ct));
        rows = disabled.GetProperty("items").EnumerateArray().ToArray();
        Assert.Empty(rows[2].GetProperty("meanings").EnumerateArray());
        await Expect(await client.GetAsync(Single + "?meaning=cut%20something", Ct), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task LookupBySpellingAndCurrentMeaningsIncludesManuallyConfirmedRelations()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new("Bearer", factory.CreateToken("admin"));
        await client.UseCatalogKeyAsync(factory);
        await Seed(factory.Services);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            db.Vocabularies.Add(Word("cut-noun", "CUT"));
            db.VocabularyMeanings.AddRange(Meaning("cut-noun-m", "cut-noun", "yes", "Incision", "n."),
                Meaning("cut-handbook", "cut", "yes", "Opening", "n."),
                Meaning("cut-hidden", "cut", "no", "Hidden sense", "adj."));
            await db.SaveChangesAsync(Ct);
        }
        var manual = await Data(await admin.PostAsJsonAsync("/admin/vocabulary-relations", new
        {
            sourceWordId = "cut",
            targetWordId = "cutter",
            type = "derived_adjective",
            confirm = true
        }, Ct));

        var input = """
            {"items":[
              {"word":"  CuT  ","meaning":" CUT SOMETHING "},
              {"word":"CUT","partOfSpeech":" N. "},
              {"word":"cut","meaning":"Incision","partOfSpeech":"n."},
              {"word":"cut","meaning":"Cut something","partOfSpeech":"n."},
              {"wordId":"cut","meaning":"Hidden sense"},
              {"word":"cutter","meaning":" FRUIT É "},
              {"word":"cutter","meaning":"fruit é"}
            ]}
            """;
        var batch = (await Data(await client.PostAsync(Lookup, Json(input), Ct))).GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(new[] { "cut", "cut", "cut-noun", null, "cut", "cutter", null },
            batch.Select(result => result.GetProperty("wordId").GetString()));
        Assert.Equal(new[] { true, true, true, false, false, true, false }, batch.Select(result => result.GetProperty("found").GetBoolean()));
        var manualId = manual.GetProperty("id").GetString();
        var actual = Assert.Single(batch[0].GetProperty("items").EnumerateArray(), row => row.GetProperty("relationId").GetString() == manualId);
        Assert.Equal("manual", actual.GetProperty("originKind").GetString());
        var definition = Assert.Single(actual.GetProperty("meanings").EnumerateArray());
        Assert.Equal("n.", definition.GetProperty("partOfSpeech").GetString());
        Assert.Equal("  FRUIT É  ", definition.GetProperty("meaning").GetString());

        var single = await Data(await client.GetAsync(Single + "?meaning=cut%20something&partOfSpeech=v.", Ct));
        Assert.True(JsonElement.DeepEquals(single.GetProperty("items"), batch[0].GetProperty("items")));
        await Expect(await client.GetAsync(Single + "?meaning=cut%20something&partOfSpeech=n.", Ct), HttpStatusCode.NotFound);

        using var updateScope = factory.Services.CreateScope();
        var catalog = updateScope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        await catalog.VocabularyMeanings.Where(meaning => meaning.Id == "target").ExecuteUpdateAsync(change => change
            .SetProperty(meaning => meaning.Meaning, "Revised meaning")
            .SetProperty(meaning => meaning.PartOfSpeech, "adj."), Ct);
        var updated = await Data(await client.GetAsync(Single + "?partOfSpeech=v.", Ct));
        var updatedRelation = Assert.Single(updated.GetProperty("items").EnumerateArray(), row => row.GetProperty("relationId").GetString() == manualId);
        var updatedDefinition = Assert.Single(updatedRelation.GetProperty("meanings").EnumerateArray());
        Assert.Equal("Revised meaning", updatedDefinition.GetProperty("meaning").GetString());
        Assert.Equal("adj.", updatedDefinition.GetProperty("partOfSpeech").GetString());
    }

    [Fact]
    public async Task LookupUsesBoundedReadOnlyQueriesAndPropagatesCancellationAndStorageFaults()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        var probe = new QueryProbe();
        await using var original = factory.Services.GetRequiredService<IDbContextFactory<VocabularyDbContext>>().CreateDbContext();
        var connectionString = original.Database.GetConnectionString();
        await using var configured = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<VocabularyDbContext>();
            services.AddScoped(_ => new VocabularyDbContext(new DbContextOptionsBuilder<VocabularyDbContext>()
                .UseSqlite(connectionString).AddInterceptors(probe).Options));
        }));
        using var client = configured.CreateClient();
        await Seed(configured.Services);
        probe.Record = true;
        await Expect(await client.GetAsync(Single, Ct), HttpStatusCode.Unauthorized);
        await Expect(await client.PostAsJsonAsync(Lookup, new { wordIds = new[] { "cut" } }, Ct), HttpStatusCode.Unauthorized);
        Assert.Empty(probe.Commands);
        probe.Record = false;
        await client.UseCatalogKeyAsync(configured);
        probe.Record = true;
        await Data(await client.PostAsJsonAsync(Lookup, new { wordIds = new[] { "cut" } }, Ct));
        var baseline = probe.Commands.ToArray();
        Assert.InRange(baseline.Length, 3, 7);
        Assert.All(baseline, sql => Assert.StartsWith("SELECT", sql.TrimStart(), StringComparison.OrdinalIgnoreCase));
        probe.Commands.Clear();
        var ids = Enumerable.Range(0, 98).Select(index => $"absent-{index}").Append("cut").Append("saw").ToArray();
        var batch = await Data(await client.PostAsJsonAsync(Lookup, new { wordIds = ids }, Ct));
        Assert.Equal(100, batch.GetProperty("items").GetArrayLength());
        Assert.Equal(baseline.Length, probe.Commands.Count);
        probe.Commands.Clear();
        var entries = Enumerable.Range(0, 100).Select(index => new { word = index == 0 ? "CUT" : $"missing-{index}", partOfSpeech = "v." });
        await Data(await client.PostAsJsonAsync(Lookup, new { items = entries }, Ct));
        Assert.Equal(baseline.Length + 1, probe.Commands.Count);
        Assert.All(probe.Commands, sql => Assert.StartsWith("SELECT", sql.TrimStart(), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(probe.Commands, sql => sql.Contains("enrichment_", StringComparison.OrdinalIgnoreCase));
        probe.Fail = true;
        using var unavailable = await client.GetAsync(Single, Ct);
        Assert.Equal(HttpStatusCode.InternalServerError, unavailable.StatusCode);
        Assert.Equal("application/problem+json", unavailable.Content.Headers.ContentType?.MediaType);
        probe.Fail = false;
        probe.Pause = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pending = client.GetAsync(Single, cancellation.Token);
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await probe.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task AuthorizationPrecedesValidationAndSizeLimits()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        await Expect(await client.GetAsync(Single, Ct), HttpStatusCode.Unauthorized);
        await Expect(await client.PostAsync(Lookup, Json("{"), Ct), HttpStatusCode.Unauthorized);
        client.DefaultRequestHeaders.Add(BusinessApiAuthorization.Header, "invalid");
        await Expect(await client.GetAsync(Single, Ct), HttpStatusCode.Unauthorized);
        client.DefaultRequestHeaders.Remove(BusinessApiAuthorization.Header);
        client.DefaultRequestHeaders.Add(BusinessApiAuthorization.Header, await factory.SeedKeyAsync(catalog: false, meaningSubmissions: true));
        await Expect(await client.GetAsync(Single, Ct), HttpStatusCode.Forbidden);
        await Expect(await client.PostAsync(Lookup, Json("{"), Ct), HttpStatusCode.Forbidden);
        client.DefaultRequestHeaders.Remove(BusinessApiAuthorization.Header);
        await client.UseCatalogKeyAsync(factory);
        foreach (var input in new[] { "{", "{}", "{\"wordIds\":[]}", "{\"wordIds\":[\"\"]}", "{\"wordIds\":[null]}", "{\"wordIds\":[\" \t\"]}" })
            await Expect(await client.PostAsync(Lookup, Json(input), Ct), HttpStatusCode.BadRequest);
        foreach (var input in new[] { "{\"items\":[null]}", "{\"items\":[{\"partOfSpeech\":\"v.\"}]}",
                     "{\"items\":[{\"word\":\"cut\",\"wordId\":\"cut\"}]}", "{\"items\":[{\"word\":\"cut\",\"meaning\":\" \"}]}",
                     "{\"wordIds\":[\"cut\"],\"items\":[{\"word\":\"cut\"}]}" })
            await Expect(await client.PostAsync(Lookup, Json(input), Ct), HttpStatusCode.BadRequest);
        await Expect(await client.GetAsync(Single + "?partOfSpeech=%20", Ct), HttpStatusCode.BadRequest);
        await Expect(await client.PostAsJsonAsync(Lookup, new { wordIds = Enumerable.Repeat("cut", 101) }, Ct), HttpStatusCode.BadRequest);
        await Expect(await client.PostAsync(Lookup, Json(new string(' ', (int)VocabularyHttpEndpoints.MaxBatchRequestBytes + 1)), Ct), HttpStatusCode.RequestEntityTooLarge);
        using var chunked = new HttpRequestMessage(HttpMethod.Post, Lookup)
        {
            Content = new StreamContent(new ChunkedBody((int)VocabularyHttpEndpoints.MaxBatchRequestBytes + 1))
        };
        chunked.Content.Headers.ContentType = new("application/json");
        await Expect(await client.SendAsync(chunked, Ct), HttpStatusCode.RequestEntityTooLarge);
        using var scope = factory.Services.CreateScope();
        var keys = scope.ServiceProvider.GetRequiredService<BusinessApiKeyRepository>();
        var catalog = Assert.Single(await keys.ListAsync(Ct), key => key.Catalog);
        Assert.True(await keys.RevokeAsync(catalog.Id, 2, Ct));
        await Expect(await client.GetAsync(Single, Ct), HttpStatusCode.Unauthorized);
        await Expect(await client.PostAsync(Lookup, Json("{"), Ct), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task KestrelEnforcesTheSameSizeLimitAfterAuthentication()
    {
        await using var factory = new VocabularyWebApplicationFactory();
        factory.UseKestrel(0);
        factory.StartServer();
        using var client = ((WebApplicationFactory<Program>)factory).CreateClient();
        var limit = (int)VocabularyHttpEndpoints.MaxBatchRequestBytes;
        await Expect(await client.PostAsync(Lookup, Json(new string(' ', limit + 1)), Ct), HttpStatusCode.Unauthorized);
        await client.UseCatalogKeyAsync(factory);
        await Data(await client.PostAsync(Lookup, Json("{\"wordIds\":[\"missing\"]}".PadRight(limit)), Ct));
        using var chunked = new HttpRequestMessage(HttpMethod.Post, Lookup)
        {
            Content = new StreamContent(new ChunkedBody(limit + 1))
        };
        chunked.Content.Headers.ContentType = new("application/json");
        chunked.Headers.TransferEncodingChunked = true;
        await Expect(await client.SendAsync(chunked, Ct), HttpStatusCode.RequestEntityTooLarge);
    }

    [Theory]
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]
    [InlineData("configured", true, false)]
    [InlineData("configured", false, true)]
    public async Task QuestionEntrypointsTopUpOnlyTheActiveSupplement(string? configured, bool managed, bool succeeds)
    {
        await using var factory = new VocabularyWebApplicationFactory("Testing", true, extraConfiguration:
            new Dictionary<string, string?> { ["MeaningSubmissions:BookId"] = configured });
        using var client = factory.CreateClient();
        await client.UseCatalogKeyAsync(factory);
        var targetBook = managed ? "managed" : configured ?? "same-name";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            db.VocabularyBooks.AddRange(Book("managed", "Renamed supplement"), Book("configured", "Configured"),
                Book("same-name", "AI Vocabulary Supplement"), Book("other", "Other"), Book("disabled", "Disabled", false));
            db.EnrichmentDestinations.Add(new() { Id = "expansion", BookId = "managed" });
            db.Vocabularies.Add(Word("q", "Question"));
            db.VocabularyMeanings.Add(Meaning("definition", "q", targetBook, "the question"));
            for (var i = 0; i < 4; i++)
            {
                db.Vocabularies.Add(Word($"w{i}", $"choice-{i}"));
                db.VocabularyMeanings.Add(Meaning($"m{i}", $"w{i}", i == 3 ? "disabled" : "other", $"definition-{i}"));
            }
            await db.SaveChangesAsync(Ct);
        }

        foreach (var chineseToEnglish in new[] { true, false })
        {
            var item = new { wordId = "q", bookId = targetBook, meaningId = "definition", chineseToEnglish };
            using var single = await client.PostAsJsonAsync("/api/vocabulary/question", item, Ct);
            Assert.Equal(succeeds ? HttpStatusCode.OK : HttpStatusCode.UnprocessableEntity, single.StatusCode);
            var batch = await Data(await client.PostAsJsonAsync("/api/vocabulary/questions", new { items = new[] { item } }, Ct));
            var result = Assert.Single(batch.GetProperty("results").EnumerateArray());
            if (succeeds) Assert.Equal(4, result.GetProperty("question").GetProperty("options").GetArrayLength());
            else Assert.Equal(422, result.GetProperty("error").GetProperty("status").GetInt32());
        }
        using var learning = await client.PostAsJsonAsync("/api/vocabulary/learning-question", new { wordId = "q", bookId = targetBook, meaningId = "definition", mode = "new" }, Ct);
        Assert.Equal(succeeds ? HttpStatusCode.OK : HttpStatusCode.UnprocessableEntity, learning.StatusCode);
        if (succeeds)
        {
            var options = (await Data(learning)).GetProperty("options").EnumerateArray().ToArray();
            Assert.Equal(4, options.Length);
            Assert.DoesNotContain(options, option => option.GetProperty("text").GetString() == "definition-3");
        }
    }

    private static async Task Seed(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        db.VocabularyBooks.AddRange(Book("yes", "Enabled"), Book("no", "Disabled", false));
        db.Vocabularies.AddRange(Word("cut", "Cut"), Word("saw", "Saw"), Word("cutter", "Cutter"),
            Word("z", "Z"), Word("isolate", "Isolate"));
        db.VocabularyMeanings.AddRange(Meaning("cut-meaning", "cut", "yes", "Cut something", "v."),
            Meaning("target", "cutter", "yes", "  FRUIT É  ", "n."), Meaning("target-hidden", "cutter", "no", "Hidden"),
            Meaning("z-hidden", "z", "no", "Hidden"));
        db.VocabularyRelations.AddRange(Relation("self", "cut", "cut", "past_tense", "manual"),
            Relation("incoming", "saw", "cut", "past_tense", "import"), Relation("derived", "cut", "cutter", "derived_noun", "generated", "task"),
            Relation("empty", "cut", "z", "derived_noun", "manual"), Relation("pending", "cut", "saw", "plural", "manual", confirmed: false));
        await db.SaveChangesAsync(Ct);
    }

    private static VocabularyBookEntity Book(string id, string name, bool enabled = true) => new() { Id = id, BookName = name, Status = enabled };
    private static VocabularyEntity Word(string id, string spelling) => new() { Id = id, Word = spelling };
    private static VocabularyMeaningEntity Meaning(string id, string word, string book, string text, string? partOfSpeech = null) =>
        new() { Id = id, VocabularyId = word, BookId = book, Meaning = text, PartOfSpeech = partOfSpeech };
    private static VocabularyRelationEntity Relation(string id, string source, string target, string type, string origin, string? task = null, bool confirmed = true) => new()
    {
        Id = id,
        SourceWordId = source,
        TargetWordId = target,
        Type = type,
        OriginKind = origin,
        OriginReference = task,
        Status = confirmed ? "confirmed" : "needs_review",
        ReviewReason = confirmed ? null : "new_relation",
        LastConfirmedAtUnixMs = confirmed ? 0 : null
    };

    private static StringContent Json(string value) => new(value, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> Data(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.True(json.RootElement.GetProperty("success").GetBoolean());
            return json.RootElement.GetProperty("data").Clone();
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

    private sealed class QueryProbe : DbCommandInterceptor
    {
        public bool Record;
        public bool Fail;
        public bool Pause;
        public List<string> Commands { get; } = [];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Record) Commands.Add(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Record) Commands.Add(command.CommandText);
            if (command.CommandText.Contains("vocabulary_relation", StringComparison.Ordinal))
            {
                if (Fail) throw new InvalidOperationException("Synthetic query failure.");
                if (Pause)
                {
                    Entered.TrySetResult();
                    try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                    finally { Cancelled.TrySetResult(); }
                }
            }
            return result;
        }
    }

    private sealed class ChunkedBody(int bytes) : MemoryStream(Enumerable.Repeat((byte)' ', bytes).ToArray())
    {
        public override bool CanSeek => false;
    }
}
