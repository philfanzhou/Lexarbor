using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lexarbor.Database.Migrations
{
    /// <summary>
    /// Collapses each run of repeated waits that the worker recorded while polling (a busy permit, an
    /// unfinished dependency, missing configuration, disabled storage or a paused service) into its first
    /// row, which keeps the run's latest next attempt. Waits that followed a supplier call or a reclaimed
    /// lease stay as they are: a throttle with an error code, every retry and every expired lease.
    /// </summary>
    public partial class CoalesceEnrichmentWaits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A run is a sequence of consecutive waits of one round with the same reason and error code,
            // in the order the repository reads them back. The kept row and the deleted rows are disjoint,
            // so the update and the delete can share one statement.
            migrationBuilder.Sql("""
                WITH flagged AS (
                    SELECT w.id, w.target_id, w.round, w.recorded_at_unix_ms, w.next_attempt_at_unix_ms,
                        CASE WHEN (w.reason IN ('Disabled', 'ConfigurationMissing', 'PreparationPending', 'StorageDisabled', 'ServicePaused')
                                   OR (w.reason = 'Throttled' AND w.error_code IS NULL))
                              AND lag(w.reason) OVER position = w.reason
                              AND lag(w.error_code) OVER position IS NOT DISTINCT FROM w.error_code
                             THEN 0 ELSE 1 END AS starts_run
                    FROM enrichment_wait w
                    WINDOW position AS (PARTITION BY w.target_id, w.round ORDER BY w.recorded_at_unix_ms, w.id)
                ), numbered AS (
                    SELECT f.*, sum(f.starts_run) OVER (PARTITION BY f.target_id, f.round
                        ORDER BY f.recorded_at_unix_ms, f.id ROWS UNBOUNDED PRECEDING) AS run
                    FROM flagged f
                ), runs AS (
                    SELECT n.id, n.next_attempt_at_unix_ms,
                        first_value(n.id) OVER whole_run AS kept_id,
                        max(n.next_attempt_at_unix_ms) OVER whole_run AS run_next_attempt
                    FROM numbered n
                    WINDOW whole_run AS (PARTITION BY n.target_id, n.round, n.run ORDER BY n.recorded_at_unix_ms, n.id
                        ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING)
                ), extended AS (
                    UPDATE enrichment_wait w SET next_attempt_at_unix_ms = r.run_next_attempt
                    FROM runs r
                    WHERE w.id = r.id AND r.id = r.kept_id AND r.run_next_attempt > r.next_attempt_at_unix_ms
                )
                DELETE FROM enrichment_wait w
                USING runs r
                WHERE w.id = r.id AND r.id <> r.kept_id;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The removed rows only repeated the row that was kept. They cannot be restored, and an older
            // version reads the coalesced history without change.
        }
    }
}
