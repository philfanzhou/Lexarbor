using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Ruoyu.Study.Vocabulary.Domain.Services;
using Ruoyu.Study.Vocabulary.Service.Dtos;

namespace Ruoyu.Study.Vocabulary.Service;

/// <summary>
/// Vocabulary HTTP endpoints (16 total: 4 Vocabulary + 12 VocabularyBook, migrated 1:1 from gRPC).
/// Business logic delegates to VocabularyDomainService / VocabularyBookDomainService,
/// identical to the original gRPC impl, only protocol adapted (proto → JSON).
/// </summary>
public static partial class VocabularyHttpEndpoints
{
    public static IEndpointRouteBuilder MapVocabularyHttpEndpoints(this IEndpointRouteBuilder app)
    {
        // /api — external business systems (student, question bank, homework)
        var apiGroup = app.MapGroup("/api");
        apiGroup.MapGet("/vocabulary/{wordId}", GetVocabulary);
        apiGroup.MapGet("/vocabulary", SearchVocabulary);
        apiGroup.MapPost("/vocabulary/question", GetQuestion);
        apiGroup.MapGet("/vocabulary-books/all", GetAllBooks);

        // /admin — management frontend only
        var adminGroup = app.MapGroup("/admin");
        adminGroup.MapPost("/vocabulary", AddOrUpdateVocabulary);
        adminGroup.MapPost("/vocabulary-books", AddBook);
        adminGroup.MapPut("/vocabulary-books", UpdateBook);
        adminGroup.MapGet("/vocabulary-books/{id}", GetBook);
        adminGroup.MapGet("/vocabulary-books", SearchBooks);
        adminGroup.MapGet("/vocabulary-books/by-category", GetBooksByCategory);
        adminGroup.MapGet("/vocabulary-books/categories", GetAllCategories);
        adminGroup.MapGet("/vocabulary-books/education-levels", GetAllEducationLevels);
        adminGroup.MapGet("/vocabulary-books/grades", GetAllGrades);
        adminGroup.MapGet("/vocabulary-books/grades-by-level", GetGradesByEducationLevel);
        adminGroup.MapGet("/vocabulary-books/{id}/words", GetBookWords);
        adminGroup.MapDelete("/vocabulary-books/{id}", DeleteBook);

        return app;
    }

    // ==================== Vocabulary endpoints ====================

    // 1. GET /api/vocabulary/{wordId} — Get vocabulary detail
    private static async Task<IResult> GetVocabulary(
        string wordId,
        [FromQuery] string bookId,
        VocabularyDomainService vocabularyService)
    {
        if (string.IsNullOrWhiteSpace(wordId))
            return VocabularyHttpResponse.BadRequest("ID is required");
        if (string.IsNullOrWhiteSpace(bookId))
            return VocabularyHttpResponse.BadRequest("Book ID is required");

        try
        {
            var (word, meanings) = await vocabularyService.GetDetailAsync(wordId, bookId).ConfigureAwait(false);
            var dto = word.ToDto();
            dto.Meanings.AddRange(meanings.Select(m => m.ToDto()));
            return VocabularyHttpResponse.Ok(dto);
        }
        catch (Exception ex)
        {
            return VocabularyHttpResponse.Internal(ex.Message);
        }
    }

    // 2. GET /api/vocabulary — Search vocabulary
    private static async Task<IResult> SearchVocabulary(
        [FromQuery] string keyword,
        [FromQuery] int page,
        [FromQuery] int size,
        VocabularyDomainService vocabularyService)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return VocabularyHttpResponse.BadRequest("Keyword is required");

