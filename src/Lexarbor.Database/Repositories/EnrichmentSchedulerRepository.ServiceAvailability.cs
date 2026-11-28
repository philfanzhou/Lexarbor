using Lexarbor.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Database.Repositories;

public sealed partial class EnrichmentSchedulerRepository
{
    // One read statement observes the scalar legacy lease and lease rows together.
    // The atomic admission transaction still makes the final ownership decision.
    public async Task<EnrichmentServiceAvailability> AvailabilityAsync(string serviceId, string configurationVersion,
        int capacity, long now, CancellationToken token)
    {
        if (capacity is < 1 or > 8 || now < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        var status = await context.EnrichmentServices.AsNoTracking().Where(s => s.Id == serviceId).Select(s => new
        {
            s.ConfigurationVersion,
            s.Paused,
            s.Blocked,
            s.NextAttemptAtUnixMs,
            s.LeaseExpiresAtUnixMs,
            Active = context.EnrichmentServiceLeases.Count(l => l.ServiceId == serviceId && l.ExpiresAtUnixMs > now),
            Earliest = context.EnrichmentServiceLeases.Where(l => l.ServiceId == serviceId && l.ExpiresAtUnixMs > now)
                .Select(l => (long?)l.ExpiresAtUnixMs).Min(),
            HasPointer = context.EnrichmentServiceLeases.Any(l => l.ServiceId == serviceId && l.Id == s.LeaseId && l.ExpiresAtUnixMs > now)
        }).SingleOrDefaultAsync(token);
        if (status == null || status.ConfigurationVersion != configurationVersion) return new(capacity, 0);
        if (status.Paused || status.Blocked) return new(0, Math.Max(now + 30_000, status.NextAttemptAtUnixMs));
        if (status.NextAttemptAtUnixMs > now) return new(0, status.NextAttemptAtUnixMs);
        var legacy = status.LeaseExpiresAtUnixMs > now && !status.HasPointer;
        var slots = Math.Max(0, capacity - status.Active - (legacy ? 1 : 0));
        var earliest = Math.Min(status.Earliest ?? long.MaxValue, legacy ? status.LeaseExpiresAtUnixMs!.Value : long.MaxValue);
        // Observe another owner releasing early without polling/writing every target.
        return new(slots, slots > 0 ? 0 : Math.Min(now + 5_000, earliest));
    }
}
