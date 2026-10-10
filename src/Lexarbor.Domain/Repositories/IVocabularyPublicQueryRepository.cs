using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

/// <summary>
/// The reads behind the anonymous book-browse endpoints. Both methods see
/// only enabled books: a missing book answers the detail route's 404 and a
/// disabled one its 422, so no unit or entry of a disabled book is ever
/// listed.
/// </summary>
public interface IVocabularyPublicQueryRepository
{
    Task<VocabularyPublicUnitList> GetUnitsAsync(string bookId, CancellationToken cancellationToken);

    /// <param name="unitId">
    /// Null reads the whole book; otherwise only meanings assigned to that
    /// unit, whose places inside it are the entries' only positions. A unit
    /// of another book answers the same 404 as a missing one.
    /// </param>
    Task<VocabularyPublicEntryPage> GetEntriesAsync(
        string bookId, string? unitId, int page, int size, CancellationToken cancellationToken);
}
