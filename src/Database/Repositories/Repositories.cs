using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mapster;
using Microsoft.EntityFrameworkCore;
using Ruoyu.Study.Vocabulary.Database.Entities;
using Ruoyu.Study.Vocabulary.Domain.Models;
using Ruoyu.Study.Vocabulary.Domain.Repositories;

namespace Ruoyu.Study.Vocabulary.Database.Repositories;

public class VocabularyRepository : IVocabularyRepository
{
    private readonly VocabularyDbContext _context;

    public VocabularyRepository(VocabularyDbContext context)
    {
        _context = context;
    }

    public async Task<VocabularyModel?> GetByIdAsync(string id)
    {
        var entity = await _context.Vocabularies.FindAsync(id);
        return entity?.Adapt<VocabularyModel>();
    }

    public async Task<VocabularyModel?> GetByWordAsync(string word)
    {
        var entity = await _context.Vocabularies.FirstOrDefaultAsync(v => v.Word == word);
        return entity?.Adapt<VocabularyModel>();
    }

    public async Task<VocabularyModel?> GetByNormalizedWordAsync(string normalizedWord)
    {
        var entity = await _context.Vocabularies
            .FirstOrDefaultAsync(vocabulary => vocabulary.Word.Trim().ToLower() == normalizedWord);
        return entity?.Adapt<VocabularyModel>();
    }

    public async Task<List<VocabularyModel>> GetByIdsAsync(IReadOnlyCollection<string> ids)
    {
        if (ids == null || ids.Count == 0)
            return new List<VocabularyModel>();

        var entities = await _context.Vocabularies
            .Where(v => ids.Contains(v.Id))
            .ToListAsync();
        return entities.Adapt<List<VocabularyModel>>();
    }

    public async Task<(List<VocabularyModel> Items, int TotalCount)> SearchAsync(string keyword, int page, int size)
    {
        var query = _context.Vocabularies.AsQueryable();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            query = query.Where(v => v.Word.Contains(keyword));
        }

        var totalCount = await query.CountAsync();
        var entities = await query.OrderBy(v => v.Word)
                                  .Skip((page - 1) * size)
                                  .Take(size)
                                  .ToListAsync();
        return (entities.Adapt<List<VocabularyModel>>(), totalCount);
    }

    public async Task AddAsync(VocabularyModel model)
    {
        var entity = model.Adapt<VocabularyEntity>();
        await _context.Vocabularies.AddAsync(entity);
    }

    public async Task UpdateAsync(VocabularyModel model)
    {
        var entity = await _context.Vocabularies.FindAsync(model.Id);
        if (entity != null)
        {
            model.Adapt(entity);
            _context.Vocabularies.Update(entity);
        }
    }

    public async Task<List<VocabularyModel>> GetRandomExceptAsync(string excludeId, int count)
    {
        var entities = await _context.Vocabularies
            .Where(v => v.Id != excludeId)
            .OrderBy(v => Guid.NewGuid())
            .Take(count)
            .ToListAsync();
        return entities.Adapt<List<VocabularyModel>>();
    }
}

public class VocabularyBookRepository : IVocabularyBookRepository
{
    private readonly VocabularyDbContext _context;

    public VocabularyBookRepository(VocabularyDbContext context)
    {
        _context = context;
    }

    public async Task<VocabularyBookModel?> GetByIdAsync(string id)
    {
        var entity = await _context.VocabularyBooks
            .AsNoTracking()
            .FirstOrDefaultAsync(book => book.Id == id);
        return entity?.Adapt<VocabularyBookModel>();
    }

    public async Task<List<VocabularyBookModel>> GetAllAsync()
    {
        var entities = await _context.VocabularyBooks
            .AsNoTracking()
            .OrderBy(book => book.DisplayOrder)
            .ToListAsync();
        return entities.Adapt<List<VocabularyBookModel>>();
    }

    public async Task<List<VocabularyBookModel>> GetActiveAsync()
    {
        var entities = await _context.VocabularyBooks
            .AsNoTracking()
            .Where(book => book.Status)
            .OrderBy(book => book.DisplayOrder)
            .ToListAsync();
        return entities.Adapt<List<VocabularyBookModel>>();
    }

    public async Task<(List<VocabularyBookModel> Items, int TotalCount)> SearchAsync(
        string keyword,
        int page,
        int size)
    {
        var query = _context.VocabularyBooks.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            query = query.Where(book =>
                book.BookName.Contains(keyword) ||
                (book.Description != null && book.Description.Contains(keyword)));
        }

