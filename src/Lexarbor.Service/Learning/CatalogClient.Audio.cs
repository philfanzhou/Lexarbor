using System.Net;
using System.Text.Json;
using static Lexarbor.Learning.Service.VocabularyCatalogErrorCodes;

namespace Lexarbor.Learning.Service;

public sealed partial class CatalogClient
{
    public async Task<CatalogAudio> GetAudioAsync(string wordId, string bookId, string meaningId, string locale,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = GetBaseUrl();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https"))
            throw AudioError();
        var target = baseUrl + "/api/vocabulary/" + Uri.EscapeDataString(wordId) + "/audio?bookId="
            + Uri.EscapeDataString(bookId) + "&meaningId=" + Uri.EscapeDataString(meaningId)
            + "&locale=" + Uri.EscapeDataString(locale);
        try
        {
            using var response = await _httpClient.GetAsync(target, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new CatalogAudio(locale, "missing", null, null);
            if (response.StatusCode != HttpStatusCode.OK) throw AudioError();
            var envelope = await ReadEnvelopeAsync<CatalogAudioList>(response, cancellationToken).ConfigureAwait(false);
            if (envelope is not { Success: true, Data.Items.Count: 1 }
                || envelope.Data.Items[0] is not { } item || !IsValidAudio(item, locale))
                throw AudioError();
            return item;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw AudioError();
        }
        catch (HttpRequestException) { throw AudioError(); }
        catch (IOException) { throw AudioError(); }
        catch (JsonException) { throw AudioError(); }
    }

    private static bool IsValidAudio(CatalogAudio item, string locale) => item.Locale == locale &&
        (item.Status == "missing"
            ? item.Url is null && item.ExpiresAt is null
            : item.Status is "ready" or "stale"
                && Uri.TryCreate(item.Url, UriKind.Absolute, out var url)
                && url.Scheme is "http" or "https"
                && item.ExpiresAt is { Offset: var offset } && offset == TimeSpan.Zero);

    private static CatalogException AudioError() =>
        new(AudioUnavailable, "Pronunciation is temporarily unavailable.");
}
