using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Lexarbor.Learning.Domain.Services;
using Microsoft.Extensions.Logging;

namespace Lexarbor.Learning.Service;

public sealed class StructaDocPhotoRecognizer : IVocabularyPhotoRecognizer
{
    public const string HttpClientName = "StructaDocRecognition";

    /// <summary>How much of a refusal's body the trace keeps.</summary>
    private const int RefusalBodyLimit = 64 * 1024;

    /// <summary>A stalled refusal body must not turn the refusal into a timeout.</summary>
    private static readonly TimeSpan RefusalReadTimeout = TimeSpan.FromSeconds(5);

    private readonly IHttpClientFactory _clients;
    private readonly VocabularyPhotoImportOptions _options;
    private readonly VocabularyPhotoTrace _trace;
    private readonly ILogger<StructaDocPhotoRecognizer> _logger;

    public StructaDocPhotoRecognizer(IHttpClientFactory clients, VocabularyPhotoImportOptions options, VocabularyPhotoTrace trace,
        ILogger<StructaDocPhotoRecognizer> logger)
    {
        _clients = clients;
        _options = options;
        _trace = trace;
        _logger = logger;
    }

    public async Task<IReadOnlyList<VocabRecognizedBlock>> RecognizeAsync(VocabPhoto photo, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) throw new VocabPhotoImportUnavailableException("Photo import is turned off.");

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(_options.RecognitionBudgetSeconds));
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(photo.Content);
            file.Headers.ContentType = new MediaTypeHeaderValue(photo.ContentType == "image/png" ? "image/png" : "image/jpeg");
            form.Add(file, "file", photo.ContentType == "image/png" ? "photo.png" : "photo.jpg");
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_options.BaseUrl + "/"), "api/v1/image/recognize"))
            {
                Content = form,
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("ApiKey", _options.ApiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _clients.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            _trace.RecognitionStatus = status;
            if (response.StatusCode != HttpStatusCode.OK)
                _trace.RecognitionResponse = await TryReadRefusalAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnsupportedMediaType)
            {
                _logger.LogWarning("StructaDoc refused the vocabulary photo with status {Status}.", status);
                throw new VocabPhotoInvalidException();
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                _logger.LogWarning("StructaDoc image recognition answered status {Status}.", status);
                throw new VocabPhotoImportUnavailableException();
            }

            var body = await ReadBoundedAsync(response.Content, _options.MaxResponseBytes, budget.Token).ConfigureAwait(false);
            _trace.RecognitionResponse = body;
            var blocks = Parse(body);
            _logger.LogInformation("StructaDoc recognized {Blocks} blocks with {Lines} lines.", blocks.Count, blocks.Sum(block => block.Lines.Count));
            return blocks;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is OperationCanceledException or HttpRequestException or IOException)
        {
            _trace.RecognitionFailure = error is OperationCanceledException
                ? $"No answer within {_options.RecognitionBudgetSeconds} seconds."
                : $"{error.GetType().Name}: {error.Message}";
            _logger.LogWarning("StructaDoc image recognition failed: {Error}.", error.GetType().Name);
            throw new VocabPhotoImportUnavailableException();
        }
        finally
        {
            _trace.RecognitionMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
    }

    /// <summary>The response's blocks in reading order; a missing or ill-typed field breaks the contract.</summary>
    internal static IReadOnlyList<VocabRecognizedBlock> Parse(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("blocks", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
                throw new VocabPhotoImportUnavailableException("StructaDoc answered without blocks.");
            var result = new List<VocabRecognizedBlock>();
            foreach (var block in blocks.EnumerateArray())
            {
                var type = block.GetProperty("type").GetString() ?? throw new VocabPhotoImportUnavailableException();
                var lines = new List<VocabRecognizedLine>();
                if (block.TryGetProperty("lines", out var lineArray) && lineArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var line in lineArray.EnumerateArray())
                    {
                        var box = line.GetProperty("bbox");
                        lines.Add(new VocabRecognizedLine(line.GetProperty("text").GetString() ?? string.Empty,
                            box.GetProperty("x0").GetDouble(), box.GetProperty("y0").GetDouble(),
                            box.GetProperty("x1").GetDouble(), box.GetProperty("y1").GetDouble()));
                    }
                }

                result.Add(new VocabRecognizedBlock(type, OptionalText(block, "content"), OptionalText(block, "contentFormat"), lines));
            }

            return result;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new VocabPhotoImportUnavailableException("StructaDoc answered outside the recognition contract.");
        }
    }

    private static string? OptionalText(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// The start of a refusal's body for the trace. It has its own short deadline, apart from the recognition budget,
    /// and failing to read it never changes how the refusal is answered; only the caller's cancellation stops it.
    /// </summary>
    private static async Task<byte[]?> TryReadRefusalAsync(HttpContent content, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(RefusalReadTimeout);
        try
        {
            await using var stream = await content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            var buffer = new byte[RefusalBodyLimit];
            var length = 0;
            int read;
            while (length < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(length), deadline.Token).ConfigureAwait(false)) > 0)
                length += read;
            return buffer[..length];
        }
        catch (Exception error) when (error is HttpRequestException or IOException
                                          || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > limit) throw new VocabPhotoImportUnavailableException("StructaDoc's answer is too large.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit) throw new VocabPhotoImportUnavailableException("StructaDoc's answer is too large.");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
