using Lexarbor.Learning.Domain.Models;

namespace Lexarbor.Learning.Domain.Services;

public sealed class FsrsScheduler
{

    public static readonly IReadOnlyList<double> DefaultParameters =
    [
        0.212, 1.2931, 2.3065, 8.2956, 6.4133, 0.8334, 3.0194, 0.001, 1.8722,
        0.1666, 0.796, 1.4835, 0.0614, 0.2629, 1.6483, 0.6014, 1.8729, 0.5425,
        0.0912, 0.0658, 0.1542,
    ];

    private const double MinimumStability = 0.001;
    private const double MinimumDifficulty = 1.0;
    private const double MaximumDifficulty = 10.0;

    private readonly double[] _w = [.. DefaultParameters];
    private readonly double _decay;  // = -w[20]
    private readonly double _factor; // = 0.9^(1/decay) - 1

    public FsrsScheduler(VocabularyFsrsOptions options)
    {
        var failures = VocabularyFsrsOptions.Validate(options).ToList();
        if (failures.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", failures));
        }

        DesiredRetention = options.DesiredRetention;
        LearningSteps = [.. options.LearningStepsMinutes.Select(minutes => TimeSpan.FromMinutes(minutes))];
        RelearningSteps = [.. options.RelearningStepsMinutes.Select(minutes => TimeSpan.FromMinutes(minutes))];
        MaximumIntervalDays = options.MaximumIntervalDays;
        _decay = -_w[20];
        _factor = Math.Pow(0.9, 1.0 / _decay) - 1;
    }

    public double DesiredRetention { get; }

    public IReadOnlyList<TimeSpan> LearningSteps { get; }

    public IReadOnlyList<TimeSpan> RelearningSteps { get; }

