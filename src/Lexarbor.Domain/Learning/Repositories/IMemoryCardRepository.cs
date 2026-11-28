using Lexarbor.Learning.Domain.Models;

namespace Lexarbor.Learning.Domain.Repositories;

public interface IMemoryCardRepository
{

    Task<MemoryCard?> FindAsync(Guid studentId, string wordKey, string meaningKey, CancellationToken cancellationToken = default);

    Task AddAsync(MemoryCard card, CancellationToken cancellationToken = default);

    Task UpdateAsync(MemoryCard card, CancellationToken cancellationToken = default);

    Task<List<MemoryCard>> GetDueAsync(Guid studentId, DateTimeOffset now, IReadOnlyCollection<CardKey>? keys = null, int limit = 100, CancellationToken cancellationToken = default);

    /// <summary>Read a due page strictly after the complete cursor, using the same stable ordering.</summary>
    Task<List<MemoryCard>> GetDuePageAsync(Guid studentId, DateTimeOffset now, MemoryCardCursor? after, int limit, CancellationToken cancellationToken = default);

    Task<HashSet<CardKey>> FindExistingKeysAsync(Guid studentId, IReadOnlyCollection<CardKey> keys, CancellationToken cancellationToken = default);

    Task<bool> HasWordAsync(Guid studentId, string wordKey, CancellationToken cancellationToken = default);

    Task<int> CountDueAsync(Guid studentId, DateTimeOffset now, CancellationToken cancellationToken = default);
}
