using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Services;

public static class EnrichmentRecoveryPolicy
{
    public static readonly string[] Errors = [
        "batch.timeout", "batch.response_too_large", "batch.invalid_response", "batch.incomplete_response", "batch.unknown_id",
        "expansion.invalid_response", "expansion.timeout", "text.invalid_plan", "text.response_too_large",
        "text.pronunciation_control_unsupported", "text.pronunciation_text_mismatch", "text.template_unexpanded",
        "text.phonetic_constraint_mismatch", "text.pronunciation_uncertain",
        "tts.capability_unsupported", "tts.prepared_plan_required"];
    // A successful preparation may omit the writable field candidate. That is a
    // step-level gap, not a supplier call failure: one bounded retry re-prepares
    // the target, while a repeated omission stays with the administrator.
    public static readonly string[] FieldStepErrors = ["fields.candidates_missing"];
    public static readonly string[] Recoverable = [.. Errors, .. FieldStepErrors];
    public static bool CanRetry(string? code) => code != null && Errors.Contains(code, StringComparer.Ordinal);

    public static EnrichmentCallResult Failure(string outcome, string? code) => outcome == "Throttled"
        ? EnrichmentCallResult.Throttled
        : outcome is "Rejected" or "InvalidInput" || outcome == "InvalidPlan"
            && (!CanRetry(code) || code!.StartsWith("tts.", StringComparison.Ordinal))
            ? EnrichmentCallResult.TerminalFailure : EnrichmentCallResult.TransientFailure;

    // Receipts are scoped to a materially different repair strategy, not a
    // process start or release number. Earlier exhausted attempts may try this
    // strategy once; subsequent restarts never replenish it.
    public const string ReceiptSuffix = ".repair-v3";

    public static string Correction(string? code) => code switch
    {
        "text.phonetic_constraint_mismatch" => "Use the supplied complete fixedPhonetic verbatim. Preserve the required reading; do not regenerate it or add an unsolicited phonetic candidate.",
        "text.pronunciation_text_mismatch" or "text.template_unexpanded" => "Preserve lexical content, expand only supported abbreviations or teaching templates, and provide IPA for the complete spoken text.",
        "text.pronunciation_uncertain" => "Recheck the supplied meanings and explicit entry kind. Select a reliable reading only; do not invent one to bypass uncertainty.",
        _ => "Return only the requested compact JSON. Recheck every required field, pronunciation control and writable permission."
    };
}
