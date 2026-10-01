using Lexarbor.Domain.Services;
using Lexarbor.Service.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Lexarbor.Service;

/// <summary>
/// The management surface for one book's units: the routes an administrator
/// uses to create and maintain the units import and query tasks reference by
/// their stable IDs. Creating units is explicit here; import never creates or
/// renames a unit implicitly.
/// </summary>
public static class VocabularyBookUnitEndpoints
{
    public static IEndpointRouteBuilder MapVocabularyBookUnitEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/vocabulary-books/{bookId}/units")
            .RequireAuthorization(AdminEndpointAuthorization.PolicyName);

        group.MapGet("/", ListUnits);
        group.MapPost("/", CreateUnit);
        group.MapPut("/{unitId}", ReplaceUnit);
        group.MapDelete("/{unitId}", DeleteUnit);

        return app;
    }

    private static async Task<IResult> ListUnits(
        string bookId,
        VocabularyBookUnitDomainService service)
    {
        var units = await service.GetByBookWithCountsAsync(bookId);
        var result = new VocabularyBookUnitListResponse();
        result.Units.AddRange(units.Select(pair => pair.Unit.ToDto(pair.MeaningCount)));
        return VocabularyHttpResponse.Ok(result);
    }

    private static async Task<IResult> CreateUnit(
        string bookId,
        [FromBody] VocabularyBookUnitUpsertRequest request,
        VocabularyBookUnitDomainService service)
    {
        if (request.Number is not int number)
        {
            return VocabularyHttpResponse.BadRequest("Unit number is required.");
        }

        // A freshly created unit cannot have assignments yet, so its count is
        // zero by construction rather than by a read.
        var unit = await service.CreateAsync(bookId, number, request.Title);
        return VocabularyHttpResponse.Ok(unit.ToDto(0));
    }

    private static async Task<IResult> ReplaceUnit(
        string bookId,
        string unitId,
        [FromBody] VocabularyBookUnitUpsertRequest request,
        VocabularyBookUnitDomainService service)
    {
        if (request.Number is not int number)
        {
            return VocabularyHttpResponse.BadRequest("Unit number is required.");
        }

        var (unit, meaningCount) = await service.UpdateAsync(bookId, unitId, number, request.Title);
        return VocabularyHttpResponse.Ok(unit.ToDto(meaningCount));
    }

    private static async Task<IResult> DeleteUnit(
        string bookId,
        string unitId,
        VocabularyBookUnitDomainService service)
    {
        await service.DeleteAsync(bookId, unitId);
        return VocabularyHttpResponse.Ok(new BoolResponse { Success = true });
    }
}
