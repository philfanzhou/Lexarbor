using System.Text.Json;

namespace Lexarbor.Host.Integration;

/// <summary>
/// The reads and JSON checks every supplier client shares: a bounded response body, the
/// Retry-After header, and the rules a supplier answer must pass before any of it is trusted —
/// no property name repeated anywhere in the document, no credential echoed back, and strings
/// kept short and free of control characters.
/// </summary>
internal static class SupplierJson
{
    /// <summary>The response ceiling every supplier client shares unless it asks for another.</summary>
    internal const int DefaultBodyBytes = 65536;

    /// <summary>The response bytes, or null once the body is known to exceed <paramref name="limit"/>.</summary>
    internal static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken token, int limit = DefaultBodyBytes)
    {
        if (content.Headers.ContentLength > limit) return null;
        await using var stream = await content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit + 1 - (int)output.Length)), token);
            if (read == 0) return output.ToArray();
            output.Write(buffer, 0, read);
            if (output.Length > limit) return null;
        }
    }

    /// <summary>The positive Retry-After delay a throttling response asks for, if any.</summary>
    internal static TimeSpan? RetryAfter(HttpResponseMessage response, TimeProvider time)
    {
        var header = response.Headers.RetryAfter;
        var delay = header?.Delta ?? (header?.Date is { } date ? date - time.GetUtcNow() : null);
        return delay is { } value && value > TimeSpan.Zero ? value : null;
    }

    /// <summary>False when any object in the document repeats a property name.</summary>
    internal static bool UniqueProperties(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == element.EnumerateObject().Count()
            && element.EnumerateObject().All(p => UniqueProperties(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().All(UniqueProperties),
        _ => true
    };

    /// <summary>True when the key appears in any string value or any property name.</summary>
    internal static bool ContainsCredential(JsonElement element, string key) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString()!.Contains(key, StringComparison.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Any(value => ContainsCredential(value, key)),
        JsonValueKind.Object => element.EnumerateObject().Any(property => property.Name.Contains(key, StringComparison.Ordinal) || ContainsCredential(property.Value, key)),
        _ => false
    };

    /// <summary>A required value: present, within the maximum, and free of control characters.</summary>
    internal static bool Bounded(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);

    /// <summary>An optional value: absent, or within the maximum and free of control characters.</summary>
    internal static bool Optional(string? value, int maximum) => value == null || value.Length <= maximum && !value.Any(char.IsControl);

    /// <summary>The trimmed value, or null for blank text.</summary>
    internal static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
