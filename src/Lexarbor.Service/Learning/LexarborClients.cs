using Lexarbor.Learning.Domain.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Lexarbor.Learning.Service;

internal static class LexarborClients
{
    internal static IServiceCollection AddLexarborClients(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LexarborOptions>()
            .Bind(configuration.GetSection(LexarborOptions.SectionName))
            .Validate(options => string.IsNullOrEmpty(options.ApiKey) || LexarborApiKey.IsValid(options.ApiKey),
                "Lexarbor:ApiKey is invalid.")
            .Validate(options => string.IsNullOrEmpty(options.ApiKey) || LexarborApiKey.IsAllowedEndpoint(options.BaseUrl?.Trim(), options.AllowInsecureHttp),
                "Lexarbor:ApiKey requires an HTTPS Lexarbor:BaseUrl; controlled HTTP requires Lexarbor:AllowInsecureHttp.")
            .ValidateOnStart();
        services.AddSingleton(LexarborIntegrationOptions.Read(configuration));
        services.AddTransient<LexarborCatalogApiKeyHandler>();
        services.AddHttpClient<ILexarborClient, LexarborClient>()
            .ConfigureLexarborTransport()
            .AddHttpMessageHandler<LexarborCatalogApiKeyHandler>();
        services.AddScoped<IVocabularyFamilyCatalog>(provider => (LexarborClient)provider.GetRequiredService<ILexarborClient>());
        services.AddScoped<IVocabularyPhotoCatalog>(provider => (LexarborClient)provider.GetRequiredService<ILexarborClient>());
        services.AddTransient<LexarborIntegrationAuthenticationHandler>();
        services.AddHttpClient(LexarborIntegrationClient.HttpClientName)
            .ConfigureLexarborTransport()
            .AddHttpMessageHandler<LexarborIntegrationAuthenticationHandler>();
        services.AddScoped<LexarborIntegrationClient>();
        services.AddScoped<IVocabularyNotebookCatalog>(provider => provider.GetRequiredService<LexarborIntegrationClient>());
        return services;
    }

    private static IHttpClientBuilder ConfigureLexarborTransport(this IHttpClientBuilder builder) =>
        builder.ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(5))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
            .RemoveAllLoggers();
}

internal sealed class LexarborCatalogApiKeyHandler(IOptions<LexarborOptions> options) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (options.Value.ApiKey is { Length: > 0 } apiKey)
            request.Headers.Add(LexarborApiKey.HeaderName, apiKey);
        return base.SendAsync(request, cancellationToken);
    }
}
