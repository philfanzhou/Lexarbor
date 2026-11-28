namespace Lexarbor.Database.Repositories;

/// <summary>
/// Builds the substring patterns used by the keyword searches.
/// </summary>
/// <remarks>
/// The searches match with <c>ILIKE</c> over the <c>"C"</c>-collated text
/// columns, which folds ASCII letters only: <c>apple</c> finds <c>Apple</c>,
/// while <c>é</c> does not find <c>É</c>. That is the folding SQLite's
/// <c>LIKE</c> applied before the move to PostgreSQL, and the same folding
/// <c>lower()</c> gives the question and equivalence queries, so the whole
/// codebase agrees on what "the same text" means. A leading-wildcard match
/// cannot use an ordinary index on either engine.
/// </remarks>
internal static class SearchPattern
{
    /// <summary>Backslash, declared with an explicit ESCAPE clause.</summary>
    internal const char EscapeCharacter = '\\';

    /// <summary>
    /// Wraps a user-supplied keyword in wildcards, escaping the characters LIKE
    /// would otherwise read as wildcards. Without this, a keyword containing
    /// <c>%</c> or <c>_</c> would silently widen the search.
    /// </summary>
    internal static string Contains(string keyword)
    {
        var escaped = keyword
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
        return $"%{escaped}%";
    }
}
