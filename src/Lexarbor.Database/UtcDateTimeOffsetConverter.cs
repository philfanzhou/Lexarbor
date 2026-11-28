namespace Lexarbor.Database;

internal sealed class UtcDateTimeOffsetConverter()
    : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, DateTimeOffset>(
        value => value.ToUniversalTime(),
        value => value);
