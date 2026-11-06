using System.Text.RegularExpressions;
using Lexarbor.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database;

internal static class AiRequestBatchModelConfiguration
{
    internal static void Configure(ModelBuilder model)
    {
        var batch = model.Entity<AiRequestBatchEntity>();
        batch.ToTable("ai_request_batch"); batch.HasKey(r => r.Id);
        batch.HasIndex(r => new { r.Kind, r.ConfigurationVersion, r.FinishedAtUnixMs });
        batch.HasIndex(r => r.Outcome);
        var member = model.Entity<AiRequestBatchMemberEntity>();
        member.ToTable("ai_request_batch_member"); member.HasKey(r => r.Id);
        member.HasIndex(r => r.TargetId);
        member.HasOne<AiRequestBatchEntity>().WithMany().HasForeignKey(r => r.BatchId).OnDelete(DeleteBehavior.Cascade);
        var lease = model.Entity<EnrichmentServiceLeaseEntity>();
        lease.ToTable("enrichment_service_lease"); lease.HasKey(r => r.Id);
        lease.HasIndex(r => new { r.ServiceId, r.ConfigurationVersion, r.ExpiresAtUnixMs });
        lease.HasOne<EnrichmentServiceEntity>().WithMany().HasForeignKey(r => r.ServiceId).OnDelete(DeleteBehavior.Cascade);
        foreach (var entity in new[] { batch.Metadata, member.Metadata, lease.Metadata })
            foreach (var property in entity.GetProperties())
                property.SetColumnName(Regex.Replace(property.Name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant());
    }
}
