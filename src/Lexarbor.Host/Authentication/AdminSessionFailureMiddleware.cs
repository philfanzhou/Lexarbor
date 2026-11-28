using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Service;

namespace Lexarbor.Host.Authentication;

public sealed class AdminSessionFailureMiddleware(RequestDelegate next,
    ILogger<AdminSessionFailureMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context); }
        catch (Exception exception) when (!context.Response.HasStarted
            && exception is AdminSessionStorageException or StorageBusyException
            && exception.Message == AdminSessionRepository.StorageFailureMessage)
        {
            if (context.GetEndpoint()?.Metadata.GetMetadata<AdminSessionLogoutMetadata>() is not null)
                AdminSessionCookie.Clear(context);
            var busy = exception is StorageBusyException;
            if (busy) context.Response.Headers.RetryAfter = "1";
            logger.LogWarning("Administrator session storage operation could not be confirmed.");
            await ApiResponse.WriteFailureAsync(context.Response,
                busy ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError,
                AdminSessionRepository.StorageFailureMessage);
        }
    }
}
