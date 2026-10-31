using Lexarbor.Domain.Models;

namespace Lexarbor.Service.Dtos;

public sealed class MeaningSubmissionCreateRequest
{
    public string? Word { get; set; }
    public string? PartOfSpeech { get; set; }
    public string? Meaning { get; set; }
    public string? Example { get; set; }
}

public sealed class MeaningSubmissionLookupRequest
{
    public List<string>? Ids { get; set; }
}

public sealed class MeaningSubmissionQuestionRequest
{
    public bool? ChineseToEnglish { get; set; }
}

public sealed class MeaningSubmissionApproveRequest
{
    public string? Word { get; set; }
    public string? PartOfSpeech { get; set; }
    public string? Meaning { get; set; }
    public string? Example { get; set; }
    public string? PhoneticUk { get; set; }
    public string? PhoneticUs { get; set; }
}

/// <summary>
/// One submission as both surfaces read it. <c>status</c> is <c>pending</c>,
/// <c>approved</c> or <c>rejected</c>; the <c>result*</c> fields are set only
/// once approved. <c>definitionKept</c> is true when the approved spelling and
/// definition equal the submitted ones, ignoring case and surrounding whitespace.
/// </summary>
public sealed class MeaningSubmissionDto
{
    public string Id { get; set; } = string.Empty;
    public string Word { get; set; } = string.Empty;
    public string? PartOfSpeech { get; set; }
    public string Meaning { get; set; } = string.Empty;
    public string? Example { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ResultBookId { get; set; }
    public string? ResultWordId { get; set; }
    public string? ResultMeaningId { get; set; }
    public string? ResultWord { get; set; }
    public string? ResultPartOfSpeech { get; set; }
    public string? ResultMeaning { get; set; }
    public bool DefinitionKept { get; set; }

    /// <summary>The catalog keys of the submitted text (<c>lower(trim(...))</c>, ASCII folding).</summary>
    public string WordKey { get; set; } = string.Empty;

    public string MeaningKey { get; set; } = string.Empty;

    /// <summary>The catalog keys of the approved text; set only when approved.</summary>
    public string? ResultWordKey { get; set; }

    public string? ResultMeaningKey { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }

    public static MeaningSubmissionDto From(MeaningSubmissionModel model) => new()
    {
        Id = model.Id,
        Word = model.Word,
        PartOfSpeech = model.PartOfSpeech,
        Meaning = model.Meaning,
        Example = model.Example,
        Status = StatusText(model.Status),
        ResultBookId = model.ResultBookId,
        ResultWordId = model.ResultWordId,
        ResultMeaningId = model.ResultMeaningId,
        ResultWord = model.ResultWord,
        ResultPartOfSpeech = model.ResultPartOfSpeech,
        ResultMeaning = model.ResultMeaning,
        DefinitionKept = model.DefinitionKept,
        WordKey = CatalogKeys.Of(model.Word),
        MeaningKey = CatalogKeys.Of(model.Meaning),
        ResultWordKey = model.ResultWord == null ? null : CatalogKeys.Of(model.ResultWord),
        ResultMeaningKey = model.ResultMeaning == null ? null : CatalogKeys.Of(model.ResultMeaning),
        CreatedAt = model.CreatedAt,
        ReviewedAt = model.ReviewedAt
    };

    public static string StatusText(MeaningSubmissionStatus status) => status switch
    {
        MeaningSubmissionStatus.Approved => "approved",
        MeaningSubmissionStatus.Rejected => "rejected",
        _ => "pending"
    };
}

/// <summary>The administrator view adds the submitting client's subject.</summary>
public sealed class MeaningSubmissionAdminDto
{
    public MeaningSubmissionDto Submission { get; set; } = new();
    public string Source { get; set; } = string.Empty;
}
