using System.Text.Json;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Lexarbor.Service.Dtos;
using Microsoft.AspNetCore.Http;

namespace Lexarbor.Service;

/// <summary>Local envelopes for the two new routes; legacy/global errors stay unchanged.</summary>
internal static class LearningQuestionEndpoints
{
    internal static async Task<IResult> Catalog(HttpRequest request, VocabularyDomainService service, CancellationToken cancellationToken)
    {
        var body = await ReadAsync<LearningQuestionRequest>(request, cancellationToken);
        if (body is null || !TryMode(body.Mode, out var mode)
            || string.IsNullOrWhiteSpace(body.WordId) || string.IsNullOrWhiteSpace(body.BookId)
            || string.IsNullOrWhiteSpace(body.MeaningId) || !TryRelation(body.Relation, out var relation)) return Invalid();
        return await Generate(() => service.CreateLearningQuestionAsync(
            body.WordId, body.BookId, body.MeaningId, mode, body.UnitId, body.SameEntryKind ?? false,
            cancellationToken, relation, body.EnablePartOfSpeechChoice == true));
    }

    internal static async Task<IResult> Submission(string id, HttpRequest request, MeaningSubmissionService service, CancellationToken cancellationToken)
    {
        if (!service.IsConfigured) return Error(503, "QUESTION_SERVICE_UNAVAILABLE", "The question service is not configured.");
        var body = await ReadAsync<SubmissionLearningQuestionRequest>(request, cancellationToken);
        if (body is null || !TryMode(body.Mode, out var mode)) return Invalid();
        return await Generate(() => service.CreateLearningQuestionAsync(id, mode, cancellationToken));
    }

    private static async Task<T?> ReadAsync<T>(HttpRequest request, CancellationToken token)
    {
        if (!request.HasJsonContentType()) return default;
        try { return await request.ReadFromJsonAsync<T>(token); }
        catch (JsonException) { return default; }
        catch (BadHttpRequestException) { return default; }
    }

    private static bool TryMode(string? value, out LearningQuestionMode mode)
    {
        mode = value == "review" ? LearningQuestionMode.Review : LearningQuestionMode.New;
        return value is "new" or "review";
    }

    private static bool TryRelation(JsonElement value, out LearningQuestionRelation? relation)
    {
        relation = null;
        if (value.ValueKind == JsonValueKind.Undefined) return true;
        if (value.ValueKind != JsonValueKind.Object) return false;

        try
        {
            relation = value.Deserialize<LearningQuestionRelation>(JsonSerializerOptions.Web);
            return relation != null && !string.IsNullOrWhiteSpace(relation.SourceWordId) && VocabularyRelationTypes.IsValid(relation.Type);
        }
        catch (JsonException) { return false; }
    }

    private static async Task<IResult> Generate(Func<Task<LearningQuestion>> create)
    {
        try { return VocabularyHttpResponse.Ok(LearningQuestionDto.From(await create())); }
        catch (ResourceNotFoundException)
        {
            return Error(404, "QUESTION_TARGET_NOT_FOUND", "The question target was not found.");
        }
        catch (LearningQuestionException exception) when (exception.Failure == LearningQuestionFailure.SourceUnavailable)
        {
            return Error(422, "QUESTION_SOURCE_UNAVAILABLE", "The question source is unavailable.");
        }
        catch (LearningQuestionException exception) when (exception.Failure == LearningQuestionFailure.Unavailable)
        {
            return Error(422, "QUESTION_UNAVAILABLE", "No allowed question type is available.");
        }
        catch (DomainValidationException) { return Invalid(); }
    }

    private static IResult Invalid() => Error(400, "QUESTION_INVALID_REQUEST", "A valid question request with mode new or review is required.");
    private static IResult Error(int status, string code, string message) => Results.Json(new { success = false, code, message }, statusCode: status);
}
