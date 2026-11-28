using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace Lexarbor.Learning.Service;

/// <summary>The query-string reads more than one learning endpoint shares.</summary>
internal static class LearningInput
{
    /// <summary>
    /// True when the query names no such key, so <paramref name="value"/> keeps its default, or
    /// names it exactly once with an invariant integer.
    /// </summary>
    internal static bool TryReadInt(HttpRequest request, string key, int defaultValue, out int value)
    {
        value = defaultValue;
        return !request.Query.TryGetValue(key, out var values) ||
            (values.Count == 1 && int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out value));
    }
}
