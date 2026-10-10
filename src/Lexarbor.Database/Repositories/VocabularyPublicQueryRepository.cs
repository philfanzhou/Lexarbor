using Lexarbor.Database.Entities;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

/// <summary>
/// The reads behind the anonymous book-browse endpoints, executed inside one
/// read snapshot per request so the totals and the page content describe the
/// same instant of the book.
/// </summary>
public sealed class VocabularyPublicQueryRepository(VocabularyDbContext context) : IVocabularyPublicQueryRepository
{
    public Task<VocabularyPublicUnitList> GetUnitsAsync(string bookId, CancellationToken cancellationToken)
        => SnapshotAsync(async () =>
        {
            await EnsureEnabledBookAsync(bookId, cancellationToken);

            var units = await context.VocabularyBookUnits.AsNoTracking()
                .Where(unit => unit.BookId == bookId)
                .OrderBy(unit => unit.Number)
                .Select(unit => new { unit.Id, unit.Number, unit.Title })
                .ToListAsync(cancellationToken);

            // Both counts speak per distinct meaning, so a meaning that holds
            // several places of one unit — both sections, or both kinds of one
            // place — counts once: the same counting the administrative unit
            // content route uses.
            var meaningCounts = await context.VocabularyMeaningUnits.AsNoTracking()
                .Where(position => position.BookId == bookId)
                .GroupBy(position => position.UnitId)
                .Select(group => new
                {
                    UnitId = group.Key,
                    Count = group.Select(position => position.MeaningId).Distinct().Count()
                })
                .ToListAsync(cancellationToken);
            var wordCounts = await (from position in context.VocabularyMeaningUnits.AsNoTracking()
                                    join meaning in context.VocabularyMeanings.AsNoTracking()
                                        on position.MeaningId equals meaning.Id
                                    where position.BookId == bookId
                                    group meaning.VocabularyId by position.UnitId into unitGroup
                                    select new { UnitId = unitGroup.Key, Count = unitGroup.Distinct().Count() })
                .ToListAsync(cancellationToken);

            var meaningsByUnit = meaningCounts.ToDictionary(row => row.UnitId, row => row.Count);
            var wordsByUnit = wordCounts.ToDictionary(row => row.UnitId, row => row.Count);
            return new VocabularyPublicUnitList(units.Select(unit => new VocabularyPublicUnit(
                unit.Id,
                unit.Number,
                unit.Title,
                wordsByUnit.GetValueOrDefault(unit.Id),
                meaningsByUnit.GetValueOrDefault(unit.Id))).ToList());
        }, cancellationToken);

