using Lexarbor.Learning.Domain.Models;

namespace Lexarbor.Learning.Domain.Repositories;
public interface IMemoryCardRepository
{

    Task<MemoryCardModel?> FindAsync(Guid studentId, string wordKey, string meaningKey, CancellationToken cancellationToken = default);

    Task AddAsync(MemoryCardModel card, CancellationToken cancellationToken = default);

    Task UpdateAsync(MemoryCardModel card, CancellationToken cancellationToken = default);

    Task<List<MemoryCardModel>> GetDueAsync(Guid studentId, DateTimeOffset now, IReadOnlyCollection<CardKey>? keys = null, int limit = 100, CancellationToken cancellationToken = default);

    /// <summary>Read a due page strictly after the complete cursor, using the same stable ordering.</summary>
    Task<List<MemoryCardModel>> GetDuePageAsync(Guid studentId, DateTimeOffset now, MemoryCardCursor? after, int limit, CancellationToken cancellationToken = default);

    Task<HashSet<CardKey>> FindExistingKeysAsync(Guid studentId, IReadOnlyCollection<CardKey> keys, CancellationToken cancellationToken = default);

    Task<bool> HasWordAsync(Guid studentId, string wordKey, CancellationToken cancellationToken = default);

    Task<int> CountDueAsync(Guid studentId, DateTimeOffset now, CancellationToken cancellationToken = default);
}
