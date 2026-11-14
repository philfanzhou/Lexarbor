using System.Text.Json;
using System.Text.Json.Serialization;
using Lexarbor.Database.Repositories;
using Lexarbor.Service;
using Microsoft.AspNetCore.Mvc;
using ServiceMantle.Web.Management;

namespace Lexarbor.Host.Authentication;

public static class BusinessApiKeyEndpoints
{
    private const int MaxBodyBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    public static void MapBusinessApiKeyEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/api-keys")
            .RequireAuthorization(ManagementAuthorizationDefaults.AdminPolicyName);
        group.MapGet("", ListAsync);
        group.MapPost("", IssueAsync).WithMetadata(new RequestSizeLimitAttribute(MaxBodyBytes));
        group.MapDelete("/{id}", RevokeAsync);
    }

    private static async Task<IResult> ListAsync(HttpResponse response, BusinessApiKeyRepository keys, CancellationToken token)
    {
        response.Headers.CacheControl = "no-store";
        var items = await keys.ListAsync(token);
        return VocabularyHttpResponse.Ok(new { items = items.Select(ToResponse).ToArray() });
    }

    private static async Task<IResult> IssueAsync(HttpRequest request, BusinessApiKeyRepository keys, TimeProvider time, CancellationToken token)
    {
        request.HttpContext.Response.Headers.CacheControl = "no-store";
        IssueRequest? body = null;
        if (request.HasJsonContentType())
        {
            try { body = await request.ReadFromJsonAsync<IssueRequest>(JsonOptions, token); }
            catch (JsonException) { }
        }
        if (body?.CallerId is not { Length: > 0 and <= 2048 } callerId || callerId.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            return VocabularyHttpResponse.BadRequest("A caller ID of 1 to 2048 characters without whitespace is required.");
        if (body.Scopes is not { Length: > 0 and <= 2 } scopes
            || scopes.Any(scope => scope is not (BusinessApiAuthorization.Catalog or BusinessApiAuthorization.MeaningSubmissions))
            || scopes.Distinct(StringComparer.Ordinal).Count() != scopes.Length)
            return VocabularyHttpResponse.BadRequest("Select at least one valid, distinct API key scope.");

        var key = BusinessApiKeyAuthentication.GenerateKey();
        var metadata = new BusinessApiKeyMetadata(Guid.NewGuid().ToString(), callerId,
            scopes.Contains(BusinessApiAuthorization.Catalog), scopes.Contains(BusinessApiAuthorization.MeaningSubmissions),
            time.GetUtcNow().ToUnixTimeMilliseconds(), null);
        if (!await keys.TryAddAsync(metadata, BusinessApiKeyAuthentication.HashKey(key), token))
            return VocabularyHttpResponse.Conflict("Meaning submission access belongs to another caller with an active key.");
        return VocabularyHttpResponse.Ok(new { key, metadata = ToResponse(metadata) });
    }

    private static async Task<IResult> RevokeAsync(string id, HttpResponse response, BusinessApiKeyRepository keys,
        TimeProvider time, CancellationToken token)
    {
        response.Headers.CacheControl = "no-store";
        return await keys.RevokeAsync(id, time.GetUtcNow().ToUnixTimeMilliseconds(), token)
            ? VocabularyHttpResponse.Ok()
            : VocabularyHttpResponse.NotFound("The API key was not found.");
    }

    private static KeyResponse ToResponse(BusinessApiKeyMetadata value)
    {
        var scopes = new List<string>(2);
        if (value.Catalog) scopes.Add(BusinessApiAuthorization.Catalog);
        if (value.MeaningSubmissions) scopes.Add(BusinessApiAuthorization.MeaningSubmissions);
        return new(value.Id, value.CallerId, scopes, value.CreatedAtUnixMs, value.RevokedAtUnixMs);
    }

    private sealed record IssueRequest(string? CallerId, string?[]? Scopes);
    private sealed record KeyResponse(string Id, string CallerId, List<string> Scopes, long CreatedAtUnixMs, long? RevokedAtUnixMs);
}
