using System.Text.Json;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Services;
using Lexarbor.Service.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Lexarbor.Service;

public static class VocabularyCleanupEndpoints
{
    public const int MaxRequestBytes = 1024 * 1024;

    public static void MapVocabularyCleanupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/vocabulary-books").RequireAuthorization("VocabularyAdmin");
        group.MapPost("/{bookId}/cleanup/preview", (string bookId, HttpRequest request, VocabularyCleanupService service, ILoggerFactory logs) =>
            HandleAsync(bookId, request, service, logs, true)).WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes));
        group.MapPost("/{bookId}/cleanup", (string bookId, HttpRequest request, VocabularyCleanupService service, ILoggerFactory logs) =>
            HandleAsync(bookId, request, service, logs, false)).WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes));
    }

    private static async Task<IResult> HandleAsync(string bookId, HttpRequest request, VocabularyCleanupService service, ILoggerFactory logs, bool preview)
    {
        var cancellationToken = request.HttpContext.RequestAborted;
        if (request.ContentLength > MaxRequestBytes) throw TooLarge();
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (bytes.Length + read > MaxRequestBytes) throw TooLarge();
            bytes.Write(buffer, 0, read);
        }
        VocabularyCleanupRequest input;
        try
        {
            if (!request.HasJsonContentType()) throw new JsonException();
            using var document = JsonDocument.Parse(bytes.ToArray());
            input = VocabularyCleanupRequest.Parse(document.RootElement, preview);
        }
        catch (JsonException) { throw new DomainValidationException("The request body is not valid JSON."); }
        if (preview) return VocabularyHttpResponse.Ok(await service.PreviewAsync(bookId, input.ToSelection(), cancellationToken));
        var result = await service.CommitAsync(bookId, input.ToSelection(), cancellationToken);
        logs.CreateLogger(nameof(VocabularyCleanupEndpoints)).LogInformation(
            "Vocabulary cleanup {Action} in book {BookId}: {MeaningCount} meanings, {WordCount} words, deleted book {DeletedBook}",
            result.Action, result.BookId, result.DeletedMeaningCount, result.DeletedWordCount, result.DeletedBook);
        return VocabularyHttpResponse.Ok(result);
    }

    private static BadHttpRequestException TooLarge() => new("The request body is too large.", StatusCodes.Status413PayloadTooLarge);
}
