using Lexarbor.Learning.Domain.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Lexarbor.Learning.Service;

internal static class CatalogServiceCollectionExtensions
{
    internal static IServiceCollection AddCatalogClients(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CatalogOptions>()
            .Bind(configuration.GetSection(CatalogOptions.SectionName))
            .Validate(options => string.IsNullOrEmpty(options.ApiKey) || ApiKeyRules.IsValid(options.ApiKey),
                "Lexarbor:ApiKey is invalid.")
            .Validate(options => string.IsNullOrEmpty(options.ApiKey) || ApiKeyRules.IsAllowedEndpoint(options.BaseUrl?.Trim(), options.AllowInsecureHttp),
                "Lexarbor:ApiKey requires an HTTPS Lexarbor:BaseUrl; controlled HTTP requires Lexarbor:AllowInsecureHttp.")
            .ValidateOnStart();
        services.AddSingleton(IntegrationOptions.Read(configuration));
        services.AddTransient<CatalogApiKeyHandler>();
        services.AddHttpClient<ICatalogClient, CatalogClient>()
            .ConfigureCatalogTransport()
            .AddHttpMessageHandler<CatalogApiKeyHandler>();
        services.AddScoped<IVocabularyFamilyCatalog>(provider => (CatalogClient)provider.GetRequiredService<ICatalogClient>());
        services.AddScoped<IVocabularyPhotoCatalog>(provider => (CatalogClient)provider.GetRequiredService<ICatalogClient>());
        services.AddTransient<IntegrationAuthenticationHandler>();
        services.AddHttpClient(IntegrationClient.HttpClientName)
            .ConfigureCatalogTransport()
            .AddHttpMessageHandler<IntegrationAuthenticationHandler>();
        services.AddScoped<IntegrationClient>();
        services.AddScoped<IVocabularyNotebookCatalog>(provider => provider.GetRequiredService<IntegrationClient>());
        return services;
    }

    private static IHttpClientBuilder ConfigureCatalogTransport(this IHttpClientBuilder builder) =>
        builder.ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(5))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false })
            .RemoveAllLoggers();
}

internal sealed class CatalogApiKeyHandler(IOptions<CatalogOptions> options) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (options.Value.ApiKey is { Length: > 0 } apiKey)
            request.Headers.Add(ApiKeyRules.HeaderName, apiKey);
        return base.SendAsync(request, cancellationToken);
    }
}
