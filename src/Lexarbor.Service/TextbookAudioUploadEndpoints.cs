using System.Security.Cryptography;
using System.Text;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Lexarbor.Service;

public sealed class TextbookAudioUploadBoundary;

public static class TextbookAudioUploadEndpoints
{
    private const long Overhead = 64 * 1024;
    private const long HardLimit = 128 * 1024 * 1024;
    private const string InvalidBody = "Textbook audio multipart body is invalid.";
    public static void MapTextbookAudioUploadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/admin/vocabulary-books/{bookId}/units/{unitId}/audio-tracks")
            .RequireAuthorization(AdminEndpointAuthorization.PolicyName);
        group.MapGet("/upload-policy", PolicyAsync);
        group.MapPut("/{trackId}/content", UploadAsync).WithMetadata(new TextbookAudioUploadBoundary());
    }

    private static async Task<IResult> PolicyAsync(string bookId, string unitId, ITextbookAudioUploadOperationFactory operations,
        ITextbookAudioTrackRepository tracks, CancellationToken token)
    {
        bookId = Id(bookId); unitId = Id(unitId);
        using var operation = await operations.AcquireAsync(token);
        await tracks.ReadSnapshotAsync(async () =>
        {
            if (!await tracks.BookExistsAsync(bookId, token)) throw new ResourceNotFoundException("Vocabulary book was not found.");
            var owner = await tracks.GetUnitBookAsync(unitId, token) ?? throw new ResourceNotFoundException("Vocabulary book unit was not found.");
            if (owner != bookId) throw new ConflictException("Vocabulary book unit does not belong to the requested vocabulary book.");
            return true;
        }, token);
        return VocabularyHttpResponse.Ok(new
        {
            enabled = operation.Settings.Enabled,
            maxUploadBytes = operation.Settings.MaxBytes,
            maxDurationSeconds = operation.Settings.MaxDurationSeconds
        });
    }

    private static async Task<IResult> UploadAsync(HttpContext context, string bookId, string unitId, string trackId,
        ITextbookAudioUploadOperationFactory operations, AudioUploadAdmission admission, ITextbookMp3Validator validator,
        CancellationToken cancellationToken)
    {
        bookId = Id(bookId); unitId = Id(unitId); trackId = Id(trackId);
        using var operation = await operations.AcquireAsync(cancellationToken);
        var settings = operation.Settings;
        if (!settings.Enabled) return Error(503, "Audio storage is disabled.");
        var keys = context.Request.Headers["Idempotency-Key"];
        if (keys.Count != 1 || !Canonical(keys[0])) return Error(400, "Idempotency-Key must be a canonical lowercase UUID.");
        var key = keys[0]!;
        using var permit = await admission.EnterAsync(cancellationToken);
        if (permit is null)
        {
            context.Response.Headers.RetryAfter = "1";
            return Error(429, "Audio upload capacity is exhausted. Retry later.");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        try
        {
            var stored = await operation.Service.FindAsync(key, timeout.Token);
            var limit = stored?.Asset.ByteLength ?? settings.MaxBytes;
            if (limit is < 1 or > HardLimit) return Error(503, "Audio storage is unavailable.");
            byte[] file; string version;
            try { (file, version) = await ParseAsync(context, limit, stored is not null, timeout.Token); }
            catch (IOException) { throw new InputException(400, InvalidBody); }
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (var offset = 0; offset < file.Length; offset += 8192)
            { timeout.Token.ThrowIfCancellationRequested(); sha.AppendData(file.AsSpan(offset, Math.Min(8192, file.Length - offset))); }
            var hash = Convert.ToHexStringLower(sha.GetHashAndReset());
            if (stored is not null)
                return Response(TextbookAudioUploadService.Match(stored,
                    TextbookAudioUploadService.Fingerprint(bookId, unitId, trackId, version, hash, file.LongLength)));
            var duration = validator.Validate(file, settings.MaxDurationSeconds, TimeSpan.FromSeconds(settings.TimeoutSeconds), timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            return Response(await operation.Service.UploadAsync(new(key, bookId, unitId, trackId, version, file, hash, duration), timeout.Token, permit.Retain));
        }
        catch (InputException exception) { return Error(exception.Status, exception.Message); }
        catch (TextbookMediaException exception)
        {
            return Error(exception.StatusCode, exception.StatusCode == 422
            ? "Textbook audio duration exceeds the configured limit." : "Textbook audio file must contain complete decodable MP3 frames.");
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
        { return Error(413, "Textbook audio file is too large."); }
        catch (Exception exception) when (exception is InvalidDataException or FormatException)
        { return Error(400, InvalidBody); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return Error(503, "Audio storage is unavailable."); }
        catch (StorageBusyException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return Error(503, "Audio storage is unavailable."); }
    }

    private static IResult Response(TextbookAudioUploadResult result) => result.StatusCode == 200
        ? VocabularyHttpResponse.Ok(new { trackId = result.TrackId, version = result.Version, durationSeconds = result.DurationSeconds })
        : Error(result.StatusCode, result.StatusCode switch
        {
            404 => "Textbook audio target was not found.",
            409 => "Textbook audio upload conflicts with the target or idempotency key.",
            _ => "Audio storage is unavailable."
        });
    private static IResult Error(int status, string message) => Results.Json(new { success = false, message }, statusCode: status);
    private static bool Canonical(string? value) => Guid.TryParseExact(value, "D", out var id) && id.ToString("D") == value;
    private static string Id(string value) => !string.IsNullOrWhiteSpace(value) ? value.Trim() : throw new DomainValidationException("An ID is required.");

    private static async Task<(byte[] File, string Version)> ParseAsync(HttpContext context, long limit, bool matching, CancellationToken token)
    {
        var request = context.Request;
        var bodyLimit = limit + Overhead;
        if (request.ContentLength > bodyLimit) throw new InputException(413, "Textbook audio file is too large.");
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature) feature.MaxRequestBodySize = bodyLimit;
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var type) || type.MediaType != "multipart/form-data") throw new InputException(400, InvalidBody);
        var boundary = HeaderUtilities.RemoveQuotes(type.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 128) throw new InputException(400, InvalidBody);
        var input = new LimitedStream(request.Body, bodyLimit);
        var reader = new MultipartReader(boundary, input) { HeadersCountLimit = 8, HeadersLengthLimit = 8192, BodyLengthLimit = bodyLimit };
        byte[]? file = null;
        string? version = null;
        var count = 0;
        try
        {
            while (await reader.ReadNextSectionAsync(token) is { } section)
            {
                if (++count > 2 || !ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                    || disposition.DispositionType != "form-data") throw new InputException(400, InvalidBody);
                var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
                var isFile = disposition.FileName.HasValue || disposition.FileNameStar.HasValue;
                if (isFile && name == "file" && file is null)
                    file = await ReadAsync(section.Body, limit, matching ? 409 : 413, token);
                else if (!isFile && name == "expectedVersion" && version is null)
                {
                    var bytes = await ReadAsync(section.Body, 36, 400, token);
                    try { version = new UTF8Encoding(false, true).GetString(bytes); }
                    catch (DecoderFallbackException) { throw new InputException(400, InvalidBody); }
                }
                else throw new InputException(400, InvalidBody);
            }
        }
        catch (InvalidDataException)
        {
            await DrainAsync(input, token);
            if (input.BytesRead - (file?.LongLength ?? 0) > Overhead) throw new InputException(413, "Textbook audio file is too large.");
            throw;
        }
        await DrainAsync(input, token);
        if (file is null || !Canonical(version) || count != 2) throw new InputException(400, InvalidBody);
        if (input.BytesRead - file.LongLength > Overhead) throw new InputException(413, "Textbook audio file is too large.");
        return (file, version!);
    }
    private static async Task DrainAsync(Stream input, CancellationToken token)
    { var bytes = new byte[4096]; while (await input.ReadAsync(bytes, token) != 0) { } }
    private static async Task<byte[]> ReadAsync(Stream input, long limit, int status, CancellationToken token)
    {
        using var output = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(bytes, token)) != 0)
        {
            if (output.Length + count > limit) throw new InputException(status, status switch
            { 413 => "Textbook audio file is too large.", 409 => "Textbook audio upload conflicts with the target or idempotency key.", _ => InvalidBody });
            await output.WriteAsync(bytes.AsMemory(0, count), token);
        }
        return output.ToArray();
    }
    private sealed class InputException(int status, string message) : Exception(message) { public int Status { get; } = status; }
    private sealed class LimitedStream(Stream source, long limit) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        private int Count(int count) { if ((BytesRead += count) > limit) throw new InputException(413, "Textbook audio file is too large."); return count; }
        public override int Read(byte[] buffer, int offset, int count) => Count(source.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => Count(await source.ReadAsync(buffer, token));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
