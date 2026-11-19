using System.Globalization;
using System.Text.Json;
using Lexarbor.Learning.Domain.Calendar;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using static Lexarbor.Learning.Service.VocabularyCatalogErrorCodes;

namespace Lexarbor.Learning.Service;

/// <summary>
/// Trusted internal vocabulary endpoints. Portals authorize student identity; the server Beijing clock
/// selects the learning day. Client date/timezone fields are validated compatibility hints only.
/// Question responses never contain correct-answer metadata before judging.
/// </summary>
public static class VocabularyLearningHttpEndpoints
{
    private const int MinTimezoneOffsetMinutes = -720;
    private const int MaxTimezoneOffsetMinutes = 840;
    private const long MaxElapsedMs = 86_400_000;

    public static IEndpointRouteBuilder MapVocabularyLearningHttpEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/vocabulary");

        group.MapGet("/students/{studentId}/reviews", GetFreeReviews);
        group.MapPost("/students/{studentId}/reviews/next", GetNextFreeReview);
        group.MapGet("/students/{studentId:guid}/today", GetToday);
        group.MapPost("/students/{studentId:guid}/questions/next", GetNextQuestion);
        group.MapPost("/questions/{questionId:guid}/answer", AnswerQuestion);
        group.MapGet("/questions/{questionId:guid}/audio", GetQuestionAudio);

