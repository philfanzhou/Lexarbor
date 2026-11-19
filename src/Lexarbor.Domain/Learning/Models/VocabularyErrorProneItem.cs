namespace Lexarbor.Learning.Domain.Models;

public sealed record VocabularyErrorProneItem(string WordKey, string MeaningKey, string Word, string Meaning,
    bool SnapshotIncomplete, long AttemptCount, long ErrorCount, double? ErrorRate,
    VocabularyErrorProneItem.Direction EnglishToChinese, VocabularyErrorProneItem.Direction ChineseToEnglish, VocabularyErrorProneItem.Direction FillLetters,
    DateTimeOffset? LastAnsweredAt, long RecordedForgetCount, long? ForgetCount,
    DateTimeOffset? StateCoverageStart, DateTimeOffset? AnswerCoverageStart,
    long KnownStateLogCount, long UnknownStateLogCount, bool HistoryInsufficient, IReadOnlyList<string> Reasons)
{
    public Direction PartOfSpeechChoice { get; init; } = new(0, 0, null);
    public Direction FormChoice { get; init; } = new(0, 0, null);
    public Direction RelationChoice { get; init; } = new(0, 0, null);
    public Direction FormFill { get; init; } = new(0, 0, null);

    public sealed record Direction(long Attempts, long Errors, double? CorrectRate);
}
