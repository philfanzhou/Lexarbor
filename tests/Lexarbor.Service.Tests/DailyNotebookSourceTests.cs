using System.Collections.Concurrent;
using System.Text.Json;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Services;
using Lexarbor.Learning.Service;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.TestHost;

namespace Lexarbor.Service.Tests;

public sealed class DailyNotebookSourceTests : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _hosts = [];
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        foreach (var host in Enumerable.Reverse(_hosts)) await host.DisposeAsync();
    }

    [Fact]
    public async Task NotebookWordInsidePlanScopeKeepsPlanLocationAndSyncsNotebookOnce()
    {
        var catalog = new DailyCatalog();
        var notebook = new NotebookCatalog();
        var (client, root) = await StudentWithPlanAsync(catalog, notebook);

        // The student saves apple from another book; the plan's unit has the same word and meaning.
        await Data(await client.PostAsJsonAsync($"{root}/notebook/items", new { actor = Student, word = "apple", meaningId = "other-apple-m" }, Ct));
        // A pending submission makes every notebook read ask Lexarbor for review results.
        await Data(await client.PostAsJsonAsync($"{root}/notebook/items", new { actor = Student, word = "plum", meaning = "李子" }, Ct));
        notebook.Lookups = 0;

        var next = await NextNewAsync(client, root);
        Assert.Equal("apple", next.GetProperty("question").GetProperty("prompt").GetString());

        // The notebook keeps its priority, but the question uses the plan's book and unit for its distractors.
        var request = Assert.Single(catalog.Requests);
        Assert.Equal(("book", "apple", "apple-m", "one"), (request.BookId, request.WordId, request.MeaningId, request.UnitId));
        Assert.Equal(1, notebook.Lookups);
    }

    [Fact]
    public async Task NotebookLocationStandsInWhenThePlanLocationGivesNoQuestion()
    {
        // The plan's book cannot ask the saved meaning, while another meaning of the same word still works.
        var catalog = new DailyCatalog
        {
            Entries = [Entry("pear", "梨"), Entry("apple", "苹果"), Entry("apple", "苹果树", "apple-tree-m")],
            Unavailable = request => request is { BookId: "book", MeaningId: "apple-m" },
        };
        var (client, root) = await StudentWithPlanAsync(catalog, new NotebookCatalog());
        await Data(await client.PostAsJsonAsync($"{root}/notebook/items", new { actor = Student, word = "apple", meaningId = "other-apple-m" }, Ct));

        await NextNewAsync(client, root);

        Assert.Equal(new[] { ("book", "apple-tree-m"), ("other", "other-apple-m") },
            catalog.Issued.Select(request => (request.BookId, request.MeaningId)).Order().ToArray());
    }

    private static readonly object Student = new { type = "student" };

    private async Task<(HttpClient Client, string Root)> StudentWithPlanAsync(DailyCatalog catalog, NotebookCatalog notebook)
    {
        var factory = new VocabularyWebApplicationFactory();
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ICatalogClient>(catalog);
            services.AddSingleton<IVocabularyNotebookCatalog>(notebook);
        }));
        _hosts.Add(factory);
        _hosts.Add(host);
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", await host.SeedKeyAsync("daily-notebook", catalog: false, learning: true));
        var student = Guid.NewGuid();
        var root = $"/learning/v1/api/vocabulary/students/{student}";
        (await client.PutAsync($"/learning/v1/learners/{student}", null, Ct)).EnsureSuccessStatusCode();
        await Data(await client.PostAsJsonAsync($"{root}/plans",
            new { actor = Student, bookId = "book", unitIds = new[] { "one" }, dailyNewWords = 5 }, Ct));
        return (client, root);
    }

    private static async Task<JsonElement> NextNewAsync(HttpClient client, string root)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        return await Data(await client.PostAsJsonAsync($"{root}/questions/next?localDate={today}&tzOffsetMinutes=0",
            new { mode = "new" }, Ct));
    }

    private static CatalogEntry Entry(string word, string meaning, string? meaningId = null) =>
        new(word, word, word, null, null, meaningId ?? word + "-m", "noun", meaning, meaning, null, [new("one", 1, null, null)]);

    private sealed class DailyCatalog : ICatalogClient
    {
        public List<CatalogEntry> Entries { get; init; } = [Entry("pear", "梨"), Entry("apple", "苹果")];
        public Func<CatalogLearningQuestionRequest, bool> Unavailable { get; init; } = _ => false;
        public ConcurrentQueue<CatalogLearningQuestionRequest> Requests { get; } = new();
        public ConcurrentQueue<CatalogLearningQuestionRequest> Issued { get; } = new();

        public Task<CatalogEntryPage> GetEntriesAsync(string bookId, string? unitId, int page, int size, CancellationToken cancellationToken)
        {
            Assert.Equal("book", bookId);
            return Task.FromResult(new CatalogEntryPage(Entries, 1, Entries.Count, Entries.Select(entry => entry.Word).Distinct().Count()));
        }

        public Task<IReadOnlyList<CatalogUnit>> GetUnitsAsync(string bookId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CatalogUnit>>([new CatalogUnit("one", 1, null, 2, 2)]);

        public Task<CatalogAudio> GetAudioAsync(string wordId, string bookId, string meaningId, string locale, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<VocabGeneratedQuestion?> GetLearningQuestionAsync(CatalogLearningQuestionRequest request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            if (Unavailable(request)) return Task.FromResult<VocabGeneratedQuestion?>(null);
            Issued.Enqueue(request);
            return Task.FromResult<VocabGeneratedQuestion?>(new(VocabQuestionDirection.EnglishToChinese, request.WordId,
                [new("苹果", true), new("梨", false), new("李子", false), new("葡萄", false)]));
        }
    }

    private sealed class NotebookCatalog : IVocabularyNotebookCatalog
    {
        public int Lookups;

        public Task<IReadOnlyList<VocabCatalogMeaning>> SearchAsync(string keyword, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VocabCatalogMeaning>>(keyword == "apple"
                ? [new("other-apple", "apple", "apple", null, null, "other", "Other book", "other-apple-m", "noun", "苹果", "苹果")]
                : []);

        public Task<VocabMeaningSubmission> SubmitAsync(string word, string? partOfSpeech, string meaning, CancellationToken cancellationToken) =>
            Task.FromResult(Pending("plum-submission", word, partOfSpeech, meaning));

        public Task<IReadOnlyList<VocabMeaningSubmission>> LookupAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Lookups);
            return Task.FromResult<IReadOnlyList<VocabMeaningSubmission>>(ids.Select(id => Pending(id, "plum", null, "李子")).ToList());
        }

        private static VocabMeaningSubmission Pending(string id, string word, string? partOfSpeech, string meaning) =>
            new(id, VocabSubmissionStatus.Pending, word, partOfSpeech, meaning, word, meaning, true,
                null, null, null, null, null, null, null, null);
    }

    private static async Task<JsonElement> Data(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("data");
    }
}
