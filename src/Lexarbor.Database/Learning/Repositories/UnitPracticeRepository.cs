using System.Text.Json;
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
        UnitPracticeMode mode, IReadOnlyList<UnitPracticeTarget> targets, IReadOnlyList<UnitPracticeIssued?> questions, CancellationToken ct)
    {
        await db.UnitPracticeSessions.Where(s => s.StudentId == studentId).ExecuteDeleteAsync(ct);
        var session = new UnitPracticeSession
        {
            StudentId = studentId,
            SessionId = sessionId,
            BookId = bookId,
            UnitId = unitId,
            Mode = mode.Name()
        };
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
            FirstQuestionId = Guid.NewGuid(),
            FirstQuestion = Serialize(questions[index])
        }));
        await db.SaveChangesAsync(ct);
        return session;
    }

    public static UnitPracticeMode ModeOf(UnitPracticeSession session) =>
        UnitPracticeModes.TryParse(session.Mode, out var mode) ? mode : throw new InvalidOperationException("Unknown unit practice mode.");

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
        var item = await items.Where(i => round == 1 ? i.FirstCorrect == null : i.RetryRound == round && i.RetryCorrect == null)
            .OrderBy(i => i.Position).FirstOrDefaultAsync(ct);
        var next = item == null ? null : round == 1
            ? Question(item, item.FirstQuestionId, item.FirstQuestion)
            : Question(item, item.RetryQuestionId!.Value, item.RetryQuestion);
        List<UnitPracticeMistake> mistakes = [];
        if (next == null && count.Answered == count.Total)
        {
            mistakes = await items.Where(i => i.FirstCorrect == false).OrderBy(i => i.Position)
                .Select(i => new UnitPracticeMistake(new(i.WordId, i.MeaningId, i.Word, i.Meaning, i.PartOfSpeech), i.RetryCorrect == true))
                .ToListAsync(ct);
        }
        return new(session.SessionId, session.BookId, session.UnitId, ModeOf(session), round, count.Total, count.Answered, count.Correct,
            round == 1 ? count.Total : count.RetryTotal, round == 1 ? count.Answered : count.RetryAnswered, next, mistakes);
    }

    /// <summary>Null when the question is unknown; throws AnswerInvalidException when the answer does not suit it.</summary>
    public async Task<UnitPracticeFeedback?> AnswerAsync(Guid sessionId, Guid questionId, UnitPracticeAnswer answer, CancellationToken ct)
    {
        var item = await db.UnitPracticeItems.SingleOrDefaultAsync(i => i.SessionId == sessionId
            && (i.FirstQuestionId == questionId || i.RetryQuestionId == questionId), ct);
        if (item is null) return null;
        var first = item.FirstQuestionId == questionId;
        var issued = Deserialize(first ? item.FirstQuestion : item.RetryQuestion);
        var correct = UnitPracticeGrading.Judge(issued, item.Word, answer) ?? throw new AnswerInvalidException();
        bool result;
        if (first) result = item.FirstCorrect ??= correct;
        else result = item.RetryCorrect ??= correct;
        await db.SaveChangesAsync(ct);
        return new(questionId, result, new(item.WordId, item.MeaningId, item.Word, item.Meaning, item.PartOfSpeech),
            issued?.Options?.Single(option => option.IsCorrect).OptionId);
    }

    /// <summary>
    /// The first-pass misses a retry of the session's next round would cover, or null when the current
    /// round is unfinished or nothing is outstanding.
    /// </summary>
    public async Task<IReadOnlyList<UnitPracticeRetryItem>?> RetryCandidatesAsync(UnitPracticeSession session, CancellationToken ct)
    {
        var items = db.UnitPracticeItems.AsNoTracking().Where(i => i.SessionId == session.SessionId);
        if (await items.AnyAsync(i => i.FirstCorrect == null || (i.RetryRound == session.Round && i.RetryCorrect == null), ct))
            return null;
        var outstanding = await items.Where(i => i.FirstCorrect == false && i.RetryCorrect != true).OrderBy(i => i.Position).ToListAsync(ct);
        return outstanding.Count == 0 ? null : outstanding.Select(i => new UnitPracticeRetryItem(i.Position,
            new(i.WordId, i.MeaningId, i.Word, i.Meaning, i.PartOfSpeech), Deserialize(i.FirstQuestion))).ToList();
    }

    /// <summary>
    /// Starts the round with fresh question IDs. A position without a supplied question keeps a spelling
    /// question or reissues its first-pass question with shuffled options.
    /// </summary>
    public async Task<bool> StartRetryAsync(UnitPracticeSession session, int round,
        IReadOnlyDictionary<int, UnitPracticeIssued?> questions, Func<int, int> pick, CancellationToken ct)
    {
        var candidates = await RetryCandidatesAsync(session, ct);
        if (candidates == null) return false;
        var positions = candidates.Select(c => c.Position).ToHashSet();
        var items = await db.UnitPracticeItems.Where(i => i.SessionId == session.SessionId && positions.Contains(i.Position)).ToListAsync(ct);
        foreach (var item in items)
        {
            item.RetryRound = round;
            item.RetryQuestionId = Guid.NewGuid();
            item.RetryCorrect = null;
            item.RetryQuestion = Serialize(questions.TryGetValue(item.Position, out var fresh)
                ? fresh : Deserialize(item.FirstQuestion)?.Reissue(pick));
        }
        await db.SaveChangesAsync(ct);
        await db.UnitPracticeSessions.Where(s => s.StudentId == session.StudentId)
            .ExecuteUpdateAsync(update => update.SetProperty(s => s.Round, round), ct);
        session.Round = round;
        return true;
    }

    private static UnitPracticeQuestion Question(UnitPracticeItem item, Guid id, string? json)
    {
        var issued = Deserialize(json);
        return issued == null
            ? new(id, item.Position, UnitPracticeIssued.Spelling, item.Meaning, item.PartOfSpeech, item.SpellingHint)
            : new(id, item.Position, issued.Type, null, issued.PartOfSpeech, null, issued.Prompt,
                issued.Options?.Select(option => new UnitPracticeOption(option.OptionId, option.Text)).ToList(),
                issued.Pattern, issued.AnchorWord);
    }

    private static string? Serialize(UnitPracticeIssued? question) =>
        question == null ? null : JsonSerializer.Serialize(question, JsonSerializerOptions.Web);

    private static UnitPracticeIssued? Deserialize(string? json) =>
        json == null ? null : JsonSerializer.Deserialize<UnitPracticeIssued>(json, JsonSerializerOptions.Web);
}

public sealed record UnitPracticeRetryItem(int Position, UnitPracticeTarget Target, UnitPracticeIssued? FirstQuestion);
