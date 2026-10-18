using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Repositories;

public interface ITextbookAudioTrackRepository
{
    Task<T> ReadSnapshotAsync<T>(Func<Task<T>> read, CancellationToken cancellationToken);
    Task<bool> BookExistsAsync(string bookId, CancellationToken cancellationToken);
    Task<string?> GetUnitBookAsync(string unitId, CancellationToken cancellationToken);
    Task<TextbookAudioTrackModel?> GetAsync(string id, CancellationToken cancellationToken);
    Task<(IReadOnlyList<TextbookAudioTrackModel> Items, int TotalCount)> ListAsync(string unitId, int page, int size, CancellationToken cancellationToken);
    Task<bool> IsPublishedAsync(string assetId, CancellationToken cancellationToken);
    Task AddAsync(TextbookAudioTrackModel track, CancellationToken cancellationToken);
    Task<bool> ReplaceAsync(TextbookAudioTrackModel track, string expectedVersion, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(string id, string expectedVersion, CancellationToken cancellationToken);
}
