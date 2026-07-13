using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ruoyu.Study.Vocabulary.Domain.Models;
using Ruoyu.Study.Vocabulary.Domain.Repositories;

namespace Ruoyu.Study.Vocabulary.Domain.Services;

public class VocabularyBookDomainService
{
    private readonly IVocabularyBookRepository _bookRepository;
    private readonly IVocabularyMeaningRepository _meaningRepository;
    private readonly IVocabularyRepository _vocabularyRepository;
    private readonly IUnitOfWork _unitOfWork;

    public VocabularyBookDomainService(
        IVocabularyBookRepository bookRepository,
        IVocabularyMeaningRepository meaningRepository,
        IVocabularyRepository vocabularyRepository,
        IUnitOfWork unitOfWork)
    {
        _bookRepository = bookRepository;
        _meaningRepository = meaningRepository;
        _vocabularyRepository = vocabularyRepository;
        _unitOfWork = unitOfWork;
    }

    public async Task<List<VocabularyBookModel>> GetAllAsync()
    {
        return await _bookRepository.GetAllAsync();
    }

    public async Task<List<VocabularyBookModel>> GetByCategoryAsync(string category, string? grade)
    {
        var all = await _bookRepository.GetAllAsync();
        if (!string.IsNullOrWhiteSpace(category))
            all = all.Where(x => x.Category == category).ToList();
        if (!string.IsNullOrWhiteSpace(grade))
            all = all.Where(x => x.Grade == grade).ToList();
        return all.OrderBy(x => x.DisplayOrder).ToList();
    }

    public async Task<VocabularyBookModel?> GetAsync(string id)
    {
        return await _bookRepository.GetByIdAsync(id);
    }

    public async Task<(List<VocabularyBookModel> Items, int TotalCount)> SearchAsync(string keyword, int page, int size)
    {
        var all = await _bookRepository.GetAllAsync();
        if (!string.IsNullOrWhiteSpace(keyword))
            all = all.Where(x => x.BookName.Contains(keyword) || (x.Description?.Contains(keyword) == true)).ToList();
        
        var totalCount = all.Count;
        var items = all.OrderBy(x => x.DisplayOrder).Skip((page - 1) * size).Take(size).ToList();
        return (items, totalCount);
    }

    public async Task<VocabularyBookModel> AddOrUpdateAsync(VocabularyBookModel book)
    {
        var existing = string.IsNullOrWhiteSpace(book.Id) ? null : await _bookRepository.GetByIdAsync(book.Id);
        if (existing == null)
        {
            book.Id = Guid.NewGuid().ToString();
            book.CreatedAt = DateTimeOffset.UtcNow;
            book.UpdatedAt = book.CreatedAt;
            await _bookRepository.AddAsync(book);
            existing = book;
        }
        else
        {
            existing.BookName = book.BookName;
            existing.Description = book.Description;
            existing.Publisher = book.Publisher;
            existing.EducationLevel = book.EducationLevel;
            existing.Grade = book.Grade;
            existing.Category = book.Category;
            existing.DisplayOrder = book.DisplayOrder;
            existing.Status = book.Status;
            existing.IconUrl = book.IconUrl;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            await _bookRepository.UpdateAsync(existing);
        }
        await _unitOfWork.SaveChangesAsync();
        return existing;
    }

    public async Task DeleteAsync(string id)
    {
        await _bookRepository.DeleteAsync(id);
        await _unitOfWork.SaveChangesAsync();
    }

    public async Task<List<VocabularyModel>> GetWordsAsync(string bookId)
    {
        var meanings = await _meaningRepository.GetByBookIdAsync(bookId);
        var vocabularyIds = meanings
            .Select(m => m.VocabularyId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .ToList();

        if (vocabularyIds.Count == 0)
            return new List<VocabularyModel>();

        var vocabularies = await _vocabularyRepository.GetByIdsAsync(vocabularyIds);
        return vocabularies
            .Where(v => !string.IsNullOrWhiteSpace(v.Word))
            .OrderBy(v => v.Word, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<List<string>> GetAllCategoriesAsync()
    {
        var all = await _bookRepository.GetAllAsync();
        return all.Select(x => x.Category ?? "").Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
    }

    public async Task<List<string>> GetAllEducationLevelsAsync()
    {
        var all = await _bookRepository.GetAllAsync();
        return all.Select(x => x.EducationLevel ?? "").Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
    }

    public async Task<List<string>> GetAllGradesAsync()
    {
        var all = await _bookRepository.GetAllAsync();
        return all.Select(x => x.Grade ?? "").Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
    }

    public async Task<List<string>> GetGradesByEducationLevelAsync(string educationLevel)
    {
        var all = await _bookRepository.GetAllAsync();
        return all.Where(x => x.EducationLevel == educationLevel).Select(x => x.Grade ?? "").Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
    }
}