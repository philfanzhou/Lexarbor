namespace Lexarbor.Domain.Models;

public enum PronunciationSpeechOutcome
{
    Ready, Waiting, Throttled, ServiceFailure, InvalidPlan, InvalidAudio, Rejected, Superseded
}

// Server-only output. Never persist supplier bodies or serialize audio into task
// history. RequestMade counts attempted dispatch, independently of retry budgets.
public sealed record PronunciationSpeechResult(PronunciationSpeechOutcome Outcome,
    byte[]? Audio = null, string? ErrorCode = null, bool RequestMade = false, TimeSpan? RetryAfter = null);
public sealed record PronunciationSpeechDispatch(EnrichmentInputVersion InputVersion,
    string TextConfigurationVersion, string TtsConfigurationVersion);
