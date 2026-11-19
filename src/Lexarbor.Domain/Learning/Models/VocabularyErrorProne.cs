namespace Lexarbor.Learning.Domain.Models;

public sealed record VocabularyErrorProne(Guid StudentId, DateTimeOffset RequestNow, DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd, int Page, int Size, int Total, VocabularyErrorProne.ThresholdsDto Thresholds,
    DateTimeOffset? StateCoverageStart, DateTimeOffset? AnswerCoverageStart,
    long KnownStateLogCount, long UnknownStateLogCount, bool HistoryInsufficient,
    IReadOnlyList<VocabularyErrorProneItem> Items)
{
    public sealed record ThresholdsDto(int WindowDays, int MinimumAttempts, double ErrorRateThreshold, int MinimumForgetCount);
}
