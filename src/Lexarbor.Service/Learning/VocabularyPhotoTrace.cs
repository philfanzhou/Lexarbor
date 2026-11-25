using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Lexarbor.Learning.Service;

/// <summary>The vision model response and timing captured during a notebook photo import.</summary>
public sealed class VocabularyPhotoTrace
{
    public int? RecognitionStatus { get; set; }

    /// <summary>The response body as received, even when it breaks the recognition contract.</summary>
    public byte[]? RecognitionResponse { get; set; }

    public long? RecognitionMilliseconds { get; set; }

    /// <summary>Why the exchange failed before a usable answer, when it did.</summary>
    public string? RecognitionFailure { get; set; }

    public bool TimedOut { get; set; }
}

/// <summary>One finished photo import request. Candidates are exactly what the caller received.</summary>
public sealed record VocabularyPhotoTraceRecord(
    DateTimeOffset At,
    Guid StudentId,
    byte[] Photo,
    string ContentType,
    string Outcome,
    string? Error,
    long TotalMilliseconds,
    VocabularyPhotoTrace Recognition,
    IReadOnlyList<object>? Candidates);

/// <summary>
/// Keeps the most recent photo import requests as files for offline analysis: a JSON record and, for a JPEG or PNG,
/// the photo under the same name. Saving never fails the request; a failure is logged and the record is dropped.
/// </summary>
public sealed partial class VocabularyPhotoTraceStore(string directory, ILogger<VocabularyPhotoTraceStore> logger)
{
    public const string DirectoryName = "photo-import-traces";
    public const int Capacity = 50;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public string Root { get; } = directory;

    public async Task SaveAsync(VocabularyPhotoTraceRecord record)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!await TryWriteAsync(record).ConfigureAwait(false)) return;
            try
            {
                Prune();
            }
            catch (Exception error)
            {
                logger.LogWarning("Old photo import traces were not removed: {Error}.", error.GetType().Name);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> TryWriteAsync(VocabularyPhotoTraceRecord record)
    {
        var name = $"{record.At.UtcDateTime:yyyyMMdd'T'HHmmssfff'Z'}-{Guid.NewGuid().ToString("N")[..8]}";
        var photoFile = PhotoExtension(record.Photo) is { } extension ? name + extension : null;
        var temporary = Path.Combine(Root, name + ".json.tmp");
        try
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(Root);
            else Directory.CreateDirectory(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (photoFile is not null)
                await File.WriteAllBytesAsync(Path.Combine(Root, photoFile), record.Photo).ConfigureAwait(false);

            // The JSON appears last and whole, so a listed record always has its photo.
            await File.WriteAllBytesAsync(temporary, Serialize(record, photoFile)).ConfigureAwait(false);
            File.Move(temporary, Path.Combine(Root, name + ".json"));
            return true;
        }
        catch (Exception error)
        {
            logger.LogWarning("Photo import trace was not saved: {Error}.", error.GetType().Name);
            // A photo without its record would hold a place among the newest traces.
            TryDelete(temporary);
            if (photoFile is not null) TryDelete(Path.Combine(Root, photoFile));
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Deletes every file of the records older than the newest <see cref="Capacity"/>.</summary>
    private void Prune()
    {
        var stale = Directory.EnumerateFiles(Root)
            .Select(path => (Path: path, Match: TraceFileName().Match(Path.GetFileName(path))))
            .Where(file => file.Match.Success)
            .GroupBy(file => file.Match.Groups["name"].Value, StringComparer.Ordinal)
            .OrderByDescending(group => group.Key, StringComparer.Ordinal)
            .Skip(Capacity)
            .SelectMany(group => group);
        foreach (var file in stale) File.Delete(file.Path);
    }

    private static byte[] Serialize(VocabularyPhotoTraceRecord record, string? photoFile)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("at", record.At.UtcDateTime);
            writer.WriteString("studentId", record.StudentId);
            writer.WriteString("outcome", record.Outcome);
            writer.WriteString("error", record.Error);

            writer.WriteStartObject("photo");
            writer.WriteString("file", photoFile);
            writer.WriteString("contentType", record.ContentType);
            writer.WriteNumber("bytes", record.Photo.Length);
            writer.WriteString("sha256", Convert.ToHexStringLower(SHA256.HashData(record.Photo)));
            writer.WriteEndObject();

            writer.WriteStartObject("durationMs");
            writer.WriteNumber("total", record.TotalMilliseconds);
            WriteNumberOrNull(writer, "recognition", record.Recognition.RecognitionMilliseconds);
            writer.WriteEndObject();

            writer.WriteStartObject("visionModel");
            WriteNumberOrNull(writer, "status", record.Recognition.RecognitionStatus);
            writer.WriteString("failure", record.Recognition.RecognitionFailure);
            writer.WritePropertyName("response");
            WriteResponse(writer, record.Recognition.RecognitionResponse);
            writer.WriteEndObject();

            writer.WritePropertyName("candidates");
            if (record.Candidates is null) writer.WriteNullValue();
            else JsonSerializer.Serialize(writer, record.Candidates, JsonSerializerOptions.Web);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>JSON stays structured; anything else is kept as text so a broken answer can still be read.</summary>
    private static void WriteResponse(Utf8JsonWriter writer, byte[]? body)
    {
        if (body is null)
        {
            writer.WriteNullValue();
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            document.RootElement.WriteTo(writer);
        }
        catch (JsonException)
        {
            writer.WriteStringValue(System.Text.Encoding.UTF8.GetString(body));
        }
    }

    private static void WriteNumberOrNull(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is { } number) writer.WriteNumber(name, number);
        else writer.WriteNull(name);
    }

    private static string? PhotoExtension(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ".jpg";
        return bytes.Length >= 8 && bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })
            ? ".png"
            : null;
    }

    [GeneratedRegex(@"^(?<name>\d{8}T\d{9}Z-[0-9a-f]{8})\.")]
    private static partial Regex TraceFileName();
}
