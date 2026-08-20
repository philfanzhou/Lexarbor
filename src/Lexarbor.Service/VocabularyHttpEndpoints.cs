using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Services;
using Lexarbor.Service.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;

namespace Lexarbor.Service;

public static partial class VocabularyHttpEndpoints
{
    /// <param name="publicApiRateLimitPolicy">
    /// Name of the rate limit policy to apply to the anonymous <c>/api</c> group,
    /// or null to apply none. Passed in rather than named here because the ceiling
    /// is a hosting decision: this project describes the routes, and the policy it
    /// would otherwise reference is defined and configured by the host.
    /// </param>
    public static IEndpointRouteBuilder MapVocabularyHttpEndpoints(
        this IEndpointRouteBuilder app,
        string? publicApiRateLimitPolicy = null)
    {
        var apiGroup = app.MapGroup("/api");
        if (!string.IsNullOrWhiteSpace(publicApiRateLimitPolicy))
        {
            apiGroup.RequireRateLimiting(publicApiRateLimitPolicy);
        }

        apiGroup.MapGet("/vocabulary/{wordId}", GetVocabulary);
        apiGroup.MapGet("/vocabulary", SearchVocabulary);
        apiGroup.MapPost("/vocabulary/question", GetQuestion);
        apiGroup.MapGet("/vocabulary-books/all", GetAllBooks);

        var adminGroup = app.MapGroup("/admin")
            .RequireAuthorization("VocabularyAdmin");
        adminGroup.MapPost("/vocabulary", AddOrUpdateVocabulary);
        adminGroup.MapPost("/vocabulary-books", AddBook);
        adminGroup.MapPut("/vocabulary-books", UpdateBook);
        adminGroup.MapGet("/vocabulary-books/{id}", GetBook);
        adminGroup.MapGet("/vocabulary-books", SearchBooks);
        adminGroup.MapGet("/vocabulary-books/by-category", GetBooksByCategory);
        adminGroup.MapGet("/vocabulary-books/categories", GetAllCategories);
        adminGroup.MapGet("/vocabulary-books/education-levels", GetAllEducationLevels);
        adminGroup.MapGet("/vocabulary-books/grades", GetAllGrades);
        adminGroup.MapGet(
            "/vocabulary-books/grades-by-level",
            GetGradesByEducationLevel);
        adminGroup.MapGet("/vocabulary-books/{id}/words", GetBookWords);
        adminGroup.MapDelete("/vocabulary-books/{id}", DeleteBook);

        return app;
    }

    private static async Task<IResult> GetVocabulary(
        string wordId,
        [FromQuery] string? bookId,
        VocabularyDomainService vocabularyService)
    {
        if (string.IsNullOrWhiteSpace(wordId))
        {
            return VocabularyHttpResponse.BadRequest("ID is required.");
        }

        if (string.IsNullOrWhiteSpace(bookId))
        {
            return VocabularyHttpResponse.BadRequest("Book ID is required.");
        }

        var (word, meanings) = await vocabularyService.GetDetailAsync(wordId, bookId);
        var dto = word.ToDto();
        dto.Meanings.AddRange(meanings.Select(meaning => meaning.ToDto()));
        return VocabularyHttpResponse.Ok(dto);
    }

