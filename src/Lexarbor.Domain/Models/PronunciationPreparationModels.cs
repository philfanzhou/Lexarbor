namespace Lexarbor.Domain.Models;

// Position kinds are explicit catalog data. Null means unclassified; an empty
// list means no positions. Neither spelling whitespace nor the model rewrites it.
public sealed record PronunciationMeaningContext(AudioMeaningInput Meaning, IReadOnlyList<string?> PositionKinds);
public sealed record PronunciationPreparationRequest(AudioWordInput Word, AudioMeaningInput? TargetMeaning,
    AudioLocale Locale, EnrichmentInputVersion InputVersion, IReadOnlyList<PronunciationMeaningContext> Meanings);

public enum PronunciationPreparationOutcome
{
    Ready, InheritsDefault, Waiting, Throttled, ServiceFailure, InvalidInput, InvalidPlan, Uncertain, Rejected, Superseded
}

public sealed record PronunciationPreparationDispatch(EnrichmentInputVersion InputVersion, string ConfigurationVersion);

// Server-only validated preparation output, not an API DTO. Content uses the
// existing bounded persistence plan. RequiredPhonetic is a mandatory TTS
// constraint, including in audio-only mode; Content.Phonetic is only a writable
// missing-default candidate. A provider adapter must reject constraints it cannot
// express. Never use the headword as fallback when preparation has no plan.
public sealed record PreparedPronunciation(EnrichmentPlan Content, AudioGenerationInput Input,
    EnrichmentInputVersion InputVersion, string ConfigurationVersion, string EntryKind,
    string ClassificationBasis, string? RequiredPhonetic);
public sealed record PronunciationPreparationResult(PronunciationPreparationOutcome Outcome,
    PreparedPronunciation? Plan = null, string? ErrorCode = null, bool RequestMade = false,
    TimeSpan? RetryAfter = null);
