using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Services;
using Lexarbor.Learning.Service;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Lexarbor.Service.Tests;

public sealed class DailyNotebookSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NotebookWordInsidePlanScopeKeepsPlanLocationAndSyncsNotebookOnce()
    {
        var catalog = new DailyCatalog();
        var notebook = new NotebookCatalog();
        await using var factory = new VocabularyWebApplicationFactory();
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ILexarborClient>(catalog);
            services.AddSingleton<IVocabularyNotebookCatalog>(notebook);
        }));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", await host.SeedKeyAsync("daily-notebook", catalog: false, learning: true));
        var student = Guid.NewGuid();
        var root = $"/learning/v1/api/vocabulary/students/{student}";
        var actor = new { type = "student" };
        (await client.PutAsync($"/learning/v1/learners/{student}", null, Ct)).EnsureSuccessStatusCode();
        await Data(await client.PostAsJsonAsync($"{root}/plans",
            new { actor, bookId = "book", unitIds = new[] { "one" }, dailyNewWords = 5 }, Ct));

        // The student saves apple from another book; the plan's unit has the same word and meaning.
        await Data(await client.PostAsJsonAsync($"{root}/notebook/items", new { actor, word = "apple", meaningId = "other-apple-m" }, Ct));
        // A pending submission makes every notebook read ask Lexarbor for review results.
        await Data(await client.PostAsJsonAsync($"{root}/notebook/items", new { actor, word = "plum", meaning = "李子" }, Ct));
        notebook.Lookups = 0;

        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");
        var next = await Data(await client.PostAsJsonAsync($"{root}/questions/next?localDate={today}&tzOffsetMinutes=0",
            new { mode = "new" }, Ct));
        Assert.Equal("apple", next.GetProperty("question").GetProperty("prompt").GetString());

        // The notebook keeps its priority, but the question uses the plan's book and unit for its distractors.
        var request = Assert.Single(catalog.Requests);
        Assert.Equal(("book", "apple", "apple-m", "one"), (request.BookId, request.WordId, request.MeaningId, request.UnitId));
        Assert.Equal(1, notebook.Lookups);
    }

    private sealed class DailyCatalog : ILexarborClient
    {
        public ConcurrentQueue<LexarborLearningQuestionRequest> Requests { get; } = new();

        public Task<LexarborEntryPage> GetEntriesAsync(string bookId, string? unitId, int page, int size, CancellationToken cancellationToken)
        {
            Assert.Equal("book", bookId);
            List<LexarborEntry> entries = [Entry("pear", "梨"), Entry("apple", "苹果")];
            return Task.FromResult(new LexarborEntryPage(entries, 1, entries.Count, entries.Count));
        }

        public Task<IReadOnlyList<LexarborUnit>> GetUnitsAsync(string bookId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LexarborUnit>>([new LexarborUnit("one", 1, null, 2, 2)]);

        public Task<LexarborAudio> GetAudioAsync(string wordId, string bookId, string meaningId, string locale, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<VocabGeneratedQuestion?> GetLearningQuestionAsync(LexarborLearningQuestionRequest request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            return Task.FromResult<VocabGeneratedQuestion?>(new(VocabQuestionDirection.EnglishToChinese, request.WordId,
                [new("苹果", true), new("梨", false), new("李子", false), new("葡萄", false)]));
        }

        private static LexarborEntry Entry(string word, string meaning) =>
            new(word, word, word, null, null, word + "-m", "noun", meaning, meaning, null, [new("one", 1, null, null)]);
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
