using Lexarbor.Learning.Database.Entities;
using Lexarbor.Learning.Database.Repositories;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Services;
using Lexarbor.Service;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Lexarbor.Learning.Service;

public static class UnitPracticeEndpoints
{
    public static void MapUnitPractice(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/vocabulary/students/{studentId:guid}/unit-practice");
        group.MapGet("", Current);
        group.MapPost("", Start);
        group.MapPost("/{sessionId:guid}/answers", Answer);
        group.MapPut("/{sessionId:guid}/rounds/{round:int}", Retry);
    }

    public sealed record StartPractice(Guid SessionId, string? BookId, string? UnitId, Guid? PreviousSessionId, string? Mode = null);
    public sealed record PracticeAnswer(Guid QuestionId, string? Answer, Guid? OptionId = null, List<string>? Letters = null, bool Unknown = false);

    private static Task<IResult> Current(Guid studentId, UnitPracticeRepository store, CancellationToken ct) =>
        Run(() => store.WithLearnerAsync<IResult>(studentId, async session =>
            Ok(session is null ? null : await store.ProgressAsync(session, ct)), ct));

    private static Task<IResult> Start(Guid studentId, StartPractice request, UnitPracticeRepository store,
        ICatalogClient catalog, UnitPracticeQuestionSource questions, CancellationToken ct) => Run(async () =>
    {
        var book = request.BookId?.Trim();
        var unit = request.UnitId?.Trim();
        if (request.SessionId == Guid.Empty || string.IsNullOrEmpty(book) || string.IsNullOrEmpty(unit)
            || !UnitPracticeModes.TryParse(request.Mode, out var mode))
            return ApiResponse.Failure(400, "VOCAB_PRACTICE_INVALID", "A session UUID, book, unit and a valid mode are required.");

        async Task<IResult?> Existing(UnitPracticeSession? session)
        {
            if (session?.SessionId == request.SessionId)
                return session.BookId == book && session.UnitId == unit && session.Mode == mode.Name()
                    ? Ok(await store.ProgressAsync(session, ct)) : Conflict();
            return session?.SessionId == request.PreviousSessionId ? null : Conflict();
        }

        // Resume without contacting the catalog. Fetch new targets outside the learner lock, then
        // check the previous session again so an old device cannot overwrite a newer start.
        var existing = await store.WithLearnerAsync(studentId, Existing, ct);
        if (existing is not null) return existing;
        var targets = new List<UnitPracticeTarget>();
        var seen = new HashSet<CardKey>();
        for (var page = 1; ; page++)
        {
            var entries = await catalog.GetEntriesAsync(book, unit, page, 100, ct);
            foreach (var e in entries.Items)
            {
                if (e.Positions.Any(p => p.UnitId == unit) && seen.Add(new(e.NormalizedWord, e.MeaningKey)))
                    targets.Add(new(e.WordId, e.MeaningId, e.Word, e.Meaning, e.PartOfSpeech));
            }
            if (page >= entries.TotalPage || entries.Items.Count == 0) break;
        }
        if (targets.Count == 0) return ApiResponse.Failure(422, "VOCAB_PRACTICE_EMPTY", "The unit contains no practice targets.");
        var issued = await questions.CreateAsync(mode, book, unit, targets, ct);
        return await store.WithLearnerAsync(studentId, async session =>
        {
            var concurrent = await Existing(session);
            if (concurrent is not null) return concurrent;
            var created = await store.ReplaceAsync(studentId, request.SessionId, book, unit, mode, targets, issued, ct);
            return Ok(await store.ProgressAsync(created, ct));
        }, ct);
    });

    private static Task<IResult> Answer(Guid studentId, Guid sessionId, PracticeAnswer request,
        UnitPracticeRepository store, CancellationToken ct) => Run(() => store.WithLearnerAsync(studentId, async session =>
    {
        if (session?.SessionId != sessionId) return Missing();
        var answer = new UnitPracticeAnswer(request.Answer, request.OptionId, request.Letters, request.Unknown);
        var given = (answer.Text != null ? 1 : 0) + (answer.OptionId != null ? 1 : 0) + (answer.Letters != null ? 1 : 0) + (answer.Unknown ? 1 : 0);
        if (request.QuestionId == Guid.Empty || given != 1 || (answer.Text != null && string.IsNullOrWhiteSpace(answer.Text))
            || answer.OptionId == Guid.Empty || answer.Letters is { Count: 0 })
            return AnswerInvalid();
        try
        {
            var result = await store.AnswerAsync(sessionId, request.QuestionId, answer, ct);
            return result is null ? Missing() : Ok(result);
        }
        catch (UnitPracticeAnswerInvalidException) { return AnswerInvalid(); }
    }, ct));

