using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Lexarbor.Service.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Lexarbor.Service;

public static class AudioAdminQueryEndpoints
{
    public static void MapAudioAdminQueryEndpoints(this IEndpointRouteBuilder app, bool enabled)
    {
        var group = app.MapGroup("/admin").RequireAuthorization(AdminEndpointAuthorization.PolicyName);
        group.MapGet("/audio-targets", async ([FromQuery] string? bookId, [FromQuery] string? locale,
            [FromQuery] string? status, [FromQuery] int? page, [FromQuery] int? size,
            AudioAdminQueryService service, CancellationToken cancellationToken) =>
        {
            var result = await service.SearchAsync(bookId, locale, status, page, size, cancellationToken);
            return VocabularyHttpResponse.Ok(new { enabled, items = result.Items.Select(ToDto), result.TotalCount, result.TotalPage });
        });
        group.MapGet("/vocabulary/{wordId}/audio", async (string wordId, [FromQuery] string? meaningId,
            AudioAdminQueryService service, CancellationToken cancellationToken) =>
        {
            var items = await service.GetAsync(wordId, meaningId, cancellationToken);
            return VocabularyHttpResponse.Ok(new { enabled, items = items.Select(ToDto) });
        });
    }

    private static AudioBindingDto? Binding(AudioBinding? binding)
        => binding == null ? null : new(binding.Asset.Id, binding.SourceRevision);

    private static AudioTargetDto ToDto(AudioTarget target)
    {
        var selection = target.Selection;
        return new(target.WordId, target.MeaningId, selection.Locale.ToCode(),
            target.MeaningId == null ? "default" : "meaning", selection.Status.ToString().ToLowerInvariant(),
            selection.Source?.ToString().ToLowerInvariant(), selection.SourceRevision,
            new(selection.Input.Word, selection.Input.Phonetic, selection.Input.PartOfSpeech, selection.Input.Meaning),
            Binding(selection.OwnBinding), Binding(selection.EffectiveBinding));
    }
}
