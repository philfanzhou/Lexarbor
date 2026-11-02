using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lexarbor.Service.Tests;

public sealed class LearningQuestionEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Path = "/api/vocabulary/learning-question";

    [Theory]
    [InlineData(0, "new", "EnglishToChinese")]
    [InlineData(0, "review", "EnglishToChinese")]
    [InlineData(1, "review", "ChineseToEnglish")]
    [InlineData(2, "review", "FillLetters")]
    public async Task CatalogModesReturnExclusiveStructures(int first, string mode, string type)
    {
        using var factory = new VocabularyWebApplicationFactory();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<LearningQuestionGenerator>();
            services.AddScoped(_ => new LearningQuestionGenerator(max => first % max));
        }));
        using var client = host.CreateClient();
        await Seed(host.Services);
        var response = await client.PostAsJsonAsync(Path, Request(mode), Ct);
        var data = await Success(response);
        Assert.Equal(type, data.GetProperty("type").GetString());
        AssertStructure(data);
        Assert.Equal(type == "EnglishToChinese" ? "Explode" : "爆炸", data.GetProperty("prompt").GetString());
    }

    [Theory]
    [InlineData(0, "EnglishToChinese")]
    [InlineData(1, "ChineseToEnglish")]
    public async Task ShortCatalogWordHasBothChoiceDirectionsAndNoFill(int pick, string expected)
    {
        using var factory = new VocabularyWebApplicationFactory();
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<LearningQuestionGenerator>();
            services.AddScoped(_ => new LearningQuestionGenerator(max => pick % max));
        }));
        using var client = host.CreateClient();
        await Seed(host.Services);
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            var word = await db.Vocabularies.SingleAsync(x => x.Id == "w", Ct);
            word.Word = "go";
            await db.SaveChangesAsync(Ct);
        }
        var data = await Success(await client.PostAsJsonAsync(Path, Request("review"), Ct));
        Assert.Equal(expected, data.GetProperty("type").GetString());
        AssertStructure(data);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{\"wordId\":\"w\",\"bookId\":\"b\",\"meaningId\":\"m\"}")]
    [InlineData("{\"wordId\":\"w\",\"bookId\":\"b\",\"meaningId\":\"m\",\"mode\":\"Review\"}")]
    [InlineData("{\"wordId\":\"w\",\"bookId\":\"b\",\"meaningId\":\"m\",\"mode\":\"new\",\"sameEntryKind\":\"true\"}")]
    public async Task InvalidBodyHasLocal400Envelope(string json)
    {
        using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        await Error(await client.PostAsync(Path, new StringContent(json, Encoding.UTF8, "application/json"), Ct),
            HttpStatusCode.BadRequest, "QUESTION_INVALID_REQUEST");
        await Error(await client.PostAsync(Path, new StringContent("plain"), Ct), HttpStatusCode.BadRequest, "QUESTION_INVALID_REQUEST");
    }

    [Fact]
    public async Task TargetSourceAndTypeFailuresAreDistinctAndOldAndUnknownRoutesKeepTheirErrors()
    {
        using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        await Seed(factory.Services, distractors: false);
        await Error(await client.PostAsJsonAsync(Path, Request("new"), Ct), HttpStatusCode.UnprocessableEntity, "QUESTION_UNAVAILABLE");
        Assert.Equal("FillLetters", (await Success(await client.PostAsJsonAsync(Path, Request("review"), Ct))).GetProperty("type").GetString());
        foreach (var body in new object[]
        {
            new { wordId = "missing", bookId = "b", meaningId = "m", mode = "review" },
            new { wordId = "w", bookId = "missing", meaningId = "m", mode = "review" },
            new { wordId = "w", bookId = "b", meaningId = "other", mode = "review" },
            new { wordId = "w", bookId = "b", meaningId = "foreign-meaning", mode = "review" },
            new { wordId = "foreign-word", bookId = "b", meaningId = "m", mode = "review" },
            new { wordId = "w", bookId = "other", meaningId = "m", mode = "review" },
            new { wordId = "w", bookId = "b", meaningId = "m", unitId = "missing", mode = "review" },
            new { wordId = "w", bookId = "b", meaningId = "m", unitId = "foreign", mode = "review" }
        }) await Error(await client.PostAsJsonAsync(Path, body, Ct), HttpStatusCode.NotFound, "QUESTION_TARGET_NOT_FOUND");
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            var book = await db.VocabularyBooks.SingleAsync(item => item.Id == "b", Ct);
            book.Status = false;
            await db.SaveChangesAsync(Ct);
        }
        await Error(await client.PostAsJsonAsync(Path, Request("review"), Ct), HttpStatusCode.UnprocessableEntity, "QUESTION_SOURCE_UNAVAILABLE");
        var old = await client.PostAsJsonAsync("/api/vocabulary/question", new { wordId = "w", bookId = "b" }, Ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, old.StatusCode);
        using var oldBody = JsonDocument.Parse(await old.Content.ReadAsStringAsync(Ct));
        Assert.Equal("vocabulary.business_rule", oldBody.RootElement.GetProperty("errorCode").GetString());
        Assert.False(oldBody.RootElement.TryGetProperty("code", out _));
        var unknown = await client.PostAsJsonAsync("/api/vocabulary/learning-question-undeployed", Request("review"), Ct);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.DoesNotContain("QUESTION_TARGET_NOT_FOUND", await unknown.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task ConcurrentRequestsHaveTheirOwnPatternsAndNoDatabaseWrites()
    {
        using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        await Seed(factory.Services, distractors: false);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        var before = await Snapshot(db);
        var responses = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => client.PostAsJsonAsync(Path, Request("review"), Ct)));
        foreach (var response in responses)
        {
            var data = await Success(response);
            AssertStructure(data);
            Assert.Equal("Explode", data.GetProperty("answerWord").GetString());
        }
        Assert.Equal(before, await Snapshot(db));
    }

    [Fact]
    public async Task CatalogScopeAndEntryKindConstrainChoicesWithoutConstrainingFillLetters()
    {
        using var factory = new VocabularyWebApplicationFactory();
        using var client = factory.CreateClient();
        await Seed(factory.Services);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            db.VocabularyBookUnits.Add(new() { Id = "unit", BookId = "b", Number = 1 });
            db.VocabularyMeaningUnits.Add(new() { UnitId = "unit", MeaningId = "m", BookId = "b", EntryKind = "phrase" });
            await db.SaveChangesAsync(Ct);
        }
        await Error(await client.PostAsJsonAsync(Path, new { wordId = "w", bookId = "b", meaningId = "m", mode = "new", unitId = "unit" }, Ct),
            HttpStatusCode.UnprocessableEntity, "QUESTION_UNAVAILABLE");
        await Error(await client.PostAsJsonAsync(Path, new { wordId = "w", bookId = "b", meaningId = "m", mode = "new", sameEntryKind = true }, Ct),
            HttpStatusCode.UnprocessableEntity, "QUESTION_UNAVAILABLE");
        Assert.Equal("FillLetters", (await Success(await client.PostAsJsonAsync(Path,
            new { wordId = "w", bookId = "b", meaningId = "m", mode = "review", unitId = "unit", sameEntryKind = true }, Ct))).GetProperty("type").GetString());
        Assert.Equal("EnglishToChinese", (await Success(await client.PostAsJsonAsync(Path, Request("new"), Ct))).GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HttpStorageFailureAndCancellationDoNotReturnFallbackQuestions(bool cancel)
    {
        using var factory = new VocabularyWebApplicationFactory();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<LearningQuestionGenerator>();
            services.AddScoped(_ => new LearningQuestionGenerator(_ => 0));
            services.AddScoped<IVocabularyMeaningRepository>(provider =>
            {
                var real = new Lexarbor.Database.Repositories.VocabularyMeaningRepository(provider.GetRequiredService<VocabularyDbContext>());
                var proxy = DispatchProxy.Create<IVocabularyMeaningRepository, ReadProbe>();
                ((ReadProbe)(object)proxy).Read = (method, args) =>
                {
                    if (method.Name.StartsWith("GetRandom"))
                    {
                        Interlocked.Increment(ref calls);
                        reached.TrySetResult();
                        Assert.True(((CancellationToken)args![^1]!).CanBeCanceled);
                        return cancel ? AwaitCancellation((CancellationToken)args[^1]!)
                            : Task.FromException<List<VocabularyMeaningModel>>(new InvalidOperationException("simulated database fault"));
                    }
                    return method.Invoke(real, args);
                };
                return proxy;
            });
        }));
        using var client = host.CreateClient();
        await Seed(host.Services);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var task = client.PostAsJsonAsync(Path, Request("review"), cancellation.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        if (cancel)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        }
        else
        {
            var response = await task;
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync(Ct);
            Assert.DoesNotContain("QUESTION_TARGET_NOT_FOUND", text);
            Assert.DoesNotContain("QUESTION_UNAVAILABLE", text);
            Assert.DoesNotContain("simulated database fault", text);
        }
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    private static async Task<List<VocabularyMeaningModel>> AwaitCancellation(CancellationToken token)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        throw new InvalidOperationException("Cancellation was not observed.");
    }

    public class ReadProbe : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Read { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Read(method!, args);
    }

    private static object Request(string mode) => new { wordId = "w", bookId = "b", meaningId = "m", mode };

    private static async Task Seed(IServiceProvider services, bool distractors = true)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        db.VocabularyBooks.AddRange(new() { Id = "b", BookName = "Book", Status = true }, new() { Id = "other", BookName = "Other", Status = true });
        db.VocabularyBookUnits.Add(new() { Id = "foreign", BookId = "other", Number = 1 });
        db.Vocabularies.AddRange(new() { Id = "w", Word = "Explode" }, new() { Id = "foreign-word", Word = "unrelated" });
        db.VocabularyMeanings.Add(new() { Id = "foreign-meaning", VocabularyId = "w", BookId = "other", Meaning = "别书释义" });
        db.VocabularyMeanings.Add(new() { Id = "m", VocabularyId = "w", BookId = "b", Meaning = "爆炸" });
        if (distractors)
            for (var index = 0; index < 3; index++)
            {
                db.Vocabularies.Add(new() { Id = "d" + index, Word = "other" + index });
                db.VocabularyMeanings.Add(new() { Id = "dm" + index, VocabularyId = "d" + index, BookId = "b", Meaning = "其他" + index });
            }
        await db.SaveChangesAsync(Ct);
    }

    internal static void AssertStructure(JsonElement data)
    {
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("prompt").GetString()));
        if (data.GetProperty("type").GetString() == "FillLetters")
        {
            Assert.Equal(new[] { "answerWord", "pattern", "prompt", "type" }, data.EnumerateObject().Select(item => item.Name).Order());
            var scalars = data.GetProperty("answerWord").GetString()!.EnumerateRunes().ToArray();
            var pattern = data.GetProperty("pattern").EnumerateArray().ToArray();
            Assert.Equal(scalars.Length, pattern.Length);
            Assert.Contains(pattern, item => item.ValueKind == JsonValueKind.Null);
            for (var index = 0; index < pattern.Length; index++)
                if (pattern[index].ValueKind == JsonValueKind.Null) Assert.True(Rune.IsLetter(scalars[index]));
                else Assert.Equal(scalars[index].ToString(), pattern[index].GetString());
        }
        else
        {
            Assert.Equal(new[] { "options", "prompt", "type" }, data.EnumerateObject().Select(item => item.Name).Order());
            var options = data.GetProperty("options").EnumerateArray().ToArray();
            Assert.Equal(4, options.Length);
            Assert.Equal(4, options.Select(item => item.GetProperty("text").GetString()).Distinct().Count());
            Assert.All(options, item =>
            {
                Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("text").GetString()));
                Assert.Contains(item.GetProperty("isCorrect").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
            });
            Assert.Single(options, item => item.GetProperty("isCorrect").GetBoolean());
        }
    }

    internal static async Task<JsonElement> Success(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        return json.RootElement.GetProperty("data").Clone();
    }

    internal static async Task Error(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("message").GetString()));
    }

    private static async Task<string> Snapshot(VocabularyDbContext db) => string.Join("|",
        await db.Vocabularies.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Id + ":" + x.Word).ToListAsync(Ct)) + ";" + string.Join("|",
        await db.VocabularyMeanings.AsNoTracking().OrderBy(x => x.Id).Select(x => x.Id + ":" + x.Meaning).ToListAsync(Ct));
}
