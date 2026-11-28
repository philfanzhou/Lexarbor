using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Lexarbor.Database;

/// <summary>
/// Lets <c>dotnet ef migrations add</c> build the model without starting the
/// host, which refuses to start without a configured database. Adding a
/// migration never connects, so the placeholder connection string is never
/// opened; commands that do connect take <c>--connection</c>.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<VocabularyDbContext>
{
    public VocabularyDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<VocabularyDbContext>()
            .UseNpgsql("Host=localhost;Database=lexarbor")
            .Options);
}
