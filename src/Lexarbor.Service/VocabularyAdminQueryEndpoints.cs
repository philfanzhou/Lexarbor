using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Lexarbor.Service.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Lexarbor.Service;

public static class VocabularyAdminQueryEndpoints
{
    public static void MapVocabularyAdminQueryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin").RequireAuthorization("VocabularyAdmin");
        group.MapGet("/vocabulary", async ([FromQuery] string? keyword, [FromQuery] string? bookId,
            [FromQuery] int? page, [FromQuery] int? size, VocabularyAdminQueryService service, CancellationToken cancellationToken) =>
        {
            var result = await service.SearchAsync(keyword, bookId, page, size, cancellationToken);
            return VocabularyHttpResponse.Ok(new { items = result.Items.Select(Summary), result.TotalCount, result.TotalPage });
        });
        group.MapGet("/vocabulary/{wordId}", async (string wordId, VocabularyAdminQueryService service, CancellationToken cancellationToken) =>
            VocabularyHttpResponse.Ok(Detail(await service.GetAsync(wordId, cancellationToken))));
        group.MapGet("/vocabulary-books/{bookId}/content", async (string bookId, [FromQuery] string? keyword,
            [FromQuery] int? page, [FromQuery] int? size, VocabularyAdminQueryService service, CancellationToken cancellationToken) =>
        {
            var result = await service.GetContentAsync(bookId, keyword, page, size, cancellationToken);
            return VocabularyHttpResponse.Ok(new
            {
                book = result.Book.ToDto(),
                result.WordCount,
                result.MeaningCount,
                items = result.Page.Items.Select(Detail),
                result.Page.TotalCount,
                result.Page.TotalPage
            });
        });
    }

    private static IReadOnlyList<VocabularyAdminBookDto> Books(VocabularyAdminWord item)
        => item.Books.Select(b => new VocabularyAdminBookDto(b.Id, b.BookName, b.Status)).ToList();
    private static VocabularyAdminWordDto Summary(VocabularyAdminWord item)
        => new(item.Word.Id, item.Word.Word, item.Word.PhoneticUk, item.Word.PhoneticUs, Books(item));
    private static VocabularyAdminDetailDto Detail(VocabularyAdminWord item)
        => new(item.Word.Id, item.Word.Word, item.Word.PhoneticUk, item.Word.PhoneticUs, Books(item), item.Meanings.Select(m => m.ToDto()).ToList());
}
