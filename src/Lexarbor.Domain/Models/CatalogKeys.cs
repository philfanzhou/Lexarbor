namespace Lexarbor.Domain.Models;

/// <summary>
/// The comparison key the public entries read calls <c>normalizedWord</c> and
/// <c>meaningKey</c>: an ASCII-only fold, the value SQLite's
/// <c>lower(trim(text))</c> produced before the move to PostgreSQL. Computed in
/// process for text that is not stored yet, so a client keying its records by
/// these values sees one rule.
/// </summary>
public static class CatalogKeys
{
    public static string Of(string text)
    {
        var trimmed = text.Trim(' ');
        return string.Create(trimmed.Length, trimmed, static (span, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                var character = source[index];
                span[index] = character is >= 'A' and <= 'Z' ? (char)(character + 32) : character;
            }
        });
    }
}
