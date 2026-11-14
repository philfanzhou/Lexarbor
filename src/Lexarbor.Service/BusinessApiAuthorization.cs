using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;

namespace Lexarbor.Service;

public static class BusinessApiAuthorization
{
    public const string Header = "X-Api-Key";
    public const string Catalog = "catalog";
    public const string MeaningSubmissions = "meaning-submissions";
    public const string ScopeClaim = "lexarbor.api_scope";
    public const string CallerClaim = "sub";

    public static RouteGroupBuilder RequireBusinessApiScope(this RouteGroupBuilder group, string scope)
    {
        group.RequireAuthorization(scope);
        // Keep a known route selected even for an unsupported Content-Type or
        // missing body. Authorization then runs before the handler's binding
        // and validation, while unknown routes can still return their own 404.
        ((IEndpointConventionBuilder)group).Finally(endpoint =>
        {
            for (var index = endpoint.Metadata.Count - 1; index >= 0; index--)
                if (endpoint.Metadata[index] is IAcceptsMetadata)
                    endpoint.Metadata.RemoveAt(index);
        });
        return group;
    }
}
