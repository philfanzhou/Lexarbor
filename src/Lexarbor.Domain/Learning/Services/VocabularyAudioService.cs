using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Repositories;

namespace Lexarbor.Learning.Domain.Services;

/// <summary>Authorize pronunciation using the saved question only, without changing learning state.</summary>
public sealed class VocabularyAudioService(IVocabIssuedQuestionRepository questions, IVocabularyAudioSource audio)
{
    public async Task<VocabAudio> GetAsync(Guid questionId, Guid studentId, string locale,
        CancellationToken cancellationToken = default)
    {
        var question = await questions.FindByIdAsync(questionId, cancellationToken).ConfigureAwait(false);
        if (question is null || question.StudentId != studentId)
            throw new VocabQuestionNotFoundException(questionId);
        if (!question.Direction.ShowsTargetWord() && question.AnsweredAt is null)
            throw new VocabAudioLockedException();
        return await audio.GetAsync(question.WordId, question.BookId, question.MeaningId, locale, cancellationToken)
            .ConfigureAwait(false);
    }
}
