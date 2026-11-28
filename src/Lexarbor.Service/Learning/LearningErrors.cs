using Lexarbor.Learning.Domain.Services;
using Lexarbor.Service;
using Microsoft.AspNetCore.Http;
using static Lexarbor.Learning.Service.VocabularyCatalogErrorCodes;

namespace Lexarbor.Learning.Service;

/// <summary>The failure responses more than one learning endpoint answers with.</summary>
internal static class LearningErrors
{
    /// <summary>
    /// The failure for a plan exception or a catalog error, or null when <paramref name="error"/>
    /// is neither, so the calling endpoint's own mapping continues.
    /// </summary>
    internal static IResult? PlanOrCatalogFailure(Exception error) => error switch
    {
        VocabPlanNotFoundException => ApiResponse.Failure(404, "VOCAB_PLAN_NOT_FOUND", error.Message),
        VocabPlanActiveExistsException => ApiResponse.Failure(409, "VOCAB_PLAN_ACTIVE_EXISTS", error.Message),
        VocabPlanVersionConflictException => ApiResponse.Failure(409, "VOCAB_PLAN_VERSION_CONFLICT", error.Message),
        VocabPlanNotActiveException => ApiResponse.Failure(409, "VOCAB_PLAN_NOT_ACTIVE", error.Message),
        VocabPlanForbiddenException => ApiResponse.Failure(403, "VOCAB_PLAN_FORBIDDEN", error.Message),
        VocabPlanScopeEmptyException => ApiResponse.Failure(400, "VOCAB_PLAN_SCOPE_EMPTY", error.Message),
        CatalogException catalog => catalog.ErrorCode switch
        {
            BookNotFound => ApiResponse.Failure(404, catalog.ErrorCode, catalog.Message),
            BookDisabled => ApiResponse.Failure(422, catalog.ErrorCode, catalog.Message),
            UnitNotFound => ApiResponse.Failure(404, catalog.ErrorCode, catalog.Message),
            _ => ApiResponse.Failure(503, catalog.ErrorCode, catalog.Message),
        },
        _ => null
    };
}
