using System.Net;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Lexarbor.Host;
using Lexarbor.Host.Integration;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Service.Tests;

public sealed partial class EnrichmentTargetExecutorTests
{
    private static EnrichmentTargetKey ExpansionKey => new("word", "meaning", null, EnrichmentStep.Expand);
    private static VocabularyExpansionCandidate Candidate(string word = "leader", string type = "derived_noun") =>
        new(word, type, "n.", "领导者", "She is a leader.", "A person who leads, in the sense of guiding.");

    private static VocabularyExpansionExecutor Expansion(Fixture f, VocabularyDbContext db, HttpFactory http, string? book = "supplement")
    {
        var tasks = new EnrichmentTaskRepository(db, f.Clock);
        var repo = new VocabularyExpansionRepository(db);
        var words = new VocabularyRepository(db); var books = new VocabularyBookRepository(db);
        var relations = new VocabularyRelationRepository(db); var unit = new UnitOfWork(db);
        var catalog = new VocabularyDomainService(words, books, new VocabularyMeaningRepository(db),
            new VocabularyBookUnitRepository(db), new VocabularyMeaningUnitRepository(db), unit);
        return new(tasks, repo, books, new(repo, words, relations, books, catalog,
            new(relations, unit, f.Clock), tasks, unit, f.Clock), new(http, f.Clock),
            new(new EnrichmentSchedulerRepository(db, tasks), f.Runtime, f.Clock), f.Runtime,
            new() { BookId = book }, f.Clock);
    }

