using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Lexarbor.Service;

public static class MeaningSubmissionReviewAdminEndpoints
{
    public static void MapMeaningSubmissionReviewAdmin(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/meaning-submission-reviews").RequireAuthorization(AdminEndpointAuthorization.PolicyName);
        group.MapGet("", async ([FromQuery] int? page, [FromQuery] int? size, [FromQuery] string? verdict,
            [FromQuery] bool? @checked, [FromQuery] bool? applied, [FromQuery] string? recordId,
            IMeaningSubmissionReviewAdminRepository repository, CancellationToken token) =>
        {
            if ((page ?? 1) < 1 || (size ?? 20) is < 1 or > 100 || (long)((page ?? 1) - 1) * (size ?? 20) > int.MaxValue)
                return ApiResponse.BadRequest("Page must be positive and size must be between 1 and 100.");
            if (verdict is not (null or "accept" or "revise" or "reject"))
                return ApiResponse.BadRequest("Verdict must be accept, revise or reject.");
            if (recordId != null && (string.IsNullOrWhiteSpace(recordId) || recordId.Length > 100))
                return ApiResponse.BadRequest("Record ID must contain between 1 and 100 characters.");
            var query = new MeaningSubmissionReviewQuery(page ?? 1, size ?? 20, verdict, @checked, applied, recordId);
            var (items, total) = await repository.PageAsync(query, token);
            return ApiResponse.Ok(new { items = items.Select(MeaningSubmissionReviewDto.From), totalCount = total, page = query.Page, size = query.Size });
        });
        group.MapPost("/{recordId}/checked", async (string recordId, IMeaningSubmissionReviewAdminRepository repository, CancellationToken token) =>
            ApiResponse.Ok(await repository.CheckAsync(recordId, token)));
    }
}

/// <summary>Explicit allow-list: no attempt, runtime lease, staging input, configuration or provider payload.</summary>
public sealed record MeaningSubmissionReviewDto(
    string RecordId, string SubmissionId, string Source, string ReviewerKind,
    MeaningSubmissionOriginalDto Original, MeaningSubmissionAiResultDto Ai,
    bool Applied, bool Checked, DateTimeOffset? CheckedAt, string? ResultWordId)
{
    public static MeaningSubmissionReviewDto From(MeaningSubmissionReviewEntry row) => new(row.RecordId, row.SubmissionId, row.Source, "ai",
        new(row.Word, row.OriginalPartOfSpeech, row.OriginalMeaning, row.OriginalExample),
        new(row.Verdict, row.PartOfSpeech, row.Meaning, row.PhoneticUk, row.PhoneticUs, row.Comment, row.Model, row.ReviewedAt),
        row.Applied, row.CheckedAt != null, row.CheckedAt, row.ResultWordId);
}
public sealed record MeaningSubmissionOriginalDto(string Word, string? PartOfSpeech, string Meaning, string? Example);
public sealed record MeaningSubmissionAiResultDto(string Verdict, string? PartOfSpeech, string? Meaning, string? PhoneticUk,
    string? PhoneticUs, string? Comment, string Model, DateTimeOffset ReviewedAt);
