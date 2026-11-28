namespace Lexarbor.Host.Integration;

/// <summary>Registration defaults for the named clients that call model and catalog endpoints.</summary>
internal static class IntegrationHttpClientExtensions
{
    /// <summary>
    /// Neither follows redirects nor keeps cookies, and turns the HTTP logging off so that
    /// request URLs and credentials never reach the logs.
    /// </summary>
    internal static IHttpClientBuilder WithoutRedirectsCookiesOrLogging(this IHttpClientBuilder builder)
        => builder.RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
}