        var totalCount = await query.CountAsync();
        var entities = await query
            .OrderBy(book => book.DisplayOrder)
            .ThenBy(book => book.BookName)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync();
        return (entities.Adapt<List<VocabularyBookModel>>(), totalCount);
    }

    public async Task<List<VocabularyBookModel>> GetByCategoryAsync(string category, string? grade)
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
        return entities.Adapt<List<VocabularyBookModel>>();
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

    public Task<List<string>> GetDistinctEducationLevelsAsync()
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

    public Task<List<string>> GetDistinctGradesByEducationLevelAsync(string educationLevel)
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

    public async Task<List<VocabularyModel>> GetWordsAsync(string bookId)
    {
        var entities = await _context.VocabularyMeanings
            .AsNoTracking()
            .Where(meaning => meaning.BookId == bookId)
            .Join(
                _context.Vocabularies.AsNoTracking(),
                meaning => meaning.VocabularyId,
                vocabulary => vocabulary.Id,
                (_, vocabulary) => vocabulary)
            .Distinct()
            .OrderBy(vocabulary => vocabulary.Word)
            .ToListAsync();
        return entities.Adapt<List<VocabularyModel>>();
    }

    public async Task AddAsync(VocabularyBookModel model)
    {
        var entity = model.Adapt<VocabularyBookEntity>();
        await _context.VocabularyBooks.AddAsync(entity);
    }

    public async Task UpdateAsync(VocabularyBookModel model)
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

public class VocabularyMeaningRepository : IVocabularyMeaningRepository
{
    private readonly VocabularyDbContext _context;

    public VocabularyMeaningRepository(VocabularyDbContext context)
    {
        _context = context;
    }

    public async Task<VocabularyMeaningModel?> GetByIdAsync(string id)
    {
        var entity = await _context.VocabularyMeanings.FindAsync(id);
        return entity?.Adapt<VocabularyMeaningModel>();
    }

    public async Task<List<VocabularyMeaningModel>> GetByVocabularyIdAsync(string vocabularyId)
    {
        var entities = await _context.VocabularyMeanings
            .Where(m => m.VocabularyId == vocabularyId)
            .ToListAsync();
        return entities.Adapt<List<VocabularyMeaningModel>>();
    }

    public async Task<List<VocabularyMeaningModel>> GetByBookIdAsync(string bookId)
    {
        var entities = await _context.VocabularyMeanings
            .Where(m => m.BookId == bookId)
            .ToListAsync();
        return entities.Adapt<List<VocabularyMeaningModel>>();
    }

    public async Task<List<VocabularyMeaningModel>> GetByBookAndVocabularyIdAsync(string bookId, string vocabularyId)
    {
        var entities = await _context.VocabularyMeanings
            .Where(m => m.BookId == bookId && m.VocabularyId == vocabularyId)
            .ToListAsync();
        return entities.Adapt<List<VocabularyMeaningModel>>();
    }

    public async Task<List<VocabularyMeaningModel>> GetRandomExceptAsync(string excludeVocabularyId, string bookId, int count)
    {
        var entities = await _context.VocabularyMeanings
            .Where(m => m.BookId == bookId && m.VocabularyId != excludeVocabularyId)
            .OrderBy(m => Guid.NewGuid())
            .Take(count)
            .ToListAsync();
        return entities.Adapt<List<VocabularyMeaningModel>>();
    }

    public async Task<VocabularyMeaningModel?> GetEquivalentAsync(
        string vocabularyId,
        string bookId,
        string normalizedPartOfSpeech,
        string meaning)
    {
        var normalizedMeaning = meaning.Trim();
        var entity = await _context.VocabularyMeanings.FirstOrDefaultAsync(item =>
            item.VocabularyId == vocabularyId &&
            item.BookId == bookId &&
            (item.PartOfSpeech ?? string.Empty).Trim().ToLower() == normalizedPartOfSpeech &&
            item.Meaning.Trim() == normalizedMeaning);
        return entity?.Adapt<VocabularyMeaningModel>();
    }

    public async Task AddAsync(VocabularyMeaningModel model)
    {
        var entity = model.Adapt<VocabularyMeaningEntity>();
        await _context.VocabularyMeanings.AddAsync(entity);
    }

    public async Task UpdateAsync(VocabularyMeaningModel model)
    {
        var entity = await _context.VocabularyMeanings.FindAsync(model.Id);
        if (entity != null)
        {
            model.Adapt(entity);
            _context.VocabularyMeanings.Update(entity);
        }
    }

    public async Task DeleteAsync(string id)
    {
        var entity = await _context.VocabularyMeanings.FindAsync(id);
        if (entity != null)
        {
            _context.VocabularyMeanings.Remove(entity);
        }
    }

    public async Task DeleteByVocabularyIdAsync(string vocabularyId)
    {
        var entities = await _context.VocabularyMeanings
            .Where(m => m.VocabularyId == vocabularyId)
            .ToListAsync();
        _context.VocabularyMeanings.RemoveRange(entities);
    }
}
