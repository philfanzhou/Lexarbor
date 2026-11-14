using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Services;

public sealed partial class MeaningSubmissionService
{
    private readonly LearningQuestionGenerator _learningQuestions;

    public async Task<LearningQuestion> CreateLearningQuestionAsync(
        string id, LearningQuestionMode mode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(id)) throw new DomainValidationException("Submission ID is required.");
        var submission = await _submissions.GetByIdAsync(id.Trim(), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (submission is null) throw new ResourceNotFoundException("Meaning submission was not found.");
        if (submission.Status == MeaningSubmissionStatus.Approved && submission.DefinitionKept)
        {
            if (string.IsNullOrWhiteSpace(submission.ResultWordId) || string.IsNullOrWhiteSpace(submission.ResultBookId)
                || string.IsNullOrWhiteSpace(submission.ResultMeaningId))
                throw new ResourceNotFoundException("Meaning submission was not found.");
            return await _vocabularyService.CreateSubmissionLearningQuestionAsync(
                submission.ResultWordId!, submission.ResultBookId!, submission.ResultMeaningId!, mode,
                cancellationToken: cancellationToken);
        }
        if (submission.Status != MeaningSubmissionStatus.Pending)
            throw new ResourceNotFoundException("Meaning submission was not found.");
        return await _learningQuestions.CreateAsync(submission.Word, submission.Meaning, mode,
            (direction, token) => BuildSubmissionQuestionAsync(submission, direction, token), cancellationToken);
    }
}
