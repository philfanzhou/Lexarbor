using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Vocabulary.Domain.Exceptions;

namespace Ruoyu.Study.Vocabulary.Service;

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
            ResourceNotFoundException => (StatusCodes.Status404NotFound, exception.Message),
            ConflictException => (StatusCodes.Status409Conflict, exception.Message),
            BusinessRuleException => (StatusCodes.Status422UnprocessableEntity, exception.Message),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred.")
        };
}
