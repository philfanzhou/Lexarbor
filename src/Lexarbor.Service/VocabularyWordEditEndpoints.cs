using Lexarbor.Domain.Services;
using Lexarbor.Service.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Lexarbor.Service;

public static class VocabularyWordEditEndpoints
{
    public static void MapVocabularyWordEditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut("/admin/vocabulary/{wordId}", async (string wordId, [FromBody] VocabularyWordEditRequest request,
            VocabularyWordEditService service, CancellationToken cancellationToken) =>
        {
            await service.ReplaceAsync(wordId, request.Word, request.PhoneticUk, request.PhoneticUs, cancellationToken);
            return VocabularyHttpResponse.Ok(new BoolResponse { Success = true });
        }).RequireAuthorization("VocabularyAdmin");
    }
}
