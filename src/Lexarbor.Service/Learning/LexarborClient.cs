using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lexarbor.Learning.Domain.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static Lexarbor.Learning.Service.VocabularyCatalogErrorCodes;

namespace Lexarbor.Learning.Service;

public sealed partial class LexarborClient : ILexarborClient
{
    private static readonly JsonSerializerOptions ReadJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly LexarborOptions _options;
    private readonly ILogger<LexarborClient>? _logger;

    public LexarborClient(HttpClient httpClient, IOptions<LexarborOptions> options, ILogger<LexarborClient>? logger = null)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<LexarborBook>> GetBooksAsync(CancellationToken cancellationToken = default)
    {
        var books = await GetListAsync<LexarborBookList, IReadOnlyList<LexarborBook>>("api/vocabulary-books/all", BookNotFound, static list => (IReadOnlyList<LexarborBook>)list.Books, cancellationToken).ConfigureAwait(false);
        return books.Where(book => !IsSupplementary(book.Id)).ToList();
    }

    public Task<IReadOnlyList<LexarborUnit>> GetUnitsAsync(string bookId, CancellationToken cancellationToken = default)
    {
        RejectSupplementary(bookId);
        return GetListAsync<LexarborUnitList, IReadOnlyList<LexarborUnit>>($"api/vocabulary-books/{EscapeSegment(bookId)}/units", BookNotFound, static list => (IReadOnlyList<LexarborUnit>)list.Units, cancellationToken);
    }

    public Task<LexarborEntryPage> GetEntriesAsync(string bookId, string? unitId, int page, int size, CancellationToken cancellationToken = default)
    {
        RejectSupplementary(bookId);


        var notFound = string.IsNullOrEmpty(unitId) ? BookNotFound : UnitNotFound;
        var query = $"?page={page}&size={size}";
        if (!string.IsNullOrEmpty(unitId))
        {
            query += $"&unitId={Uri.EscapeDataString(unitId)}";
        }
        return GetAsync<LexarborEntryPage>($"api/vocabulary-books/{EscapeSegment(bookId)}/entries{query}", notFound, cancellationToken);
    }

    /// <summary>
    /// On-demand learning question (Lexarbor POST /api/vocabulary/learning-question). Decoding lives in
    /// <see cref="LexarborLearningQuestionReader"/>: only the documented
    /// unavailable business codes return null; every other failure throws VocabCatalogUnavailableException.
    /// Correct answers in the response only flow into the saved snapshot.
    /// </summary>
    public async Task<VocabGeneratedQuestion?> GetLearningQuestionAsync(LexarborLearningQuestionRequest request, CancellationToken cancellationToken = default)
    {
        var baseUrl = GetBaseUrl();
        if (baseUrl.Length == 0)
        {
            _logger?.LogWarning("The vocabulary catalog is not configured (Lexarbor:BaseUrl is empty).");
            throw new VocabCatalogUnavailableException();
        }

        var payload = JsonSerializer.Serialize(request, WriteJsonOptions);
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsync(
                baseUrl + "/api/vocabulary/learning-question",
                new StringContent(payload, System.Text.Encoding.UTF8, "application/json"),
                cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger?.LogWarning("Vocabulary learning question request timed out.");
            throw new VocabCatalogUnavailableException();
        }
        catch (HttpRequestException)
        {
            _logger?.LogWarning("Vocabulary learning question request failed.");
            throw new VocabCatalogUnavailableException();
        }

        using (response)
        {
            return await LexarborLearningQuestionReader.ReadAsync(response, request, _logger, cancellationToken).ConfigureAwait(false);
        }
    }

    private string CatalogUrl(string relativeUrl)
    {
        var baseUrl = GetBaseUrl();
        if (baseUrl.Length == 0) throw new VocabCatalogUnavailableException();
        return baseUrl + "/" + relativeUrl;
    }

