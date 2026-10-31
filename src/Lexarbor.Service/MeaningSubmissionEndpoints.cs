using System.Security.Claims;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Lexarbor.Service.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Lexarbor.Service;

/// <summary>
/// The integration surface (<c>/integration/meaning-submissions</c>, service
/// tokens with the <c>meaning-submissions</c> scope) and the administrator review
/// surface (<c>/admin/meaning-submissions</c>).
/// </summary>
public static class MeaningSubmissionEndpoints
{
    /// <summary>The policy the Host binds to the integration authentication scheme.</summary>
    public const string IntegrationPolicyName = "Lexarbor.Integration.MeaningSubmissions";

    public const string IntegrationScope = "meaning-submissions";

    public static void MapMeaningSubmissionIntegrationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/integration/meaning-submissions").RequireAuthorization(IntegrationPolicyName);
        group.MapPost("", async ([FromBody] MeaningSubmissionCreateRequest request, ClaimsPrincipal user,
            MeaningSubmissionService service, CancellationToken cancellationToken) =>
        {
            if (!service.IsConfigured) return NotConfigured();
            var source = user.FindFirst("sub")?.Value ?? string.Empty;
            var submission = await service.SubmitAsync(
                source, request.Word, request.PartOfSpeech, request.Meaning, request.Example, cancellationToken);
            return VocabularyHttpResponse.Ok(MeaningSubmissionDto.From(submission));
        });
        group.MapPost("/lookup", async ([FromBody] MeaningSubmissionLookupRequest request,
            MeaningSubmissionService service, CancellationToken cancellationToken) =>
        {
            var items = await service.LookupAsync(request.Ids, cancellationToken);
            return VocabularyHttpResponse.Ok(new { items = items.Select(MeaningSubmissionDto.From) });
        });
        group.MapPost("/{id}/question", async (string id, [FromBody] MeaningSubmissionQuestionRequest request,
            MeaningSubmissionService service, CancellationToken cancellationToken) =>
        {
            var chineseToEnglish = request.ChineseToEnglish ?? Random.Shared.Next(2) == 0;
            var question = await service.CreateQuestionAsync(id, chineseToEnglish, cancellationToken);
            return VocabularyHttpResponse.Ok(new QuestionResponse
            {
                Word = question.Word,
                WordId = question.WordId,
                MeaningId = question.MeaningId,
                ChineseToEnglish = question.ChineseToEnglish,
                Options = question.Options.Select(option => new OptionDto
                {
                    Meaning = option.Text,
                    IsCorrect = option.IsCorrect,
                    WordId = option.WordId,
                    MeaningId = option.MeaningId
                }).ToList()
            });
        });
    }

    public static void MapMeaningSubmissionAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/meaning-submissions").RequireAuthorization(AdminEndpointAuthorization.PolicyName);
        group.MapGet("", async ([FromQuery] string? status, [FromQuery] int? page, [FromQuery] int? size,
            MeaningSubmissionService service, CancellationToken cancellationToken) =>
        {
            var parsed = status?.Trim().ToLowerInvariant() switch
            {
                null or "" or "pending" => MeaningSubmissionStatus.Pending,
                "approved" => MeaningSubmissionStatus.Approved,
                "rejected" => MeaningSubmissionStatus.Rejected,
                _ => (MeaningSubmissionStatus?)null
            };
            if (parsed is null) return VocabularyHttpResponse.BadRequest("Status must be pending, approved or rejected.");
            var pageSize = Math.Clamp(size ?? 20, 1, 100);
            var (items, total) = await service.PageAsync(parsed.Value, page ?? 1, pageSize, cancellationToken);
            return VocabularyHttpResponse.Ok(new
            {
                configured = service.IsConfigured,
                bookId = service.BookId,
                assistantEnabled = service.ReviewAssistantEnabled,
                items = items.Select(item => new MeaningSubmissionAdminDto
                {
                    Submission = MeaningSubmissionDto.From(item),
                    Source = item.Source
                }),
                totalCount = total,
                totalPage = (int)Math.Ceiling(total / (double)pageSize)
            });
        });
        group.MapPost("/{id}/approve", async (string id, [FromBody] MeaningSubmissionApproveRequest request,
            MeaningSubmissionService service, CancellationToken cancellationToken) =>
        {
            if (!service.IsConfigured) return NotConfigured();
            var submission = await service.ApproveAsync(id, new MeaningSubmissionApproval(
                request.Word ?? string.Empty, request.PartOfSpeech, request.Meaning ?? string.Empty,
                request.Example, request.PhoneticUk, request.PhoneticUs), cancellationToken);
            return VocabularyHttpResponse.Ok(MeaningSubmissionDto.From(submission));
        });
        group.MapPost("/{id}/reject", async (string id, MeaningSubmissionService service, CancellationToken cancellationToken) =>
            VocabularyHttpResponse.Ok(MeaningSubmissionDto.From(await service.RejectAsync(id, cancellationToken))));
        group.MapPost("/{id}/advice", async (string id, MeaningSubmissionService service, CancellationToken cancellationToken) =>
            VocabularyHttpResponse.Ok(await service.AdviseAsync(id, cancellationToken)));
    }

    private static IResult NotConfigured() =>
        VocabularyHttpResponse.ServiceUnavailable("Meaning submissions are not configured.");
}
