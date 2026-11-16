using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Domain.Services;

public sealed class VocabularyExpansionService(IVocabularyExpansionRepository expansions,
    IVocabularyRepository words, IVocabularyRelationRepository relations, IVocabularyBookRepository books,
    VocabularyDomainService vocabulary, VocabularyRelationService relationService,
    IEnrichmentTaskRepository tasks, IUnitOfWork unitOfWork, TimeProvider clock)
{
    public Task<bool> PublishAsync(EnrichmentTask task, EnrichmentExecutionGuard guard,
        VocabularyExpansionPlan plan, Func<bool> canPublish, CancellationToken token)
    {
        if (!VocabularyExpansionValidation.Valid(plan)) throw new DomainValidationException("Invalid vocabulary expansion plan.");
        return unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
            if (!canPublish() || task.Id != guard.TargetId || task.Target.Step != EnrichmentStep.Expand
                || !await tasks.ValidateInTransactionAsync(guard, now, token)) return false;
            if (await books.GetByIdAsync(plan.BookId) is not { Status: true }) return false;
            foreach (var candidate in plan.Candidates)
            {
                token.ThrowIfCancellationRequested();
                var existing = await expansions.FindExistingMeaningAsync(candidate, token);
                var targetMeaningId = existing?.MeaningId;
                var targetWordId = existing?.WordId ?? (await words.GetByNormalizedWordAsync(CatalogKeys.Of(candidate.Word)))?.Id;
                if (targetWordId == null)
                {
                    var created = await vocabulary.AddOrUpdateCoreAsync(new VocabularyModel { Word = candidate.Word },
                        new VocabularyMeaningModel
                        {
                            BookId = plan.BookId,
                            PartOfSpeech = candidate.PartOfSpeech,
                            Meaning = candidate.Meaning,
                            Example = candidate.Example
                        });
                    targetWordId = created.Word.Id;
                    targetMeaningId = created.Meaning.Id;
                    await expansions.MarkGeneratedAsync(targetWordId, task.Id, token);
                }
                // Existing facts, including manual notes and review status, always win.
                var note = candidate.UsageNote.Trim();
                var previous = await relations.FindAsync(task.Target.WordId, targetWordId, candidate.Type, token);
                var edge = previous ?? (await relationService.CreateInsideAsync(new(task.Target.WordId, targetWordId, candidate.Type,
                        note, Confirm: true), "generated", task.Id)).Relation;
                if (edge.Status == "confirmed" && edge.OriginKind == "generated" && edge.UsageNote == note
                    && task.Target.MeaningId is { } sourceMeaningId && targetMeaningId != null
                    && await relations.AddMeaningPairAsync(edge.Id, new(sourceMeaningId, targetMeaningId), token)
                    && previous != null)
                    await relationService.AdvanceVersionAsync(edge, token);
            }
            if (!canPublish() || !await tasks.CompleteInTransactionAsync(guard, clock.GetUtcNow().ToUnixTimeMilliseconds(), token))
                throw new ConflictException("Vocabulary expansion changed before publication.");
            return true;
        }, token);
    }
}
