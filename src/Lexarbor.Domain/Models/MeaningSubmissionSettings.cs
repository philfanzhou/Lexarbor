namespace Lexarbor.Domain.Models;

/// <summary>Deployment settings of the submission feature.</summary>
public sealed class MeaningSubmissionSettings
{
    /// <summary>
    /// The supplementary book approved submissions are written into. Unset turns
    /// the feature off: submissions are refused and approval is impossible.
    /// </summary>
    public string? BookId { get; init; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BookId);
}
