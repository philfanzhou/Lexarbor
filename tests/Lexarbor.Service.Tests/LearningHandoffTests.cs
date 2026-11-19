using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lexarbor.Learning.Database;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lexarbor.Service.Tests;

public sealed class LearningHandoffTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Root = "/learning/v1";

    [Fact]
    public async Task LearningKeysIsolateCallersAndRotationRetainsPendingQuestions()
    {
        await using var host = new VocabularyWebApplicationFactory();
        var student = Guid.NewGuid();
        var question = Guid.NewGuid();
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", await host.SeedKeyAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsync($"{Root}/learners/{student}", null, Ct)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Api-Key");
        client.DefaultRequestHeaders.Add("X-Api-Key", await host.SeedKeyAsync("first", catalog:false, learning:true));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsync($"{Root}/learners/{student}", null, Ct)).StatusCode);
        var storage = host.Services.GetRequiredService<LearningStorage>();
        await using var db = new LearningDbContext(storage.Options("first"));
        // A free review has no plan. This issued snapshot survives catalog changes and provider outages.
        var option = Guid.NewGuid();
        var options = JsonSerializer.Serialize(new[] { new { optionId = option.ToString("D"), text = "test definition" } });
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO vocab_issued_question(id,student_id,kind,word_key,meaning_key,word_id,meaning_id,book_id,
                direction,prompt,options_snapshot,correct_option_id,issued_at,expires_at)
            VALUES ({question},{student},2,'sample','definition','word','meaning','book',1,'sample',
                {options},{option},now(),now()+interval '1 hour')
            """, Ct);
        var path = $"{Root}/api/vocabulary/questions/{question}/answer";
        var answer = new { studentId = student, optionId = option, elapsedMs = 800,
            localDate = DateTime.UtcNow.ToString("yyyy-MM-dd"), tzOffsetMinutes = 480 };
        using var other = host.CreateClient();
        other.DefaultRequestHeaders.Add("X-Api-Key", await host.SeedKeyAsync("second", catalog:false, learning:true));
        Assert.Equal(HttpStatusCode.NoContent, (await other.PutAsync($"{Root}/learners/{student}", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsJsonAsync(path, answer, Ct)).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Api-Key");
        client.DefaultRequestHeaders.Add("X-Api-Key", await host.SeedKeyAsync("first", catalog:false, learning:true));
        var submissions = await Task.WhenAll(client.PostAsJsonAsync(path, answer, Ct), client.PostAsJsonAsync(path, answer, Ct));
        foreach (var response in submissions) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, await db.VocabAttempts.CountAsync(Ct));
        Assert.Equal(1, await db.MemoryReviewLogs.CountAsync(Ct));
        Assert.Equal(1, (await db.MemoryCards.SingleAsync(Ct)).Reps);
        Assert.Equal(HttpStatusCode.NoContent, (await other.DeleteAsync($"{Root}/learners/{student}", Ct)).StatusCode);
        Assert.Equal(1, await db.VocabAttempts.CountAsync(Ct));
        var generation = Guid.NewGuid();
        await db.Database.ExecuteSqlAsync($"UPDATE learning_schema_version SET generation={generation}", Ct);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"{Root}/learners/{student}", Ct)).StatusCode);
        client.DefaultRequestHeaders.Add("X-Learning-Generation", generation.ToString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Root}/learners/{student}", Ct)).StatusCode);
        Assert.Empty(await db.MemoryReviewLogs.ToListAsync(Ct));
    }
}
