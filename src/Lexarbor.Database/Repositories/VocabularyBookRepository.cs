using Lexarbor.Database.Entities;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Mapster;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public class VocabularyBookRepository : IVocabularyBookRepository
{
    private readonly VocabularyDbContext _context;

    public VocabularyBookRepository(VocabularyDbContext context)
    {
        _context = context;
    }

    public Task<VocabularyBook?> GetByIdAsync(string id) => GetByIdAsync(id, CancellationToken.None);

    public async Task<VocabularyBook?> GetByIdAsync(string id, CancellationToken cancellationToken)
    {
        var books = UnitOfWork.OwnsWriteContext(_context)
            ? _context.VocabularyBooks.FromSqlInterpolated($"SELECT * FROM vocabulary_book WHERE id = {id} FOR SHARE")
            : _context.VocabularyBooks;
        var entity = await books
            .AsNoTracking()
            .FirstOrDefaultAsync(book => book.Id == id, cancellationToken);
        return entity?.Adapt<VocabularyBook>();
    }

    public async Task<List<VocabularyBook>> GetAllAsync()
    {
        var entities = await _context.VocabularyBooks
            .AsNoTracking()
            .OrderBy(book => book.DisplayOrder)
            .ToListAsync();
        return entities.Adapt<List<VocabularyBook>>();
    }

    public async Task<List<VocabularyBook>> GetActiveAsync(bool excludeManagedSupplement = false)
    {
        var query = _context.VocabularyBooks.AsNoTracking().Where(book => book.Status);
        if (excludeManagedSupplement)
        {
            query = query.Where(book => !_context.EnrichmentDestinations.Any(destination =>
                destination.Id == EnrichmentDestinationRepository.ExpansionRole && destination.BookId == book.Id));
        }
        var entities = await query
            .OrderBy(book => book.DisplayOrder)
            .ToListAsync();
        return entities.Adapt<List<VocabularyBook>>();
    }

    public async Task<(List<VocabularyBook> Items, int TotalCount)> SearchAsync(
        string? keyword,
        int page,
        int size)
    {
        var query = _context.VocabularyBooks.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = SearchPattern.Contains(keyword);
            var escape = SearchPattern.EscapeCharacter.ToString();
            query = query.Where(book =>
                EF.Functions.ILike(book.BookName, pattern, escape) ||
                (book.Description != null &&
                 EF.Functions.ILike(book.Description, pattern, escape)));
        }

        var totalCount = await query.CountAsync();
        var entities = await query
            .OrderBy(book => book.DisplayOrder)
            .ThenBy(book => book.BookName)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync();
        return (entities.Adapt<List<VocabularyBook>>(), totalCount);
    }

    public async Task<List<VocabularyBook>> GetByCategoryAsync(string? category, string? grade)
    {
        var query = _context.VocabularyBooks.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(category))
        {
            query = query.Where(book => book.Category == category);
        }

        if (!string.IsNullOrWhiteSpace(grade))
        {
            query = query.Where(book => book.Grade == grade);
        }

        var entities = await query
            .OrderBy(book => book.DisplayOrder)
            .ThenBy(book => book.BookName)
            .ToListAsync();
        return entities.Adapt<List<VocabularyBook>>();
    }

    public Task<List<string>> GetDistinctCategoriesAsync()
    {
        return _context.VocabularyBooks
            .AsNoTracking()
            .Where(book => book.Category != null && book.Category != string.Empty)
            .Select(book => book.Category!)
            .Distinct()
            .OrderBy(category => category)
            .ToListAsync();
    }

    public Task<List<string>> GetEducationLevelsAsync()
    {
        return _context.VocabularyBooks
            .AsNoTracking()
            .Where(book => book.EducationLevel != null && book.EducationLevel != string.Empty)
            .Select(book => book.EducationLevel!)
            .Distinct()
            .OrderBy(educationLevel => educationLevel)
            .ToListAsync();
    }

    public Task<List<string>> GetDistinctGradesAsync()
    {
        return _context.VocabularyBooks
            .AsNoTracking()
            .Where(book => book.Grade != null && book.Grade != string.Empty)
            .Select(book => book.Grade!)
            .Distinct()
            .OrderBy(grade => grade)
            .ToListAsync();
    }

    public Task<List<string>> GetGradesByEducationLevelAsync(string educationLevel)
    {
        return _context.VocabularyBooks
            .AsNoTracking()
            .Where(book =>
                book.EducationLevel == educationLevel &&
                book.Grade != null &&
                book.Grade != string.Empty)
            .Select(book => book.Grade!)
            .Distinct()
            .OrderBy(grade => grade)
            .ToListAsync();
    }

    public Task<bool> HasMeaningsAsync(string bookId)
    {
        return _context.VocabularyMeanings
            .AsNoTracking()
            .AnyAsync(meaning => meaning.BookId == bookId);
    }

    /// <summary>
    /// One page of the distinct words a book contains, in word order.
    /// </summary>
    /// <remarks>
    /// The join this replaced needed DISTINCT over whole vocabulary rows, because
    /// a word with several definitions in the book joined to several meaning
    /// rows. SQLite answered that with a temp B-tree over every column of every
    /// row in the book, and a second one for the ordering. Asking whether any
    /// meaning links the word to the book instead cannot produce a duplicate in
    /// the first place, so both disappear, and it is the same shape the word
    /// search already uses.
    /// </remarks>
    public async Task<(List<Vocabulary> Items, int TotalCount)> GetWordsAsync(
        string bookId,
        int page,
        int size)
    {
        var query = _context.Vocabularies
            .AsNoTracking()
            .Where(vocabulary => _context.VocabularyMeanings.Any(meaning =>
                meaning.BookId == bookId &&
                meaning.VocabularyId == vocabulary.Id));

        var totalCount = await query.CountAsync();
        var entities = await query
            .OrderBy(vocabulary => vocabulary.Word)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync();
        return (entities.Adapt<List<Vocabulary>>(), totalCount);
    }

    public async Task AddAsync(VocabularyBook model)
    {
        var entity = model.Adapt<VocabularyBookEntity>();
        await _context.VocabularyBooks.AddAsync(entity);
    }

    public async Task UpdateAsync(VocabularyBook model)
    {
        var entity = await _context.VocabularyBooks.FindAsync(model.Id);
        if (entity != null)
        {
            model.Adapt(entity);
            _context.VocabularyBooks.Update(entity);
        }
    }

    public async Task DeleteAsync(string id)
    {
        var entity = await _context.VocabularyBooks.FindAsync(id);
        if (entity != null)
        {
            _context.VocabularyBooks.Remove(entity);
        }
    }
}
