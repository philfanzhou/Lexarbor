using Lexarbor.Learning.Database;
using Lexarbor.Learning.Database.Repositories;
using Lexarbor.Learning.Domain.Services;
using Lexarbor.Learning.Service;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Host;

internal sealed class PhotoImportWorker(LearningStorage storage, IServiceScopeFactory scopes,
    ILogger<PhotoImportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            try
            {
                foreach (var schema in await storage.PhotoTaskSchemasAsync(stoppingToken))
                {
                    try { worked |= await ProcessOneAsync(schema, stoppingToken); }
                    catch (Exception error) when (!stoppingToken.IsCancellationRequested)
                    {
                        logger.LogWarning("Photo import queue processing failed: {Error}.", error.GetType().Name);
                    }
                }
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Photo import queues could not be read: {Error}.", error.GetType().Name);
            }
            if (!worked) await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    internal async Task<bool> ProcessOneAsync(string schema, CancellationToken ct)
    {
        var options = storage.OptionsForSchema(schema);
        await using var gate = new LearningDbContext(options);
        await using var lease = await gate.Database.BeginTransactionAsync(ct);
        await gate.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock_shared(hashtextextended(current_schema(), 79193))", ct);
        await using var db = new LearningDbContext(options);
        var tasks = new PhotoImportTaskRepository(db);
        var claim = await tasks.ClaimAsync(ct);
        if (claim is null) return false;

        await using var scope = scopes.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var service = ActivatorUtilities.CreateInstance<VocabularyPhotoImportService>(provider, new LearnerDirectory(db));
        var trace = provider.GetRequiredService<VocabularyPhotoTrace>();
        var photo = new VocabPhoto(claim.Photo, claim.Photo[0] == 0x89 ? "image/png" : "image/jpeg");
        IReadOnlyList<VocabPhotoCandidate> entries = [];
        string? errorCode = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(9));
        try
        {
            entries = await VocabularyNotebookHttpEndpoints.PreviewWithTraceAsync(claim.StudentId, photo, service, trace,
                provider.GetRequiredService<VocabularyPhotoTraceStore>(), deadline.Token);
            if (entries.Count == 0) errorCode = "VOCAB_PHOTO_NO_ENTRIES";
        }
        catch (Exception error) when (!ct.IsCancellationRequested)
        {
            errorCode = error switch
            {
                VocabPhotoInvalidException => "VOCAB_PHOTO_INVALID",
                VocabCatalogUnavailableException => "VOCAB_CATALOG_UNAVAILABLE",
                _ when deadline.IsCancellationRequested || trace.TimedOut => "VOCAB_PHOTO_TIMEOUT",
                _ => "VOCAB_PHOTO_IMPORT_UNAVAILABLE",
            };
            logger.LogWarning("Photo import attempt {Attempt} failed with {Code} ({Error}).", claim.Attempts, errorCode, error.GetType().Name);
        }
        await tasks.FinishAsync(claim, entries, errorCode, ct);
        return true;
    }
}
