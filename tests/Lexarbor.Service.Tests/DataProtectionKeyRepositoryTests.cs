using System.Net;
using System.Security.Cryptography;
using Lexarbor.Database;
using Lexarbor.Host;
using Lexarbor.Host.Authentication;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Lexarbor.Service.Tests;

/// <summary>
/// The ServiceMantle EF Core Data Protection key repository: root-key
/// resolution (injected value wins, otherwise an owner-only file created
/// atomically under data/), keys stored as authenticated envelopes in the
/// business database, fail-closed startup on a wrong root key, and the retired
/// plaintext data/admin-keys directory being irrelevant.
/// </summary>
public class DataProtectionKeyRepositoryTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lexarbor-rootkey-{Guid.NewGuid():N}");
    // Database files pass through the strict startup gate, so they live in the
    // gate-safe directory rather than beside the root-key material.
    private readonly string _databaseRoot = VocabularyWebApplicationFactory.CreateGateSafeDirectory(
        $"lexarbor-rootkey-db-{Guid.NewGuid():N}");

    public DataProtectionKeyRepositoryTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        if (Directory.Exists(_databaseRoot))
        {
            try
            {
                Directory.Delete(_databaseRoot, recursive: true);
            }
            catch (IOException)
            {
                // A WAL sidecar SQLite still holds goes with the next run's new
                // directory name.
            }
        }
    }

    private IConfiguration Configuration(Dictionary<string, string?> extra) =>
        new ConfigurationBuilder().AddInMemoryCollection(extra).Build();

    [Fact]
    public void WithoutInjectedKey_CreatesOwnerOnlyFileAndReusesIt()
    {
        var keyFile = Path.Combine(_root, "data", "data-protection-root-key");
        var first = DataProtectionRootKey.Resolve(
            Configuration(new Dictionary<string, string?>()),
            contentRootPath: _root);
        Assert.True(File.Exists(keyFile));
        Assert.Equal(Convert.FromBase64String(first), Convert.FromBase64String(File.ReadAllText(keyFile)));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(keyFile));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(keyFile)!));
        }

        var second = DataProtectionRootKey.Resolve(
            Configuration(new Dictionary<string, string?>()),
            contentRootPath: _root);
        Assert.Equal(first, second);
    }

    [Fact]
    public void InjectedKey_WinsOverAnyFile()
    {
        var keyFile = Path.Combine(_root, "data", "data-protection-root-key");
        var resolved = DataProtectionRootKey.Resolve(
            Configuration(new Dictionary<string, string?> { ["DataProtection:RootKey"] = "injected-root-key" }),
            contentRootPath: _root);

        Assert.Equal("injected-root-key", resolved);
        Assert.False(File.Exists(keyFile));
    }

    [Fact]
    public void CustomKeyFilePath_IsHonored()
    {
        var keyFile = Path.Combine(_root, "elsewhere", "root-key");
        var resolved = DataProtectionRootKey.Resolve(
            Configuration(new Dictionary<string, string?> { ["DataProtection:RootKeyFile"] = keyFile }),
            contentRootPath: _root);

        Assert.NotEmpty(resolved);
        Assert.True(File.Exists(keyFile));
    }

    [Fact]
    public void SymbolicLinkForTheKeyFile_IsRefused()
    {
        var target = Path.Combine(_root, "target-key");
        File.WriteAllText(target, "anything");
        var keyFile = Path.Combine(_root, "data", "data-protection-root-key");
        Directory.CreateDirectory(Path.GetDirectoryName(keyFile)!);
        File.CreateSymbolicLink(keyFile, target);

        var failure = Assert.Throws<InvalidOperationException>(() =>
            DataProtectionRootKey.Resolve(
                Configuration(new Dictionary<string, string?>()),
                contentRootPath: _root));
        Assert.Equal(DataProtectionRootKey.StartupFailureMessage, failure.Message);
    }

    [Fact]
    public void LooseOrEmptyKeyFile_IsRefused()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var keyFile = Path.Combine(_root, "data", "data-protection-root-key");
        Directory.CreateDirectory(Path.GetDirectoryName(keyFile)!);
        File.WriteAllText(keyFile, "group-readable-root-key");
        File.SetUnixFileMode(keyFile, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.UserWrite);

        Assert.Throws<InvalidOperationException>(() =>
            DataProtectionRootKey.Resolve(
                Configuration(new Dictionary<string, string?>()),
                contentRootPath: _root));

        var emptyFile = Path.Combine(_root, "data", "empty");
        Directory.CreateDirectory(Path.GetDirectoryName(emptyFile)!);
        File.WriteAllText(emptyFile, "   ");
        File.SetUnixFileMode(emptyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var failure = Assert.Throws<InvalidOperationException>(() =>
            DataProtectionRootKey.Resolve(
                Configuration(new Dictionary<string, string?> { ["DataProtection:RootKeyFile"] = emptyFile }),
                contentRootPath: _root));
        Assert.Equal(DataProtectionRootKey.StartupFailureMessage, failure.Message);
    }

    [Fact]
    public async Task RetiredAdminKeysDirectory_IsNeverRead()
    {
        // The upgrade leaves data/admin-keys behind; nothing may read it, and
        // its presence must not affect the repository. The root-key file is the
        // only input besides the database.
        Directory.CreateDirectory(Path.Combine(_root, "admin-keys"));
        File.WriteAllText(Path.Combine(_root, "admin-keys", "key-legacy.xml"), "<key>legacy</key>");

        var database = Path.Combine(_databaseRoot, "keys.db");
        string handle;
        using (var first = new VocabularyWebApplicationFactory("Testing", true,
                     keyContentRoot: _root, databasePath: database))
        {
            using var scope = first.Services.CreateScope();
            handle = await scope.ServiceProvider.GetRequiredService<AdminSessionStore>().CreateAsync(new()
            {
                AccessToken = "synthetic-access-marker",
                Issuer = VocabularyWebApplicationFactory.Issuer,
                Subject = "subject",
                DisplayName = "user",
                Roles = ["admin"],
                AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
            }, Ct);
        }

        using (var second = new VocabularyWebApplicationFactory("Testing", true,
                     keyContentRoot: _root, databasePath: database))
        using (var scope = second.Services.CreateScope())
        {
            var recovered = await scope.ServiceProvider.GetRequiredService<AdminSessionStore>().ReadAsync(handle, Ct);
            Assert.NotNull(recovered);
        }

        Assert.Equal("<key>legacy</key>", await File.ReadAllTextAsync(
            Path.Combine(_root, "admin-keys", "key-legacy.xml"), Ct));
    }

    [Fact]
    public async Task KeysAreStoredAsEnvelopeRows_WithoutPlaintextXml()
    {
        var database = Path.Combine(_databaseRoot, "enveloped.db");
        using var factory = new VocabularyWebApplicationFactory("Testing", true,
            keyContentRoot: _root, databasePath: database);
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AdminSessionStore>().CreateAsync(new()
        {
            AccessToken = "synthetic-access-marker",
            Issuer = VocabularyWebApplicationFactory.Issuer,
            Subject = "subject",
            DisplayName = "user",
            Roles = ["admin"],
            AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        }, Ct);

        var rows = await SqliteRowsAsync(database,
            "SELECT service_id, key_id, encrypted_xml FROM service_data_protection_keys");
        Assert.NotEmpty(rows);
        foreach (var (serviceId, encryptedXml) in rows)
        {
            Assert.Equal("lexarbor", serviceId);
            Assert.StartsWith("sm:v1:", encryptedXml, StringComparison.Ordinal);
            Assert.DoesNotContain("<key>", encryptedXml, StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic-access-marker", encryptedXml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task WrongRootKeyAgainstExistingRows_FailsClosedAtStartup()
    {
        var database = Path.Combine(_databaseRoot, "wrongkey.db");
        using (var created = new VocabularyWebApplicationFactory("Testing", true,
                     keyContentRoot: _root, databasePath: database))
        {
            using var client = created.CreateClient();
        }

        // A different root-key file against the same database rows must refuse
        // to boot rather than serve unreadable sessions.
        var otherRoot = Path.Combine(_root, "other");
        Directory.CreateDirectory(otherRoot);
        var failure = Assert.ThrowsAny<Exception>(() =>
        {
            using var mismatched = new VocabularyWebApplicationFactory("Testing", true,
                keyContentRoot: otherRoot, databasePath: database);
            using var client = mismatched.CreateClient();
            return client;
        });
        Assert.NotNull(failure);
    }

    private static async Task<List<(string ServiceId, string EncryptedXml)>> SqliteRowsAsync(
        string database, string sql)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={database};Mode=ReadOnly");
        await connection.OpenAsync(Ct);
        var results = new List<(string, string)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                results.Add((reader.GetString(0), reader.GetString(2)));
            }
        }

        return results;
    }
}
