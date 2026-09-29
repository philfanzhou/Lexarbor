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
        group.MapGet("/vocabulary-books/{bookId}/phrase-positions", async (string bookId,
            [FromQuery] string? unitId, [FromQuery] string? section, [FromQuery] string? keyword,
            [FromQuery] int? page, [FromQuery] int? size, VocabularyAdminQueryService service,
            CancellationToken cancellationToken) =>
        {
            var result = await service.GetPhrasePositionsAsync(bookId, unitId, section, keyword, page, size, cancellationToken);
            return VocabularyHttpResponse.Ok(new { result.Items, result.TotalCount, result.TotalPage });
        });
        // Read-only unit content. A disabled book stays readable, matching the
        // whole-book content route; the meanings listed are only those assigned
        // to this unit, and the unit-scoped totals ignore the keyword. A
        // `section` of A, B, or none narrows the page and the counts to that
        // section's places, and an `entryKind` of word, phrase, or none to that
        // kind's places — the two combine as independent dimensions of one
        // position — while the section and kind counts always speak for the
        // whole unit.
        group.MapGet("/vocabulary-books/{bookId}/units/{unitId}/content", async (string bookId, string unitId,
            [FromQuery] string? keyword, [FromQuery] string? section, [FromQuery] string? entryKind,
            [FromQuery] int? page, [FromQuery] int? size,
            VocabularyAdminQueryService service, CancellationToken cancellationToken) =>
        {
            var result = await service.GetUnitContentAsync(bookId, unitId, keyword, section, entryKind, page, size, cancellationToken);
            return VocabularyHttpResponse.Ok(new
            {
                book = result.Book.ToDto(),
                unit = new VocabularyAdminUnitRefDto(result.Unit.Id, result.Unit.BookId, result.Unit.Number, result.Unit.Title),
                result.WordCount,
                result.MeaningCount,
                sectionCounts = new
                {
                    sectionA = result.SectionCounts.SectionA,
                    sectionB = result.SectionCounts.SectionB,
                    noSection = result.SectionCounts.NoSection
                },
                entryKindCounts = new
                {
                    word = result.EntryKindCounts.Word,
                    phrase = result.EntryKindCounts.Phrase,
                    none = result.EntryKindCounts.None
                },
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
        => new(item.Word.Id, item.Word.Word, item.Word.PhoneticUk, item.Word.PhoneticUs, Books(item),
            item.Meanings.Select(m => new VocabularyAdminMeaningDetailDto(
                m.Meaning.Id, m.Meaning.VocabularyId, m.Meaning.BookId, m.Meaning.PartOfSpeech,
                m.Meaning.Meaning, m.Meaning.Example,
                m.Units.Select(u => new VocabularyAdminUnitDto(u.UnitId, u.Number, u.Title, u.Section, u.EntryKind)).ToList())).ToList());
}

/// <summary>The unit a unit-content response is scoped to.</summary>
public record VocabularyAdminUnitRefDto(string Id, string BookId, int Number, string? Title);
