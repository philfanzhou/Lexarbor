using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Domain.Services;

public sealed class TextbookAudioTrackService(ITextbookAudioTrackRepository repository, IUnitOfWork unitOfWork)
{
    public Task<TextbookAudioTrackPage> ListAsync(string bookId, string unitId, int page, int size, CancellationToken cancellationToken = default)
    {
        bookId = NormalizeId(bookId); unitId = NormalizeId(unitId);
        if (page < 0 || size < 0) throw new DomainValidationException("Page and size must be nonnegative integers.");
        page = page == 0 ? 1 : page; size = size == 0 ? 20 : Math.Min(size, 100);
        return repository.ReadSnapshotAsync(async () =>
        {
            await VerifyUnitAsync(bookId, unitId, cancellationToken);
            var (items, count) = await repository.ListAsync(unitId, page, size, cancellationToken);
            return new TextbookAudioTrackPage(items, count, (int)Math.Ceiling(count / (double)size));
        }, cancellationToken);
    }

    public Task<TextbookAudioTrackModel> CreateAsync(string bookId, string unitId, string? title, int displayOrder, CancellationToken cancellationToken = default)
    {
        bookId = NormalizeId(bookId); unitId = NormalizeId(unitId); title = NormalizeTitle(title, displayOrder);
        return unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            await VerifyUnitAsync(bookId, unitId, cancellationToken);
            var track = new TextbookAudioTrackModel(Guid.NewGuid().ToString("D"), bookId, unitId, title, displayOrder, null, null, false, NewVersion());
            await repository.AddAsync(track, cancellationToken);
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return track;
        }, cancellationToken);
    }

    public Task<TextbookAudioTrackModel> ReplaceAsync(string bookId, string unitId, string trackId, string? title, int displayOrder,
        bool isAvailable, string? expectedVersion, CancellationToken cancellationToken = default)
    {
        bookId = NormalizeId(bookId); unitId = NormalizeId(unitId); trackId = NormalizeId(trackId);
        title = NormalizeTitle(title, displayOrder); var expected = ValidateVersion(expectedVersion);
        return unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            var track = await ResolveAsync(bookId, unitId, trackId, expected, cancellationToken);
            if (isAvailable && (track.CurrentAssetId == null || !await repository.IsPublishedAsync(track.CurrentAssetId, cancellationToken)))
                throw new ConflictException("An audio track needs a published current file before it can be available.");
            var updated = track with { Title = title, DisplayOrder = displayOrder, IsAvailable = isAvailable, Version = NewVersion() };
            if (!await repository.ReplaceAsync(updated, expected, cancellationToken)) throw VersionConflict();
            return updated;
        }, cancellationToken);
    }

    public Task<bool> DeleteAsync(string bookId, string unitId, string trackId, string? expectedVersion, CancellationToken cancellationToken = default)
    {
        bookId = NormalizeId(bookId); unitId = NormalizeId(unitId); trackId = NormalizeId(trackId);
        var expected = ValidateVersion(expectedVersion);
        return unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            _ = await ResolveAsync(bookId, unitId, trackId, expected, cancellationToken);
            if (!await repository.DeleteAsync(trackId, expected, cancellationToken)) throw VersionConflict();
            return true;
        }, cancellationToken);
    }

    private async Task<TextbookAudioTrackModel> ResolveAsync(string bookId, string unitId, string id, string expected, CancellationToken token)
    {
        await VerifyUnitAsync(bookId, unitId, token);
        var track = await repository.GetAsync(id, token) ?? throw new ResourceNotFoundException("Textbook audio track was not found.");
        if (track.BookId != bookId || track.UnitId != unitId)
            throw new ConflictException("Textbook audio track does not belong to the requested book and unit.");
        if (track.Version != expected) throw VersionConflict();
        return track;
    }

    private async Task VerifyUnitAsync(string bookId, string unitId, CancellationToken token)
    {
        if (!await repository.BookExistsAsync(bookId, token)) throw new ResourceNotFoundException("Vocabulary book was not found.");
        var owner = await repository.GetUnitBookAsync(unitId, token) ?? throw new ResourceNotFoundException("Vocabulary book unit was not found.");
        if (owner != bookId) throw new ConflictException("Vocabulary book unit does not belong to the requested vocabulary book.");
    }

    private static string NormalizeId(string? id) => !string.IsNullOrWhiteSpace(id) ? id.Trim() : throw new DomainValidationException("An ID is required.");
    private static string NormalizeTitle(string? title, int order)
    {
        title = title?.Trim();
        if (string.IsNullOrEmpty(title) || title.Length > 200) throw new DomainValidationException("Title must contain between 1 and 200 UTF-16 code units after trimming.");
        if (order < 0) throw new DomainValidationException("Display order must be a nonnegative integer.");
        return title;
    }
    private static string ValidateVersion(string? version) => Guid.TryParseExact(version, "D", out var id) && id.ToString("D") == version
        ? version : throw new DomainValidationException("Expected version must be a canonical lowercase UUID.");
    private static string NewVersion() => Guid.NewGuid().ToString("D");
    private static ConflictException VersionConflict() => new("Textbook audio track has changed. Read its current version and retry.");
}
