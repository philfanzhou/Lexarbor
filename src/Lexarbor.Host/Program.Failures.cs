using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Services;
using Lexarbor.Host.Integration;
using ServiceMantle.Web;
using KestrelBadHttpRequestException = Microsoft.AspNetCore.Server.Kestrel.Core.BadHttpRequestException;

public partial class Program
{
    /// <summary>
    /// Business exceptions map to HTTP through the ServiceMantle Problem Details
    /// pipeline: a fixed type/title/status/errorCode (plus correlation id) per
    /// mapping, with no room for exception text, SQL or credentials to reach a
    /// response. The titles are deliberately generic — the per-case domain reasons
    /// stay in server logs, not in public responses. Endpoint-explicit failures
    /// keep the existing envelope; only exception-generated responses change shape.
    /// </summary>
    private static void AddExceptionMappings(ServiceMantleBuilder serviceMantle)
    {
        serviceMantle.AddExceptionMapping<DomainValidationException>(
            StatusCodes.Status400BadRequest, "vocabulary.validation", "The request is invalid.");
        serviceMantle.AddConditionalExceptionMapping<BadHttpRequestException>(
        [
            // Kestrel reports a body over the endpoint's size limit this way; ordered so
            // the 413 case wins before the generic bad-request fallback.
            new(413, "vocabulary.request_too_large", "The request body is too large.",
            exception => exception.StatusCode == StatusCodes.Status413PayloadTooLarge),
        new(400, "vocabulary.bad_request", "The request is invalid.")
        ]);
        // Kestrel rejects oversized bodies with its own derived BadHttpRequestException
        // whose exact type the registry matches separately from the public base class.
        // The type is marked obsolete in favor of the public base class, but Kestrel
        // still throws it, so the mapping targets it deliberately.
#pragma warning disable CS0618 // Type or member is obsolete
        serviceMantle.AddConditionalExceptionMapping<KestrelBadHttpRequestException>(
        [
            new(413, "vocabulary.request_too_large", "The request body is too large.",
            exception => exception.StatusCode == StatusCodes.Status413PayloadTooLarge),
        new(400, "vocabulary.bad_request", "The request is invalid.")
        ]);
#pragma warning restore CS0618 // Type or member is obsolete
        serviceMantle.AddExceptionMapping<ResourceNotFoundException>(
            StatusCodes.Status404NotFound, "vocabulary.not_found", "The requested resource was not found.");
        serviceMantle.AddExceptionMapping<ConflictException>(
            StatusCodes.Status409Conflict, "vocabulary.conflict", "The request conflicts with existing data.");
        serviceMantle.AddExceptionMapping<BusinessRuleException>(
            StatusCodes.Status422UnprocessableEntity, "vocabulary.business_rule", "The request violates a business rule.");
        serviceMantle.AddExceptionMapping<SubmissionsNotConfiguredException>(
            StatusCodes.Status503ServiceUnavailable, "vocabulary.submissions_not_configured",
            "Meaning submissions are not configured.");
        serviceMantle.AddExceptionMapping<AssistantUnavailableException>(
            StatusCodes.Status502BadGateway, "vocabulary.review_assistant_unavailable",
            "The review assistant did not return usable advice.");
        serviceMantle.AddConditionalExceptionMapping<StorageBusyException>(
        [
            // The one caller-retryable storage failure keeps its bounded Retry-After;
            // the library clamps the value to a safe range.
            new(503, "vocabulary.storage_busy", "The vocabulary database is temporarily busy.",
            retryAfterSeconds: 1)
        ]);
    }
}
