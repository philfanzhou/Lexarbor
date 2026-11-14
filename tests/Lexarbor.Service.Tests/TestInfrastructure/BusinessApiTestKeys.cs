using Lexarbor.Database.Repositories;
using Lexarbor.Host.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Lexarbor.Service.Tests.TestInfrastructure;

internal static class BusinessApiTestKeys
{
    internal static async Task<string> SeedKeyAsync(this WebApplicationFactory<Program> host, string callerId = "catalog-tests",
        bool catalog = true, bool meaningSubmissions = false)
    {
        var secret = BusinessApiKeyAuthentication.GenerateKey();
        using var scope = host.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<BusinessApiKeyRepository>();
        if (!await repository.TryAddAsync(new(Guid.NewGuid().ToString(), callerId, catalog, meaningSubmissions, 1, null),
            BusinessApiKeyAuthentication.HashKey(secret), TestContext.Current.CancellationToken))
            throw new InvalidOperationException("Could not provision the synthetic test caller.");
        return secret;
    }

    internal static async Task UseCatalogKeyAsync(this HttpClient client, WebApplicationFactory<Program> host) =>
        client.DefaultRequestHeaders.Add(BusinessApiAuthorization.Header, await host.SeedKeyAsync());
}