    public int MaximumIntervalDays { get; }
    public (MemoryCardModel Card, MemoryReviewLogModel Log) Schedule(
        MemoryCardModel card,
        ReviewRating rating,
        DateTimeOffset now)
    {

        var wasNew = card.State == MemoryCardState.New;
        var state = wasNew ? MemoryCardState.Learning : card.State;
        var step = wasNew ? 0 : card.LearningStep;
        var stability = card.Stability;
        var difficulty = card.Difficulty;
        DateTimeOffset? lastReview = card.LastReviewedAt;
        var daysSinceLastReview = lastReview is null
            ? (int?)null
            : (int)Math.Floor((now - lastReview.Value).TotalDays);

        TimeSpan nextInterval;
        switch (state)
        {
            case MemoryCardState.Learning:
                {
                    if (stability is null || difficulty is null)
                    {
                        stability = InitialStability(rating);
                        difficulty = InitialDifficulty(rating, clamp: true);
                    }
                    else if (daysSinceLastReview is int days && days < 1)
                    {
                        stability = ShortTermStability(stability.Value, rating);
                        difficulty = NextDifficulty(difficulty.Value, rating);
                    }
                    else
                    {
                        stability = NextStability(difficulty.Value, stability.Value, Retrievability(stability.Value, lastReview, now), rating);
                        difficulty = NextDifficulty(difficulty.Value, rating);
                    }

                    if (LearningSteps.Count == 0 ||
                        (step >= LearningSteps.Count && rating != ReviewRating.Again))
                    {
                        state = MemoryCardState.Review;
                        step = null;
                        nextInterval = TimeSpan.FromDays(NextInterval(stability.Value));
                    }
                    else
                    {
                        switch (rating)
                        {
                            case ReviewRating.Again:
                                step = 0;
                                nextInterval = LearningSteps[0];
                                break;
                            case ReviewRating.Hard:

                                nextInterval = HardStepInterval(LearningSteps, step!.Value);
                                break;
                            case ReviewRating.Good:
                                if (step!.Value + 1 == LearningSteps.Count)
                                {
                                    state = MemoryCardState.Review;
                                    step = null;
                                    nextInterval = TimeSpan.FromDays(NextInterval(stability.Value));
                                }
                                else
                                {
                                    step += 1;
                                    nextInterval = LearningSteps[step.Value];
                                }

                                break;
                            case ReviewRating.Easy:
                                state = MemoryCardState.Review;
                                step = null;
                                nextInterval = TimeSpan.FromDays(NextInterval(stability.Value));
                                break;
                            default:
                                throw new ArgumentOutOfRangeException(nameof(rating), rating, "Unknown rating.");
                        }
                    }

                    break;
                }

            case MemoryCardState.Review:
                {
                    if (daysSinceLastReview is int days && days < 1)
                    {
                        stability = ShortTermStability(stability!.Value, rating);
                    }
                    else
                    {
                        stability = NextStability(difficulty!.Value, stability!.Value, Retrievability(stability.Value, lastReview, now), rating);
                    }

                    difficulty = NextDifficulty(difficulty!.Value, rating);

                    if (rating == ReviewRating.Again)
                    {
                        if (RelearningSteps.Count == 0)
                        {
                            nextInterval = TimeSpan.FromDays(NextInterval(stability.Value));
                        }
                        else
                        {
                            state = MemoryCardState.Relearning;
                            step = 0;
                            nextInterval = RelearningSteps[0];
                        }
                    }
                    else
                    {
                        nextInterval = TimeSpan.FromDays(NextInterval(stability.Value));
                    }

                    break;
                }

            case MemoryCardState.Relearning:
                {
                    if (daysSinceLastReview is int days && days < 1)
                    {
                        stability = ShortTermStability(stability!.Value, rating);
                        difficulty = NextDifficulty(difficulty!.Value, rating);
                    }
                    else
                    {
                        stability = NextStability(difficulty!.Value, stability!.Value, Retrievability(stability.Value, lastReview, now), rating);
                        difficulty = NextDifficulty(difficulty!.Value, rating);
                    }

                    if (RelearningSteps.Count == 0 ||
                        (step >= RelearningSteps.Count && rating != ReviewRating.Again))
                    {
                        state = MemoryCardState.Review;
                        step = null;
                        nextInterval = TimeSpan.FromDays(NextInterval(stability.Value));
                    }
                    else
                    {
                        switch (rating)
                        {
                            case ReviewRating.Again:
                                step = 0;
                                nextInterval = RelearningSteps[0];
                                break;
                            case ReviewRating.Hard:
                                nextInterval = HardStepInterval(RelearningSteps, step!.Value);
                                break;
                            case ReviewRating.Good:
                                if (step!.Value + 1 == RelearningSteps.Count)
                                {
                                    state = MemoryCardState.Review;
                                    step = null;
                                    nextInterval = TimeSpan.FromDays(NextInterval(stability.Value));
                                }
                                else
                                {
                                    step += 1;
                                    nextInterval = RelearningSteps[step.Value];
                                }

                                break;
                            case ReviewRating.Easy:
                                state = MemoryCardState.Review;
                                step = null;
                                nextInterval = TimeSpan.FromDays(NextInterval(stability.Value));
                                break;
                            default:
                                throw new ArgumentOutOfRangeException(nameof(rating), rating, "Unknown rating.");
                        }
                    }

                    break;
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(card), card.State, "Unknown card state.");
        }

        var updated = new MemoryCardModel
        {
            StudentId = card.StudentId,
            WordKey = card.WordKey,
            MeaningKey = card.MeaningKey,
            State = state,
            Stability = stability,
            Difficulty = difficulty,
            DueAt = now + nextInterval,
            Reps = card.Reps + 1,
            Lapses = card.Lapses + (rating == ReviewRating.Again && card.State == MemoryCardState.Review ? 1 : 0),
            LastReviewedAt = now,
            FirstLearnedAt = card.FirstLearnedAt,
            FirstLearnedPlanId = card.FirstLearnedPlanId,
            LearningStep = step,
            Version = card.Version,
        };

        var log = new MemoryReviewLogModel
        {
            StudentId = card.StudentId,
            WordKey = card.WordKey,
            MeaningKey = card.MeaningKey,
            Rating = rating,
            ReviewedAt = now,
            StateBefore = card.State,
            StateAfter = updated.State,
            StabilityBefore = card.Stability,
            DifficultyBefore = card.Difficulty,
            DueBefore = wasNew ? null : card.DueAt,
            StabilityAfter = updated.Stability!.Value,
            DifficultyAfter = updated.Difficulty!.Value,
            DueAfter = updated.DueAt,
            ElapsedDays = daysSinceLastReview ?? 0,
        };

        return (updated, log);
    }

