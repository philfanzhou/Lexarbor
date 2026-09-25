using System.Text.Json;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Models;
using Lexarbor.Domain.Services;
using Lexarbor.Service.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Lexarbor.Service;

public static partial class VocabularyHttpEndpoints
{
    /// <summary>
    /// Request body ceiling for a batch import, in bytes. 500 entries of
    /// ordinary vocabulary fit in a fraction of it; see ADR-005.
    /// </summary>
    public const long MaxBatchRequestBytes = 1024 * 1024;

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
        adminGroup.MapPost("/vocabulary/batch", ImportVocabularyBatch)
            .WithMetadata(new RequestSizeLimitAttribute(MaxBatchRequestBytes));
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
        [FromQuery] int? page,
        [FromQuery] int? size,
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

    // The body is read here rather than bound as a parameter. Bound, a body
    // over the size limit is answered by the framework with an empty 413, and
    // malformed JSON with a generic 400; read explicitly, both reach the
    // envelope and the order ADR-005 fixes for the checks below.
    private static async Task<IResult> ImportVocabularyBatch(
        HttpRequest httpRequest,
        VocabularyDomainService vocabularyService,
        ILoggerFactory loggerFactory)
    {
        VocabularyBatchImportRequest? request = null;
        if (httpRequest.HasJsonContentType())
        {
            try
            {
                request = await httpRequest.ReadFromJsonAsync<VocabularyBatchImportRequest>(
                    httpRequest.HttpContext.RequestAborted);
            }
            catch (JsonException)
            {
                request = null;
            }
        }

        if (request == null)
        {
            return VocabularyHttpResponse.BadRequest("The request body is not valid JSON.");
        }

        if (string.IsNullOrWhiteSpace(request.BookId))
        {
            return VocabularyHttpResponse.BadRequest("Book ID is required.");
        }

        if (request.Entries == null || request.Entries.Count == 0)
        {
            return VocabularyHttpResponse.BadRequest("At least one entry is required.");
        }

        if (request.Entries.Count > VocabularyDomainService.MaxBatchEntries)
        {
            return VocabularyHttpResponse.BadRequest(
                $"A batch can contain at most {VocabularyDomainService.MaxBatchEntries} entries.");
        }

        var entries = new List<(VocabularyModel Word, VocabularyMeaningModel Meaning)>(
            request.Entries.Count);
        var errors = new List<VocabularyBatchEntryError>();
        for (var index = 0; index < request.Entries.Count; index++)
        {
            var entry = request.Entries[index];
            if (entry == null)
            {
                errors.Add(new VocabularyBatchEntryError { Index = index, Message = "Entry is required." });
                continue;
            }

            var models = entry.ToEntities(request.BookId);
            var error = VocabularyDomainService.ValidateBatchEntry(models.Word, models.Meaning);
            if (error != null)
            {
                errors.Add(new VocabularyBatchEntryError { Index = index, Message = error });
                continue;
            }

            entries.Add(models);
        }

        if (errors.Count > 0)
        {
            return VocabularyHttpResponse.BadRequest(
                errors.Count == 1 ? "1 entry is invalid." : $"{errors.Count} entries are invalid.",
                errors);
        }

        var result = await vocabularyService.ImportBatchAsync(request.BookId, entries);

        // Counts only: entry content is user data and stays out of the log.
        loggerFactory.CreateLogger(nameof(VocabularyHttpEndpoints)).LogInformation(
            "Imported a vocabulary batch into book {BookId}: {Total} entries, {Created} created, {Reused} reused",
            request.BookId.Trim(),
            result.Total,
            result.Created,
            result.Reused);

        return VocabularyHttpResponse.Ok(new VocabularyBatchImportResponse
        {
            Total = result.Total,
            Created = result.Created,
            Reused = result.Reused
        });
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

        // Create has nothing to overwrite, so an omitted DisplayOrder or Status
        // takes the field's default rather than being rejected. Only the replace
        // path below has to insist on them.
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

        // This is a replace, not a merge: every field is written to the stored
        // book, so one the request leaves out is not "unchanged", it is written
        // back as the field's default. Blanking the name, or disabling the book
        // and thereby hiding it from the public catalogue and making every word
        // in it answer 422, is a worse outcome for a caller who only meant to
        // edit a description than a rejected request is. So the three fields
        // whose defaults are destructive have to be sent explicitly.
        if (string.IsNullOrWhiteSpace(request.BookName))
        {
            return VocabularyHttpResponse.BadRequest("BookName is required.");
        }

        if (request.DisplayOrder == null)
        {
            return VocabularyHttpResponse.BadRequest("DisplayOrder is required.");
        }

        if (request.Status == null)
        {
            return VocabularyHttpResponse.BadRequest("Status is required.");
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
        [FromQuery] int? page,
        [FromQuery] int? size,
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

    // Paged like the other two list endpoints. It used to return every word in
    // the book in one response, with no ceiling a caller could set and none the
    // server imposed, so the response grew with the book: 20,000 words took 255
    // ms and materialised an entity and a DTO for each of them before
    // serialising the lot. A book is the thing this service exists to let grow.
    private static async Task<IResult> GetBookWords(
        string id,
        [FromQuery] int? page,
        [FromQuery] int? size,
        VocabularyBookDomainService bookService)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return VocabularyHttpResponse.BadRequest("BookId is required.");
        }

        var paging = NormalizePaging(page, size);
        var (words, totalCount) = await bookService.GetWordsAsync(
            id,
            paging.Page,
            paging.Size);
        var result = new VocabularyPageResponse
        {
            TotalPage = (int)Math.Ceiling(totalCount / (double)paging.Size),
            TotalCount = totalCount
        };
        result.Items.AddRange(words.Select(word => word.ToDto()));
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

    /// <summary>
    /// Resolves the paging a list endpoint was asked for.
    /// </summary>
    /// <remarks>
    /// The parameters are nullable because they are optional, and they were not:
    /// bound as plain integers with ThrowOnBadRequest set, a request that omitted
    /// them was rejected as malformed rather than taking the documented defaults,
    /// so "a missing page is treated as 1" held for <c>page=0</c> and not for a
    /// request with no query string at all. That is the shape a caller reaches
    /// for first, and the 400 it got said only "The request is invalid."
    /// </remarks>
    private static (int Page, int Size) NormalizePaging(int? requestedPage, int? requestedSize)
    {
        var page = requestedPage is null or 0 ? 1 : requestedPage.Value;
        var size = requestedSize is null or 0 ? 20 : requestedSize.Value;
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
