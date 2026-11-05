using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lexarbor.Domain.Models;
using Lexarbor.Host.Configuration;
using Lexarbor.Host.Integration;

namespace Lexarbor.Host;

public static class SpeechPreviewEndpoints
{
    public const string Path = EnrichmentSettingsEndpoints.Path + "/speech-preview";

    internal static async Task<IResult> GenerateAsync(HttpContext context, PersistentConfigurationCoordinator coordinator,
        EnrichmentRuntimeConfiguration runtime, PronunciationSpeechClient speech, SpeechPreviewAdmission admission)
    {
        context.Response.Headers.CacheControl = "no-store";
        var revision = context.Request.Headers.IfMatch.ToString();
        if (revision.Length == 0) return Error(428, "revision_required");
        if (!Regex.IsMatch(revision, "^\"[0-9a-f]{32}\"$", RegexOptions.CultureInvariant)) return Error(400, "invalid_revision");
        using var buffer = new MemoryStream();
        var bytes = new byte[1024];
        while (true)
        {
            var count = await context.Request.Body.ReadAsync(bytes, context.RequestAborted);
            if (count == 0) break;
            if (buffer.Length + count > 16384) return Error(413, "request_too_large");
            buffer.Write(bytes, 0, count);
        }
        var changes = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        var supplied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? locale = null, sample = null;
        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return Error(400, "invalid_preview");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!seen.Add(property.Name)) return Error(400, "invalid_preview");
                switch (property.Name)
                {
                    case "locale" when property.Value.ValueKind == JsonValueKind.String: locale = property.Value.GetString(); break;
                    case "sample" when property.Value.ValueKind == JsonValueKind.String: sample = property.Value.GetString(); break;
                    case "tts" when property.Value.ValueKind == JsonValueKind.Object:
                        if (property.Value.EnumerateObject().Any()
                            && !EnrichmentSettingsEndpoints.ReadChanges(property.Value, "enrichment.tts", changes, supplied))
                            return Error(400, "invalid_preview");
                        break;
                    default: return Error(400, "invalid_preview");
                }
            }
            if (locale is not ("uk" or "us") || sample is not ("word" or "sentence") || !seen.Contains("tts"))
                return Error(400, "invalid_preview");
        }
        catch (JsonException) { return Error(400, "invalid_preview"); }

        var captured = await coordinator.ReadAsync(read => (Read: read, Snapshot: runtime.Current), context.RequestAborted);
        if (revision != captured.Read.Revision) return Error(412, "settings_changed");
        if (changes.Keys.Any(key => !captured.Read.Sources[key].Editable)) return Error(409, "configuration_overridden");
        var fields = captured.Snapshot.Fields.ToDictionary(p => p.Key, p => p.Value?.DeepClone(), StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes) fields[change.Key] = change.Value;
        // Parse with the same normalization and validation as persisted configuration, without publishing it.
        EnrichmentSettings candidate;
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(fields.Select(p =>
                new KeyValuePair<string, string?>(p.Key, p.Value?.ToString()))).Build();
            using var lifetime = config as IDisposable;
            candidate = new(EnrichmentConfigurationFields.Read(config));
        }
        catch (InvalidOperationException) { return Error(400, "invalid_preview"); }
        using var lease = admission.Acquire(out var retry);
        if (lease == null)
        {
            context.Response.Headers.RetryAfter = retry.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Error(429, "preview_rate_limited");
        }
        var result = await speech.PreviewAsync(candidate, locale == "uk" ? AudioLocale.EnGb : AudioLocale.EnUs, sample, context.RequestAborted);
        if (result.Outcome == PronunciationSpeechOutcome.Ready && result.Audio != null)
            return Results.Bytes(result.Audio, "audio/mpeg");
        var status = result.Outcome switch
        {
            PronunciationSpeechOutcome.Throttled => 429,
            PronunciationSpeechOutcome.ServiceFailure or PronunciationSpeechOutcome.InvalidAudio => 502,
            _ => 400
        };
        if (result.RetryAfter is { } delay)
            context.Response.Headers.RetryAfter = Math.Clamp(Math.Ceiling(delay.TotalSeconds), 1, 86400).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Error(status, result.ErrorCode ?? "preview_failed");
    }

    private static IResult Error(int status, string code) => Results.Json(new { message = "Speech preview could not be generated.", code }, statusCode: status);
}
