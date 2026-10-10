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

    /// <summary>
    /// The most questions one batch request may ask for. Every item runs its
    /// own queries against the one SQLite connection, so this is what bounds
    /// how long a single anonymous request holds the database busy.
    /// </summary>
    public const int MaxQuestionBatchItems = 50;

    /// <param name="publicApiRateLimitPolicy">
    /// Name of the rate limit policy to apply to the anonymous <c>/api</c> group,
    /// or null to apply none. Passed in rather than named here because the ceiling
    /// is a hosting decision: this project describes the routes, and the policy it
    /// would otherwise reference is defined and configured by the host.
    /// </param>
    /// <param name="configureAdminGroup">
    /// Optional host-side conventions for the <c>/admin</c> group, such as the
    /// ServiceMantle security response-header requirement. This project cannot
    /// reference the library that defines it, so the host hands the decision in —
    /// the same split as the rate limit policy above.
    /// </param>
    public static IEndpointRouteBuilder MapVocabularyHttpEndpoints(
        this IEndpointRouteBuilder app,
        string? publicApiRateLimitPolicy = null,
        Action<RouteGroupBuilder>? configureAdminGroup = null)
    {
        var apiGroup = app.MapGroup("/api");
        if (!string.IsNullOrWhiteSpace(publicApiRateLimitPolicy))
        {
            apiGroup.RequireRateLimiting(publicApiRateLimitPolicy);
        }

        apiGroup.MapGet("/vocabulary/{wordId}", GetVocabulary);
        apiGroup.MapGet("/vocabulary", SearchVocabulary);
        apiGroup.MapPost("/vocabulary/question", GetQuestion);
        apiGroup.MapPost("/vocabulary/questions", CreateQuestions)
            .WithMetadata(new RequestSizeLimitAttribute(MaxBatchRequestBytes));
        apiGroup.MapGet("/vocabulary-books/all", GetAllBooks);
        apiGroup.MapGet("/vocabulary-books/{bookId}/units", GetPublicBookUnits);
        apiGroup.MapGet("/vocabulary-books/{bookId}/entries", GetPublicBookEntries);

        var adminGroup = app.MapGroup("/admin")
            .RequireAuthorization(AdminEndpointAuthorization.PolicyName);
        configureAdminGroup?.Invoke(adminGroup);
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

        var entries = new List<(VocabularyModel Word, VocabularyMeaningModel Meaning, string? UnitId, string? Section, string? EntryKind)>(
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
            var error = VocabularyDomainService.ValidateBatchEntry(models.Word, models.Meaning, models.UnitId, models.Section, models.EntryKind);
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

        // Unit references are the one entry check that needs the database, so
        // the domain service runs it inside the batch transaction and reports
        // the failures the same way the static checks above do.
        VocabularyBatchImportResult result;
        try
        {
            result = await vocabularyService.ImportBatchAsync(request.BookId, entries);
        }
        catch (BatchEntryValidationException exception)
        {
            var unitErrors = exception.EntryErrors
                .Select(unitError => new VocabularyBatchEntryError
                {
                    Index = unitError.Index,
                    Message = unitError.Message
                })
                .ToList();
            return VocabularyHttpResponse.BadRequest(exception.Message, unitErrors);
        }

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
        // Blank optional identifiers count as absent, so a caller cannot ask
        // for the meaning or unit named "".
        var question = await vocabularyService.CreateQuestionAsync(
            request.WordId,
            request.BookId,
            chineseToEnglish,
            string.IsNullOrWhiteSpace(request.MeaningId) ? null : request.MeaningId.Trim(),
            string.IsNullOrWhiteSpace(request.UnitId) ? null : request.UnitId.Trim(),
            request.SameEntryKind == true);

        var response = new QuestionResponse
        {
            Word = question.Word,
            WordId = question.WordId,
            MeaningId = question.MeaningId,
            ChineseToEnglish = question.ChineseToEnglish
        };
        response.Options.AddRange(question.Options.Select(option => new OptionDto
        {
            Meaning = option.Text,
            IsCorrect = option.IsCorrect,
            WordId = option.WordId,
            MeaningId = option.MeaningId
        }));
        return VocabularyHttpResponse.Ok(response);
    }

    // The body is read here for the same reason ImportVocabularyBatch reads
    // its own: an over-limit body must reach the envelope's 413 and a malformed
    // one its 400, not the framework's bare answers.
    private static async Task<IResult> CreateQuestions(
        HttpRequest httpRequest,
        VocabularyDomainService vocabularyService)
    {
        CreateQuestionsRequest? request = null;
        if (httpRequest.HasJsonContentType())
        {
            try
            {
                request = await httpRequest.ReadFromJsonAsync<CreateQuestionsRequest>(
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

        if (request.Items == null || request.Items.Count == 0)
        {
            return VocabularyHttpResponse.BadRequest("At least one item is required.");
        }

        if (request.Items.Count > MaxQuestionBatchItems)
        {
            return VocabularyHttpResponse.BadRequest(
                $"A batch can contain at most {MaxQuestionBatchItems} items.");
        }

        var results = new List<QuestionBatchResultDto>(request.Items.Count);
        for (var index = 0; index < request.Items.Count; index++)
        {
            // SQLite serves one connection, so the items run one after another;
            // a cancelled request stops here rather than answering the
            // remaining items nobody will read.
            httpRequest.HttpContext.RequestAborted.ThrowIfCancellationRequested();

            var item = request.Items[index];
            if (item == null || string.IsNullOrWhiteSpace(item.WordId) ||
                string.IsNullOrWhiteSpace(item.BookId))
            {
                results.Add(ItemError(index, StatusCodes.Status400BadRequest,
                    "WordId and BookId are required."));
                continue;
            }

            try
            {
                // The same normalization the single-question endpoint applies,
                // so an item answers exactly what that endpoint answers the
                // same request: the direction is drawn when the item left it
                // unset, and blank optional identifiers count as absent.
                var chineseToEnglish =
                    item.ChineseToEnglish ?? Random.Shared.Next(2) == 0;
                var question = await vocabularyService.CreateQuestionAsync(
                    item.WordId,
                    item.BookId,
                    chineseToEnglish,
                    string.IsNullOrWhiteSpace(item.MeaningId) ? null : item.MeaningId.Trim(),
                    string.IsNullOrWhiteSpace(item.UnitId) ? null : item.UnitId.Trim(),
                    item.SameEntryKind == true);

                var response = new QuestionResponse
                {
                    Word = question.Word,
                    WordId = question.WordId,
                    MeaningId = question.MeaningId,
                    ChineseToEnglish = question.ChineseToEnglish
                };
                response.Options.AddRange(question.Options.Select(option => new OptionDto
                {
                    Meaning = option.Text,
                    IsCorrect = option.IsCorrect,
                    WordId = option.WordId,
                    MeaningId = option.MeaningId
                }));
                results.Add(new QuestionBatchResultDto { Index = index, Question = response });
            }
            // One item's 404 or 422 — the same conditions the single endpoint
            // maps through the global exception middleware, reported with the
            // same status and message that middleware answers, so a caller can
            // treat an item's error exactly like a failed single request.
            catch (ResourceNotFoundException)
            {
                results.Add(ItemError(index, StatusCodes.Status404NotFound,
                    "The requested resource was not found."));
            }
            catch (BusinessRuleException)
            {
                results.Add(ItemError(index, StatusCodes.Status422UnprocessableEntity,
                    "The request violates a business rule."));
            }
        }

        return VocabularyHttpResponse.Ok(new CreateQuestionsResponse { Results = results });

        static QuestionBatchResultDto ItemError(int index, int status, string message) => new()
        {
            Index = index,
            Error = new QuestionBatchErrorDto { Status = status, Message = message }
        };
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

    // The two anonymous book-browse routes. They expose an enabled book's
    // units and entries to callers without administrator credentials — the
    // catalogue, search, and detail routes already expose the same data less
    // directly — and answer a missing book 404 and a disabled one 422 exactly
    // like the public detail route, so a disabled book's contents stay
    // unreadable rather than merely unlisted.
    private static async Task<IResult> GetPublicBookUnits(
        string bookId,
        VocabularyPublicQueryService queryService,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(bookId))
        {
            return VocabularyHttpResponse.BadRequest("Book ID is required.");
        }

        var units = await queryService.GetUnitsAsync(bookId, cancellationToken);
        var result = new VocabularyPublicUnitListResponse();
        result.Units.AddRange(units.Units.Select(unit => new VocabularyPublicUnitDto(
            unit.Id, unit.Number, unit.Title, unit.WordCount, unit.MeaningCount)));
        return VocabularyHttpResponse.Ok(result);
    }

    private static async Task<IResult> GetPublicBookEntries(
        string bookId,
        [FromQuery] string? unitId,
        [FromQuery] int? page,
        [FromQuery] int? size,
        VocabularyPublicQueryService queryService,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(bookId))
        {
            return VocabularyHttpResponse.BadRequest("Book ID is required.");
        }

        var paging = NormalizePaging(page, size);
        var entries = await queryService.GetEntriesAsync(
            bookId, unitId, paging.Page, paging.Size, cancellationToken);
        var result = new VocabularyPublicEntryPageResponse
        {
            TotalPage = entries.TotalPage,
            TotalCount = entries.TotalCount,
            WordCount = entries.WordCount
        };
        result.Items.AddRange(entries.Items.Select(entry => new VocabularyPublicEntryDto(
            entry.WordId,
            entry.Word,
            entry.NormalizedWord,
            entry.PhoneticUk,
            entry.PhoneticUs,
            entry.MeaningId,
            entry.PartOfSpeech,
            entry.Meaning,
            entry.MeaningKey,
            entry.Example,
            entry.Positions.Select(position => new VocabularyPublicEntryPositionDto(
                position.UnitId, position.UnitNumber, position.Section, position.EntryKind)).ToList())));
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
