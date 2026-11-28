using Lexarbor.Domain.Services;
using Lexarbor.Service.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Lexarbor.Service;

public static class VocabularyMeaningEditEndpoints
{
    public static void MapVocabularyMeaningEdit(this IEndpointRouteBuilder app)
    {
        app.MapPut("/admin/vocabulary-books/{bookId}/words/{wordId}/meanings/{meaningId}",
            async (string bookId, string wordId, string meaningId, [FromBody] VocabularyMeaningEditRequest request,
                VocabularyMeaningPhoneticEditService service, IMeaningPhoneticWorkVersionsProvider versions,
                TimeProvider clock, CancellationToken cancellationToken) =>
            {
                await service.ReplaceAsync(bookId, wordId, meaningId, request.PartOfSpeech, request.Meaning, request.Example,
                    request.Edits(bookId, wordId, meaningId), versions.Capture(), clock.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken);
                return ApiResponse.Ok(new BoolResponse { Success = true });
            }).RequireAuthorization(AdminEndpointAuthorization.PolicyName);
    }
}
