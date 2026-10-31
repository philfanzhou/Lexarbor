using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Host.Integration;

/// <summary>
/// Optional review advice from an OpenAI-compatible chat-completions endpoint.
/// It is off unless <c>MeaningSubmissions:Ai:Endpoint</c> and <c>Model</c> are
/// set. The model only advises; nothing is approved or rejected without an
/// administrator. The submission text is the only data sent.
/// </summary>
public sealed class OpenAiCompatibleReviewAssistant : IMeaningSubmissionReviewAssistant
{
    public const string HttpClientName = "MeaningSubmissionReview";
    public const string Section = "MeaningSubmissions:Ai";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string Instructions =
        "You review vocabulary definitions proposed by learners of English whose first language is Chinese. " +
        "Given an English word or phrase, an optional part of speech and a proposed Chinese definition, decide " +
        "whether the definition is correct and suitable for a learner's dictionary. Answer with one JSON object " +
        "with the fields verdict (accept, revise or reject), partOfSpeech, meaning (a corrected concise Chinese " +
        "definition), phoneticUk, phoneticUs (IPA without slashes, or null for phrases) and comment (one short " +
        "Chinese sentence explaining the verdict). Reject spellings that are not English words or phrases.";

    private readonly IHttpClientFactory _clients;
    private readonly ReviewAssistantOptions _options;

    public OpenAiCompatibleReviewAssistant(IHttpClientFactory clients, ReviewAssistantOptions options)
    {
        _clients = clients;
        _options = options;
    }

    public bool IsEnabled => _options.IsEnabled;

    public async Task<MeaningSubmissionReviewAdvice> ReviewAsync(
        MeaningSubmissionModel submission,
        CancellationToken cancellationToken = default)
    {
        var client = _clients.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint!.TrimEnd('/') + "/chat/completions");
        if (!string.IsNullOrEmpty(_options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        var proposal = JsonSerializer.Serialize(new
        {
            word = submission.Word,
            partOfSpeech = submission.PartOfSpeech,
            meaning = submission.Meaning,
            example = submission.Example
        }, Json);
        request.Content = JsonContent.Create(new
        {
            model = _options.Model,
            temperature = 0,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = Instructions },
                new { role = "user", content = proposal }
            }
        });

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new ReviewAssistantUnavailableException();
            }

            var completion = await response.Content.ReadFromJsonAsync<Completion>(Json, cancellationToken);
            var content = completion?.Choices?.FirstOrDefault()?.Message?.Content;
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new ReviewAssistantUnavailableException();
            }

            var advice = JsonSerializer.Deserialize<Advice>(StripFence(content), Json)
                ?? throw new ReviewAssistantUnavailableException();
            var verdict = advice.Verdict?.Trim().ToLowerInvariant();
            if (verdict is not ("accept" or "revise" or "reject"))
            {
                throw new ReviewAssistantUnavailableException();
            }

            return new MeaningSubmissionReviewAdvice(
                verdict, Clean(advice.PartOfSpeech), Clean(advice.Meaning),
                Clean(advice.PhoneticUk), Clean(advice.PhoneticUs), Clean(advice.Comment));
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or TaskCanceledException
                                          && !cancellationToken.IsCancellationRequested)
        {
            throw new ReviewAssistantUnavailableException();
        }
    }

    // Some compatible servers wrap JSON in a Markdown code fence despite response_format.
    private static string StripFence(string content)
    {
        var text = content.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var start = text.IndexOf('\n');
        var end = text.LastIndexOf("```", StringComparison.Ordinal);
        return start >= 0 && end > start ? text[(start + 1)..end] : text;
    }

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > 500 ? trimmed[..500] : trimmed;
    }

    private sealed record Completion([property: JsonPropertyName("choices")] List<Choice>? Choices);

    private sealed record Choice([property: JsonPropertyName("message")] Message? Message);

    private sealed record Message([property: JsonPropertyName("content")] string? Content);

    private sealed record Advice(
        string? Verdict, string? PartOfSpeech, string? Meaning, string? PhoneticUk, string? PhoneticUs, string? Comment);
}

public sealed class ReviewAssistantOptions
{
    public string? Endpoint { get; init; }
    public string? Model { get; init; }
    public string? ApiKey { get; init; }
    public int TimeoutSeconds { get; init; } = 30;

    public bool IsEnabled => !string.IsNullOrWhiteSpace(Endpoint) && !string.IsNullOrWhiteSpace(Model);

    public static ReviewAssistantOptions Read(IConfiguration configuration)
    {
        var section = configuration.GetSection(OpenAiCompatibleReviewAssistant.Section);
        var endpoint = section["Endpoint"]?.Trim();
        if (!string.IsNullOrEmpty(endpoint)
            && (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                || uri.UserInfo.Length != 0 || uri.Query.Length != 0))
        {
            throw new InvalidOperationException($"{OpenAiCompatibleReviewAssistant.Section}:Endpoint is invalid.");
        }

        var timeout = 30;
        var timeoutText = section["TimeoutSeconds"];
        if (timeoutText != null && (!int.TryParse(timeoutText, out timeout) || timeout is < 1 or > 300))
        {
            throw new InvalidOperationException($"{OpenAiCompatibleReviewAssistant.Section}:TimeoutSeconds must be 1 to 300.");
        }

        return new ReviewAssistantOptions
        {
            Endpoint = string.IsNullOrEmpty(endpoint) ? null : endpoint,
            Model = section["Model"]?.Trim(),
            ApiKey = section["ApiKey"],
            TimeoutSeconds = timeout
        };
    }
}

public sealed class ReviewAssistantUnavailableException : Exception
{
    public ReviewAssistantUnavailableException()
        : base("The review assistant did not return usable advice.")
    {
    }
}
