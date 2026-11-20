using Lexarbor.Learning.Database.Entities;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Learning.Database.Repositories;

public sealed class UnitPracticeRepository(LearningDbContext db)
{
    // The learner row exists even before the first session. Serialize two devices starting,
    // answering or replacing the current session, including concurrent learner deletion.
    public async Task<T> WithLearnerAsync<T>(Guid studentId, Func<UnitPracticeSession?, Task<T>> operation, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var learner = await db.Database.SqlQuery<Guid>(
            $"SELECT id AS \"Value\" FROM learner_identity WHERE id = {studentId} FOR UPDATE").ToListAsync(ct);
        if (learner.Count == 0) throw new VocabStudentNotFoundException();
        var session = await db.UnitPracticeSessions.AsNoTracking().SingleOrDefaultAsync(s => s.StudentId == studentId, ct);
        var result = await operation(session);
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task<UnitPracticeSession> ReplaceAsync(Guid studentId, Guid sessionId, string bookId, string unitId,
        IReadOnlyList<UnitPracticeTarget> targets, CancellationToken ct)
    {
        await db.UnitPracticeSessions.Where(s => s.StudentId == studentId).ExecuteDeleteAsync(ct);
        var session = new UnitPracticeSession { StudentId = studentId, SessionId = sessionId, BookId = bookId, UnitId = unitId };
        db.UnitPracticeSessions.Add(session);
        await db.SaveChangesAsync(ct);
        var hints = UnitPracticeSpelling.Hints(targets);
        db.UnitPracticeItems.AddRange(targets.Select((target, index) => new UnitPracticeItem
        {
            SessionId = sessionId,
            Position = index + 1,
            WordId = target.WordId,
            MeaningId = target.MeaningId,
            Word = target.Word,
            Meaning = target.Meaning,
            PartOfSpeech = target.PartOfSpeech,
            SpellingHint = hints.GetValueOrDefault(target.MeaningId),
            FirstQuestionId = Guid.NewGuid()
        }));
        await db.SaveChangesAsync(ct);
        return session;
    }

    public async Task<UnitPracticeProgress> ProgressAsync(UnitPracticeSession session, CancellationToken ct)
    {
        var items = db.UnitPracticeItems.AsNoTracking().Where(i => i.SessionId == session.SessionId);
        var round = session.Round;
        var count = await items.GroupBy(i => i.SessionId).Select(g => new
        {
            Total = g.Count(),
            Answered = g.Count(i => i.FirstCorrect != null),
            Correct = g.Count(i => i.FirstCorrect == true),
            RetryTotal = g.Count(i => i.RetryRound == round),
            RetryAnswered = g.Count(i => i.RetryRound == round && i.RetryCorrect != null)
        }).SingleAsync(ct);
        var next = await items.Where(i => round == 1 ? i.FirstCorrect == null : i.RetryRound == round && i.RetryCorrect == null)
            .OrderBy(i => i.Position).Select(i => new UnitPracticeQuestion(round == 1 ? i.FirstQuestionId : i.RetryQuestionId!.Value,
                i.Meaning, i.PartOfSpeech, i.SpellingHint, i.Position)).FirstOrDefaultAsync(ct);
        List<UnitPracticeMistake> mistakes = [];
        if (next == null && count.Answered == count.Total)
        {
            mistakes = await items.Where(i => i.FirstCorrect == false).OrderBy(i => i.Position)
                .Select(i => new UnitPracticeMistake(new(i.WordId, i.MeaningId, i.Word, i.Meaning, i.PartOfSpeech), i.RetryCorrect == true))
                .ToListAsync(ct);
        }
        return new(session.SessionId, session.BookId, session.UnitId, round, count.Total, count.Answered, count.Correct,
            round == 1 ? count.Total : count.RetryTotal, round == 1 ? count.Answered : count.RetryAnswered, next, mistakes);
    }

    public async Task<UnitPracticeFeedback?> AnswerAsync(Guid sessionId, Guid questionId, string? answer, CancellationToken ct)
    {
        var item = await db.UnitPracticeItems.SingleOrDefaultAsync(i => i.SessionId == sessionId
            && (i.FirstQuestionId == questionId || i.RetryQuestionId == questionId), ct);
        if (item is null) return null;
        var correct = string.Equals(answer?.Trim(), item.Word.Trim(), StringComparison.OrdinalIgnoreCase);
        var first = item.FirstQuestionId == questionId;
        bool result;
        if (first) result = item.FirstCorrect ??= correct;
        else result = item.RetryCorrect ??= correct;
        await db.SaveChangesAsync(ct);
        return new(questionId, result, new(item.WordId, item.MeaningId, item.Word, item.Meaning, item.PartOfSpeech));
    }

    public async Task<bool> StartRetryAsync(UnitPracticeSession session, int round, CancellationToken ct)
    {
        var items = db.UnitPracticeItems.Where(i => i.SessionId == session.SessionId);
        if (await items.AnyAsync(i => i.FirstCorrect == null || (i.RetryRound == session.Round && i.RetryCorrect == null), ct))
            return false;
        var assigned = await db.Database.ExecuteSqlAsync($"""
            UPDATE vocab_unit_practice_item SET retry_round = {round}, retry_question_id = gen_random_uuid(), retry_correct = NULL
            WHERE session_id = {session.SessionId} AND first_correct = false AND retry_correct IS DISTINCT FROM true
            """, ct);
        if (assigned == 0) return false;
        await db.UnitPracticeSessions.Where(s => s.StudentId == session.StudentId)
            .ExecuteUpdateAsync(update => update.SetProperty(s => s.Round, round), ct);
        session.Round = round;
        return true;
    }
}
