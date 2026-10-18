using Lexarbor.Service;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using SignaCore.Client.AspNetCore;

namespace Lexarbor.Host.Audio;

/// <summary>Keep the official CSRF check from reading this bounded multipart protocol before admission.</summary>
public sealed class TextbookAudioUploadCsrfMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context, IOptions<SignaCoreHostedLoginOptions> options)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<TextbookAudioUploadBoundary>() is not null)
        {
            var header = options.Value.AntiforgeryHeaderName;
            if (StringValues.IsNullOrEmpty(context.Request.Headers[header]))
                // The official session scheme rejects this invalid header without its form-body fallback.
                // Bearer authentication keeps its normal priority and does not consult this header.
                context.Request.Headers[header] = "missing";
        }
        return next(context);
    }
}