    private static async Task<IResult> SearchVocabulary(
        [FromQuery] string? keyword,
        [FromQuery] int page,
        [FromQuery] int size,
        VocabularyDomainService vocabularyService)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return VocabularyHttpResponse.BadRequest("Keyword is required.");
        }

        var paging = NormalizePaging(page, size);
        var (items, totalCount) = await vocabularyService.SearchAsync(
            keyword,
            paging.Page,
            paging.Size);
        var result = new VocabularyPageResponse
        {
            TotalPage = (int)Math.Ceiling(totalCount / (double)paging.Size),
            TotalCount = totalCount
        };
        result.Items.AddRange(items.Select(item => item.ToDto()));
        return VocabularyHttpResponse.Ok(result);
    }

    private static async Task<IResult> AddOrUpdateVocabulary(
        [FromBody] AddOrUpdateRequest request,
        VocabularyDomainService vocabularyService)
    {
        if (request.Word == null || request.Meaning == null)
        {
            return VocabularyHttpResponse.BadRequest(
                "Word and Meaning are required.");
        }

        await vocabularyService.AddOrUpdateAsync(
            request.Word.ToEntity(),
            request.Meaning.ToEntity());
        return VocabularyHttpResponse.Ok(new BoolResponse { Success = true });
    }

    private static async Task<IResult> GetQuestion(
        [FromBody] GetQuestionRequest request,
        VocabularyDomainService vocabularyService)
    {
        if (string.IsNullOrWhiteSpace(request.WordId) ||
            string.IsNullOrWhiteSpace(request.BookId))
        {
            return VocabularyHttpResponse.BadRequest(
                "WordId and BookId are required.");
        }

        var chineseToEnglish =
            request.ChineseToEnglish ?? Random.Shared.Next(2) == 0;
        var question = await vocabularyService.CreateQuestionAsync(
            request.WordId,
            request.BookId,
            chineseToEnglish);

        var response = new QuestionResponse { Word = question.Word };
        response.Options.AddRange(question.Options.Select(option => new OptionDto
        {
            Meaning = option.Text,
            IsCorrect = option.IsCorrect
        }));
        return VocabularyHttpResponse.Ok(response);
    }

    private static async Task<IResult> AddBook(
        [FromBody] VocabularyBookDto request,
        VocabularyBookDomainService bookService)
    {
        if (string.IsNullOrWhiteSpace(request.BookName))
        {
            return VocabularyHttpResponse.BadRequest("BookName is required.");
        }

        if (!string.IsNullOrWhiteSpace(request.Id))
        {
            return VocabularyHttpResponse.BadRequest(
                "Id must be empty when creating a vocabulary book.");
        }

        await bookService.AddOrUpdateAsync(request.ToEntity());
        return VocabularyHttpResponse.Ok(new BoolResponse { Success = true });
    }

    private static async Task<IResult> UpdateBook(
        [FromBody] VocabularyBookDto request,
        VocabularyBookDomainService bookService)
    {
        if (string.IsNullOrWhiteSpace(request.Id))
        {
            return VocabularyHttpResponse.BadRequest("Id is required.");
        }

        await bookService.AddOrUpdateAsync(request.ToEntity());
        return VocabularyHttpResponse.Ok(new BoolResponse { Success = true });
    }

    private static async Task<IResult> GetBook(
        string id,
        VocabularyBookDomainService bookService)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return VocabularyHttpResponse.BadRequest("Id is required.");
        }

        var book = await bookService.GetAsync(id)
                   ?? throw new ResourceNotFoundException(
                       "Vocabulary book was not found.");
        return VocabularyHttpResponse.Ok(book.ToDto());
    }

    private static async Task<IResult> SearchBooks(
        [FromQuery] string? keyword,
        [FromQuery] int page,
        [FromQuery] int size,
        VocabularyBookDomainService bookService)
    {
        var paging = NormalizePaging(page, size);
        var (books, totalCount) = await bookService.SearchAsync(
            keyword ?? string.Empty,
            paging.Page,
            paging.Size);
        var result = new VocabularyBookPageResponse
        {
            TotalPage = (int)Math.Ceiling(totalCount / (double)paging.Size),
            TotalCount = totalCount
        };
        result.Items.AddRange(books.Select(book => book.ToDto()));
        return VocabularyHttpResponse.Ok(result);
    }

    private static async Task<IResult> GetBooksByCategory(
        [FromQuery] string? category,
        [FromQuery] string? grade,
        VocabularyBookDomainService bookService)
    {
        var books = await bookService.GetByCategoryAsync(
            category ?? string.Empty,
            grade);
        var result = new VocabularyBookListResponse();
        result.Books.AddRange(books.Select(book => book.ToDto()));
        return VocabularyHttpResponse.Ok(result);
    }

    private static async Task<IResult> GetAllBooks(
        VocabularyBookDomainService bookService)
    {
        var books = await bookService.GetAllAsync();
        var result = new VocabularyBookListResponse();
        result.Books.AddRange(books.Select(book => book.ToDto()));
        return VocabularyHttpResponse.Ok(result);
    }

    private static async Task<IResult> GetAllCategories(
        VocabularyBookDomainService bookService)
    {
        var result = new StringListResponse();
        result.Items.AddRange(await bookService.GetAllCategoriesAsync());
        return VocabularyHttpResponse.Ok(result);
    }

    private static async Task<IResult> GetAllEducationLevels(
        VocabularyBookDomainService bookService)
    {
        var result = new StringListResponse();
        result.Items.AddRange(await bookService.GetAllEducationLevelsAsync());
        return VocabularyHttpResponse.Ok(result);
    }

    private static async Task<IResult> GetAllGrades(
        VocabularyBookDomainService bookService)
    {
        var result = new StringListResponse();
        result.Items.AddRange(await bookService.GetAllGradesAsync());
        return VocabularyHttpResponse.Ok(result);
    }

    private static async Task<IResult> GetGradesByEducationLevel(
        [FromQuery] string? value,
        VocabularyBookDomainService bookService)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return VocabularyHttpResponse.BadRequest(
                "Education level is required.");
        }

        var result = new StringListResponse();
        result.Items.AddRange(
            await bookService.GetGradesByEducationLevelAsync(value));
        return VocabularyHttpResponse.Ok(result);
    }

    private static async Task<IResult> GetBookWords(
        string id,
        VocabularyBookDomainService bookService)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return VocabularyHttpResponse.BadRequest("BookId is required.");
        }

        var result = new VocabularyListResponse();
        result.Words.AddRange(
            (await bookService.GetWordsAsync(id)).Select(word => word.ToDto()));
        return VocabularyHttpResponse.Ok(result);
    }

    private static async Task<IResult> DeleteBook(
        string id,
        VocabularyBookDomainService bookService)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return VocabularyHttpResponse.BadRequest("Id is required.");
        }

        await bookService.DeleteAsync(id);
        return VocabularyHttpResponse.Ok(new BoolResponse { Success = true });
    }

    private static (int Page, int Size) NormalizePaging(int page, int size)
    {
        page = page == 0 ? 1 : page;
        size = size == 0 ? 20 : size;
        if (page < 1 ||
            size < 1 ||
            size > 100 ||
            (long)(page - 1) * size > int.MaxValue)
        {
            throw new DomainValidationException("Paging parameters are invalid.");
        }

        return (page, size);
    }
}