        return app;
    }

    private static async Task<IResult> GetQuestionAudio(Guid questionId, HttpContext context,
        [FromServices] VocabularyAudioService audio, CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        var query = context.Request.Query;
        if (!query.TryGetValue("studentId", out var students) || students.Count != 1
            || !Guid.TryParse(students[0], out var studentId) || studentId == Guid.Empty)
            return Error(400, "VOCAB_STUDENT_ID_REQUIRED", "Student id is required.");
        if (!query.TryGetValue("locale", out var locales) || locales.Count != 1
            || locales[0] is not ("en-GB" or "en-US"))
            return Error(400, "VOCAB_AUDIO_LOCALE_INVALID", "Locale must be 'en-GB' or 'en-US'.");
        try
        {
            var item = await audio.GetAsync(questionId, studentId, locales[0]!, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new { success = true, data = new { item.Locale, item.Status, item.Url, item.ExpiresAt } });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return MapDomainError(error);
        }
    }

    private static bool TryFreeReviewQuery(Guid studentId, HttpRequest request, out DateOnly localDate)
    {
        localDate = default;
        var query = request.Query;
        return studentId != Guid.Empty && query.Count == 2
            && query.TryGetValue("localDate", out var dates) && dates.Count == 1
            && DateOnly.TryParseExact(dates[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out localDate)
            && query.TryGetValue("tzOffsetMinutes", out var offsets) && offsets.Count == 1
            && int.TryParse(offsets[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var offset)
            && IsTimezoneInRange(offset);
    }

    private static async Task<IResult> GetFreeReviews(Guid studentId, HttpRequest request,
        VocabularyLearningService learningService, CancellationToken cancellationToken)
    {
        if (!TryFreeReviewQuery(studentId, request, out var localDate))
            return Error(400, "VOCAB_REVIEW_QUERY_INVALID", "A student id, strict localDate and one valid tzOffsetMinutes are required.");
        try
        {
            var snapshot = await learningService.GetFreeReviewsAsync(studentId, LearningDay.Today(request.HttpContext.RequestServices.GetService(typeof(TimeProvider)) as TimeProvider), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new { success = true, data = snapshot });
        }
        catch (Exception error) when (error is not OperationCanceledException) { return MapDomainError(error); }
    }

    private static async Task<IResult> GetNextFreeReview(Guid studentId, HttpRequest request,
        [FromBody] JsonElement body, VocabularyLearningService learningService, CancellationToken cancellationToken)
    {
        if (!TryFreeReviewQuery(studentId, request, out _) || body.ValueKind != JsonValueKind.Object || body.EnumerateObject().Any())
            return Error(400, "VOCAB_REVIEW_QUERY_INVALID", "Valid review query parameters and an empty object are required.");
        try
        {
            var outcome = await learningService.GetNextFreeReviewAsync(studentId, cancellationToken).ConfigureAwait(false);
            return outcome.Question is not null
                ? Results.Ok(new { success = true, data = new { status = "question", question = ToDto(outcome.Question) } })
                : Results.Ok(new { success = true, data = new { status = "done", reason = outcome.DoneReason } });
        }
        catch (Exception error) when (error is not OperationCanceledException) { return MapDomainError(error); }
    }

    private static async Task<IResult> GetToday(
        Guid studentId,
        DateOnly? localDate,
        int? tzOffsetMinutes,
        VocabularyLearningService learningService,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (localDate is null || tzOffsetMinutes is null || !IsTimezoneInRange(tzOffsetMinutes.Value))
        {
            return Error(400, "VOCAB_LOCAL_DATE_INVALID", "A valid localDate and tzOffsetMinutes (-720..840) are required.");
        }

        try
        {
            var snapshot = await learningService.GetTodayAsync(studentId, LearningDay.Today(context.RequestServices.GetService(typeof(TimeProvider)) as TimeProvider), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new { success = true, data = ToDto(snapshot) });
        }
        catch (Exception error)
        {
            return MapDomainError(error);
        }
    }

    private static async Task<IResult> GetNextQuestion(
        Guid studentId,
        DateOnly? localDate,
        int? tzOffsetMinutes,
        [FromBody] NextVocabQuestionRequest request,
        VocabularyLearningService learningService,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (!TryParseMode(request.Mode, out var mode))
        {
            return Error(400, "VOCAB_MODE_INVALID", "Mode must be 'new', 'review' or 'extra'.");
        }
        Guid? batchId = null;
        if (request.ExtraBatchId is not null)
        {
            if (mode != VocabQuestionMode.Extra || !Guid.TryParse(request.ExtraBatchId, out var parsed) || parsed == Guid.Empty)
                return Error(400, "VOCAB_EXTRA_BATCH_INVALID", "Extra batch id must be a nonempty GUID in extra mode.");
            batchId = parsed;
        }
        if (localDate is null || tzOffsetMinutes is null || !IsTimezoneInRange(tzOffsetMinutes.Value))
        {
            return Error(400, "VOCAB_LOCAL_DATE_INVALID", "A valid localDate and tzOffsetMinutes (-720..840) are required.");
        }

        try
        {
            var outcome = await learningService.GetNextAsync(studentId, mode, LearningDay.Today(context.RequestServices.GetService(typeof(TimeProvider)) as TimeProvider), cancellationToken, batchId).ConfigureAwait(false);
            if (mode == VocabQuestionMode.Extra)
                return outcome.Question is { } extra
                    ? Results.Ok(new { success = true, data = new { status = "question", question = ToDto(extra), extraBatch = outcome.ExtraBatch } })
                    : Results.Ok(new { success = true, data = new { status = "done", reason = outcome.DoneReason, extraBatch = outcome.ExtraBatch } });
            if (outcome.Question is not null)
            {
                return Results.Ok(new { success = true, data = new { status = "question", question = ToDto(outcome.Question) } });
            }
            return Results.Ok(new { success = true, data = new { status = "done", reason = outcome.DoneReason } });
        }
        catch (Exception error)
        {
            return MapDomainError(error);
        }
    }

    private static async Task<IResult> AnswerQuestion(
        Guid questionId,
        [FromBody] AnswerVocabQuestionRequest request,
        VocabularyLearningService learningService,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (request.StudentId == Guid.Empty)
        {
            return Error(400, "VOCAB_STUDENT_ID_REQUIRED", "Student id is required.");
        }
        List<string>? letters = null;
        if (request.Letters.ValueKind != JsonValueKind.Undefined)
        {
            if (request.Letters.ValueKind != JsonValueKind.Array || request.OptionId is not null || request.Unknown)
                return MapDomainError(new VocabLettersInvalidException());
            letters = [];
            foreach (var entry in request.Letters.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String)
                    return MapDomainError(new VocabLettersInvalidException());
                try { letters.Add(entry.GetString()!); }
                catch (InvalidOperationException) { return MapDomainError(new VocabLettersInvalidException()); }
            }
        }
        if (letters is null && request.Unknown && request.OptionId is not null)
            return Error(400, "VOCAB_OPTION_INVALID", "Send either optionId or unknown, not both.");
        if (request.ElapsedMs < 0 || request.ElapsedMs > MaxElapsedMs)
        {
            return Error(400, "VOCAB_ELAPSED_INVALID", "Elapsed milliseconds must be between 0 and 86400000.");
        }
        if (!DateOnly.TryParse(request.LocalDate, out var localDate))
        {
            return Error(400, "VOCAB_LOCAL_DATE_INVALID", "A valid localDate (YYYY-MM-DD) is required.");
        }
        if (request.TzOffsetMinutes < MinTimezoneOffsetMinutes || request.TzOffsetMinutes > MaxTimezoneOffsetMinutes)
        {
            return Error(400, "VOCAB_TIMEZONE_INVALID", "Timezone offset is out of range.");
        }

        try
        {
            var result = await learningService.AnswerAsync(
                questionId, request.StudentId, request.OptionId, request.Unknown,
                (int)Math.Min(request.ElapsedMs, int.MaxValue), LearningDay.Today(context.RequestServices.GetService(typeof(TimeProvider)) as TimeProvider), cancellationToken, letters).ConfigureAwait(false);
            return Results.Ok(new { success = true, data = ToDto(result) });
        }
        catch (VocabOptionInvalidException) when (!request.Unknown && request.OptionId is null && letters is null)
        {
            return Error(400, "VOCAB_OPTION_INVALID", "Either optionId or unknown is required.");
        }
        catch (Exception error)
        {
            return MapDomainError(error);
        }
    }


    private static bool IsTimezoneInRange(int tzOffsetMinutes) =>
        tzOffsetMinutes >= MinTimezoneOffsetMinutes && tzOffsetMinutes <= MaxTimezoneOffsetMinutes;

    private static bool TryParseMode(string? mode, out VocabQuestionMode parsed)
    {
        switch ((mode ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "new":
                parsed = VocabQuestionMode.New;
                return true;
            case "review":
                parsed = VocabQuestionMode.Review;
                return true;
            case "extra":
                parsed = VocabQuestionMode.Extra;
                return true;
            default:
                parsed = VocabQuestionMode.New;
                return false;
        }
    }


    private static object ToDto(VocabTodaySnapshot snapshot) => new
    {
        snapshot.PlanId,
        snapshot.LocalDate,
        newWords = new
        {
            target = snapshot.NewWordsTarget,
            learned = snapshot.NewWordsLearned,

            exhausted = snapshot.NewWordsExhausted,
        },
        learning = new
        {
            required = snapshot.LearningRequired ?? snapshot.NewWordsTarget,
            completed = snapshot.LearningCompleted ?? snapshot.NewWordsLearned,
            fillReviewRequired = snapshot.FillReviewRequired,
            fillReviewCompleted = snapshot.FillReviewCompleted,
        },
        review = new
        {
            inScopeRequired = snapshot.ReviewRequired,
            inScopeCompleted = snapshot.ReviewCompleted,
            allDue = snapshot.AllDueCount,
        },
        dayCompleted = snapshot.CompletedAt is not null,
        completedAt = snapshot.CompletedAt,
        canLearnExtra = snapshot.CanLearnExtra,
        extraBatchSize = snapshot.ExtraBatchSize,
        extraBatch = snapshot.ExtraBatch,
        teacherAdjustedAt = snapshot.TeacherAdjustedAt,
    };

    /// <summary>Safe pending view: choices or scalar pattern, without stored answers.</summary>
    private static object ToDto(VocabIssuedQuestionModel question) => question.Direction switch
    {
        VocabQuestionDirection.FillLetters => new
        {
            question.Id,
            kind = question.Kind.ToString(),
            direction = question.Direction.ToString(),
            question.Prompt,
            pattern = question.LetterPattern,
            expiresAt = question.ExpiresAt,
        },
        VocabQuestionDirection.FormFill => new
        {
            question.Id,
            kind = question.Kind.ToString(),
            direction = question.Direction.ToString(),
            question.Prompt,
            question.AnchorWord,
            question.RelationType,
            pattern = question.LetterPattern,
            expiresAt = question.ExpiresAt,
        },
        VocabQuestionDirection.FormChoice => new
        {
            question.Id,
            kind = question.Kind.ToString(),
            direction = question.Direction.ToString(),
            question.Prompt,
            question.RelationType,
            options = ToOptionsDto(question),
            expiresAt = question.ExpiresAt,
        },
        VocabQuestionDirection.RelationChoice => new
        {
            question.Id,
            kind = question.Kind.ToString(),
            direction = question.Direction.ToString(),
            question.Prompt,
            question.AnchorWord,
            options = ToOptionsDto(question),
            expiresAt = question.ExpiresAt,
        },
        VocabQuestionDirection.PartOfSpeechChoice => new
        {
            question.Id,
            kind = question.Kind.ToString(),
            direction = question.Direction.ToString(),
            question.Prompt,
            question.AnchorWord,
            question.PartOfSpeech,
            options = ToOptionsDto(question),
            expiresAt = question.ExpiresAt,
        },
        VocabQuestionDirection.EnglishToChinese when question.FamilySource is { } source => new
        {
            question.Id,
            kind = question.Kind.ToString(),
            direction = question.Direction.ToString(),
            question.Prompt,
            familySource = new { source.Word, source.RelationType, source.Direction },
            options = ToOptionsDto(question),
            expiresAt = question.ExpiresAt,
        },
        _ => new
        {
            question.Id,
            kind = question.Kind.ToString(),
            direction = question.Direction.ToString(),
            question.Prompt,
            options = ToOptionsDto(question),
            expiresAt = question.ExpiresAt,
        },
    };

    private static object ToOptionsDto(VocabIssuedQuestionModel question) =>
        question.Options.Select(option => new { optionId = option.OptionId, text = option.Text }).ToList();

    private static object ToDto(VocabAnswerResult result) => result.CorrectWord is not null
        ? new
        {
            questionId = result.QuestionId,
            result = result.Result.ToString(),
            correctOptionId = result.CorrectOptionId,
            correctWord = result.CorrectWord,
            rating = result.Rating.ToString(),
            dueAt = result.DueAt,
        }
        : new
        {
            questionId = result.QuestionId,
            result = result.Result.ToString(),
            correctOptionId = result.CorrectOptionId,
            rating = result.Rating.ToString(),
            dueAt = result.DueAt,
        };

    private static IResult Error(int statusCode, string code, string message) =>
        Results.Json(new { success = false, code, message }, statusCode: statusCode);

    private static IResult MapDomainError(Exception error) => error switch
    {
        VocabAudioLockedException => Error(409, "VOCAB_AUDIO_LOCKED", error.Message),
        VocabAudioUnavailableException => Error(503, AudioUnavailable, error.Message),
        VocabStudentNotFoundException => Error(404, "VOCAB_STUDENT_NOT_FOUND", error.Message),
        VocabQuestionNotFoundException => Error(404, "VOCAB_QUESTION_NOT_FOUND", error.Message),
        VocabQuestionExpiredException => Error(410, "VOCAB_QUESTION_EXPIRED", error.Message),
        VocabLettersInvalidException => Error(400, "VOCAB_LETTERS_INVALID", error.Message),
        VocabOptionInvalidException => Error(400, "VOCAB_OPTION_INVALID", error.Message),
        VocabDayNotCompletedException => Error(409, "VOCAB_DAY_NOT_COMPLETED", error.Message),
        VocabCatalogUnavailableException => Error(503, CatalogUnavailable, error.Message),
        VocabPlanNotFoundException => Error(404, "VOCAB_PLAN_NOT_FOUND", error.Message),
        VocabPlanActiveExistsException => Error(409, "VOCAB_PLAN_ACTIVE_EXISTS", error.Message),
        VocabPlanVersionConflictException => Error(409, "VOCAB_PLAN_VERSION_CONFLICT", error.Message),
        VocabPlanNotActiveException => Error(409, "VOCAB_PLAN_NOT_ACTIVE", error.Message),
        VocabPlanForbiddenException => Error(403, "VOCAB_PLAN_FORBIDDEN", error.Message),
        VocabPlanScopeEmptyException => Error(400, "VOCAB_PLAN_SCOPE_EMPTY", error.Message),
        LexarborCatalogException catalog => catalog.ErrorCode switch
        {
            BookNotFound => Error(404, catalog.ErrorCode, catalog.Message),
            BookDisabled => Error(422, catalog.ErrorCode, catalog.Message),
            UnitNotFound => Error(404, catalog.ErrorCode, catalog.Message),
            _ => Error(503, catalog.ErrorCode, catalog.Message),
        },
        _ => Error(500, "VOCAB_LEARNING_INTERNAL", "An unexpected error occurred."),
    };
}


public sealed class NextVocabQuestionRequest
{
    public string? ExtraBatchId { get; set; }

    public string? Mode { get; set; }
}

public sealed class AnswerVocabQuestionRequest
{
    public Guid StudentId { get; set; }

    public Guid? OptionId { get; set; }

    public bool Unknown { get; set; }

    /// <summary>Unparsed to return the fixed domain error for invalid letter shapes.</summary>
    public JsonElement Letters { get; set; }

    public long ElapsedMs { get; set; }

    public string? LocalDate { get; set; }

    public int TzOffsetMinutes { get; set; }
}
