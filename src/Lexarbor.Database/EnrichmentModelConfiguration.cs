using System.Text.RegularExpressions;
using Lexarbor.Database.Entities;
using Lexarbor.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Lexarbor.Database;

internal static class EnrichmentModelConfiguration
{
    public static void Configure(ModelBuilder builder)
    {
        var target = builder.Entity<EnrichmentTargetEntity>();
        target.ToTable("enrichment_target", table =>
        {
            table.HasCheckConstraint("CK_enrichment_target_locale", "locale IN ('en-GB','en-US')");
            table.HasCheckConstraint("CK_enrichment_target_step", "step IN ('Prepare','Fields','Audio') AND (meaning_id IS NULL OR step != 'Fields')");
            table.HasCheckConstraint("CK_enrichment_target_version", "current_round > 0 AND epoch > 0");
        });
        target.HasKey(row => row.Id);
        target.Property(row => row.Locale).HasConversion(new ValueConverter<AudioLocale, string>(v => v.ToCode(), v => AudioLocales.FromCode(v)));
        target.Property(row => row.Step).HasConversion<string>();
        target.HasIndex(row => new { row.WordId, row.Locale, row.Step }).IsUnique().HasFilter("meaning_id IS NULL");
        target.HasIndex(row => new { row.WordId, row.MeaningId, row.Locale, row.Step }).IsUnique().HasFilter("meaning_id IS NOT NULL");
        // Historical identities deliberately outlive vocabulary deletion.
        var round = builder.Entity<EnrichmentRoundEntity>();
        round.ToTable("enrichment_round", table =>
        {
            table.HasCheckConstraint("CK_enrichment_round_state", "state IN ('Pending','Running','Waiting','Succeeded','Failed','Superseded','Cancelled')");
            table.HasCheckConstraint("CK_enrichment_round_counts", "failure_budget BETWEEN 1 AND 20 AND consumed_failures BETWEEN 0 AND failure_budget AND request_count >= consumed_failures AND lease_fence >= 0");
            table.HasCheckConstraint("CK_enrichment_round_input", "length(input_revision) = 64 AND input_revision NOT GLOB '*[^0-9a-f]*' AND length(source_revision) = 64 AND source_revision NOT GLOB '*[^0-9a-f]*'");
            table.HasCheckConstraint("CK_enrichment_round_config", "length(configuration_version) BETWEEN 1 AND 128");
            table.HasCheckConstraint("CK_enrichment_round_plan", "plan_json IS NULL OR length(plan_json) <= 16384");
            table.HasCheckConstraint("CK_enrichment_round_lease", "(state = 'Running' AND lease_owner IS NOT NULL AND lease_fence > 0 AND lease_expires_at_unix_ms IS NOT NULL) OR (state != 'Running' AND lease_owner IS NULL AND lease_expires_at_unix_ms IS NULL)");
            table.HasCheckConstraint("CK_enrichment_round_wait", "wait_reason IS NULL OR wait_reason IN ('Disabled','ConfigurationMissing','StorageDisabled','ServicePaused','Throttled','Retry','LeaseExpired')");
            table.HasCheckConstraint("CK_enrichment_round_times", "created_at_unix_ms >= 0 AND updated_at_unix_ms >= created_at_unix_ms");
        });
        round.HasKey(row => new { row.TargetId, row.Number });
        round.HasOne<EnrichmentTargetEntity>().WithMany().HasForeignKey(row => row.TargetId).OnDelete(DeleteBehavior.Cascade);
        round.Property(row => row.State).HasConversion<string>();
        round.Property(row => row.WaitReason).HasConversion<string>();
        round.HasIndex(row => new { row.State, row.NextAttemptAtUnixMs });
        round.HasIndex(row => new { row.TargetId, row.RetryKey }).IsUnique().HasFilter("retry_key IS NOT NULL");
        var call = builder.Entity<EnrichmentCallEntity>();
        call.ToTable("enrichment_call", table =>
        {
            table.HasCheckConstraint("CK_enrichment_call_result", "result IN ('Started','Succeeded','Throttled','TransientFailure','TerminalFailure')");
            table.HasCheckConstraint("CK_enrichment_call_service", "service IN ('text','tts')");
            table.HasCheckConstraint("CK_enrichment_call_times", "started_at_unix_ms >= 0 AND ((result = 'Started' AND finished_at_unix_ms IS NULL) OR (result != 'Started' AND finished_at_unix_ms >= started_at_unix_ms))");
        });
        call.HasKey(row => row.Id);
        call.Property(row => row.Result).HasConversion<string>();
        call.HasOne<EnrichmentRoundEntity>().WithMany().HasForeignKey(row => new { row.TargetId, row.Round }).OnDelete(DeleteBehavior.Cascade);
        call.HasIndex(row => new { row.TargetId, row.Round }).IsUnique().HasFilter("result = 'Started'");
        call.HasIndex(row => row.UploadIdempotencyKey).IsUnique().HasFilter("upload_idempotency_key IS NOT NULL");
        var wait = builder.Entity<EnrichmentWaitEntity>();
        wait.ToTable("enrichment_wait", table =>
        {
            table.HasCheckConstraint("CK_enrichment_wait_reason", "reason IN ('Disabled','ConfigurationMissing','StorageDisabled','ServicePaused','Throttled','Retry','LeaseExpired')");
            table.HasCheckConstraint("CK_enrichment_wait_times", "recorded_at_unix_ms >= 0 AND next_attempt_at_unix_ms >= recorded_at_unix_ms");
        });
        wait.HasKey(row => row.Id);
        wait.Property(row => row.Reason).HasConversion<string>();
        wait.HasOne<EnrichmentRoundEntity>().WithMany().HasForeignKey(row => new { row.TargetId, row.Round }).OnDelete(DeleteBehavior.Cascade);
        foreach (var entity in new[] { target.Metadata, round.Metadata, call.Metadata, wait.Metadata })
            foreach (var property in entity.GetProperties())
                property.SetColumnName(Regex.Replace(property.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant());
    }
}
