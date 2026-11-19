using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lexarbor.Learning.Service;

/// <summary>Require an explicit UTC offset rather than interpreting an unspecified local time.</summary>
internal sealed class AudioExpiryConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || reader.GetString() is not { } value
            || !(value.EndsWith("Z", StringComparison.Ordinal) || value.EndsWith("+00:00", StringComparison.Ordinal))
            || !reader.TryGetDateTimeOffset(out var result) || result.Offset != TimeSpan.Zero)
            throw new JsonException("Audio expiry must be a UTC ISO timestamp.");
        return result;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime());
}
