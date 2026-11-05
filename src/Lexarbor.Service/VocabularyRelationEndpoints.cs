using System.Text.Json;
using System.Text.Json.Serialization;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Lexarbor.Service.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Lexarbor.Service;

public static class VocabularyRelationEndpoints
{
    public const int MaxRequestBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        AllowDuplicateProperties = false
    };

    public static void MapVocabularyRelationEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/admin").RequireAuthorization(AdminEndpointAuthorization.PolicyName);
        admin.MapGet("/vocabulary-relation-types", () => VocabularyHttpResponse.Ok(VocabularyRelationTypes.All));
        admin.MapGet("/vocabulary/{wordId}/relations", async (string wordId, string? direction, string? type, string? status,
            int? page, int? size, VocabularyRelationService service, CancellationToken token) =>
            VocabularyHttpResponse.Ok(await service.ListAsync(wordId,
                new(direction ?? "both", type, status, page ?? 1, size ?? 20), token)));
        var relations = admin.MapGroup("/vocabulary-relations");
        relations.MapGet("/", async (string? type, string? status, int? page, int? size,
            VocabularyRelationService service, CancellationToken token) =>
            VocabularyHttpResponse.Ok(await service.ListAsync(null,
                new(Type: type, Status: status, Page: page ?? 1, Size: size ?? 20), token)));
        relations.MapGet("/{relationId}", async (string relationId, VocabularyRelationService service, CancellationToken token) =>
            VocabularyHttpResponse.Ok(await service.GetAsync(relationId, token)));
        relations.MapPost("/", async (HttpRequest request, VocabularyRelationService service, CancellationToken token) =>
        {
            var input = await ReadAsync<VocabularyRelationRequest>(request, token);
            var result = await service.CreateAsync(input.ToInput(), token);
            return VocabularyHttpResponse.Ok(result.Relation);
        }).WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes));
        relations.MapPut("/{relationId}", async (string relationId, HttpRequest request,
            VocabularyRelationService service, CancellationToken token) =>
        {
            var input = await ReadAsync<VocabularyRelationReplaceRequest>(request, token);
            return VocabularyHttpResponse.Ok(await service.ReplaceAsync(relationId, input.ToInput(), input.ExpectedVersion, token));
        }).WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes));
        relations.MapDelete("/{relationId}", async (string relationId, long expectedVersion,
            VocabularyRelationService service, CancellationToken token) =>
        {
            await service.DeleteAsync(relationId, expectedVersion, token);
            return VocabularyHttpResponse.Ok(new BoolResponse { Success = true });
        });
        relations.MapPost("/batch", async (HttpRequest request, VocabularyRelationService service, CancellationToken token) =>
        {
            var input = await ReadAsync<VocabularyRelationBatchRequest>(request, token);
            if (input.Items == null) throw new DomainValidationException("Items are required.");
            return VocabularyHttpResponse.Ok(await service.CreateBatchAsync(input.Items.Select(item => item?.ToInput()).ToArray(), token));
        }).WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes));
    }

    private static async Task<T> ReadAsync<T>(HttpRequest request, CancellationToken token) where T : class
    {
        if (request.ContentLength > MaxRequestBytes) throw TooLarge();
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, token)) != 0)
        {
            if (bytes.Length + read > MaxRequestBytes) throw TooLarge();
            bytes.Write(buffer, 0, read);
        }
        try
        {
            if (!request.HasJsonContentType()) throw new JsonException();
            return JsonSerializer.Deserialize<T>(bytes.GetBuffer().AsSpan(0, (int)bytes.Length), JsonOptions)
                ?? throw new JsonException();
        }
        catch (JsonException) { throw new DomainValidationException("The request body is not valid relation JSON."); }
    }

    private static BadHttpRequestException TooLarge() => new("The request body is too large.", StatusCodes.Status413PayloadTooLarge);
}
