using System.Text;
using System.Text.RegularExpressions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Lexarbor.Service;

public sealed record AudioUploadHttpSettings(bool Enabled, long MaxBytes, int TimeoutSeconds);
public static class AudioUploadEndpoints
{
    public static void MapAudioUploadEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPut("/admin/vocabulary/{wordId}/audio/{locale}", HandleAsync)
            .RequireAuthorization(AdminEndpointAuthorization.PolicyName);
    }

    private static async Task<IResult> HandleAsync(HttpContext context, string wordId, string locale,
        [FromQuery] string? meaningId, AudioUploadService service, AudioUploadAdmission admission,
        AudioUploadHttpSettings settings, CancellationToken cancellationToken)
    {
        if (!settings.Enabled) return Error(503, "Audio storage is disabled.");
        if (locale is not ("en-GB" or "en-US")) return Error(400, "Audio locale must be en-GB or en-US.");
        if (meaningId != null && string.IsNullOrWhiteSpace(meaningId)) return Error(400, "Meaning ID is invalid.");
        var keys = context.Request.Headers["Idempotency-Key"];
        if (keys.Count != 1 || !Guid.TryParseExact(keys[0], "D", out var key)) return Error(400, "Idempotency-Key must be a UUID.");
        using var permit = await admission.EnterAsync(cancellationToken);
        if (permit == null)
        {
            context.Response.Headers.RetryAfter = "1";
            return Error(429, "Audio upload capacity is exhausted. Retry later.");
        }
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readTimeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        AudioUploadContent content;
        try
        {
            content = await ParseAsync(context, key.ToString(), wordId, meaningId, AudioLocales.FromCode(locale), settings.MaxBytes, readTimeout.Token);
        }
        catch (UploadInputException error) { return Error(error.Status, error.Message); }
        catch (BadHttpRequestException error) when (error.StatusCode == 413) { return Error(413, "Audio upload is too large."); }
        catch (Exception error) when (error is InvalidDataException or IOException or FormatException)
        { return Error(400, "Audio multipart body is invalid."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Error(503, "Audio upload timed out."); }
        var result = await service.UploadAsync(content, cancellationToken);
        return result.StatusCode == 200
            ? VocabularyHttpResponse.Ok(new { assetId = result.AssetId, sourceRevision = result.SourceRevision })
            : Error(result.StatusCode, result.StatusCode switch
            {
                404 => "Audio target was not found.",
                409 => "Audio upload conflicts with the target or idempotency key.",
                _ => "Audio storage is unavailable."
            });
    }

    private static IResult Error(int status, string message) => Results.Json(new { success = false, message }, statusCode: status);

    private static async Task<AudioUploadContent> ParseAsync(HttpContext context, string key, string wordId, string? meaningId,
        AudioLocale locale, long maxBytes, CancellationToken token)
    {
        var request = context.Request;
        var bodyLimit = maxBytes + 64 * 1024;
        if (request.ContentLength > bodyLimit) throw new UploadInputException(413, "Audio upload is too large.");
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature) feature.MaxRequestBodySize = bodyLimit;
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var type) || type.MediaType != "multipart/form-data")
            throw new UploadInputException(400, "Audio multipart body is invalid.");
        var boundary = HeaderUtilities.RemoveQuotes(type.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 128) throw new UploadInputException(400, "Audio multipart body is invalid.");
        var input = new LimitedReadStream(request.Body, bodyLimit);
        var reader = new MultipartReader(boundary, input) { HeadersCountLimit = 8, HeadersLengthLimit = 8192, BodyLengthLimit = bodyLimit };
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        byte[]? file = null;
        var sections = 0;
        try
        {
            while (await reader.ReadNextSectionAsync(token) is { } section)
            {
                if (++sections > 5 || !ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                    || disposition.DispositionType != "form-data") throw new UploadInputException(400, "Audio multipart body is invalid.");
                var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
                var isFile = disposition.FileName.HasValue || disposition.FileNameStar.HasValue;
                if (isFile)
                {
                    if (name != "file" || file != null) throw new UploadInputException(400, "Exactly one audio file is required.");
                    file = await ReadSectionAsync(section.Body, maxBytes, token);
                }
                else
                {
                    if (name is not ("sourceRevision" or "expectedAssetId" or "producer" or "voice") || fields.ContainsKey(name))
                        throw new UploadInputException(400, "Audio multipart fields are invalid.");
                    var bytes = await ReadSectionAsync(section.Body, 512, token);
                    try { fields.Add(name, new UTF8Encoding(false, true).GetString(bytes)); }
                    catch (DecoderFallbackException) { throw new UploadInputException(400, "Audio multipart fields are invalid."); }
                }
            }
        }
        catch (InvalidDataException)
        {
            // MultipartReader can reject a long epilogue before exposing its final boundary.
            // Finish only the already bounded raw stream to classify excess overhead safely.
            var drain = new byte[4096];
            while (await input.ReadAsync(drain.AsMemory(), token) != 0) { }
            if (file != null && input.BytesRead - file.LongLength > 64 * 1024)
                throw new UploadInputException(413, "Audio upload is too large.");
            throw;
        }
        // Count the epilogue too, including bodies sent without Content-Length.
        var remainder = new byte[4096];
        while (await input.ReadAsync(remainder.AsMemory(), token) != 0) { }
        if (file == null) throw new UploadInputException(400, "Exactly one audio file is required.");
        if (input.BytesRead - file.LongLength > 64 * 1024) throw new UploadInputException(413, "Audio upload is too large.");
        if (!fields.TryGetValue("sourceRevision", out var revision) || !Regex.IsMatch(revision, @"\A[a-f0-9]{64}\z", RegexOptions.CultureInvariant)
            || !fields.TryGetValue("expectedAssetId", out var expected)) throw new UploadInputException(400, "Audio upload conditions are required and must be valid.");
        if (expected != "" && (!Guid.TryParseExact(expected, "D", out var asset) || asset.ToString() != expected))
            throw new UploadInputException(400, "Audio upload conditions are required and must be valid.");
        var producer = fields.GetValueOrDefault("producer"); var voice = fields.GetValueOrDefault("voice");
        if (new[] { producer, voice }.Any(value => value != null && (value.Length > 128 || value.Any(char.IsControl))))
            throw new UploadInputException(400, "Audio producer and voice must be at most 128 characters without control characters.");
        if (!Mp3Validation.IsValid(file)) throw new UploadInputException(415, "Audio file must contain complete MP3 frames.");
        token.ThrowIfCancellationRequested();
        return new(key, wordId, meaningId, locale, revision, expected == "" ? null : expected, file, producer, voice);
    }

    private static async Task<byte[]> ReadSectionAsync(Stream input, long limit, CancellationToken token)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (output.Length + count > limit) throw new UploadInputException(413, "Audio upload is too large.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        return output.ToArray();
    }

    private sealed class UploadInputException(int status, string message) : Exception(message) { public int Status { get; } = status; }
    private sealed class LimitedReadStream(Stream source, long limit) : Stream
    {
        private long _read;
        public long BytesRead => _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        private int Count(int count) { if ((_read += count) > limit) throw new UploadInputException(413, "Audio upload is too large."); return count; }
        public override int Read(byte[] buffer, int offset, int count) => Count(source.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Count(await source.ReadAsync(buffer, cancellationToken));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