    private async Task<T?> ReadCatalogAsync<T>(HttpRequestMessage request, bool allowMissingWord, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (allowMissingWord && response.StatusCode == HttpStatusCode.NotFound)
            {
                var missing = await response.Content.ReadFromJsonAsync<CatalogEnvelope<T>>(ReadJsonOptions, cancellationToken).ConfigureAwait(false);
                if (missing is { Success: false, Message: "Vocabulary word was not found." }) return null;
            }
            if (response.IsSuccessStatusCode)
            {
                var envelope = await response.Content.ReadFromJsonAsync<CatalogEnvelope<T>>(ReadJsonOptions, cancellationToken).ConfigureAwait(false);
                if (envelope is { Success: true, Data: { } data }) return data;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (HttpRequestException) { }
        catch (JsonException) { }
        catch (NotSupportedException) { }

        throw new VocabCatalogUnavailableException();
    }

    private sealed record CatalogEnvelope<T>(bool? Success, T? Data, string? Message);

    private static readonly JsonSerializerOptions WriteJsonOptions = new(JsonSerializerDefaults.Web);

    private async Task<TResult> GetListAsync<TList, TResult>(string relativeUrl, string notFoundError, Func<TList, TResult> select, CancellationToken cancellationToken)
        where TList : class
    {
        var list = await GetAsync<TList>(relativeUrl, notFoundError, cancellationToken).ConfigureAwait(false);
        return select(list);
    }

    private async Task<T> GetAsync<T>(string relativeUrl, string notFoundError, CancellationToken cancellationToken)
    {
        var baseUrl = GetBaseUrl();
        if (baseUrl.Length == 0)
        {
            throw new LexarborCatalogException(CatalogUnavailable, retryable: false,
                "The vocabulary catalog is not configured (Lexarbor:BaseUrl is empty).");
        }

        var requestUri = baseUrl + "/" + relativeUrl;
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(requestUri, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {

            _logger?.LogWarning("Vocabulary catalog request timed out: {Url}", relativeUrl);
            throw new LexarborCatalogException(CatalogUnavailable, retryable: true,
                "The vocabulary catalog did not respond in time.");
        }
        catch (HttpRequestException)
        {
            _logger?.LogWarning("Vocabulary catalog request failed: {Url}", relativeUrl);
            throw new LexarborCatalogException(CatalogUnavailable, retryable: true,
                "The vocabulary catalog is unreachable.");
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            switch (status)
            {
                case 200:
                    T? payload;
                    var envelope = await ReadEnvelopeAsync<T>(response, cancellationToken).ConfigureAwait(false);
                    if (envelope is null || !envelope.Success || envelope.Data is null)
                    {
                        throw new LexarborCatalogException(CatalogUnavailable, retryable: false,
                            "The vocabulary catalog returned an unreadable response.");
                    }
                    payload = envelope.Data;
                    return payload!;

                case 404:
                    throw new LexarborCatalogException(notFoundError, retryable: false,
                        notFoundError == BookNotFound
                            ? "The vocabulary book was not found."
                            : "The vocabulary unit was not found.");

                case 422:
                    throw new LexarborCatalogException(BookDisabled, retryable: false,
                        "The vocabulary book is disabled.");

                case 429:
                case >= 500:
                    _logger?.LogWarning("Vocabulary catalog answered {Status} for {Url}", status, relativeUrl);
                    throw new LexarborCatalogException(CatalogUnavailable, retryable: true,
                        "The vocabulary catalog is temporarily unavailable.");

                default:
                    _logger?.LogWarning("Vocabulary catalog answered unexpected {Status} for {Url}", status, relativeUrl);
                    throw new LexarborCatalogException(CatalogUnavailable, retryable: false,
                        "The vocabulary catalog answered an unexpected status.");
            }
        }
    }

    private static async Task<LexarborEnvelope<T>?> ReadEnvelopeAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var options = typeof(T) == typeof(LexarborEntryPage) ? EntryPageJsonContext.Default.Options : ReadJsonOptions;
            return await JsonSerializer.DeserializeAsync<LexarborEnvelope<T>>(stream, options, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private bool IsSupplementary(string? bookId) =>
        !string.IsNullOrWhiteSpace(_options.SupplementaryBookId)
        && string.Equals(bookId?.Trim(), _options.SupplementaryBookId.Trim(), StringComparison.Ordinal);

    private string GetBaseUrl() => (_options.BaseUrl ?? string.Empty).Trim().TrimEnd('/');

    private void RejectSupplementary(string bookId)
    {
        if (IsSupplementary(bookId))
            throw new LexarborCatalogException(BookNotFound, retryable: false, "The vocabulary book was not found.");
    }

    private static string EscapeSegment(string segment) => Uri.EscapeDataString((segment ?? string.Empty).Trim());

    private sealed record LexarborEnvelope<T>(bool Success, T? Data, string? Message);
}
