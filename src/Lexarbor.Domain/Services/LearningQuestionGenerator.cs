using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Services;

/// <summary>Stateless, on-demand selection shared by catalog and submission targets.</summary>
public sealed class LearningQuestionGenerator(Func<int, int>? pickIndex = null)
{
    private readonly Func<int, int> _pickIndex = pickIndex ?? Random.Shared.Next;

    public async Task<LearningQuestion> CreateAsync(
        string word, string meaning, LearningQuestionMode mode,
        Func<bool, CancellationToken, Task<VocabularyQuestionModel?>> createChoice,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(mode)) throw new DomainValidationException("Mode must be new or review.");
        if (string.IsNullOrWhiteSpace(word) || string.IsNullOrWhiteSpace(meaning))
            throw new LearningQuestionException(LearningQuestionFailure.Unavailable);

        List<LearningQuestionType> types = [LearningQuestionType.EnglishToChinese];
        if (mode == LearningQuestionMode.Review)
        {
            types.Add(LearningQuestionType.ChineseToEnglish);
            if (LetterHiding.IsEligible(word)) types.Add(LearningQuestionType.FillLetters);
        }

        // A uniform permutation makes the first available type uniform among
        // available types, without querying distractors for a chosen FillLetters.
        while (types.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = _pickIndex(types.Count);
            var type = types[index];
            types.RemoveAt(index);
            if (type == LearningQuestionType.FillLetters)
            {
                var pattern = LetterHiding.Create(word, _pickIndex);
                cancellationToken.ThrowIfCancellationRequested();
                return new FillLettersLearningQuestion(meaning, pattern, word);
            }

            // Null alone means this type has insufficient distinct options.
            // Infrastructure, target/source failures and cancellation propagate.
            var choice = await createChoice(type == LearningQuestionType.ChineseToEnglish, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (choice != null) return new ChoiceLearningQuestion(type, choice.Word, choice.Options);
        }
        throw new LearningQuestionException(LearningQuestionFailure.Unavailable);
    }
}