    // Generated retry questions come from the catalog outside the learner lock. The round is checked
    // again under the lock, so a device that started the same round first wins and others resume it.
    private static Task<IResult> Retry(Guid studentId, Guid sessionId, int round, UnitPracticeRepository store,
        UnitPracticeQuestionSource questions, CancellationToken ct) => Run(async () =>
    {
        IReadOnlyList<UnitPracticeRetryItem>? candidates = null;
        UnitPracticeSession? current = null;
        var early = await store.WithLearnerAsync<IResult?>(studentId, async session =>
        {
            if (session?.SessionId != sessionId) return Missing();
            if (round < 2) return ApiResponse.Failure(400, "VOCAB_PRACTICE_ROUND_INVALID", "Retry rounds start at 2.");
            if (round == session.Round) return Ok(await store.ProgressAsync(session, ct));
            if (round != session.Round + 1 || (candidates = await store.RetryCandidatesAsync(session, ct)) is null) return Conflict();
            current = session;
            return null;
        }, ct);
        if (early is not null) return early;
        var fresh = await questions.RetryAsync(UnitPracticeRepository.ModeOf(current!), current!.BookId, current.UnitId, candidates!, ct);
        return await store.WithLearnerAsync(studentId, async session =>
        {
            if (session?.SessionId != sessionId) return Missing();
            if (round == session.Round) return Ok(await store.ProgressAsync(session, ct));
            if (round != session.Round + 1 || !await store.StartRetryAsync(session, round, fresh, Random.Shared.Next, ct))
                return Conflict();
            return Ok(await store.ProgressAsync(session, ct));
        }, ct);
    });

    private static async Task<IResult> Run(Func<Task<IResult>> operation)
    {
        try { return await operation(); }
        catch (VocabStudentNotFoundException) { return ApiResponse.Failure(404, "VOCAB_STUDENT_NOT_FOUND", "Student was not found."); }
        catch (VocabCatalogUnavailableException error) { return ApiResponse.Failure(503, VocabularyCatalogErrorCodes.CatalogUnavailable, error.Message); }
        catch (CatalogException error)
        {
            var status = error.ErrorCode switch
            {
                VocabularyCatalogErrorCodes.BookDisabled => 422,
                VocabularyCatalogErrorCodes.BookNotFound or VocabularyCatalogErrorCodes.UnitNotFound => 404,
                _ => 503
            };
            return ApiResponse.Failure(status, error.ErrorCode, error.Message);
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "vocab_unit_practice_session_id_key" })
        {
            return Conflict();
        }
    }

    private static IResult Missing() => ApiResponse.Failure(404, "VOCAB_PRACTICE_NOT_FOUND", "The session or question was not found.");
    private static IResult Conflict() => ApiResponse.Failure(409, "VOCAB_PRACTICE_CONFLICT", "Read the current session before continuing.");
    private static IResult AnswerInvalid() => ApiResponse.Failure(400, "VOCAB_PRACTICE_ANSWER_INVALID",
        "Supply a question UUID and exactly one of answer, optionId, letters or unknown that suits the question.");
    private static IResult Ok(object? data) => Results.Ok(new { success = true, data = data is UnitPracticeProgress progress ? View(progress) : data });

    private static object View(UnitPracticeProgress p) => new
    {
        p.SessionId,
        p.BookId,
        p.UnitId,
        mode = p.Mode.Name(),
        p.Round,
        p.Total,
        p.Answered,
        p.FirstPassCorrect,
        p.RoundTotal,
        p.RoundAnswered,
        nextQuestion = p.NextQuestion is { } q ? View(q) : null,
        p.Mistakes
    };

    // Spelling keeps its original fields. Other types carry only what their learning-question type defines.
    private static Dictionary<string, object?> View(UnitPracticeQuestion q)
    {
        var view = new Dictionary<string, object?> { ["questionId"] = q.QuestionId, ["position"] = q.Position, ["type"] = q.Type };
        if (q.Type == UnitPracticeIssued.Spelling)
        {
            view["meaning"] = q.Meaning;
            view["partOfSpeech"] = q.PartOfSpeech;
            view["spellingHint"] = q.SpellingHint;
            return view;
        }
        view["prompt"] = q.Prompt;
        if (q.Options != null) view["options"] = q.Options;
        if (q.Pattern != null) view["pattern"] = q.Pattern;
        if (q.AnchorWord != null) view["anchorWord"] = q.AnchorWord;
        if (q.PartOfSpeech != null) view["partOfSpeech"] = q.PartOfSpeech;
        return view;
    }
}