    public Task<VocabularyPublicEntryPage> GetEntriesAsync(
        string bookId, string? unitId, int page, int size, CancellationToken cancellationToken)
        => SnapshotAsync(async () =>
        {
            await EnsureEnabledBookAsync(bookId, cancellationToken);
            // A unit of another book answers the same 404 as a missing one,
            // for the reason the administrative unit-content route records:
            // the path names a unit of this book, and distinguishing the two
            // would leak that the other book has the id.
            if (unitId != null && !await context.VocabularyBookUnits.AsNoTracking()
                    .AnyAsync(unit => unit.Id == unitId && unit.BookId == bookId, cancellationToken))
            {
                throw new ResourceNotFoundException("Vocabulary book unit was not found.");
            }

            var scopeIsUnit = unitId != null;
            var meanings = from meaning in context.VocabularyMeanings.AsNoTracking()
                           join word in context.Vocabularies.AsNoTracking() on meaning.VocabularyId equals word.Id
                           where meaning.BookId == bookId
                                 && (!scopeIsUnit || context.VocabularyMeaningUnits.Any(position =>
                                     position.MeaningId == meaning.Id && position.UnitId == unitId))
                           select new { meaning, word };

            var totalCount = await meanings.CountAsync(cancellationToken);
            var wordCount = await meanings.Select(row => row.word.Id).Distinct().CountAsync(cancellationToken);
            var pageRows = await meanings
                // The order is a total one and lives on the database side, so
                // it holds across pages. A meaning is keyed by the smallest
                // place it holds in the scope: the unit number first, then the
                // section's A before B before none — the same reading order a
                // book's units are studied in — packed into one integer, with
                // a meaning that holds no place in the scope sorted last.
                // After that the spelling's normalized key, then the ids,
                // settle every tie, so two pages of one query never overlap
                // or skip.
                .OrderBy(row => context.VocabularyMeaningUnits
                    .Where(position => position.MeaningId == row.meaning.Id
                        && position.BookId == bookId
                        && (!scopeIsUnit || position.UnitId == unitId))
                    .Min(position => (int?)position.Unit!.Number * 4
                        + (position.Section == "A" ? 0 : position.Section == "B" ? 1 : 2)) ?? int.MaxValue)
                .ThenBy(row => row.word.NormalizedWord)
                .ThenBy(row => row.word.Id)
                .ThenBy(row => row.meaning.Id)
                .Skip((page - 1) * size)
                .Take(size)
                .Select(row => new
                {
                    WordId = row.word.Id,
                    row.word.Word,
                    row.word.NormalizedWord,
                    row.word.PhoneticUk,
                    row.word.PhoneticUs,
                    MeaningId = row.meaning.Id,
                    row.meaning.PartOfSpeech,
                    row.meaning.Meaning,
                    // The stored column is trim(meaning); lowering it here
                    // gives the lower(trim(meaning)) the question generator
                    // calls an equivalent definition, so a caller comparing
                    // keys across books is using the server's own rule.
                    MeaningKey = row.meaning.NormalizedMeaning.ToLower(),
                    row.meaning.Example
                })
                .ToListAsync(cancellationToken);

            // Only this page's places are loaded, in one batched read, so a
            // book's page costs no query per entry.
            var meaningIds = pageRows.Select(row => row.MeaningId).ToArray();
            var positions = (await (from position in context.VocabularyMeaningUnits.AsNoTracking()
                                    join unit in context.VocabularyBookUnits.AsNoTracking() on position.UnitId equals unit.Id
                                    where position.BookId == bookId
                                          && meaningIds.Contains(position.MeaningId)
                                          && (!scopeIsUnit || position.UnitId == unitId)
                                    select new { position.MeaningId, position.UnitId, unit.Number, position.Section, position.EntryKind })
                .ToListAsync(cancellationToken))
                .GroupBy(position => position.MeaningId)
                .ToDictionary(
                    group => group.Key,
                    // The same order the administrative detail reports a
                    // meaning's places in: unit number, unit id, then the
                    // section and kind ordinals, which sort the empty-string
                    // sentinels first and keep the order a property of the
                    // data rather than of the host's locale.
                    group => (IReadOnlyList<VocabularyPublicEntryPosition>)group
                        .OrderBy(position => position.Number)
                        .ThenBy(position => position.UnitId, StringComparer.Ordinal)
                        .ThenBy(position => position.Section, StringComparer.Ordinal)
                        .ThenBy(position => position.EntryKind, StringComparer.Ordinal)
                        .Select(position => new VocabularyPublicEntryPosition(
                            position.UnitId,
                            position.Number,
                            position.Section == string.Empty ? null : position.Section,
                            position.EntryKind == string.Empty ? null : position.EntryKind))
                        .ToList());

            return new VocabularyPublicEntryPage(
                pageRows.Select(row => new VocabularyPublicEntry(
                    row.WordId,
                    row.Word,
                    row.NormalizedWord,
                    row.PhoneticUk,
                    row.PhoneticUs,
                    row.MeaningId,
                    row.PartOfSpeech,
                    row.Meaning,
                    row.MeaningKey,
                    row.Example,
                    positions.GetValueOrDefault(row.MeaningId, []))).ToList(),
                totalCount,
                (int)Math.Ceiling(totalCount / (double)size),
                wordCount);
        }, cancellationToken);

    private async Task EnsureEnabledBookAsync(string bookId, CancellationToken cancellationToken)
    {
        var book = await context.VocabularyBooks.AsNoTracking()
                       .SingleOrDefaultAsync(book => book.Id == bookId, cancellationToken)
                   ?? throw new ResourceNotFoundException("Vocabulary book was not found.");
        if (!book.Status)
        {
            // The same answer the public detail and question routes give, so
            // the browse routes never reveal that a disabled book's units or
            // entries exist.
            throw new BusinessRuleException("Vocabulary book is disabled.");
        }
    }

    private async Task<T> SnapshotAsync<T>(Func<Task<T>> read, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            // BEGIN (deferred), not UnitOfWork's serialized BEGIN IMMEDIATE,
            // so the counts and the page read one instant of the book.
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
