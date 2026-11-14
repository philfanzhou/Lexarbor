using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Xunit;

namespace Lexarbor.Domain.Tests;

public sealed class LearningQuestionGeneratorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryAvailabilitySubsetHasEqualWeightWithBoundedAttempts()
    {
        // Six equiprobable permutations; the earliest available type wins.
        for (var mask = 0; mask < 8; mask++)
        {
            Dictionary<LearningQuestionType, int> counts = [];
            var candidates = (mask & 4) != 0 ? 3 : 2;
            for (var first = 0; first < candidates; first++)
                for (var second = 0; second < candidates - 1; second++)
                {
                    Queue<int> draws = new([first, second, 0]);
                    var generator = new LearningQuestionGenerator(max => draws.Count > 0 ? draws.Dequeue() % max : 0);
                    List<bool> attempts = [];
                    var task = generator.CreateAsync((mask & 4) != 0 ? "abcd" : "go", "目标", LearningQuestionMode.Review,
                        (direction, token) =>
                        {
                            Assert.Equal(Ct, token);
                            attempts.Add(direction);
                            var bit = direction ? 2 : 1;
                            return Task.FromResult((mask & bit) != 0 ? Choice(direction) : null);
                        }, Ct);
                    if (mask == 0)
                    {
                        var failure = await Assert.ThrowsAsync<LearningQuestionException>(() => task);
                        Assert.Equal(LearningQuestionFailure.Unavailable, failure.Failure);
                    }
                    else
                    {
                        var question = await task;
                        counts[question.Type] = counts.GetValueOrDefault(question.Type) + 1;
                    }
                    Assert.Equal(attempts.Count, attempts.Distinct().Count());
                    Assert.InRange(attempts.Count, 0, 2);
                }
            var available = Enumerable.Range(0, 3).Count(bit => (mask & (1 << bit)) != 0);
            Assert.Equal(available, counts.Count);
            if (available > 0) Assert.All(counts.Values, count => Assert.Equal(candidates * (candidates - 1) / available, count));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RelatedTypesShareTheUniformDrawAndUnavailableTypesAreTriedOnce(bool allAvailable)
    {
        var counts = new Dictionary<LearningQuestionType, int>();
        foreach (var first in Enumerable.Range(0, 6))
            foreach (var second in Enumerable.Range(0, 5))
                foreach (var third in Enumerable.Range(0, 4))
                    foreach (var fourth in Enumerable.Range(0, 3))
                    {
                        var draws = new Queue<int>([first, second, third, fourth]);
                        var generator = new LearningQuestionGenerator(max => draws.Count == 0 ? 0 : draws.Dequeue() % max);
                        var attempts = new HashSet<LearningQuestionType>();
                        var result = await generator.CreateAsync("went", "去了", LearningQuestionMode.Review,
                            (direction, _) =>
                            {
                                Assert.True(attempts.Add(direction ? LearningQuestionType.ChineseToEnglish : LearningQuestionType.EnglishToChinese));
                                return Task.FromResult(direction && !allAvailable ? null : Choice(direction));
                            }, Ct, type =>
                            {
                                Assert.True(attempts.Add(type));
                                return type switch
                                {
                                    LearningQuestionType.FormChoice when allAvailable => new FormChoiceLearningQuestion("go", "past_tense", []),
                                    LearningQuestionType.RelationChoice when allAvailable => new RelationChoiceLearningQuestion("went", "go", []),
                                    LearningQuestionType.FormFill => new FormFillLearningQuestion("去了", "go", "past_tense", ["w", null, null, null], "went"),
                                    _ => null
                                };
                            });
                        counts[result.Type] = counts.GetValueOrDefault(result.Type) + 1;
                    }

        var available = allAvailable ? 6 : 3;
        Assert.Equal(available, counts.Count);
        Assert.All(counts.Values, count => Assert.Equal(360 / available, count));
    }

    [Fact]
    public async Task PartOfSpeechSharesTheDrawWithAllSixExistingTypes()
    {
        var counts = new Dictionary<LearningQuestionType, int>();
        for (var first = 0; first < 7; first++)
        {
            var initial = true;
            var generator = new LearningQuestionGenerator(max =>
            {
                if (!initial) return 0;
                initial = false;
                Assert.Equal(7, max);
                return first;
            });
            var result = await generator.CreateAsync("happily", "快乐地", LearningQuestionMode.Review,
                (direction, _) => Task.FromResult<VocabularyQuestionModel?>(Choice(direction)), Ct,
                type => new ChoiceLearningQuestion(type, "Related", []),
                token =>
                {
                    Assert.Equal(Ct, token);
                    return Task.FromResult<LearningQuestion?>(new PartOfSpeechChoiceLearningQuestion("快乐地", "happiness", "adverb", []));
                });
            counts[result.Type] = counts.GetValueOrDefault(result.Type) + 1;
        }
        Assert.Equal(7, counts.Count);
        Assert.All(counts.Values, count => Assert.Equal(1, count));
    }

    [Fact]
    public async Task NewAttemptsOnlyEnglishToChineseAndDoesNotFallBack()
    {
        var calls = 0;
        var generator = new LearningQuestionGenerator(max => max - 1);
        var failure = await Assert.ThrowsAsync<LearningQuestionException>(() => generator.CreateAsync(
            "explode", "爆炸", LearningQuestionMode.New, (direction, _) =>
            {
                Assert.False(direction);
                calls++;
                return Task.FromResult<VocabularyQuestionModel?>(null);
            }, Ct));
        Assert.Equal(LearningQuestionFailure.Unavailable, failure.Failure);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FillLettersNeverInvokesChoiceGenerator()
    {
        var generator = new LearningQuestionGenerator(max => max - 1);
        var question = Assert.IsType<FillLettersLearningQuestion>(await generator.CreateAsync(
            "Explode", "爆炸", LearningQuestionMode.Review,
            (_, _) => throw new InvalidOperationException("Distractors must not be queried."), Ct));
        Assert.Equal("Explode", question.AnswerWord);
        Assert.Equal("爆炸", question.Prompt);
        Assert.Equal(3, question.Pattern.Count(item => item is null));
    }

    [Fact]
    public async Task FaultsAndCancellationNeverTryAnotherType()
    {
        foreach (var failure in new Exception[] { new InvalidOperationException("Storage failure"), new BusinessRuleException("Other rule"), new OperationCanceledException(Ct) })
        {
            var calls = 0;
            var generator = new LearningQuestionGenerator(_ => 0);
            var actual = await Assert.ThrowsAnyAsync<Exception>(() => generator.CreateAsync("explode", "爆炸", LearningQuestionMode.Review,
                (_, _) => { calls++; return Task.FromException<VocabularyQuestionModel?>(failure); }, Ct));
            Assert.Same(failure, actual);
            Assert.Equal(1, calls);
        }
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LearningQuestionGenerator().CreateAsync(
            "explode", "爆炸", LearningQuestionMode.Review, (_, _) => throw new InvalidOperationException(), cancelled.Token));
    }

    private static VocabularyQuestionModel Choice(bool direction) => new()
    {
        ChineseToEnglish = direction,
        Word = direction ? "目标" : "abcd",
        Options = Enumerable.Range(0, 4).Select(index => new VocabularyQuestionOptionModel { Text = index.ToString(), IsCorrect = index == 0 }).ToList()
    };
}
