namespace Lexarbor.Host.Authentication;

/// <summary>
/// Normalizes backchannel timeouts of the hosted-login client's named HTTP client. The
/// protocol outcomes treat a cancellation of the request token as caller cancellation and
/// every other failure as an unreachable authority, but a client-side timeout surfaces as a
/// cancellation that did not come from the caller. This handler rewrites timeout
/// cancellations into transport failures, so a Discovery, JWKS or token-endpoint timeout keeps
/// answering the fixed provider-unavailable outcome instead of an unhandled exception.
/// </summary>
public sealed class AdminOidcBackchannelTimeoutHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await base.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("The authority request timed out.", new TimeoutException(exception.Message, exception));
        }
    }
}
