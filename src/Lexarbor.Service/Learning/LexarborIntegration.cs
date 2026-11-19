using System.Net.Http.Json;
using System.Text.Json;
using Lexarbor.Learning.Domain.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Lexarbor.Learning.Service;

public sealed record LexarborIntegrationOptions(string BaseUrl, string ApiKey)
{
    public bool Enabled => ApiKey.Length > 0;

    public static LexarborIntegrationOptions Read(IConfiguration configuration)
    {
        var url = configuration["Lexarbor:BaseUrl"]?.TrimEnd('/') ?? "";
        var key = configuration["Lexarbor:Integration:ApiKey"] ?? "";
        if (key.Length > 0 && (!LexarborApiKey.IsValid(key) ||
            !LexarborApiKey.IsAllowedEndpoint(url, configuration.GetValue<bool>("Lexarbor:AllowInsecureHttp"))))
            throw new InvalidOperationException("The learning submission client requires a valid key and catalog URL.");
        return new(url, key);
    }
}

internal sealed class LexarborIntegrationAuthenticationHandler(LexarborIntegrationOptions options) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!options.Enabled) throw new InvalidOperationException("The submission integration is disabled.");
        request.Headers.Add(LexarborApiKey.HeaderName, options.ApiKey);
        return base.SendAsync(request, cancellationToken);
    }
}

public sealed class LexarborIntegrationClient : IVocabularyNotebookCatalog
{
    public const string HttpClientName = "LexarborIntegration";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _clients;
    private readonly LexarborIntegrationOptions _options;
    private readonly ILogger<LexarborIntegrationClient>? _logger;

    public LexarborIntegrationClient(IHttpClientFactory clients, LexarborIntegrationOptions options, ILogger<LexarborIntegrationClient>? logger = null)
    {
        _clients = clients;
        _options = options;
        _logger = logger;
    }

    public async Task<IReadOnlyList<VocabCatalogMeaning>> SearchAsync(string keyword, CancellationToken cancellationToken = default)
    {
        var data = await SendAsync<ItemList<VocabCatalogMeaning>>(HttpMethod.Get,
            "integration/vocabulary/search?keyword=" + Uri.EscapeDataString(keyword), null, cancellationToken).ConfigureAwait(false);
        return data?.Items ?? throw new VocabCatalogUnavailableException();
    }

    public async Task<VocabMeaningSubmission> SubmitAsync(string word, string? partOfSpeech, string meaning, CancellationToken cancellationToken = default)
    {
        var data = await SendAsync<SubmissionDto>(HttpMethod.Post, "integration/meaning-submissions",
            new { word, partOfSpeech, meaning }, cancellationToken).ConfigureAwait(false);
        return data?.ToModel() ?? throw new VocabCatalogUnavailableException();
    }

    public async Task<IReadOnlyList<VocabMeaningSubmission>> LookupAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default)
    {
        var data = await SendAsync<ItemList<SubmissionDto>>(HttpMethod.Post, "integration/meaning-submissions/lookup",
            new { ids }, cancellationToken).ConfigureAwait(false);
        return data?.Items.Select(item => item.ToModel()).ToList() ?? throw new VocabCatalogUnavailableException();
    }

    private async Task<T?> SendAsync<T>(HttpMethod method, string relativeUrl, object? body, CancellationToken cancellationToken)
        where T : class
    {
        if (!_options.Enabled) throw new VocabCatalogUnavailableException();
        using var request = Request(method, relativeUrl, body);
        using var response = await SendRawAsync(request, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status == 200)
        {
            try
            {
                var envelope = await response.Content.ReadFromJsonAsync<Envelope<T>>(Json, cancellationToken).ConfigureAwait(false);
                if (envelope is { Success: true, Data: not null }) return envelope.Data;
            }
            catch (JsonException)
            {
            }
            throw new VocabCatalogUnavailableException();
        }

        if (status == 400) throw new VocabNotebookInvalidException();
        _logger?.LogWarning("Lexarbor integration endpoint answered {Status}.", status);
        throw new VocabCatalogUnavailableException();
    }

    private HttpRequestMessage Request(HttpMethod method, string relativeUrl, object? body)
    {
        var request = new HttpRequestMessage(method, _options.BaseUrl + "/" + relativeUrl);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        return request;
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _clients.CreateClient(HttpClientName).SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger?.LogWarning("Lexarbor integration request timed out.");
            throw new VocabCatalogUnavailableException();
        }
        catch (HttpRequestException)
        {
            _logger?.LogWarning("Lexarbor integration request failed.");
            throw new VocabCatalogUnavailableException();
        }
    }

    private sealed record Envelope<T>(bool Success, T? Data);

    private sealed record ItemList<T>(List<T> Items);

    // ReviewComment is absent from Lexarbor versions before its review outcome metadata; it then reads as null.
    private sealed record SubmissionDto(
        string Id, string Status, string Word, string? PartOfSpeech, string Meaning, string WordKey, string MeaningKey,
        bool DefinitionKept, string? ResultBookId, string? ResultWordId, string? ResultMeaningId, string? ResultWord,
        string? ResultPartOfSpeech, string? ResultMeaning, string? ResultWordKey, string? ResultMeaningKey,
        string? ReviewComment = null)
    {
        public VocabMeaningSubmission ToModel() => new(Id,
            Status switch
            {
                "approved" => VocabSubmissionStatus.Approved,
                "rejected" => VocabSubmissionStatus.Rejected,
                _ => VocabSubmissionStatus.Pending,
            },
            Word, PartOfSpeech, Meaning, WordKey, MeaningKey, DefinitionKept, ResultBookId, ResultWordId, ResultMeaningId,
            ResultWord, ResultPartOfSpeech, ResultMeaning, ResultWordKey, ResultMeaningKey, ReviewComment);
    }
}
