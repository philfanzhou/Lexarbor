using Lexarbor.Learning.Database.Entities;
using Lexarbor.Learning.Database.Repositories;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Services;
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

    public sealed record StartPractice(Guid SessionId, string? BookId, string? UnitId, Guid? PreviousSessionId);
    public sealed record PracticeAnswer(Guid QuestionId, string? Answer, bool Unknown = false);

    private static Task<IResult> Current(Guid studentId, UnitPracticeRepository store, CancellationToken ct) =>
        Run(() => store.WithLearnerAsync<IResult>(studentId, async session =>
            Ok(session is null ? null : await store.ProgressAsync(session, ct)), ct));

    private static Task<IResult> Start(Guid studentId, StartPractice request, UnitPracticeRepository store,
        ILexarborClient catalog, CancellationToken ct) => Run(async () =>
    {
        var book = request.BookId?.Trim();
        var unit = request.UnitId?.Trim();
        if (request.SessionId == Guid.Empty || string.IsNullOrEmpty(book) || string.IsNullOrEmpty(unit))
            return Error(400, "VOCAB_PRACTICE_INVALID", "A session UUID, book and unit are required.");

        async Task<IResult?> Existing(UnitPracticeSession? session)
        {
            if (session?.SessionId == request.SessionId)
                return session.BookId == book && session.UnitId == unit ? Ok(await store.ProgressAsync(session, ct)) : Conflict();
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
        if (targets.Count == 0) return Error(422, "VOCAB_PRACTICE_EMPTY", "The unit contains no practice targets.");
        return await store.WithLearnerAsync(studentId, async session =>
        {
            var concurrent = await Existing(session);
            if (concurrent is not null) return concurrent;
            var created = await store.ReplaceAsync(studentId, request.SessionId, book, unit, targets, ct);
            return Ok(await store.ProgressAsync(created, ct));
        }, ct);
    });

    private static Task<IResult> Answer(Guid studentId, Guid sessionId, PracticeAnswer request,
        UnitPracticeRepository store, CancellationToken ct) => Run(() => store.WithLearnerAsync(studentId, async session =>
    {
        if (session?.SessionId != sessionId) return Missing();
        if (request.QuestionId == Guid.Empty || (request.Unknown ? request.Answer != null : string.IsNullOrWhiteSpace(request.Answer)))
            return Error(400, "VOCAB_PRACTICE_ANSWER_INVALID", "Supply a question UUID and either an answer or unknown.");
        var result = await store.AnswerAsync(sessionId, request.QuestionId, request.Answer, ct);
        return result is null ? Missing() : Ok(result);
    }, ct));

    private static Task<IResult> Retry(Guid studentId, Guid sessionId, int round, UnitPracticeRepository store,
        CancellationToken ct) => Run(() => store.WithLearnerAsync(studentId, async session =>
    {
        if (session?.SessionId != sessionId) return Missing();
        if (round < 2) return Error(400, "VOCAB_PRACTICE_ROUND_INVALID", "Retry rounds start at 2.");
        if (round != session.Round && (round != session.Round + 1 || !await store.StartRetryAsync(session, round, ct)))
            return Conflict();
        return Ok(await store.ProgressAsync(session, ct));
    }, ct));

    private static async Task<IResult> Run(Func<Task<IResult>> operation)
    {
        try { return await operation(); }
        catch (VocabStudentNotFoundException) { return Error(404, "VOCAB_STUDENT_NOT_FOUND", "Student was not found."); }
        catch (LexarborCatalogException error)
        {
            var status = error.ErrorCode switch
            {
                VocabularyCatalogErrorCodes.BookDisabled => 422,
                VocabularyCatalogErrorCodes.BookNotFound or VocabularyCatalogErrorCodes.UnitNotFound => 404,
                _ => 503
            };
            return Error(status, error.ErrorCode, error.Message);
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "vocab_unit_practice_session_id_key" })
        {
            return Conflict();
        }
    }

    private static IResult Missing() => Error(404, "VOCAB_PRACTICE_NOT_FOUND", "The session or question was not found.");
    private static IResult Conflict() => Error(409, "VOCAB_PRACTICE_CONFLICT", "Read the current session before continuing.");
    private static IResult Ok(object? data) => Results.Ok(new { success = true, data });
    private static IResult Error(int status, string code, string message) =>
        Results.Json(new { success = false, code, message }, statusCode: status);
}
