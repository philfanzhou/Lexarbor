using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lexarbor.Service.Tests;

public sealed partial class MeaningSubmissionEndpointTests
{
    [Theory]
    [InlineData(0, "new", "EnglishToChinese")]
    [InlineData(0, "review", "EnglishToChinese")]
    [InlineData(1, "review", "ChineseToEnglish")]
    [InlineData(2, "review", "FillLetters")]
    public async Task LearningQuestionSharesModesAndExclusiveContract(int pick, string mode, string type)
    {
        var pending = await Data(await Post("/integration/meaning-submissions", new { word = "Explode", meaning = "爆炸" }, _submissionKey));
        var path = $"/integration/meaning-submissions/{pending.GetProperty("id").GetString()}/learning-question";
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<LearningQuestionGenerator>();
            services.AddScoped(_ => new LearningQuestionGenerator(max => pick % max));
        }));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(BusinessApiAuthorization.Header, _submissionKey);
        var data = await LearningQuestionEndpointTests.Success(await client.PostAsJsonAsync(path, new { mode }, Ct));
        Assert.Equal(type, data.GetProperty("type").GetString());
        LearningQuestionEndpointTests.AssertStructure(data);
    }

    [Theory]
    [InlineData(0, "EnglishToChinese")]
    [InlineData(1, "ChineseToEnglish")]
    public async Task LearningQuestionShortPendingHasBothChoiceDirectionsAndNoFill(int pick, string expected)
    {
        var pending = await Data(await Post("/integration/meaning-submissions", new { word = "go", meaning = "去" }, _submissionKey));
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<LearningQuestionGenerator>();
            services.AddScoped(_ => new LearningQuestionGenerator(max => pick % max));
        }));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(BusinessApiAuthorization.Header, _submissionKey);
        var data = await Data(await client.PostAsJsonAsync($"/integration/meaning-submissions/{pending.GetProperty("id").GetString()}/learning-question", new { mode = "review" }, Ct));
        Assert.Equal(expected, data.GetProperty("type").GetString());
        LearningQuestionEndpointTests.AssertStructure(data);
    }

    [Fact]
    public async Task LearningQuestionAuthenticationConfigurationAndInputKeepTheirBoundaries()
    {
        const string path = "/integration/meaning-submissions/missing/learning-question";
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(path, new { mode = "review" }, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(path, new { mode = "review" }, _factory.CreateToken("admin"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Post(path, new { mode = "review" }, _catalogKey)).StatusCode);
        await LearningQuestionEndpointTests.Error(await Post(path, new { }, _submissionKey), HttpStatusCode.BadRequest, "QUESTION_INVALID_REQUEST");
        await LearningQuestionEndpointTests.Error(await Post(path, new { mode = "review" }, _submissionKey), HttpStatusCode.NotFound, "QUESTION_TARGET_NOT_FOUND");
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent("{", Encoding.UTF8, "application/json") };
        request.Headers.Add(BusinessApiAuthorization.Header, _submissionKey);
        await LearningQuestionEndpointTests.Error(await _client.SendAsync(request, Ct), HttpStatusCode.BadRequest, "QUESTION_INVALID_REQUEST");
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<MeaningSubmissionSettings>();
            services.AddSingleton(new MeaningSubmissionSettings());
        }));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(BusinessApiAuthorization.Header, _submissionKey);
        await LearningQuestionEndpointTests.Error(await client.PostAsJsonAsync(path, new { mode = "review" }, Ct), HttpStatusCode.ServiceUnavailable, "QUESTION_SERVICE_UNAVAILABLE");
    }

    [Fact]
    public async Task LearningQuestionSubmissionStatesAndSupplementaryChoiceSourcesStayCompatible()
    {
        var pending = await Data(await Post("/integration/meaning-submissions", new { word = "Explode", meaning = "爆炸" }, _submissionKey));
        var id = pending.GetProperty("id").GetString()!;
        var path = $"/integration/meaning-submissions/{id}/learning-question";
        using (var scope = _factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<MeaningSubmissionService>();
            await service.ApproveAsync(id, new("Explode", null, "爆炸", null, null, null), Ct);
        }
        var choice = await Data(await Post(path, new { mode = "new" }, _submissionKey));
        Assert.Equal("EnglishToChinese", choice.GetProperty("type").GetString());
        LearningQuestionEndpointTests.AssertStructure(choice);
        var catalogMeanings = new[] { "苹果", "河流", "云", "石头" };
        var distractors = choice.GetProperty("options").EnumerateArray().Where(option => !option.GetProperty("isCorrect").GetBoolean()).ToArray();
        Assert.Equal(3, distractors.Length);
        Assert.All(distractors, option => Assert.Contains(option.GetProperty("text").GetString(), catalogMeanings));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            var book = await db.VocabularyBooks.SingleAsync(x => x.Id == "supplementary", Ct);
            book.Status = false;
            await db.SaveChangesAsync(Ct);
        }
        await LearningQuestionEndpointTests.Error(await Post(path, new { mode = "review" }, _submissionKey), HttpStatusCode.UnprocessableEntity, "QUESTION_SOURCE_UNAVAILABLE");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            var book = await db.VocabularyBooks.SingleAsync(x => x.Id == "supplementary", Ct);
            book.Status = true;
            await db.SaveChangesAsync(Ct);
        }
        // POS-only/IPA-only revise remains unavailable even when the two displayed texts are unchanged.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            var submission = await db.MeaningSubmissions.SingleAsync(x => x.Id == id, Ct);
            submission.AiReviewVerdict = "revise";
            await db.SaveChangesAsync(Ct);
        }
        await LearningQuestionEndpointTests.Error(await Post(path, new { mode = "review" }, _submissionKey), HttpStatusCode.NotFound, "QUESTION_TARGET_NOT_FOUND");
        foreach (var reject in new[] { false, true })
        {
            var item = await Data(await Post("/integration/meaning-submissions", new { word = "changed" + reject, meaning = "原义" }, _submissionKey));
            var itemId = item.GetProperty("id").GetString()!;
            using var scope = _factory.Services.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<MeaningSubmissionService>();
            if (reject) await service.RejectAsync(itemId, cancellationToken: Ct);
            else await service.ApproveAsync(itemId, new("changed" + reject, null, "新义", null, null, null), Ct);
            await LearningQuestionEndpointTests.Error(await Post($"/integration/meaning-submissions/{itemId}/learning-question", new { mode = "review" }, _submissionKey), HttpStatusCode.NotFound, "QUESTION_TARGET_NOT_FOUND");
        }
    }

    [Fact]
    public async Task ApprovedSubmissionReviewKeepsOrdinaryDistractors()
    {
        var pending = await Data(await Post("/integration/meaning-submissions", new { word = "Explode", meaning = "爆炸" }, _submissionKey));
        var id = pending.GetProperty("id").GetString()!;
        using (var scope = _factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<MeaningSubmissionService>();
            await service.ApproveAsync(id, new("Explode", null, "爆炸", null, null, null), Ct);
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            var target = await db.Vocabularies.SingleAsync(item => item.NormalizedWord == "explode", Ct);
            for (var index = 0; index < 3; index++)
            {
                var wordId = $"family-{index}";
                db.Vocabularies.Add(new() { Id = wordId, Word = $"form-{index}" });
                db.VocabularyMeanings.Add(new() { Id = $"m-{wordId}", VocabularyId = wordId, BookId = "main", Meaning = $"词族 {index}" });
                db.VocabularyMeanings.Add(new() { Id = $"local-{index}", VocabularyId = $"w{index + 1}", BookId = "supplementary", Meaning = $"补充词书 {index}" });
                db.VocabularyRelations.Add(new()
                {
                    Id = wordId,
                    SourceWordId = target.Id,
                    TargetWordId = wordId,
                    Type = "derived_noun",
                    OriginKind = "manual",
                    Status = "confirmed",
                    ReviewReason = null,
                    LastConfirmedAtUnixMs = 0
                });
            }
            await db.SaveChangesAsync(Ct);
        }
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<LearningQuestionGenerator>();
            services.AddScoped(_ => new LearningQuestionGenerator(_ => 0));
        }));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(BusinessApiAuthorization.Header, _submissionKey);
        var route = $"/integration/meaning-submissions/{id}/learning-question";
        var data = await LearningQuestionEndpointTests.Success(await client.PostAsJsonAsync(route,
            new { mode = "review", relation = new { sourceWordId = "ignored", type = "derived_noun" } }, Ct));
        Assert.Equal("EnglishToChinese", data.GetProperty("type").GetString());
        LearningQuestionEndpointTests.AssertStructure(data);
        var wrongOptions = data.GetProperty("options").EnumerateArray().Where(option => !option.GetProperty("isCorrect").GetBoolean());
        Assert.Equal(new[] { "补充词书 0", "补充词书 1", "补充词书 2" }, wrongOptions.Select(option => option.GetProperty("text").GetString()).Order());
    }

    [Fact]
    public async Task LearningQuestionShortPendingUsesOnlyTheActuallyAvailableChoiceDirection()
    {
        var pending = await Data(await Post("/integration/meaning-submissions", new { word = "go", meaning = "去" }, _submissionKey));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            await db.VocabularyMeanings.ExecuteUpdateAsync(set => set.SetProperty(x => x.Meaning, "共同释义"), Ct);
        }
        using var host = _factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<LearningQuestionGenerator>();
            services.AddScoped(_ => new LearningQuestionGenerator(_ => 0));
        }));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(BusinessApiAuthorization.Header, _submissionKey);
        var path = $"/integration/meaning-submissions/{pending.GetProperty("id").GetString()}/learning-question";
        var data = await Data(await client.PostAsJsonAsync(path, new { mode = "review" }, Ct));
        Assert.Equal("ChineseToEnglish", data.GetProperty("type").GetString());
        LearningQuestionEndpointTests.AssertStructure(data);
        await LearningQuestionEndpointTests.Error(await client.PostAsJsonAsync(path, new { mode = "new" }, Ct), HttpStatusCode.UnprocessableEntity, "QUESTION_UNAVAILABLE");
    }

    [Fact]
    public async Task LearningQuestionEmptyCatalogFallsBackForReviewButNotForNewOrAnIneligibleWord()
    {
        var pending = await Data(await Post("/integration/meaning-submissions", new { word = "Explode", meaning = "爆炸" }, _submissionKey));
        var shortPending = await Data(await Post("/integration/meaning-submissions", new { word = "go", meaning = "去" }, _submissionKey));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            await db.VocabularyMeanings.ExecuteDeleteAsync(Ct);
            await db.Vocabularies.ExecuteDeleteAsync(Ct);
        }
        var path = $"/integration/meaning-submissions/{pending.GetProperty("id").GetString()}/learning-question";
        LearningQuestionEndpointTests.AssertStructure(await Data(await Post(path, new { mode = "review" }, _submissionKey)));
        await LearningQuestionEndpointTests.Error(await Post(path, new { mode = "new" }, _submissionKey), HttpStatusCode.UnprocessableEntity, "QUESTION_UNAVAILABLE");
        await LearningQuestionEndpointTests.Error(await Post($"/integration/meaning-submissions/{shortPending.GetProperty("id").GetString()}/learning-question", new { mode = "review" }, _submissionKey), HttpStatusCode.UnprocessableEntity, "QUESTION_UNAVAILABLE");
    }
}
