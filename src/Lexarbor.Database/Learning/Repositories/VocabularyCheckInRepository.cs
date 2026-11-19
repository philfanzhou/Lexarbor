using System.Data;
using System.Globalization;
using System.Text.Json;
using Lexarbor.Learning.Domain.Models;
using Lexarbor.Learning.Domain.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Lexarbor.Learning.Database.Repositories;

public sealed class VocabularyCheckInRepository(LearningDbContext context) : IVocabularyCheckInRepository
{
    public async Task<VocabularyCheckInHistory> ReadAsync(Guid studentId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);
        await context.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken).ConfigureAwait(false);
        if (!await context.Learners.AsNoTracking().AnyAsync(s => s.Id == studentId, cancellationToken).ConfigureAwait(false))
            return new VocabularyCheckInHistory(false, [], []);

        var plans = await context.VocabPlans.AsNoTracking()
            .Where(p => p.StudentId == studentId)
            .Select(p => new { p.Id, p.Status, p.CreatedAt, p.UpdatedAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var planIds = plans.Select(p => p.Id).ToList();
        var changes = await context.VocabPlanChanges.AsNoTracking()
            .Where(c => planIds.Contains(c.PlanId)
                && (c.Action == VocabPlanChangeActions.Complete || c.Action == VocabPlanChangeActions.Reactivate))
            .Select(c => new { c.PlanId, c.Action, c.CreatedAt })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var dates = await context.VocabPlanDays.AsNoTracking()
            .Where(d => planIds.Contains(d.PlanId) && d.CompletedAt != null)
            .Select(d => d.LocalDate).Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var result = plans.Select(plan =>
        {
            // Latest reactivate and the latest complete before it, as in the plan statistics.
            var reactivatedAt = changes.Where(c => c.PlanId == plan.Id && c.Action == VocabPlanChangeActions.Reactivate)
                .Select(c => (DateTimeOffset?)c.CreatedAt).Max();
            var completedAt = reactivatedAt is null ? null : changes
                .Where(c => c.PlanId == plan.Id && c.Action == VocabPlanChangeActions.Complete && c.CreatedAt <= reactivatedAt)
                .Select(c => (DateTimeOffset?)c.CreatedAt).Max();
            return new VocabularyCheckInPlan(plan.Id, (VocabPlanStatus)plan.Status, plan.CreatedAt, plan.UpdatedAt,
                reactivatedAt is { } reactivated && completedAt is { } completed ? new VocabularyPlanReactivation(completed, reactivated) : null);
        }).ToList();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText = """
            SELECT source_plan_id,status,started_at,ended_at,completed_at,reactivated_at,completed_days::text
            FROM vocab_plan_legacy_history WHERE student_id=@student
            """;
        command.Parameters.Add(new NpgsqlParameter("student", studentId));
        await using var rows = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new(rows.GetGuid(0), (VocabPlanStatus)rows.GetInt16(1),
                rows.GetFieldValue<DateTimeOffset>(2), rows.GetFieldValue<DateTimeOffset>(3),
                rows.IsDBNull(4) ? null : new VocabularyPlanReactivation(
                    rows.GetFieldValue<DateTimeOffset>(4), rows.GetFieldValue<DateTimeOffset>(5))));
            using var completed = JsonDocument.Parse(rows.GetString(6));
            foreach (var day in completed.RootElement.EnumerateArray())
                dates.Add(DateOnly.ParseExact(day.GetProperty("date").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        // No writes: disposing rolls back the read-only transaction.
        return new VocabularyCheckInHistory(true, result, dates.Distinct().ToList());
    }
}
