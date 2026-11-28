using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Service.Tests;

/// <summary>
/// Pinned end to end against the real host: a full 500-entry batch import
/// runs in one write transaction, and the catalog reads that arrive while it
/// runs succeed. Under SQLite's rollback journal they queued behind the
/// import; PostgreSQL serves them from their own snapshots beside it.
/// </summary>
public class BatchImportConcurrentReadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Entry(int index) => new
    {
        word = $"rollback-concurrency-{index:D3}",
        meaning = $"meaning {index}"
    };

    [Fact]
    public async Task FullBatchImport_WithConcurrentCatalogReads_ReadsSucceed()
    {
        await using var factory = new VocabularyWebApplicationFactory(
            "Testing",
            includeAppCredentials: true);
        using var catalog = factory.CreateClient();
        await catalog.UseCatalogKeyAsync(factory);
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", factory.CreateToken("admin"));

        // One readable word in one enabled book, seeded directly so the
        // concurrent reads have a stable target that the batch never
        // touches.
        const string bookId = "rollback-reads";
        const string wordId = "stable-word";
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            var now = DateTimeOffset.UtcNow;
            context.VocabularyBooks.Add(new VocabularyBookEntity
            {
                Id = bookId,
                BookName = "Rollback Reads",
                Status = true,
                CreatedAt = now,
                UpdatedAt = now
            });
            context.Vocabularies.Add(new VocabularyEntity
            {
                Id = wordId,
                Word = "stable",
                CreatedAt = now,
                UpdatedAt = now
            });
            context.VocabularyMeanings.Add(new VocabularyMeaningEntity
            {
                Id = "stable-meaning",
                VocabularyId = wordId,
                BookId = bookId,
                Meaning = "the read target",
                CreatedAt = now,
                UpdatedAt = now
            });
            await context.SaveChangesAsync(Ct);
        }

        // The maximum-size batch — 500 entries in one transaction — is the
        // longest write the service issues, so it is the window the concurrent reads
        // below are issued in.
        var batch = new
        {
            bookId,
            entries = Enumerable.Range(0, 500).Select(Entry).ToArray()
        };
        var import = await admin.PostAsJsonAsync("/admin/vocabulary/batch", batch, Ct);
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);
        using var imported = JsonDocument.Parse(await import.Content.ReadAsStringAsync(Ct));
        Assert.True(imported.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(500, imported.RootElement.GetProperty("data").GetProperty("total").GetInt32());

        // Reads issued against the seeded word while an import of the same
        // scale runs: each one either slips through beside the open
        // transaction or queues behind its commit, and either way answers
        // 200 with the word — never 5xx and never a timeout failure.
        var import2 = Task.Run(() => admin.PostAsJsonAsync(
            "/admin/vocabulary/batch",
            new
            {
                bookId,
                entries = Enumerable.Range(0, 500).Select(index => new
                {
                    word = $"rollback-second-{index:D3}",
                    meaning = $"meaning {index}"
                }).ToArray()
            },
            Ct));
        var reads = Enumerable.Range(0, 5)
            .Select(_ => catalog.GetAsync($"/api/vocabulary/{wordId}?bookId={bookId}", Ct))
            .ToArray();
        var second = await import2;
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        foreach (var read in reads)
        {
            var response = await read;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            Assert.True(body.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(
                "stable",
                body.RootElement.GetProperty("data").GetProperty("word").GetString());
        }

        await using var scope2 = factory.Services.CreateAsyncScope();
        var context2 = scope2.ServiceProvider.GetRequiredService<VocabularyDbContext>();
        Assert.Equal(
            1001,
            await context2.Vocabularies.CountAsync(TestContext.Current.CancellationToken));
    }
}
