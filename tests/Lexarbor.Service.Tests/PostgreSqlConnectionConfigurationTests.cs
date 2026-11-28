using Lexarbor.Database;
using Lexarbor.Host;
using Lexarbor.Learning.Database;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Lexarbor.Service.Tests;

/// <summary>
/// <c>ConnectionStrings:Default</c> is a required PostgreSQL connection
/// string. Every rejection is a fixed diagnostic that names the setting and
/// never the value, with no provider exception attached.
/// </summary>
public class PostgreSqlConnectionConfigurationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void MissingConfiguration_FailsWithTheRequiredDiagnostic(string? configured)
    {
        var error = Assert.Throws<InvalidOperationException>(() => PostgreSqlConnectionConfiguration.Build(configured));
        Assert.Equal(PostgreSqlConnectionConfiguration.MissingDiagnostic, error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("Data Source=data/vocabulary.db")]
    [InlineData("Data Source=/app/data/synthetic-sensitive-marker.db;Pooling=False")]
    [InlineData("DataSource=synthetic-sensitive-marker.db")]
    [InlineData("Filename=synthetic-sensitive-marker.db")]
    public void SqliteConnectionString_FailsWithTheUpgradeDiagnostic(string configured)
    {
        var error = Assert.Throws<InvalidOperationException>(() => PostgreSqlConnectionConfiguration.Build(configured));
        Assert.Equal(PostgreSqlConnectionConfiguration.SqliteDiagnostic, error.Message);
        Assert.DoesNotContain("synthetic-sensitive-marker", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("synthetic-sensitive-marker")]
    [InlineData("Host=db;Database=lexarbor;Port=synthetic-sensitive-marker")]
    [InlineData("Host=db;Database=lexarbor;Unknown Keyword=synthetic-sensitive-marker")]
    [InlineData("Host=db;Password=synthetic-sensitive-marker")]
    [InlineData("Database=lexarbor;Password=synthetic-sensitive-marker")]
    [InlineData("Host=db;Database=lexarbor;Password='synthetic-sensitive-marker")]
    public void InvalidConfiguration_FailsWithoutEchoingInputOrProviderDetails(string configured)
    {
        var error = Assert.Throws<InvalidOperationException>(() => PostgreSqlConnectionConfiguration.Build(configured));
        Assert.Equal(PostgreSqlConnectionConfiguration.InvalidDiagnostic, error.Message);
        Assert.DoesNotContain("synthetic-sensitive-marker", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void ValidConfiguration_KeepsEveryParameter_AndAddsTheBoundedLockWaitAndJitOff()
    {
        var result = new NpgsqlConnectionStringBuilder(PostgreSqlConnectionConfiguration.Build(
            "Host=db;Port=6543;Database=lexarbor;Username=app;Password=secret;Maximum Pool Size=7"));

        Assert.Equal(("db", 6543, "lexarbor", "app", "secret", 7), (result.Host, result.Port, result.Database, result.Username, result.Password, result.MaxPoolSize));
        Assert.Equal("-c lock_timeout=5s -c jit=off", result.Options);
    }

    [Theory]
    [InlineData("-c lock_timeout=30s", "-c lock_timeout=30s -c jit=off")]
    [InlineData("-c statement_timeout=60s", "-c statement_timeout=60s -c lock_timeout=5s -c jit=off")]
    [InlineData("-c jit=on", "-c jit=on -c lock_timeout=5s")]
    [InlineData("-c jit_above_cost=1000000", "-c jit_above_cost=1000000 -c lock_timeout=5s")]
    [InlineData("-c lock_timeout=1s -c jit=on", "-c lock_timeout=1s -c jit=on")]
    public void ExistingOptions_ArePreserved_AndExplicitLockTimeoutAndJitWin(string options, string expected)
    {
        var result = new NpgsqlConnectionStringBuilder(PostgreSqlConnectionConfiguration.Build(
            new NpgsqlConnectionStringBuilder { Host = "db", Database = "lexarbor", Options = options }.ConnectionString));
        Assert.Equal(expected, result.Options);
    }

    [Fact]
    public async Task HostConnections_RunWithJitOff()
    {
        await using var factory = new VocabularyWebApplicationFactory(
            "Testing", includeAppCredentials: true, useProductionDatabase: true);
        _ = factory.CreateClient();
        var token = TestContext.Current.CancellationToken;

        await using var catalog = await factory.Services.GetRequiredService<IDbContextFactory<VocabularyDbContext>>().CreateDbContextAsync(token);
        Assert.Equal("off", await Jit(catalog.Database.GetDbConnection(), token));
        await using var learner = new LearningDbContext(factory.Services.GetRequiredService<LearningStorage>().Options("caller"));
        Assert.Equal("off", await Jit(learner.Database.GetDbConnection(), token));
    }

    private static async Task<string?> Jit(System.Data.Common.DbConnection connection, CancellationToken token)
    {
        await connection.OpenAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SHOW jit";
        return (string?)await command.ExecuteScalarAsync(token);
    }

    [Fact]
    public void HostStartup_WithASqliteConnectionString_FailsWithTheUpgradeDiagnostic()
    {
        using var factory = new VocabularyWebApplicationFactory(
            "Testing",
            includeAppCredentials: true,
            extraConfiguration: new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "Data Source=/app/data/synthetic-sensitive-marker.db"
            },
            useProductionDatabase: true);
        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Equal(PostgreSqlConnectionConfiguration.SqliteDiagnostic, error.Message);
        Assert.DoesNotContain("synthetic-sensitive-marker", error.ToString(), StringComparison.Ordinal);
    }
}
