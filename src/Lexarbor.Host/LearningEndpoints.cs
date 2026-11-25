using Lexarbor.Database.Diagnostics;
using Lexarbor.Host.Integration;
using Lexarbor.Learning.Database;
using Lexarbor.Learning.Database.Repositories;
using Lexarbor.Learning.Domain.Calendar;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Repositories;
using Lexarbor.Learning.Domain.Services;
using Lexarbor.Learning.Service;
using Lexarbor.Service;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Lexarbor.Host;

internal static class LearningEndpoints
{
    internal static void AddLearnerServices(this IServiceCollection services, IConfiguration configuration, string connection)
    {
        services.AddSingleton(provider => new LearningStorage(connection,
            [.. provider.GetServices<SlowCommandLog>()]));
        services.AddScoped(provider =>
        {
            var request = provider.GetRequiredService<IHttpContextAccessor>().HttpContext;
            var caller = request?.User.FindFirst(BusinessApiAuthorization.CallerClaim)?.Value
                ?? throw new InvalidOperationException("Missing learner caller identity.");
            return new LearningDbContext(provider.GetRequiredService<LearningStorage>().Options(caller));
        });
        services.AddScoped<UnitPracticeRepository>();
        services.AddScoped<UnitPracticeQuestionSource>();
        services.AddScoped<ILearnerDirectory, LearnerDirectory>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IMemoryCardRepository, MemoryCardRepository>();
        services.AddScoped<IVocabCardLocationRepository, VocabCardLocationRepository>();
        services.AddScoped<IMemoryReviewLogRepository, MemoryReviewLogRepository>();
        services.AddScoped<IVocabPlanRepository, VocabPlanRepository>();
        services.AddScoped<IVocabPlanChangeRepository, VocabPlanChangeRepository>();
        services.AddScoped<IVocabPlanDayRepository, VocabPlanDayRepository>();
        services.AddScoped<IVocabIssuedQuestionRepository, VocabIssuedQuestionRepository>();
        services.AddScoped<IVocabAttemptRepository, VocabAttemptRepository>();
        services.AddScoped<VocabularyMemoryService>();
        services.AddScoped<VocabularyPlanService>();
        services.AddScoped<VocabularyLearningService>();
        services.AddScoped<VocabularyAudioService>();
        services.AddScoped<IVocabularyAudioSource, LexarborAudioSource>();
        services.AddScoped<IVocabularyOverviewRepository, VocabularyOverviewRepository>();
        services.AddScoped<VocabularyOverviewService>();
        services.AddScoped<IVocabularyPlanStatsRepository, VocabularyPlanStatsRepository>();
        services.AddScoped<VocabularyPlanStatsService>();
        services.AddScoped<IVocabularyCheckInRepository, VocabularyCheckInRepository>();
        services.AddScoped<VocabularyCheckInService>();
        services.AddScoped<IVocabularyErrorProneRepository, VocabularyErrorProneRepository>();
        services.AddScoped<VocabularyErrorProneService>();
        services.AddScoped<IVocabularyWordListRepository, VocabularyWordListRepository>();
        services.AddScoped<VocabularyWordListService>();
        services.AddOptions<VocabularyErrorProneOptions>()
            .Bind(configuration.GetSection(VocabularyErrorProneOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<VocabularyErrorProneOptions>, VocabularyErrorProneOptionsValidation>();
        services.AddLexarborClients(configuration);
        services.AddScoped<IVocabularyPlanScopeReader, LexarborCatalogReader>();
        services.AddScoped<IVocabularyScopeEntryReader, LexarborScopeEntryReader>();
        services.AddScoped<IVocabularyQuestionGenerator, LexarborQuestionGenerator>();
        services.AddScoped<IVocabNotebookRepository, VocabNotebookRepository>();
        services.AddScoped<VocabularyNotebookService>();
        services.AddScoped<VocabularyFamilyService>();
        services.AddScoped<IVocabularyNotebookSource>(provider => provider.GetRequiredService<VocabularyNotebookService>());
        services.AddHttpClient(VisionPhotoRecognizer.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
        services.AddScoped<VocabularyPhotoTrace>();
        services.AddScoped<IVocabularyPhotoRecognizer, VisionPhotoRecognizer>();
        // The latest photo imports stay under the persistent data/ directory for offline analysis.
        services.AddSingleton(provider => new VocabularyPhotoTraceStore(
            Path.Combine(provider.GetRequiredService<IHostEnvironment>().ContentRootPath, "data", VocabularyPhotoTraceStore.DirectoryName),
            provider.GetRequiredService<ILogger<VocabularyPhotoTraceStore>>()));
        services.AddScoped<VocabularyPhotoImportService>();
        services.AddScoped<PhotoImportTaskRepository>();
        services.AddHostedService<PhotoImportWorker>();
        services.AddOptions<VocabularyFsrsOptions>()
            .Bind(configuration.GetSection(VocabularyFsrsOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<VocabularyFsrsOptions>, VocabularyFsrsOptionsValidation>();
        services.AddOptions<VocabularyLearningOptions>()
            .Bind(configuration.GetSection(VocabularyLearningOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<VocabularyLearningOptions>, VocabularyLearningOptionsValidation>();
        services.AddSingleton(provider => LearningCalendar.Create(
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<VocabularyLearningOptions>>().Value.TimeZone));
    }

    internal static void MapLearnerEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/learning/v1").RequireBusinessApiScope(BusinessApiAuthorization.Learning);
        group.WithMetadata(new LearnerRoute());
        group.MapGet("/storage", async (LearningDbContext db, CancellationToken ct) =>
            Results.Ok(new { storeId = await db.Database.SqlQueryRaw<Guid>("SELECT store_id AS \"Value\" FROM learning_schema_version").SingleAsync(ct) })).WithMetadata(new StoreIdentityRoute());
        group.MapPost("/plan-owners", async (Guid[] ids, LearningDbContext db, CancellationToken ct) =>
            ids.Length is < 1 or > 100 ? Results.BadRequest() : Results.Ok(await db.VocabPlans.AsNoTracking()
                .Where(plan => ids.Contains(plan.Id)).Select(plan => new { plan.Id, plan.StudentId }).ToListAsync(ct)));
        group.MapPut("/learners/{id:guid}", async (Guid id, LearningDbContext db, CancellationToken ct) =>
        {
            if (id == Guid.Empty) return Results.BadRequest();
            await db.Database.ExecuteSqlAsync($"INSERT INTO learner_identity (id) VALUES ({id}) ON CONFLICT DO NOTHING", ct);
            return Results.NoContent();
        });
        group.MapDelete("/learners/{id:guid}", async (Guid id, LearningDbContext db, CancellationToken ct) =>
        {
            await db.Learners.Where(row => row.Id == id).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });
        group.MapUnitPractice();
        group.MapVocabularyPlanHttpEndpoints();
        group.MapVocabularyNotebookHttpEndpoints();
        group.MapVocabularyFamilyHttpEndpoints();
        group.MapVocabularyLearningHttpEndpoints();
        group.MapVocabularyOverviewHttpEndpoints();
        group.MapVocabularyPlanStatsHttpEndpoints();
        group.MapVocabularyCheckInHttpEndpoints();
        group.MapVocabularyErrorProneHttpEndpoints();
        group.MapVocabularyWordListHttpEndpoints();
    }

    internal static async Task PrepareAsync(HttpContext request, RequestDelegate next)
    {
        if (request.GetEndpoint()?.Metadata.GetMetadata<LearnerRoute>() is not null)
        {
            var caller = request.User.FindFirst(BusinessApiAuthorization.CallerClaim)?.Value;
            if (caller is null || !request.User.HasClaim(BusinessApiAuthorization.ScopeClaim, BusinessApiAuthorization.Learning))
            {
                request.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            request.Response.Headers.CacheControl = "no-store";
            var storage = request.RequestServices.GetRequiredService<LearningStorage>();
            var ct = request.RequestAborted;
            await storage.EnsureAsync(caller, ct);
            await using var gate = storage.OpenGate(caller);
            await gate.OpenAsync(ct);
            await using var lease = await gate.BeginTransactionAsync(ct);
            await using var acquire = new NpgsqlCommand("SELECT pg_advisory_xact_lock_shared(hashtextextended(current_schema(), 79193))", gate);
            await acquire.ExecuteNonQueryAsync(ct);
            Guid? generation;
            await using (var current = new NpgsqlCommand("SELECT generation, store_id FROM learning_schema_version", gate))
            await using (var reader = await current.ExecuteReaderAsync(ct))
            {
                await reader.ReadAsync(ct);
                generation = reader.IsDBNull(0) ? null : reader.GetGuid(0);
                var store = reader.GetGuid(1);
                request.Response.Headers["X-Learning-Store"] = store.ToString();
                if (request.Request.Headers.TryGetValue("X-Learning-Store", out var binding)
                    && (binding.Count != 1 || !Guid.TryParse(binding[0], out var expectedStore) || expectedStore != store))
                {
                    request.Response.StatusCode = StatusCodes.Status409Conflict;
                    return;
                }
            }
            var supplied = request.Request.Headers["X-Learning-Generation"];
            var pending = request.Request.Headers["X-Learning-Pending"];
            var matches = generation is Guid expected
                ? Matches(supplied, expected) || Matches(pending, expected)
                : supplied.Count == 0;
            if (request.GetEndpoint()?.Metadata.GetMetadata<StoreIdentityRoute>() is null && !matches)
            {
                request.Response.StatusCode = StatusCodes.Status409Conflict;
                return;
            }
            await next(request);
            return;
        }
        await next(request);
    }

    private static bool Matches(Microsoft.Extensions.Primitives.StringValues header, Guid expected) =>
        header.Count == 1 && Guid.TryParse(header[0], out var value) && value == expected;

    private sealed class StoreIdentityRoute;
    private sealed class LearnerRoute;
}