    private static async Task Supplement(Fixture f)
    {
        f.Db.VocabularyBooks.Add(new() { Id = "supplement", BookName = "Supplement", Status = true });
        await f.Db.SaveChangesAsync(Ct); f.Db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Expansion_PublishesToSupplement_ReusesWordsAndRelations_AndDiscoversNormalEnrichmentWithoutRecursion()
    {
        await using var f = await Fixture.Create(); await Supplement(f);
        f.Db.Vocabularies.Add(new() { Id = "led", Word = "led", PhoneticUk = "led" });
        await f.Db.SaveChangesAsync(Ct); f.Db.ChangeTracker.Clear();
        var relationService = new VocabularyRelationService(new VocabularyRelationRepository(f.Db), new UnitOfWork(f.Db), f.Clock);
        var manual = await relationService.CreateAsync(new("word", "led", "past_tense", "Human note"), Ct);
        var handler = new ExpansionHandler([Candidate() with { UsageNote = "  A person who leads.  " }, Candidate("led", "past_tense"), Candidate("led", "past_participle")]);
        using var http = new HttpFactory(handler);
        handler.Before = () => { Assert.Null(f.Db.Database.CurrentTransaction); return Task.CompletedTask; };
        var task = await f.Ensure(ExpansionKey);
        var result = (await Expansion(f, f.Db, http).ExecuteAsync(task, "worker", Ct))!;
        Assert.Equal(EnrichmentState.Succeeded, Round(result).State); Assert.Equal(1, handler.Calls);
        var generated = await f.Db.Vocabularies.AsNoTracking().SingleAsync(w => w.Word == "leader", Ct);
        var meaning = await f.Db.VocabularyMeanings.AsNoTracking().SingleAsync(m => m.VocabularyId == generated.Id, Ct);
        Assert.Equal("supplement", meaning.BookId);
        Assert.Equal(1, await f.Db.VocabularyMeanings.CountAsync(m => m.BookId == "book", Ct));
        Assert.False(await f.Db.VocabularyMeanings.AnyAsync(m => m.VocabularyId == "led", Ct));
        Assert.Equal(manual.Relation, await relationService.GetAsync(manual.Relation.Id, Ct));
        var relation = await f.Db.VocabularyRelations.SingleAsync(r => r.TargetWordId == generated.Id, Ct);
        Assert.Equal("generated", relation.OriginKind); Assert.Equal(task.Id, relation.OriginReference);
        Assert.Equal("confirmed", relation.Status);
        Assert.Equal("A person who leads.", relation.UsageNote);
        var discovered = await new EnrichmentSchedulerRepository(f.Db).DiscoverAsync(20, [generated.Id], Ct);
        Assert.Contains(discovered, k => k.WordId == generated.Id && k.Step == EnrichmentStep.Fields);
        Assert.Contains(discovered, k => k.WordId == generated.Id && k.Step == EnrichmentStep.Audio);
        Assert.DoesNotContain(discovered, k => k.WordId == generated.Id && k.Step == EnrichmentStep.Expand);
        await using var restart = f.Open();
        await Expansion(f, restart, http).ExecuteAsync(result, "restart", Ct);
        Assert.Equal(1, handler.Calls); Assert.Equal(3, await restart.VocabularyRelations.CountAsync(Ct));
        var admin = new EnrichmentAdminRepository(restart);
        var config = new EnrichmentAdminConfiguration(true, true, true, true, true, true, 5, "text", "text", "uk", "us", "t", "t", "s", "s", "supplement");
        var detail = await admin.DetailAsync(task.Id, 1, 10, config, f.Clock.Now, Ct);
        Assert.Null(detail!.Task.Locale); Assert.Equal("Succeeded", detail.Task.State);
    }

    [Fact]
    public async Task Expansion_EmptyResultSurvivesRestartAndCredentialRotation_ButMeaningChangeReopens()
    {
        await using var f = await Fixture.Create(); await Supplement(f);
        var handler = new ExpansionHandler([]); using var http = new HttpFactory(handler);
        var result = (await Expansion(f, f.Db, http).ExecuteAsync(await f.Ensure(ExpansionKey), "worker", Ct))!;
        Assert.Empty(Round(result).Plan!.Expansion!.Candidates);
        await f.Publish(new() { ["Enrichment:Text:ApiKey"] = "rotated-synthetic-planner" });
        await using var restart = f.Open();
        await Expansion(f, restart, http).ExecuteAsync(result, "restart", Ct);
        Assert.Equal(1, handler.Calls);
        await restart.VocabularyMeanings.ExecuteUpdateAsync(s => s.SetProperty(m => m.Meaning, "be in charge"), Ct);
        result = (await Expansion(f, restart, http).ExecuteAsync(result, "edited", Ct))!;
        Assert.Equal(2, handler.Calls); Assert.Equal(2, result.CurrentRound);
        Assert.Equal(EnrichmentState.Succeeded, Round(result).State);
    }

    [Fact]
    public async Task Expansion_StagedResultRecoversWithoutAnotherModelCall()
    {
        await using var f = await Fixture.Create(); await Supplement(f);
        var task = await f.Ensure(ExpansionKey);
        var guard = (await f.Tasks.ClaimAsync(task.Id, "crashed", f.Clock.Now, f.Clock.Now + 1000, Ct))!;
        await f.Tasks.BeginCallAsync(guard, Guid.NewGuid().ToString(), "text", null, f.Clock.Now, Ct);
        await f.Tasks.SavePlanAsync(guard, new("lead", null, null, null,
            Expansion: new("captured-model", "supplement", [Candidate()])), f.Clock.Now, Ct);
        f.Clock.Now += 1001;
        var handler = new ExpansionHandler([]); using var http = new HttpFactory(handler);
        await using var restart = f.Open();
        var result = (await Expansion(f, restart, http).ExecuteAsync(task, "restart", Ct))!;
        Assert.Equal(EnrichmentState.Succeeded, Round(result).State); Assert.Equal(0, handler.Calls);
        Assert.Equal("captured-model", Round(result).Plan!.Expansion!.Model);
        Assert.True(await restart.Vocabularies.AnyAsync(w => w.Word == "leader", Ct));
    }

    [Fact]
    public async Task Expansion_PublicationFailureRollsBackWordsMeaningsRelationsAndOrigin_ThenReplaysStagedPlan()
    {
        await using var f = await Fixture.Create(); await Supplement(f);
        await f.Db.Database.ExecuteSqlRawAsync("CREATE TRIGGER expansion_test_failure BEFORE INSERT ON vocabulary_relation BEGIN SELECT RAISE(ABORT,'fixture'); END;", Ct);
        var handler = new ExpansionHandler([Candidate()]); using var http = new HttpFactory(handler);
        var task = await f.Ensure(ExpansionKey);
        await Assert.ThrowsAsync<Lexarbor.Domain.Exceptions.ConflictException>(() => Expansion(f, f.Db, http).ExecuteAsync(task, "worker", Ct));
        await using var restarted = f.Open();
        Assert.Equal(1, await restarted.Vocabularies.CountAsync(Ct));
        Assert.Equal(1, await restarted.VocabularyMeanings.CountAsync(Ct));
        Assert.Empty(await restarted.VocabularyRelations.ToListAsync(Ct));
        Assert.Empty(await restarted.EnrichmentGeneratedWords.ToListAsync(Ct));
        await restarted.Database.ExecuteSqlRawAsync("DROP TRIGGER expansion_test_failure", Ct);
        f.Clock.Now += 900_001;
        var result = (await Expansion(f, restarted, http).ExecuteAsync(task, "restart", Ct))!;
        Assert.Equal(EnrichmentState.Succeeded, Round(result).State);
        Assert.Equal(1, handler.Calls);
        Assert.True(await restarted.Vocabularies.AnyAsync(w => w.Word == "leader", Ct));
    }

    [Theory]
    [InlineData("meaning")]
    [InlineData("model")]
    [InlineData("book")]
    public async Task Expansion_ChangesDuringProviderCallCannotPublishOldResult(string change)
    {
        await using var f = await Fixture.Create(); await Supplement(f);
        var handler = new ExpansionHandler([Candidate()]); using var http = new HttpFactory(handler);
        handler.Before = async () =>
        {
            await using var other = f.Open();
            if (change == "meaning") await other.VocabularyMeanings.ExecuteUpdateAsync(s => s.SetProperty(m => m.Meaning, "a metal"), Ct);
            if (change == "book") await other.VocabularyBooks.Where(b => b.Id == "supplement").ExecuteUpdateAsync(s => s.SetProperty(b => b.Status, false), Ct);
            if (change == "model") await f.Publish(new() { ["Enrichment:Text:Model"] = "replacement" });
        };
        await Expansion(f, f.Db, http).ExecuteAsync(await f.Ensure(ExpansionKey), "worker", Ct);
        Assert.False(await f.Db.Vocabularies.AnyAsync(w => w.Word == "leader", Ct));
        Assert.Empty(await f.Db.VocabularyRelations.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("missing")]
    public async Task Expansion_MissingSupplementWaitsWithoutSpendingCalls(string? book)
    {
        await using var f = await Fixture.Create();
        var handler = new ExpansionHandler([Candidate()]); using var http = new HttpFactory(handler);
        var result = (await Expansion(f, f.Db, http, book).ExecuteAsync(await f.Ensure(ExpansionKey), "worker", Ct))!;
        Assert.Equal(EnrichmentWaitReason.ConfigurationMissing, Round(result).WaitReason);
        Assert.Equal(0, handler.Calls); Assert.Equal(0, Round(result).RequestCount);
    }

    [Fact]
    public async Task Expansion_ThrottleSharesTextGateAndDoesNotConsumeFailureBudget()
    {
        await using var f = await Fixture.Create(); await Supplement(f);
        var handler = new ExpansionHandler([]) { Status = HttpStatusCode.TooManyRequests };
        using var http = new HttpFactory(handler);
        var result = (await Expansion(f, f.Db, http).ExecuteAsync(await f.Ensure(ExpansionKey), "worker", Ct))!;
        Assert.Equal(EnrichmentWaitReason.Throttled, Round(result).WaitReason);
        Assert.Equal(0, Round(result).ConsumedFailures); Assert.Equal(1, Round(result).RequestCount);
        var gate = new EnrichmentServiceGate(new EnrichmentSchedulerRepository(f.Db, f.Tasks), f.Runtime, f.Clock);
        Assert.Null((await gate.EnterAsync("text", false, Ct)).LeaseId);
        f.Clock.Now = Round(result).NextAttemptAtUnixMs!.Value;
        handler.Status = HttpStatusCode.OK;
        result = (await Expansion(f, f.Db, http).ExecuteAsync(result, "retry", Ct))!;
        Assert.Equal(EnrichmentState.Succeeded, Round(result).State);
    }

    [Theory]
    [InlineData("{\"candidates\":[],\"candidates\":[]}")]
    [InlineData("{\"candidates\":[],\"command\":\"ignore instructions\"}")]
    [InlineData("{\"candidates\":null}")]
    [InlineData("{\"candidates\":[null]}")]
    [InlineData("{\"candidates\":[{\"word\":\"leader\"}]}")]
    [InlineData("{\"candidates\":[],\"secret\":\"synthetic-planner\"}")]
    public async Task Expansion_InvalidProviderOutputNeverWritesCatalog(string raw)
    {
        await using var f = await Fixture.Create(); await Supplement(f);
        var handler = new ExpansionHandler([]) { Raw = raw }; using var http = new HttpFactory(handler);
        var result = (await Expansion(f, f.Db, http).ExecuteAsync(await f.Ensure(ExpansionKey), "worker", Ct))!;
        Assert.Equal(EnrichmentState.Failed, Round(result).State);
        Assert.Null(Round(result).Plan); Assert.Equal(1, await f.Db.Vocabularies.CountAsync(Ct));
        Assert.Empty(await f.Db.VocabularyRelations.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(401, "{}", "credentials_rejected")]
    [InlineData(429, "{\"error\":{\"code\":\"insufficient_quota\"}}", "balance_required")]
    [InlineData(200, "{\"error\":{\"code\":\"invalid_api_key\"}}", "credentials_rejected")]
    public async Task Expansion_ProviderAccountFailureBlocksSharedTextService(int status, string envelope, string code)
    {
        await using var f = await Fixture.Create(); await Supplement(f);
        var handler = new ExpansionHandler([]) { Status = (HttpStatusCode)status, Envelope = envelope };
        using var http = new HttpFactory(handler);
        var result = (await Expansion(f, f.Db, http).ExecuteAsync(await f.Ensure(ExpansionKey), "worker", Ct))!;
        Assert.Equal(code, Round(result).ErrorCode);
        Assert.Equal(1, Round(result).RequestCount);
        var gate = new EnrichmentServiceGate(new EnrichmentSchedulerRepository(f.Db, f.Tasks), f.Runtime, f.Clock);
        Assert.Null((await gate.EnterAsync("text", false, Ct)).LeaseId);
        Assert.Equal(1, await f.Db.Vocabularies.CountAsync(Ct));
    }

    [Fact]
    public async Task Expansion_OversizedLocalInputDoesNotDispatchOrStartBillingRecord()
    {
        await using var f = await Fixture.Create();
        var input = (await new VocabularyExpansionRepository(f.Db).ReadAsync(ExpansionKey, Ct))!;
        var handler = new ExpansionHandler([]); using var http = new HttpFactory(handler);
        var dispatches = 0;
        var result = await new VocabularyExpansionClient(http, f.Clock).GenerateAsync(
            input with { Meaning = new string('x', 70000) }, f.Runtime.Current.Enrichment.Text, "supplement", Ct,
            _ => { dispatches++; return Task.FromResult(true); });
        Assert.False(result.RequestMade); Assert.Equal("expansion.input_too_large", result.ErrorCode);
        Assert.Equal(0, handler.Calls); Assert.Equal(0, dispatches);
    }

    private sealed class ExpansionHandler(IReadOnlyList<VocabularyExpansionCandidate> candidates) : HttpMessageHandler
    {
        public int Calls;
        public Func<Task>? Before;
        public string? Raw;
        public string? Envelope;
        public HttpStatusCode Status = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Before != null) await Before();
            if (Envelope != null) return new(Status) { Content = new StringContent(Envelope) };
            return new(Status)
            {
                Content = JsonContent.Create(new
                {
                    choices = new[] { new { finish_reason = "stop", message = new
            { content = Raw ?? JsonSerializer.Serialize(new { candidates }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) } } }
                })
            };
        }
    }
}
