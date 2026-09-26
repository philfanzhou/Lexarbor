using Lexarbor.Domain.Services;
using Lexarbor.Service.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Lexarbor.Service;

public static class VocabularyMeaningEditEndpoints
{
    public static void MapVocabularyMeaningEditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut("/admin/vocabulary-books/{bookId}/words/{wordId}/meanings/{meaningId}",
            async (string bookId, string wordId, string meaningId, [FromBody] VocabularyMeaningEditRequest request,
                VocabularyMeaningEditService service, CancellationToken cancellationToken) =>
            {
                await service.ReplaceAsync(bookId, wordId, meaningId, request.PartOfSpeech, request.Meaning, request.Example, cancellationToken);
                return VocabularyHttpResponse.Ok(new BoolResponse { Success = true });
            }).RequireAuthorization("VocabularyAdmin");
    }
}
