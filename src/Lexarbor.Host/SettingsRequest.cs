using System.Text.Json;
using Lexarbor.Host.Configuration;

namespace Lexarbor.Host;

/// <summary>
/// The parts every settings endpoint shares: a bounded request-body read, the credential object
/// of a secret field, and the outcomes every save answers the same way. Parsing stays manual so
/// credential-bearing payloads never enter model binding, binder diagnostics or request-body logs.
/// </summary>
internal static class SettingsRequest
{
    /// <summary>The body, or null once it is known to exceed <paramref name="maxBytes"/>.</summary>
    internal static async Task<byte[]?> ReadBodyAsync(HttpContext context, int maxBytes, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var bytes = new byte[1024];
        while (true)
        {
            var count = await context.Request.Body.ReadAsync(bytes, cancellationToken);
            if (count == 0) break;
            if (buffer.Length + count > maxBytes) return null;
            buffer.Write(bytes, 0, count);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// The credential object <c>{ action, value }</c>: both are strings, <c>value</c> accompanies
    /// only <c>replace</c>, and a replacement is never blank nor, where a limit applies, longer
    /// than <paramref name="maxReplaceLength"/>.
    /// </summary>
    internal static bool TryReadCredential(JsonElement element, int? maxReplaceLength, out string? action, out string? value)
    {
        action = null; value = null;
        if (element.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateObject())
        {
            if (!seen.Add(item.Name) || item.Value.ValueKind != JsonValueKind.String) return false;
            if (item.Name == "action") action = item.Value.GetString();
            else if (item.Name == "value") value = item.Value.GetString();
            else return false;
        }
        return action switch
        {
            "replace" => !string.IsNullOrWhiteSpace(value) && (maxReplaceLength is null || value.Length <= maxReplaceLength),
            "keep" or "clear" => !seen.Contains("value"),
            _ => false
        };
    }

    /// <summary>
    /// The failure every settings save answers the same way: a missing, invalid or superseded
    /// revision, and the storage failure for any other outcome the module does not name itself.
    /// </summary>
    internal static IResult SaveFailure(ConfigurationSaveStatus status) => status switch
    {
        ConfigurationSaveStatus.MissingRevision => SettingsError.Write(428, "If-Match is required. Reload settings."),
        ConfigurationSaveStatus.InvalidRevision => SettingsError.Write(400, "Invalid If-Match. Reload settings."),
        ConfigurationSaveStatus.Conflict => SettingsError.Write(412, "Settings changed. Reload settings; offline edits require a restart."),
        _ => SettingsError.Write(503, ConfigurationSaveResult.StorageFailureMessage)
    };

    /// <summary>409 naming the fields a deployment setting overrides.</summary>
    internal static IResult Overridden(IEnumerable<string>? fields)
        => Results.Json(new { message = "Settings are overridden by deployment configuration.", fields }, statusCode: 409);
}
