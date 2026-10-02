using Lexarbor.Database;
using Lexarbor.Host;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.Sqlite;

namespace Lexarbor.Service.Tests;

[Collection("Sqlite configuration diagnostics")]
public class SqliteConnectionConfigurationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void MissingConfiguration_UsesProductDefaultsWithoutCreatingFiles(string? configured)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "uncreated", Guid.NewGuid().ToString("N"));
        var result = new SqliteConnectionStringBuilder(SqliteConnectionConfiguration.Build(configured, root));
        Assert.Equal(Path.Combine(root, "data", "vocabulary.db"), result.DataSource);
        Assert.Equal(5, result.DefaultTimeout);
        Assert.False(Directory.Exists(root));
        Assert.False(File.Exists(result.DataSource));
    }

    [Theory]
    [InlineData(":memory:")]
    [InlineData(":MEMORY:")]
    [InlineData("file:synthetic-uri-marker.db")]
    [InlineData("FILE:synthetic-uri-marker.db?mode=memory")]
    public void PureResolution_PreservesSpecialSourcesWithoutCreatingFiles(string source)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "uncreated", Guid.NewGuid().ToString("N"));
        var result = new SqliteConnectionStringBuilder(SqliteConnectionConfiguration.Build($"Data Source={source};Default Timeout=7", root));
        Assert.Equal(source, result.DataSource);
        Assert.Equal(7, result.DefaultTimeout);
        Assert.False(Directory.Exists(root));
        Assert.False(File.Exists(Path.Combine(root, source)));
    }

    [Theory]
    [InlineData("Data Source=")]
    [InlineData("Data Source=' '")]
    [InlineData("Cache=Private")]
    public void EmptySource_KeepsSafeProductDiagnostic(string configured)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            SqliteConnectionConfiguration.Build(configured, AppContext.BaseDirectory));
        Assert.Equal("ConnectionStrings:Default must define a SQLite data source.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("synthetic-sensitive-marker=value")]
    [InlineData("Data Source=|DataDirectory|/synthetic-sensitive-marker.db")]
    [InlineData("Data Source=synthetic-sensitive-marker.db;Mode=invalid")]
    [InlineData("Data Source=synthetic-sensitive-marker.db;Default Timeout=invalid")]
    [InlineData("Data Source='synthetic-sensitive-marker\0.db'")]
    public void InvalidConfiguration_FailsWithoutEchoingInputOrProviderDetails(string configured)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            SqliteConnectionConfiguration.Build(configured, AppContext.BaseDirectory));
        Assert.DoesNotContain("synthetic-sensitive-marker", error.ToString());
        Assert.Null(error.InnerException);
        Assert.Contains("ConnectionStrings:Default", error.Message);
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData(30, 5)]
    [InlineData(7, 7)]
    [InlineData(0, 0)]
    public void TimeoutAndOtherParameters_PreserveProductSemantics(int? timeout, int expected)
    {
        var configured = "Data Source=data/catalog.db;Mode=ReadWriteCreate;Cache=Private;Pooling=False;Foreign Keys=True;Recursive Triggers=True";
        if (timeout is not null) configured += $";Default Timeout={timeout}";
        var result = new SqliteConnectionStringBuilder(SqliteConnectionConfiguration.Build(configured, AppContext.BaseDirectory));
        Assert.Equal(expected, result.DefaultTimeout);
        Assert.Equal(SqliteOpenMode.ReadWriteCreate, result.Mode);
        Assert.Equal(SqliteCacheMode.Private, result.Cache);
        Assert.False(result.Pooling);
        Assert.True(result.ForeignKeys);
        Assert.True(result.RecursiveTriggers);
    }

    [Fact]
    public void RelativeAndAbsolutePaths_UseExplicitRootAndNormalizeDotSegments()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "uncreated", Guid.NewGuid().ToString("N"));
        var relative = "nested/../data/catalog.db";
        var expected = Path.Combine(root, "data", "catalog.db");
        Assert.NotEqual(expected, Path.GetFullPath(relative));
        var result = new SqliteConnectionStringBuilder(SqliteConnectionConfiguration.Build($"Data Source={relative}", root));
        Assert.Equal(expected, result.DataSource);
        Assert.Equal(expected, new SqliteConnectionStringBuilder(SqliteConnectionConfiguration.Build($"Data Source={expected}", root)).DataSource);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(expected, new SqliteConnectionStringBuilder(SqliteConnectionConfiguration.Build("Data Source=nested\\..\\data\\catalog.db", root)).DataSource);
        }
        Assert.False(Directory.Exists(root));
        Assert.False(File.Exists(expected));
    }

    [Fact]
    public async Task RelativeHostConfiguration_ReachesProductionDbContextAndStartupTargetUnchanged()
    {
        var root = VocabularyWebApplicationFactory.CreateGateSafeDirectory($"resolution-{Guid.NewGuid():N}");
        var expected = Path.Combine(root, "data", "catalog.db");
        const string configured = "Data Source=nested/../data/catalog.db;Default Timeout=7;Pooling=False;Cache=Private";
        try
        {
            var constructed = SqliteConnectionConfiguration.Build(configured, root);
            Assert.False(Directory.Exists(Path.Combine(root, "data")));
            Assert.False(File.Exists(expected));
            await using var factory = new VocabularyWebApplicationFactory("Testing", true,
                extraConfiguration: new Dictionary<string, string?> { ["ConnectionStrings:Default"] = configured },
                keyContentRoot: Path.Combine(root, "keys"), databasePath: expected,
                useProductionDatabase: true, contentRootPath: root);
            using var client = factory.CreateClient();
            Assert.Equal(root, factory.Services.GetRequiredService<IHostEnvironment>().ContentRootPath);
            await using var scope = factory.Services.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            Assert.Equal(constructed, context.Database.GetConnectionString());
            Assert.True(File.Exists(expected));
            Assert.Equal(context.Database.GetMigrations().Order(), (await context.Database.GetAppliedMigrationsAsync(Ct)).Order());
            var provider = factory.Services.GetRequiredService<SqliteDatabaseTargetPreparationProvider>();
            var actualIdentity = await provider.GetCanonicalTargetIdentityAsync(
                new BootstrapDatabaseConfiguration(WellKnownDatabaseProviderIds.Sqlite, null, constructed), Ct);
            var expectedIdentity = await provider.GetCanonicalTargetIdentityAsync(
                new BootstrapDatabaseConfiguration(WellKnownDatabaseProviderIds.Sqlite, null, $"Data Source={expected};Pooling=False"), Ct);
            Assert.Equal(expectedIdentity, actualIdentity);
            using var ready = await client.GetAsync("/health/ready", Ct);
            Assert.Equal(System.Net.HttpStatusCode.OK, ready.StatusCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(":memory:")]
    [InlineData(":MEMORY:")]
    [InlineData("file:synthetic-uri-marker.db")]
    [InlineData("FILE:synthetic-uri-marker.db")]
    [InlineData("|DataDirectory|/synthetic-uri-marker.db")]
    public void ActualStartup_RejectsUnsupportedSourcesWithoutCreatingLiteralUriFile(string source)
    {
        var root = VocabularyWebApplicationFactory.CreateGateSafeDirectory($"rejected-resolution-{Guid.NewGuid():N}");
        var originalOutput = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            using var factory = new VocabularyWebApplicationFactory("Testing", true,
                extraConfiguration: new Dictionary<string, string?> { ["ConnectionStrings:Default"] = $"Data Source={source};Pooling=False" },
                keyContentRoot: Path.Combine(root, "keys"), databasePath: Path.Combine(root, "unused.db"),
                useProductionDatabase: true, contentRootPath: root);
            var error = Assert.ThrowsAny<InvalidOperationException>(() => factory.CreateClient());
            Assert.DoesNotContain(source, error.ToString());
            Assert.DoesNotContain("synthetic-uri-marker", output.ToString());
            Assert.False(File.Exists(Path.Combine(root, source)));
            if (!OperatingSystem.IsWindows()) Assert.False(File.Exists(Path.GetFullPath(source)));
            Assert.Empty(Directory.GetFiles(root, "*.db", SearchOption.AllDirectories));
        }
        finally
        {
            Console.SetOut(originalOutput);
            Directory.Delete(root, recursive: true);
        }
    }
}

[CollectionDefinition("Sqlite configuration diagnostics", DisableParallelization = true)]
public class SqliteConfigurationDiagnosticCollection
{
}
