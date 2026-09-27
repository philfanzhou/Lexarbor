using Lexarbor.Database.Entities;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Mapster;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed class VocabularyAdminQueryRepository(VocabularyDbContext context) : IVocabularyAdminQueryRepository
{
    public Task<VocabularyAdminPage> SearchAsync(string? keyword, string? bookId, int page, int size, CancellationToken cancellationToken)
        => SnapshotAsync(async () =>
        {
            if (bookId != null) await GetBookAsync(bookId, cancellationToken);
            return await PageAsync(keyword, bookId, page, size, false, cancellationToken);
        }, cancellationToken);

    public Task<VocabularyAdminWord> GetAsync(string wordId, CancellationToken cancellationToken)
        => SnapshotAsync(async () =>
        {
            var word = await context.Vocabularies.AsNoTracking().SingleOrDefaultAsync(v => v.Id == wordId, cancellationToken)
                ?? throw new ResourceNotFoundException("Vocabulary word was not found.");
            return (await LoadPageAsync([word], null, true, cancellationToken)).Single();
        }, cancellationToken);

    public Task<VocabularyAdminContent> GetContentAsync(string bookId, string? keyword, int page, int size, CancellationToken cancellationToken)
        => SnapshotAsync(async () =>
        {
            var book = await GetBookAsync(bookId, cancellationToken);
            var meanings = context.VocabularyMeanings.Where(m => m.BookId == bookId);
            var meaningCount = await meanings.CountAsync(cancellationToken);
            var wordCount = await meanings.Select(m => m.VocabularyId).Distinct().CountAsync(cancellationToken);
            var result = await PageAsync(keyword, bookId, page, size, true, cancellationToken);
            return new VocabularyAdminContent(book, wordCount, meaningCount, result);
        }, cancellationToken);

    private async Task<VocabularyBookModel> GetBookAsync(string bookId, CancellationToken cancellationToken)
    {
        var book = await context.VocabularyBooks.AsNoTracking().SingleOrDefaultAsync(b => b.Id == bookId, cancellationToken)
            ?? throw new ResourceNotFoundException("Vocabulary book was not found.");
        return book.Adapt<VocabularyBookModel>();
    }

    private async Task<VocabularyAdminPage> PageAsync(string? keyword, string? bookId, int page, int size,
        bool includeMeanings, CancellationToken cancellationToken)
    {
        var query = context.Vocabularies.AsNoTracking();
        if (bookId != null)
            query = query.Where(v => context.VocabularyMeanings.Any(m => m.BookId == bookId && m.VocabularyId == v.Id));
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = SqliteSearchPattern.Contains(keyword);
            query = query.Where(v => EF.Functions.Like(v.Word, pattern, SqliteSearchPattern.EscapeCharacter.ToString()));
        }
        var count = await query.CountAsync(cancellationToken);
        var words = await query.OrderBy(v => v.Word).ThenBy(v => v.Id).Skip((page - 1) * size).Take(size).ToListAsync(cancellationToken);
        var items = await LoadPageAsync(words, bookId, includeMeanings, cancellationToken);
        return new VocabularyAdminPage(items, count, (int)Math.Ceiling(count / (double)size));
    }

    private async Task<IReadOnlyList<VocabularyAdminWord>> LoadPageAsync(List<VocabularyEntity> words, string? bookId,
        bool includeMeanings, CancellationToken cancellationToken)
    {
        if (words.Count == 0) return [];
        var ids = words.Select(v => v.Id).ToArray();
        // Only this page's associations are materialized. Book filtering must not
        // hide a selected word's other (including disabled) book memberships.
        var memberships = await context.VocabularyMeanings.AsNoTracking().Where(m => ids.Contains(m.VocabularyId))
            .Select(m => new { m.VocabularyId, m.Book!.Id, m.Book.BookName, m.Book.Status }).Distinct()
            .ToListAsync(cancellationToken);
        var books = memberships.ToLookup(m => m.VocabularyId);
        var meanings = new List<VocabularyMeaningEntity>();
        if (includeMeanings)
        {
            meanings = await context.VocabularyMeanings.AsNoTracking()
                .Where(m => ids.Contains(m.VocabularyId) && (bookId == null || m.BookId == bookId))
                .ToListAsync(cancellationToken);
        }
        var byWord = meanings.ToLookup(m => m.VocabularyId);
        return words.Select(word => new VocabularyAdminWord(word.Adapt<VocabularyModel>(),
            books[word.Id].OrderBy(b => b.BookName, StringComparer.Ordinal).ThenBy(b => b.Id, StringComparer.Ordinal)
                .Select(b => new VocabularyAdminBook(b.Id, b.BookName, b.Status)).ToList(),
            byWord[word.Id].OrderBy(m => m.BookId, StringComparer.Ordinal)
                .ThenBy(m => m.PartOfSpeech, StringComparer.Ordinal).ThenBy(m => m.Meaning, StringComparer.Ordinal)
                .ThenBy(m => m.Id, StringComparer.Ordinal).Select(m => m.Adapt<VocabularyMeaningModel>()).ToList())).ToList();
    }

    private async Task<T> SnapshotAsync<T>(Func<Task<T>> read, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            // BEGIN (deferred), not UnitOfWork's serialized BEGIN IMMEDIATE.
            await using var transaction = ((SqliteConnection)context.Database.GetDbConnection()).BeginTransaction(deferred: true);
            await using var enlisted = await context.Database.UseTransactionAsync(transaction, cancellationToken);
            var result = await read();
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }
}
