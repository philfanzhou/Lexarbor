using Lexarbor.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database;

internal static class VocabularyRelationModelConfiguration
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<VocabularyRelationEntity>();
        entity.Property(r => r.Status).HasDefaultValue("needs_review");
        // Explicit null means confirmed. Do not let EF substitute the database default.
        entity.Property(r => r.ReviewReason).HasDefaultValue("new_relation").ValueGeneratedNever();
        entity.Property(r => r.Version).HasDefaultValue(1L).IsConcurrencyToken();
        entity.HasOne<VocabularyEntity>().WithMany().HasForeignKey(r => r.SourceWordId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_vocabulary_relation_source");
        entity.HasOne<VocabularyEntity>().WithMany().HasForeignKey(r => r.TargetWordId)
            .OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_vocabulary_relation_target");
        entity.HasIndex(r => new { r.SourceWordId, r.TargetWordId, r.Type })
            .IsUnique().HasDatabaseName("UX_vocabulary_relation_identity");
        entity.HasIndex(r => new { r.SourceWordId, r.Type, r.Status, r.Id })
            .HasDatabaseName("IX_vocabulary_relation_source_type_status");
        entity.HasIndex(r => new { r.TargetWordId, r.Type, r.Status, r.Id })
            .HasDatabaseName("IX_vocabulary_relation_target_type_status");
        entity.HasIndex(r => new { r.UpdatedAtUnixMs, r.Id }).HasFilter("status = 'needs_review'")
            .HasDatabaseName("IX_vocabulary_relation_needs_review");
        entity.ToTable(table =>
        {
            table.HasCheckConstraint("CK_vocabulary_relation_id", "length(id) BETWEEN 1 AND 64 AND id = trim(id)");
            table.HasCheckConstraint("CK_vocabulary_relation_type", "type IN ('past_tense', 'past_participle', 'ing_form', 'third_person_singular', 'plural', 'comparative', 'superlative', 'derived_noun', 'derived_verb', 'derived_adjective', 'derived_adverb')");
            table.HasCheckConstraint("CK_vocabulary_relation_usage_note", "usage_note IS NULL OR (length(usage_note) BETWEEN 1 AND 2000 AND usage_note = trim(usage_note))");
            table.HasCheckConstraint("CK_vocabulary_relation_status", "status IN ('confirmed', 'needs_review')");
            table.HasCheckConstraint("CK_vocabulary_relation_review_reason", "review_reason IS NULL OR review_reason IN ('new_relation', 'word_changed', 'relation_changed')");
            table.HasCheckConstraint("CK_vocabulary_relation_confirmation", "(status = 'confirmed' AND review_reason IS NULL AND last_confirmed_at_unix_ms IS NOT NULL) OR (status = 'needs_review' AND review_reason IS NOT NULL)");
            table.HasCheckConstraint("CK_vocabulary_relation_origin", "origin_kind IN ('manual', 'import', 'generated')");
            table.HasCheckConstraint("CK_vocabulary_relation_origin_reference", "origin_reference IS NULL OR (length(origin_reference) BETWEEN 1 AND 200 AND origin_reference = trim(origin_reference))");
            table.HasCheckConstraint("CK_vocabulary_relation_version", "typeof(version) = 'integer' AND version >= 1");
            table.HasCheckConstraint("CK_vocabulary_relation_times", "typeof(created_at_unix_ms) = 'integer' AND created_at_unix_ms >= 0 AND typeof(updated_at_unix_ms) = 'integer' AND updated_at_unix_ms >= created_at_unix_ms AND (last_confirmed_at_unix_ms IS NULL OR (typeof(last_confirmed_at_unix_ms) = 'integer' AND last_confirmed_at_unix_ms >= created_at_unix_ms AND last_confirmed_at_unix_ms <= updated_at_unix_ms))");
        });
    }
}
