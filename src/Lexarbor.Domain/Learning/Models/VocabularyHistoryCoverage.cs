namespace Lexarbor.Learning.Domain.Models;

public sealed record VocabularyHistoryCoverage(DateTimeOffset? StateCoverageStart,
    DateTimeOffset? AnswerCoverageStart, long KnownStateLogCount, long UnknownStateLogCount)
{
    public bool IsInsufficient(DateTimeOffset windowStart) =>
        StateCoverageStart is null || StateCoverageStart > windowStart || UnknownStateLogCount > 0;
}