        try
        {
            var normalizedPage = page > 0 ? page : 1;
            var normalizedSize = size > 0 ? size : 20;

            var (items, totalCount) = await vocabularyService.SearchAsync(keyword, normalizedPage, normalizedSize).ConfigureAwait(false);
            var totalPages = (int)Math.Ceiling(totalCount / (double)normalizedSize);

            var result = new VocabularyPageResponse
            {
                TotalPage = totalPages,
                TotalCount = totalCount
            };
            result.Items.AddRange(items.Select(e => e.ToDto()));
            return VocabularyHttpResponse.Ok(result);
        }
        catch (Exception ex)
        {
            return VocabularyHttpResponse.Internal(ex.Message);
        }
    }

    // 3. POST /api/vocabulary — Add or update vocabulary
    private static async Task<IResult> AddOrUpdateVocabulary(
        [FromBody] AddOrUpdateRequest request,
        VocabularyDomainService vocabularyService)
    {
        if (request.Word == null || request.Meaning == null)
            return VocabularyHttpResponse.BadRequest("Word and Meaning are required");

        try
        {
            await vocabularyService.AddOrUpdateAsync(request.Word.ToEntity(), request.Meaning.ToEntity()).ConfigureAwait(false);
            return VocabularyHttpResponse.Ok(new BoolResponse { Success = true });
        }
        catch (Exception ex)
        {
            return VocabularyHttpResponse.Internal(ex.Message);
        }
    }

    // 4. POST /api/vocabulary/question — Get a quiz question
    private static async Task<IResult> GetQuestion(
        [FromBody] GetQuestionRequest request,
        VocabularyDomainService vocabularyService)
    {
        if (string.IsNullOrWhiteSpace(request.WordId) || string.IsNullOrWhiteSpace(request.BookId))
            return VocabularyHttpResponse.BadRequest("WordId and BookId are required");

        try
        {
            var (word, meanings) = await vocabularyService.GetDetailAsync(request.WordId, request.BookId).ConfigureAwait(false);
            var correctMeaning = meanings.FirstOrDefault()
                                 ?? throw new InvalidOperationException("Meaning not found");

            bool useChineseQuestion = request.ChineseToEnglish ?? (Guid.NewGuid().GetHashCode() % 2 == 0);

            if (useChineseQuestion)
            {
                var distractorWords = await vocabularyService.GetDistractorWordsAsync(request.WordId, request.BookId, 3).ConfigureAwait(false);
                var options = new List<OptionDto>
                {
                    new OptionDto { Meaning = word.Word, IsCorrect = true }
                };
                options.AddRange(distractorWords.Select(w => new OptionDto { Meaning = w.Word, IsCorrect = false }));
                options = options.OrderBy(_ => Guid.NewGuid()).ToList();

                var response = new QuestionResponse { Word = correctMeaning.Meaning };
                response.Options.AddRange(options);
                return VocabularyHttpResponse.Ok(response);
            }
            else
            {
                var distractorMeanings = await vocabularyService.GetDistractorMeaningsAsync(request.WordId, request.BookId, 3).ConfigureAwait(false);
                var options = new List<OptionDto>
                {
                    new OptionDto { Meaning = correctMeaning.Meaning, IsCorrect = true }
                };
                options.AddRange(distractorMeanings.Select(d => new OptionDto { Meaning = d.Meaning, IsCorrect = false }));
                options = options.OrderBy(_ => Guid.NewGuid()).ToList();

                var response = new QuestionResponse { Word = word.Word };
                response.Options.AddRange(options);
                return VocabularyHttpResponse.Ok(response);
            }
        }
        catch (Exception ex)
        {
            return VocabularyHttpResponse.Internal(ex.Message);
        }
    }

    // ==================== VocabularyBook endpoints ====================

    // 5. POST /api/vocabulary-books — Add a book
    private static async Task<IResult> AddBook(
        [FromBody] VocabularyBookDto request,
        VocabularyBookDomainService bookService)
    {
        if (string.IsNullOrWhiteSpace(request.BookName))
            return VocabularyHttpResponse.BadRequest("BookName is required");

        try
        {
            var entity = request.ToEntity();
            await bookService.AddOrUpdateAsync(entity).ConfigureAwait(false);
            return VocabularyHttpResponse.Ok(new BoolResponse { Success = true });
        }
        catch (Exception ex)
        {
            return VocabularyHttpResponse.Internal(ex.Message);
        }
    }

    // 6. PUT /api/vocabulary-books — Update a book
    private static async Task<IResult> UpdateBook(
        [FromBody] VocabularyBookDto request,
        VocabularyBookDomainService bookService)
    {
        if (string.IsNullOrWhiteSpace(request.Id))
            return VocabularyHttpResponse.BadRequest("Id is required");

        try
        {
            var entity = request.ToEntity();
            await bookService.AddOrUpdateAsync(entity).ConfigureAwait(false);
            return VocabularyHttpResponse.Ok(new BoolResponse { Success = true });
        }
        catch (Exception ex)
        {
            return VocabularyHttpResponse.Internal(ex.Message);
        }
    }

    // 7. GET /api/vocabulary-books/{id} — Get a book
    private static async Task<IResult> GetBook(
        string id,
        VocabularyBookDomainService bookService)
    {
        if (string.IsNullOrWhiteSpace(id))
            return VocabularyHttpResponse.BadRequest("Id is required");

        try
        {
            var entity = await bookService.GetAsync(id).ConfigureAwait(false)
                         ?? throw new InvalidOperationException("Book not found");
            return VocabularyHttpResponse.Ok(entity.ToDto());
        }
        catch (Exception ex)
        {
            return VocabularyHttpResponse.Internal(ex.Message);
        }
    }

    // 8. GET /api/vocabulary-books — Search books
    private static async Task<IResult> SearchBooks(
        [FromQuery] string keyword,
        [FromQuery] int page,
        [FromQuery] int size,
        VocabularyBookDomainService bookService)
    {
        try
        {
            var normalizedPage = page > 0 ? page : 1;
            var normalizedSize = size > 0 ? size : 20;

            var (entities, totalCount) = await bookService.SearchAsync(keyword ?? string.Empty, normalizedPage, normalizedSize).ConfigureAwait(false);
            var totalPages = (int)Math.Ceiling(totalCount / (double)normalizedSize);

            var result = new VocabularyBookPageResponse
            {
                TotalPage = totalPages,
                TotalCount = totalCount
            };
            result.Items.AddRange(entities.Select(e => e.ToDto()));
            return VocabularyHttpResponse.Ok(result);
        }
        catch (Exception ex)
        {
            return VocabularyHttpResponse.Internal(ex.Message);
        }
    }

    // 9. GET /api/vocabulary-books/by-category — Get books by category
    private static async Task<IResult> GetBooksByCategory(
        [FromQuery] string category,
        [FromQuery] string? grade,
        VocabularyBookDomainService bookService)
    {
        try
        {
            var entities = await bookService.GetByCategoryAsync(category, grade).ConfigureAwait(false);

            var result = new VocabularyBookListResponse();
            result.Books.AddRange(entities.Select(e => e.ToDto()));
            return VocabularyHttpResponse.Ok(result);
        }
        catch (Exception ex)
        {
            return VocabularyHttpResponse.Internal(ex.Message);
        }
    }

    // 10. GET /api/vocabulary-books/all — Get all books
    private static async Task<IResult> GetAllBooks(
        VocabularyBookDomainService bookService)
    {
        try
        {
            var entities = await bookService.GetAllAsync().ConfigureAwait(false);

            var result = new VocabularyBookListResponse();
            result.Books.AddRange(entities.Select(e => e.ToDto()));
            return VocabularyHttpResponse.Ok(result);
        }
        catch (Exception ex)
        {
            return VocabularyHttpResponse.Internal(ex.Message);
        }
    }

    // 11. GET /api/vocabulary-books/categories — Get all categories
    private static async Task<IResult> GetAllCategories(
        VocabularyBookDomainService bookService)
    {
        var list = await bookService.GetAllCategoriesAsync().ConfigureAwait(false);
        var res = new StringListResponse();
        res.Items.AddRange(list);
        return VocabularyHttpResponse.Ok(res);
    }

    // 12. GET /api/vocabulary-books/education-levels — Get all education levels
    private static async Task<IResult> GetAllEducationLevels(
        VocabularyBookDomainService bookService)
    {
        var list = await bookService.GetAllEducationLevelsAsync().ConfigureAwait(false);
        var res = new StringListResponse();
        res.Items.AddRange(list);
        return VocabularyHttpResponse.Ok(res);
    }

    // 13. GET /api/vocabulary-books/grades — Get all grades
    private static async Task<IResult> GetAllGrades(
        VocabularyBookDomainService bookService)
    {
        var list = await bookService.GetAllGradesAsync().ConfigureAwait(false);
        var res = new StringListResponse();
        res.Items.AddRange(list);
        return VocabularyHttpResponse.Ok(res);
    }

    // 14. GET /api/vocabulary-books/grades-by-level — Get grades by education level
    private static async Task<IResult> GetGradesByEducationLevel(
        [FromQuery] string value,
        VocabularyBookDomainService bookService)
    {
        var list = await bookService.GetGradesByEducationLevelAsync(value).ConfigureAwait(false);
        var res = new StringListResponse();
        res.Items.AddRange(list);
        return VocabularyHttpResponse.Ok(res);
    }

    // 15. GET /api/vocabulary-books/{id}/words — Get words in a book
    private static async Task<IResult> GetBookWords(
        string id,
        VocabularyBookDomainService bookService)
    {
        if (string.IsNullOrWhiteSpace(id))
            return VocabularyHttpResponse.BadRequest("BookId is required");

        try
        {
            var words = await bookService.GetWordsAsync(id).ConfigureAwait(false);
            var result = new VocabularyListResponse();
            result.Words.AddRange(words.Select(e => e.ToDto()));
            return VocabularyHttpResponse.Ok(result);
        }
        catch (Exception ex)
        {
            return VocabularyHttpResponse.Internal(ex.Message);
        }
    }

    // 16. DELETE /api/vocabulary-books/{id} — Delete a book
    private static async Task<IResult> DeleteBook(
        string id,
        VocabularyBookDomainService bookService)
    {
        if (string.IsNullOrWhiteSpace(id))
            return VocabularyHttpResponse.BadRequest("Id is required");

        try
        {
            await bookService.DeleteAsync(id).ConfigureAwait(false);
            return VocabularyHttpResponse.Ok(new BoolResponse { Success = true });
        }
        catch (Exception ex)
        {
            return VocabularyHttpResponse.Internal(ex.Message);
        }
    }
}
