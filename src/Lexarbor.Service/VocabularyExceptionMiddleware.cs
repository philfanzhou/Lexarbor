using Lexarbor.Domain.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Lexarbor.Service;

public sealed class VocabularyExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<VocabularyExceptionMiddleware> _logger;

    public VocabularyExceptionMiddleware(
        RequestDelegate next,
        ILogger<VocabularyExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            var (statusCode, message) = MapException(exception);

            if (statusCode == StatusCodes.Status500InternalServerError)
            {
                _logger.LogError(exception, "Unhandled exception while processing a Vocabulary request");
            }
            else if (statusCode == StatusCodes.Status503ServiceUnavailable)
            {
                // Logged with the exception, unlike the other expected
                // rejections: a busy database is a capacity signal, and the
                // exception type alone would not say which write lost.
                _logger.LogWarning(
                    exception,
                    "Vocabulary request rejected with status {StatusCode}: the database was busy",
                    statusCode);

                // The caller can retry this one, and nothing else in the
                // envelope distinguishes a temporary failure from a permanent
                // one.
                context.Response.Headers.RetryAfter = "1";
            }
            else
            {
                _logger.LogWarning(
                    "Vocabulary request rejected with status {StatusCode}: {ExceptionType}",
                    statusCode,
                    exception.GetType().Name);
            }

            await VocabularyHttpResponse.WriteFailureAsync(context.Response, statusCode, message);
        }
    }

    private static (int StatusCode, string Message) MapException(Exception exception) =>
        exception switch
        {
            DomainValidationException => (StatusCodes.Status400BadRequest, exception.Message),
            BadHttpRequestException => (
                StatusCodes.Status400BadRequest,
                "The request is invalid."),
            ResourceNotFoundException => (StatusCodes.Status404NotFound, exception.Message),
            ConflictException => (StatusCodes.Status409Conflict, exception.Message),
            BusinessRuleException => (StatusCodes.Status422UnprocessableEntity, exception.Message),
            StorageBusyException => (StatusCodes.Status503ServiceUnavailable, exception.Message),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.")
        };
}
