using System.Reflection;
using System.Text.Json;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Services;
using Xunit;

namespace Lexarbor.Domain.Tests.Learning;

public sealed class FsrsSchedulerTests
{
    private const double Tolerance = 1e-6;
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static List<ReferenceCase>? _cases;

    private sealed record ReferenceCase(
        string Name,
        ReviewRating Rating,
        DateTimeOffset Now,
        MemoryCard Input,
        MemoryCardState ExpectedState,
        int? ExpectedStep,
        double ExpectedStability,
        double ExpectedDifficulty,
        DateTimeOffset ExpectedDue,
        int ExpectedReps,
        int ExpectedLapses);

    [Fact]
    public void ScheduleMatchesPyFsrsReferenceWithinTolerance()
    {
        var scheduler = new FsrsScheduler(new VocabularyFsrsOptions());
        foreach (var reference in LoadCases())
        {
            var (actual, log) = scheduler.Schedule(reference.Input, reference.Rating, reference.Now);
            Assert.Equal(reference.Input.State, log.StateBefore);
            Assert.Equal(actual.State, log.StateAfter);

            Assert.True(
                actual.Stability is { } stability && Math.Abs(stability - reference.ExpectedStability) <= Tolerance,
                $"{reference.Name}: stability {actual.Stability} != {reference.ExpectedStability}");
            Assert.True(
                actual.Difficulty is { } difficulty && Math.Abs(difficulty - reference.ExpectedDifficulty) <= Tolerance,
                $"{reference.Name}: difficulty {actual.Difficulty} != {reference.ExpectedDifficulty}");
            Assert.True(actual.State == reference.ExpectedState,
                $"{reference.Name}: state {actual.State} != {reference.ExpectedState}");
            Assert.True(actual.LearningStep == reference.ExpectedStep,
                $"{reference.Name}: step {actual.LearningStep} != {reference.ExpectedStep}");
            Assert.True(actual.Reps == reference.ExpectedReps,
                $"{reference.Name}: reps {actual.Reps} != {reference.ExpectedReps}");
            Assert.True(actual.Lapses == reference.ExpectedLapses,
                $"{reference.Name}: lapses {actual.Lapses} != {reference.ExpectedLapses}");
            Assert.True(
                Math.Abs((actual.DueAt - reference.ExpectedDue).Ticks) <= TimeSpan.TicksPerSecond,
                $"{reference.Name}: due {actual.DueAt:O} != {reference.ExpectedDue:O}");
            Assert.Equal(reference.Now, actual.LastReviewedAt);
        }
    }

    [Fact]
    public void ReferenceFixtureHasAtLeastTwentyCases()
    {
        Assert.True(LoadCases().Count >= 20, "fsrs_vectors.json must carry at least 20 reference cases.");
    }

    [Fact]
    public void NewCardFirstAgainRepeatsFirstStepAndFirstGoodAdvances()
    {
        var scheduler = new FsrsScheduler(new VocabularyFsrsOptions());

        var (afterAgain, _) = scheduler.Schedule(NewCard(), ReviewRating.Again, Base);
        Assert.Equal(MemoryCardState.Learning, afterAgain.State);
        Assert.Equal(0, afterAgain.LearningStep);
        Assert.Equal(Base + TimeSpan.FromMinutes(1), afterAgain.DueAt);
        Assert.Equal(1, afterAgain.Reps);
        Assert.Equal(0, afterAgain.Lapses);

        var (afterGood, _) = scheduler.Schedule(NewCard(), ReviewRating.Good, Base);
        Assert.Equal(MemoryCardState.Learning, afterGood.State);
        Assert.Equal(1, afterGood.LearningStep);
        Assert.Equal(Base + TimeSpan.FromMinutes(10), afterGood.DueAt);
    }

    [Fact]
    public void ReviewCardForgetMovesToRelearningAndCountsLapse()
    {
        var scheduler = new FsrsScheduler(new VocabularyFsrsOptions());
        var now = Base;
        var (card, _) = scheduler.Schedule(NewCard(), ReviewRating.Good, now);   // Learning step 1
        now += TimeSpan.FromMinutes(1);
        (card, _) = scheduler.Schedule(card, ReviewRating.Good, now);            // → Review
        Assert.Equal(MemoryCardState.Review, card.State);

        now += TimeSpan.FromDays(3);
        var (forgotten, log) = scheduler.Schedule(card, ReviewRating.Again, now);
        Assert.Equal(MemoryCardState.Relearning, forgotten.State);
        Assert.Equal(card.Lapses + 1, forgotten.Lapses);
        Assert.Equal(card.Reps + 1, forgotten.Reps);
        Assert.Equal(now + TimeSpan.FromMinutes(10), forgotten.DueAt);
        Assert.Equal(3, log.ElapsedDays);
    }

    [Fact]
    public void ReviewCardHardIntervalIsNotLongerThanGood()
    {
        var scheduler = new FsrsScheduler(new VocabularyFsrsOptions());
        var now = Base;
        var (card, _) = scheduler.Schedule(NewCard(), ReviewRating.Good, now);
        now += TimeSpan.FromMinutes(1);
        (card, _) = scheduler.Schedule(card, ReviewRating.Good, now);
        Assert.Equal(MemoryCardState.Review, card.State);

        var reviewAt = now + TimeSpan.FromDays(5);
        var (hard, _) = scheduler.Schedule(card, ReviewRating.Hard, reviewAt);
        var (good, _) = scheduler.Schedule(card, ReviewRating.Good, reviewAt);
        Assert.True(hard.DueAt <= good.DueAt, $"Hard due {hard.DueAt:O} must not exceed Good due {good.DueAt:O}");
    }

