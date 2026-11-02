namespace Lexarbor.Domain.Models;

/// <summary>Administrator projection of permanent history, never the automatic worker's staging input.</summary>
public sealed record MeaningSubmissionReviewEntry(
    string RecordId, string SubmissionId, string Source,
    string Word, string? OriginalPartOfSpeech, string OriginalMeaning, string? OriginalExample,
    string Verdict, string? PartOfSpeech, string? Meaning, string? PhoneticUk, string? PhoneticUs,
    string? Comment, string Model, DateTimeOffset ReviewedAt, bool Applied,
    DateTimeOffset? CheckedAt, string? ResultWordId);

public sealed record MeaningSubmissionReviewQuery(int Page = 1, int Size = 20,
    string? Verdict = null, bool? Checked = null, bool? Applied = null, string? RecordId = null);

public sealed record MeaningSubmissionReviewCheck(string RecordId, DateTimeOffset CheckedAt);
