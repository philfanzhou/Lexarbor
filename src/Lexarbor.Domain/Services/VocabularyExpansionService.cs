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
                var word = await words.GetByNormalizedWordAsync(candidate.Word.Trim().ToLowerInvariant());
                if (word == null)
                {
                    var created = await vocabulary.AddOrUpdateCoreAsync(new VocabularyModel { Word = candidate.Word },
                        new VocabularyMeaningModel
                        {
                            BookId = plan.BookId,
                            PartOfSpeech = candidate.PartOfSpeech,
                            Meaning = candidate.Meaning,
                            Example = candidate.Example
                        });
                    word = created.Word;
                    await expansions.MarkGeneratedAsync(word.Id, task.Id, token);
                }
                // Existing facts, including manual notes and review status, always win.
                if (await relations.FindAsync(task.Target.WordId, word.Id, candidate.Type, token) == null)
                    await relationService.CreateInsideAsync(new(task.Target.WordId, word.Id, candidate.Type,
                        candidate.UsageNote.Trim(), Confirm: true), "generated", task.Id);
            }
            if (!canPublish() || !await tasks.CompleteInTransactionAsync(guard, clock.GetUtcNow().ToUnixTimeMilliseconds(), token))
                throw new ConflictException("Vocabulary expansion changed before publication.");
            return true;
        }, token);
    }
}
