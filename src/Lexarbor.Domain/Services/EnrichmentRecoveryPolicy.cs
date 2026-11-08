namespace Lexarbor.Domain.Services;

public static class EnrichmentRecoveryPolicy
{
    public static readonly string[] Errors = [
        "batch.timeout", "batch.response_too_large", "batch.invalid_response", "batch.incomplete_response", "batch.unknown_id",
        "expansion.invalid_response", "expansion.timeout", "text.invalid_plan", "text.response_too_large",
        "text.pronunciation_control_unsupported", "text.pronunciation_text_mismatch", "text.template_unexpanded",
        "tts.capability_unsupported", "tts.prepared_plan_required"];
    public static bool CanRetry(string? code) => code != null && Errors.Contains(code, StringComparer.Ordinal);
}
