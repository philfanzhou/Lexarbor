namespace Lexarbor.Learning.Domain.Models;

// Internal query projection. Private answer identifiers never enter response models.
public sealed record VocabularyErrorProneRow(string WordKey, string MeaningKey, double? Stability,
    long AttemptCount, long ErrorCount, long EnglishAttempts, long EnglishErrors,
    long ChineseAttempts, long ChineseErrors, long FillAttempts, long FillErrors, DateTimeOffset? LastAnsweredAt,
    long RecordedForgetCount, VocabularyHistoryCoverage Coverage,
    int? SnapshotDirection, string? Prompt, string? OptionsSnapshot, Guid? CorrectOptionId, string? AnswerWord,
    long PartOfSpeechAttempts = 0, long PartOfSpeechErrors = 0,
    long FormChoiceAttempts = 0, long FormChoiceErrors = 0,
    long RelationChoiceAttempts = 0, long RelationChoiceErrors = 0,
    long FormFillAttempts = 0, long FormFillErrors = 0);
