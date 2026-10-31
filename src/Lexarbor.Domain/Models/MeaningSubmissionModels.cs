namespace Lexarbor.Domain.Models;

public enum MeaningSubmissionStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2
}

/// <summary>
/// A definition an integration client proposed for the catalog. It belongs to no
/// book until an administrator approves it; approval writes the reviewed
/// definition into the supplementary book and records where it landed.
/// </summary>
public class MeaningSubmissionModel
{
    public string Id { get; set; } = string.Empty;

    /// <summary>The verified subject of the integration client that submitted it.</summary>
    public string Source { get; set; } = string.Empty;

    public string Word { get; set; } = string.Empty;
    public string? PartOfSpeech { get; set; }
    public string Meaning { get; set; } = string.Empty;
    public string? Example { get; set; }
    public MeaningSubmissionStatus Status { get; set; }
    public string? ResultBookId { get; set; }
    public string? ResultWordId { get; set; }
    public string? ResultMeaningId { get; set; }

    /// <summary>The approved spelling as stored; it may correct <see cref="Word"/>.</summary>
    public string? ResultWord { get; set; }

    /// <summary>
    /// The approved definition as the administrator stored it. It may differ from
    /// <see cref="Meaning"/>, which always keeps what the client submitted.
    /// </summary>
    public string? ResultMeaning { get; set; }

    public string? ResultPartOfSpeech { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }

    /// <summary>
    /// True when the submission was approved with the spelling and definition the
    /// client submitted, compared case- and surrounding-whitespace-insensitively.
    /// A client that keys its own records by that text can keep using them.
    /// </summary>
    public bool DefinitionKept =>
        Status == MeaningSubmissionStatus.Approved
        && ResultMeaning != null
        && ResultWord != null
        && string.Equals(Normalize(ResultWord), Normalize(Word), StringComparison.Ordinal)
        && string.Equals(Normalize(ResultMeaning), Normalize(Meaning), StringComparison.Ordinal);

    private static string Normalize(string value) => CatalogKeys.Of(value);
}

/// <summary>
/// The comparison key the public entries read calls <c>normalizedWord</c> and
/// <c>meaningKey</c>: SQLite's <c>lower(trim(text))</c>, which folds ASCII letters
/// only. Computed in process for text that is not stored yet, so a client keying
/// its records by these values sees one rule.
/// </summary>
public static class CatalogKeys
{
    public static string Of(string text)
    {
        var trimmed = text.Trim(' ');
        return string.Create(trimmed.Length, trimmed, static (span, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                var character = source[index];
                span[index] = character is >= 'A' and <= 'Z' ? (char)(character + 32) : character;
            }
        });
    }
}

/// <summary>One meaning of a word in an enabled book, as the catalog search answers it.</summary>
public sealed record CatalogMeaningMatch(
    string WordId,
    string Word,
    string WordKey,
    string? PhoneticUk,
    string? PhoneticUs,
    string BookId,
    string BookName,
    string MeaningId,
    string? PartOfSpeech,
    string Meaning,
    string MeaningKey);

/// <summary>The definition an administrator approves, possibly corrected.</summary>
public sealed record MeaningSubmissionApproval(
    string Word,
    string? PartOfSpeech,
    string Meaning,
    string? Example,
    string? PhoneticUk,
    string? PhoneticUs);

/// <summary>
/// An assistant's advice on one submission. The verdict is one of
/// <c>accept</c>, <c>revise</c> or <c>reject</c>; the administrator decides.
/// </summary>
public sealed record MeaningSubmissionReviewAdvice(
    string Verdict,
    string? PartOfSpeech,
    string? Meaning,
    string? PhoneticUk,
    string? PhoneticUs,
    string? Comment);

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
