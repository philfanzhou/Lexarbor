namespace Lexarbor.Database.Repositories;

/// <summary>
/// Draws a small random window of question candidates instead of sorting a whole
/// vocabulary book.
/// </summary>
/// <remarks>
/// <para>
/// Both distractor queries used <c>ORDER BY random()</c> over every candidate in
/// the book, which SQLite can only answer with a temp B-tree over all of them,
/// so the cost of one anonymous question grew with the size of the book it was
/// asked about. Measured before this change: 2.2 ms at 300 words, 5.4 ms at
/// 2000, and 56 ms at 20000 -- and 129 ms for the English-to-Chinese direction,
/// which also ran two window functions across the same rows.
/// </para>
/// <para>
/// Every vocabulary id in this database is a v4 GUID, written that way by the
/// domain service and by the former bundled seed alike, so id order is already an
/// arbitrary and uniform shuffle of the book. Starting at a random point in that
/// order and reading forward is therefore a random sample, and it is one an
/// index can walk: the window is a seek plus a handful of rows rather than a
/// sort of everything.
/// </para>
/// </remarks>
internal static class RandomCandidateWindow
{
    /// <summary>
    /// Candidates read for each one returned. The window is shuffled and then
    /// truncated, so over-fetching is what keeps the three options from being
    /// three consecutive ids -- which would pair the same words together in
    /// every question they appeared in.
    /// </summary>
    private const int OverfetchFactor = 4;

    internal static int SizeFor(int count) => count * OverfetchFactor;

    /// <summary>
    /// A random point in the id space to start reading from. A GUID rather than
    /// a number because that is the shape of the column being compared.
    /// </summary>
    internal static string NewProbe() => Guid.NewGuid().ToString();

    /// <summary>
    /// Appends the rows of a wrapped-around second window that the first did not
    /// already contain.
    /// </summary>
    internal static List<T> Merge<T>(List<T> first, List<T> second, Func<T, string> keySelector)
    {
        var seen = new HashSet<string>(first.Select(keySelector), StringComparer.Ordinal);
        first.AddRange(second.Where(item => seen.Add(keySelector(item))));
        return first;
    }

    /// <summary>Fisher-Yates, in place.</summary>
    internal static List<T> Shuffle<T>(List<T> items)
    {
        for (var index = items.Count - 1; index > 0; index--)
        {
            var swapWith = Random.Shared.Next(index + 1);
            (items[index], items[swapWith]) = (items[swapWith], items[index]);
        }

        return items;
    }
}
