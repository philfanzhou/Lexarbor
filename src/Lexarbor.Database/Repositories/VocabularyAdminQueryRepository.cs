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
            return (await LoadPageAsync([word], null, null, null, true, cancellationToken)).Single();
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

    public Task<VocabularyAdminUnitContent> GetUnitContentAsync(string bookId, string unitId, string? keyword,
        string? section, int page, int size, CancellationToken cancellationToken)
        => SnapshotAsync(async () =>
        {
            var book = await GetBookAsync(bookId, cancellationToken);
            // A unit of another book answers the same 404 as a missing one: the
            // path names a unit of this book, and distinguishing the two would
            // leak that the other book has the id.
            var unit = await context.VocabularyBookUnits.AsNoTracking()
                           .SingleOrDefaultAsync(u => u.Id == unitId && u.BookId == bookId, cancellationToken)
                       ?? throw new ResourceNotFoundException("Vocabulary book unit was not found.");
            // Unit totals ignore the keyword, matching the whole-book content
            // route's totals: the keyword narrows the page only. A section
            // narrows the totals too — it selects which places of the unit
            // count — while meanings stay counted per distinct meaning.
            var unitMeanings = context.VocabularyMeanings
                .Where(m => m.BookId == bookId
                    && context.VocabularyMeaningUnits.Any(mu => mu.MeaningId == m.Id && mu.UnitId == unitId
                        && (section == null || mu.Section == section)));
            var meaningCount = await unitMeanings.CountAsync(cancellationToken);
            var wordCount = await unitMeanings.Select(m => m.VocabularyId).Distinct().CountAsync(cancellationToken);
            // The section counts always speak for the whole unit, whatever the
            // section the page was narrowed to: one grouped read over the
            // unit's places, split by the stored section.
            var sections = await context.VocabularyMeaningUnits.AsNoTracking()
                .Where(mu => mu.UnitId == unitId)
                .GroupBy(mu => mu.Section)
                .Select(group => new { Section = group.Key, Count = group.Count() })
                .ToListAsync(cancellationToken);
            var sectionCounts = new VocabularyAdminSectionCounts(
                sections.FirstOrDefault(entry => entry.Section == "A")?.Count ?? 0,
                sections.FirstOrDefault(entry => entry.Section == "B")?.Count ?? 0,
                sections.FirstOrDefault(entry => entry.Section == string.Empty)?.Count ?? 0);
            var result = await PageUnitAsync(bookId, unitId, keyword, section, page, size, cancellationToken);
            return new VocabularyAdminUnitContent(
                book, unit.Adapt<VocabularyBookUnitModel>(), wordCount, meaningCount, sectionCounts, result);
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
        var items = await LoadPageAsync(words, bookId, null, null, includeMeanings, cancellationToken);
        return new VocabularyAdminPage(items, count, (int)Math.Ceiling(count / (double)size));
    }

    /// <summary>
    /// The unit-content twin of <see cref="PageAsync"/>: same keyword filter,
    /// same stable order and paging, but a word qualifies through a meaning
    /// assigned to the unit — to the named section's place of it when one is
    /// given — rather than through any meaning of the book.
    /// </summary>
    private async Task<VocabularyAdminPage> PageUnitAsync(string bookId, string unitId, string? keyword,
        string? section, int page, int size, CancellationToken cancellationToken)
    {
        var query = context.Vocabularies.AsNoTracking()
            .Where(v => context.VocabularyMeanings.Any(m => m.BookId == bookId && m.VocabularyId == v.Id
                && context.VocabularyMeaningUnits.Any(mu => mu.MeaningId == m.Id && mu.UnitId == unitId
                    && (section == null || mu.Section == section))));
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = SqliteSearchPattern.Contains(keyword);
            query = query.Where(v => EF.Functions.Like(v.Word, pattern, SqliteSearchPattern.EscapeCharacter.ToString()));
        }
        var count = await query.CountAsync(cancellationToken);
        var words = await query.OrderBy(v => v.Word).ThenBy(v => v.Id).Skip((page - 1) * size).Take(size).ToListAsync(cancellationToken);
        var items = await LoadPageAsync(words, bookId, unitId, section, true, cancellationToken);
        return new VocabularyAdminPage(items, count, (int)Math.Ceiling(count / (double)size));
    }

    private async Task<IReadOnlyList<VocabularyAdminWord>> LoadPageAsync(List<VocabularyEntity> words, string? bookId,
        string? unitId, string? section, bool includeMeanings, CancellationToken cancellationToken)
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
        // Meaning-level unit assignments of the page, in one batched read over
        // the meanings that were loaded, so a detail page never costs one query
        // per meaning.
        var assignments = new List<(string MeaningId, string UnitId, int Number, string? Title, string Section)>();
        if (includeMeanings)
        {
            meanings = await context.VocabularyMeanings.AsNoTracking()
                .Where(m => ids.Contains(m.VocabularyId) && (bookId == null || m.BookId == bookId)
                    && (unitId == null
                        || context.VocabularyMeaningUnits.Any(mu => mu.MeaningId == m.Id && mu.UnitId == unitId
                            && (section == null || mu.Section == section))))
                .ToListAsync(cancellationToken);
            if (meanings.Count > 0)
            {
                var meaningIds = meanings.Select(m => m.Id).ToArray();
                assignments = (await context.VocabularyMeaningUnits.AsNoTracking()
                        .Where(mu => meaningIds.Contains(mu.MeaningId))
                        .Select(mu => new { mu.MeaningId, mu.UnitId, mu.Unit!.Number, mu.Unit.Title, mu.Section })
                        .ToListAsync(cancellationToken))
                    .Select(mu => (mu.MeaningId, mu.UnitId, mu.Number, mu.Title, mu.Section))
                    .ToList();
            }
        }
        var byWord = meanings.ToLookup(m => m.VocabularyId);
        var unitsByMeaning = assignments.ToLookup(a => a.MeaningId);
        return words.Select(word => new VocabularyAdminWord(word.Adapt<VocabularyModel>(),
            books[word.Id].OrderBy(b => b.BookName, StringComparer.Ordinal).ThenBy(b => b.Id, StringComparer.Ordinal)
                .Select(b => new VocabularyAdminBook(b.Id, b.BookName, b.Status)).ToList(),
            byWord[word.Id].OrderBy(m => m.BookId, StringComparer.Ordinal)
                .ThenBy(m => m.PartOfSpeech, StringComparer.Ordinal).ThenBy(m => m.Meaning, StringComparer.Ordinal)
                .ThenBy(m => m.Id, StringComparer.Ordinal).Select(m => new VocabularyAdminMeaningDetail(
                    m.Adapt<VocabularyMeaningModel>(),
                    // Every place of the page's meanings, whatever section the
                    // page was narrowed to: the detail's assignments are the
                    // whole truth about each meaning it lists. A meaning in a
                    // unit's A and B reads as two entries; the empty-string
                    // sentinel becomes null for the contract.
                    unitsByMeaning[m.Id].OrderBy(u => u.Number).ThenBy(u => u.UnitId, StringComparer.Ordinal)
                        .ThenBy(u => u.Section, StringComparer.Ordinal)
                        .Select(u => new VocabularyAdminUnit(
                            u.UnitId, u.Number, u.Title, u.Section == string.Empty ? null : u.Section)).ToList())).ToList())).ToList();
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