    [Theory]
    [InlineData(MemoryCardState.New, false, MemoryCardState.Learning)]
    [InlineData(MemoryCardState.New, true, MemoryCardState.Review)]
    [InlineData(MemoryCardState.Learning, false, MemoryCardState.Learning)]
    [InlineData(MemoryCardState.Learning, true, MemoryCardState.Review)]
    [InlineData(MemoryCardState.Review, false, MemoryCardState.Relearning)]
    [InlineData(MemoryCardState.Review, true, MemoryCardState.Review)]
    [InlineData(MemoryCardState.Relearning, false, MemoryCardState.Relearning)]
    [InlineData(MemoryCardState.Relearning, true, MemoryCardState.Review)]
    public void LogCapturesActualStatesIncludingEmptySteps(MemoryCardState before, bool emptySteps, MemoryCardState after)
    {
        var input = NewCard();
        input.State = before;
        if (before != MemoryCardState.New)
        {
            input.Stability = 5;
            input.Difficulty = 5;
            input.LastReviewedAt = Base - TimeSpan.FromDays(3);
            input.LearningStep = before == MemoryCardState.Review ? null : 0;
        }
        var original = JsonSerializer.Serialize(input);
        var options = new VocabularyFsrsOptions();
        if (emptySteps)
        {
            options.LearningStepsMinutes = [];
            options.RelearningStepsMinutes = [];
        }
        var (card, log) = new FsrsScheduler(options).Schedule(input, ReviewRating.Again, Base);
        Assert.Equal(before, log.StateBefore);
        Assert.Equal(after, card.State);
        Assert.Equal(after, log.StateAfter);
        Assert.Equal(original, JsonSerializer.Serialize(input));
    }

    private static MemoryCard NewCard() => new()
    {
        StudentId = Guid.NewGuid(),
        WordKey = "apple",
        MeaningKey = "苹果",
        State = MemoryCardState.New,
        DueAt = Base,
        FirstLearnedAt = Base,
    };

    private static List<ReferenceCase> LoadCases()
    {
        if (_cases is not null) return _cases;

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("fsrs_vectors.json")
            ?? throw new InvalidOperationException("Embedded resource fsrs_vectors.json is missing.");
        using var document = JsonDocument.Parse(stream);

        var defaults = new VocabularyFsrsOptions();
        Assert.Equal(0.9, defaults.DesiredRetention);
        Assert.Equal(new[] { 1, 10 }, defaults.LearningStepsMinutes);
        Assert.Equal(new[] { 10 }, defaults.RelearningStepsMinutes);

        var cases = new List<ReferenceCase>();
        foreach (var element in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var card = element.GetProperty("card");
            var expected = element.GetProperty("expected");
            var stability = card.GetProperty("stability");
            var state = stability.ValueKind == JsonValueKind.Null
                ? MemoryCardState.New
                : (MemoryCardState)card.GetProperty("state").GetInt32();
            var rating = (ReviewRating)element.GetProperty("rating").GetInt32();
            var expectedReps = expected.GetProperty("reps").GetInt32();
            var expectedLapses = expected.GetProperty("lapses").GetInt32();
            var inputLapses = expectedLapses -
                (state == MemoryCardState.Review && rating == ReviewRating.Again ? 1 : 0);

            var input = new MemoryCard
            {
                StudentId = Guid.NewGuid(),
                WordKey = "vector",
                MeaningKey = "词义",
                State = state,
                Stability = stability.ValueKind == JsonValueKind.Null ? null : stability.GetDouble(),
                Difficulty = card.GetProperty("difficulty").ValueKind == JsonValueKind.Null
                    ? null
                    : card.GetProperty("difficulty").GetDouble(),
                DueAt = card.GetProperty("due").GetDateTimeOffset(),
                Reps = expectedReps - 1,
                Lapses = inputLapses,
                LastReviewedAt = card.GetProperty("last_review").ValueKind == JsonValueKind.Null
                    ? null
                    : card.GetProperty("last_review").GetDateTimeOffset(),
                FirstLearnedAt = Base,
                LearningStep = card.GetProperty("step").ValueKind == JsonValueKind.Null
                    ? null
                    : card.GetProperty("step").GetInt32(),
            };

            cases.Add(new ReferenceCase(
                element.GetProperty("name").GetString()!,
                rating,
                element.GetProperty("review_datetime").GetDateTimeOffset(),
                input,
                (MemoryCardState)expected.GetProperty("state").GetInt32(),
                expected.GetProperty("step").ValueKind == JsonValueKind.Null
                    ? null
                    : expected.GetProperty("step").GetInt32(),
                expected.GetProperty("stability").GetDouble(),
                expected.GetProperty("difficulty").GetDouble(),
                expected.GetProperty("due").GetDateTimeOffset(),
                expectedReps,
                expectedLapses));
        }

        _cases = cases;
        return cases;
    }
}
