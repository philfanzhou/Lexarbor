using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;

namespace Lexarbor.Domain.Services;

public class VocabularyBookDomainService
{
    private readonly IVocabularyBookRepository _bookRepository;
    private readonly IUnitOfWork _unitOfWork;

    public VocabularyBookDomainService(
        IVocabularyBookRepository bookRepository,
        IUnitOfWork unitOfWork)
    {
        _bookRepository = bookRepository;
        _unitOfWork = unitOfWork;
    }

    public Task<List<VocabularyBookModel>> GetAllAsync()
    {
        return _bookRepository.GetActiveAsync();
    }

    public Task<List<VocabularyBookModel>> GetByCategoryAsync(string? category, string? grade)
    {
        return _bookRepository.GetByCategoryAsync(category, grade);
    }

    public Task<VocabularyBookModel?> GetAsync(string id)
    {
        return _bookRepository.GetByIdAsync(id);
    }

    public Task<(List<VocabularyBookModel> Items, int TotalCount)> SearchAsync(
        string? keyword,
        int page,
        int size)
    {
        return _bookRepository.SearchAsync(keyword, page, size);
    }

    public async Task<VocabularyBookModel> AddOrUpdateAsync(VocabularyBookModel book)
    {
        VocabularyBookModel existing;
        if (!string.IsNullOrWhiteSpace(book.Id))
        {
            existing = await _bookRepository.GetByIdAsync(book.Id)
                       ?? throw new ResourceNotFoundException("Vocabulary book was not found.");

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
        else
        {
            var now = DateTimeOffset.UtcNow;
            book.Id = Guid.NewGuid().ToString();
            book.CreatedAt = now;
            book.UpdatedAt = now;
            await _bookRepository.AddAsync(book);
            existing = book;
        }

        await _unitOfWork.SaveChangesAsync();
        return existing;
    }

    public async Task DeleteAsync(string id)
    {
        _ = await _bookRepository.GetByIdAsync(id)
            ?? throw new ResourceNotFoundException("Vocabulary book was not found.");

        if (await _bookRepository.HasMeaningsAsync(id))
        {
            throw new ConflictException(
                "A vocabulary book with meanings cannot be deleted. Disable it instead.");
        }

        await _bookRepository.DeleteAsync(id);
        await _unitOfWork.SaveChangesAsync();
    }

    public async Task<List<VocabularyModel>> GetWordsAsync(string bookId)
    {
        _ = await _bookRepository.GetByIdAsync(bookId)
            ?? throw new ResourceNotFoundException("Vocabulary book was not found.");

        return await _bookRepository.GetWordsAsync(bookId);
    }

    public Task<List<string>> GetAllCategoriesAsync()
    {
        return _bookRepository.GetDistinctCategoriesAsync();
    }

    public Task<List<string>> GetAllEducationLevelsAsync()
    {
        return _bookRepository.GetDistinctEducationLevelsAsync();
    }

    public Task<List<string>> GetAllGradesAsync()
    {
        return _bookRepository.GetDistinctGradesAsync();
    }

    public Task<List<string>> GetGradesByEducationLevelAsync(string educationLevel)
    {
        return _bookRepository.GetDistinctGradesByEducationLevelAsync(educationLevel);
    }
}