    private static TimeSpan HardStepInterval(IReadOnlyList<TimeSpan> steps, int step) => step switch
    {
        0 when steps.Count == 1 => TimeSpan.FromTicks(steps[0].Ticks * 3 / 2),
        0 when steps.Count >= 2 => TimeSpan.FromTicks((steps[0].Ticks + steps[1].Ticks) / 2),
        _ => steps[step],
    };

    private double Retrievability(double stability, DateTimeOffset? lastReview, DateTimeOffset now)
    {
        if (lastReview is not { } review) return 0;
        var elapsedDays = Math.Max(0, Math.Floor((now - review).TotalDays));
        return Math.Pow(1 + _factor * elapsedDays / stability, _decay);
    }

    private double InitialStability(ReviewRating rating) =>
        Math.Max(_w[(int)rating - 1], MinimumStability);

    private double InitialDifficulty(ReviewRating rating, bool clamp)
    {
        var difficulty = _w[4] - Math.Exp(_w[5] * ((int)rating - 1)) + 1;
        return clamp ? ClampDifficulty(difficulty) : difficulty;
    }

    private static double ClampDifficulty(double difficulty) =>
        Math.Min(Math.Max(difficulty, MinimumDifficulty), MaximumDifficulty);

    private int NextInterval(double stability)
    {
        var interval = (stability / _factor) * (Math.Pow(DesiredRetention, 1.0 / _decay) - 1);
        var rounded = (int)Math.Round(interval, MidpointRounding.ToEven);
        return Math.Max(Math.Min(rounded, MaximumIntervalDays), 1);
    }

    private double ShortTermStability(double stability, ReviewRating rating)
    {
        var increase = Math.Exp(_w[17] * ((int)rating - 3 + _w[18])) * Math.Pow(stability, -_w[19]);
        if (rating is ReviewRating.Hard or ReviewRating.Good or ReviewRating.Easy)
        {
            increase = Math.Max(increase, 1.0);
        }

        return Math.Max(stability * increase, MinimumStability);
    }

    private double NextDifficulty(double difficulty, ReviewRating rating)
    {
        var arg1 = InitialDifficulty(ReviewRating.Easy, clamp: false);
        var deltaDifficulty = -_w[6] * ((int)rating - 3);
        var arg2 = difficulty + (10.0 - difficulty) * deltaDifficulty / 9.0;
        return ClampDifficulty(_w[7] * arg1 + (1 - _w[7]) * arg2);
    }

    private double NextStability(double difficulty, double stability, double retrievability, ReviewRating rating)
    {
        double next;
        if (rating == ReviewRating.Again)
        {
            var longTerm = _w[11]
                * Math.Pow(difficulty, -_w[12])
                * (Math.Pow(stability + 1, _w[13]) - 1)
                * Math.Exp((1 - retrievability) * _w[14]);
            var shortTermCap = stability / Math.Exp(_w[17] * _w[18]);
            next = Math.Min(longTerm, shortTermCap);
        }
        else
        {
            var hardPenalty = rating == ReviewRating.Hard ? _w[15] : 1;
            var easyBonus = rating == ReviewRating.Easy ? _w[16] : 1;
            next = stability
                * (1
                    + Math.Exp(_w[8])
                    * (11 - difficulty)
                    * Math.Pow(stability, -_w[9])
                    * (Math.Exp((1 - retrievability) * _w[10]) - 1)
                    * hardPenalty
                    * easyBonus);
        }

        return Math.Max(next, MinimumStability);
    }
}
