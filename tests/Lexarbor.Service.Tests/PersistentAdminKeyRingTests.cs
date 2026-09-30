using System.Security.Cryptography;
using Lexarbor.Host;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lexarbor.Service.Tests;

public sealed class PersistentAdminKeyRingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lexarbor-keys-{Guid.NewGuid():N}");
    private string RingPath => Path.Combine(_root, "data", "admin-keys");
    private const string Payload = "synthetic-test-payload";

    private ServiceProvider Build(string? root = null, string? application = null)
    {
        var services = new ServiceCollection();
        PersistentAdminKeyRing.Register(services);
        services.RemoveAll<PersistentAdminKeyRing>();
        services.AddSingleton(new PersistentAdminKeyRing(root ?? _root));
        if (application is not null)
        {
            services.AddDataProtection().SetApplicationName(application);
        }

        return services.BuildServiceProvider();
    }

    private static IDataProtector Protector(IServiceProvider services, string? purpose = null) =>
        services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(purpose ?? PersistentAdminKeyRing.AdminSessionPurpose);

    [Fact]
    public void IndependentProviders_PreserveKeysAndIsolateApplicationAndPurpose()
    {
        using var first = Build();
        PersistentAdminKeyRing.Validate(first);
        var protectedPayload = Protector(first).Protect(Payload);
        var originalFiles = Directory.GetFiles(RingPath, "*.xml");
        using var second = Build();
        PersistentAdminKeyRing.Validate(second);
        Assert.True(Protector(second).Unprotect(protectedPayload) == Payload);
        Assert.Equal(originalFiles, Directory.GetFiles(RingPath, "*.xml"));
        Assert.Throws<CryptographicException>(() => Protector(second, "another-purpose").Unprotect(protectedPayload));
        using var otherApplication = Build(application: "another-application");
        Assert.Throws<CryptographicException>(() => Protector(otherApplication).Unprotect(protectedPayload));
        var corrupted = WebEncoders.Base64UrlDecode(protectedPayload);
        corrupted[^1] ^= 1;
        Assert.Throws<CryptographicException>(() => Protector(second).Unprotect(
            WebEncoders.Base64UrlEncode(corrupted)));
        using var missing = Build(Path.Combine(_root, "missing"));
        PersistentAdminKeyRing.Validate(missing);
        Assert.Throws<CryptographicException>(() => Protector(missing).Unprotect(protectedPayload));
    }

    [Fact]
    public void Rotation_RetainsOldKeysAndCiphertext()
    {
        using var first = Build();
        PersistentAdminKeyRing.Validate(first);
        var oldPayload = Protector(first).Protect(Payload);
        var manager = first.GetRequiredService<IKeyManager>();
        var originalKeys = manager.GetAllKeys().Select(key => key.KeyId).ToArray();
        var newKey = manager.CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));
        using var second = Build();
        PersistentAdminKeyRing.Validate(second);
        Assert.True(Protector(second).Unprotect(oldPayload) == Payload);
        var rotatedPayload = Protector(second).Protect(Payload);
        Assert.True(Protector(first).Unprotect(rotatedPayload) == Payload);
        Assert.Equal(newKey.KeyId, new Guid(WebEncoders.Base64UrlDecode(rotatedPayload).AsSpan(4, 16)));
        Assert.All(originalKeys, id => Assert.Contains(second.GetRequiredService<IKeyManager>().GetAllKeys(), key => key.KeyId == id));
        Assert.Contains(second.GetRequiredService<IKeyManager>().GetAllKeys(), key => key.KeyId == newKey.KeyId);
        AssertPrivateModes();
    }

    [Fact]
    public async Task ConcurrentInitialization_TwoProvidersSharePersistentKeys()
    {
        using var first = Build();
        using var second = Build();
        await Task.WhenAll(Task.Run(() => PersistentAdminKeyRing.Validate(first), TestContext.Current.CancellationToken),
            Task.Run(() => PersistentAdminKeyRing.Validate(second), TestContext.Current.CancellationToken));
        var firstPayload = Protector(first).Protect(Payload);
        var secondPayload = Protector(second).Protect(Payload);
        // Fresh providers force a disk read, without relying on either initial cache.
        using var restarted = Build();
        PersistentAdminKeyRing.Validate(restarted);
        Assert.True(Protector(restarted).Unprotect(firstPayload) == Payload);
        Assert.True(Protector(restarted).Unprotect(secondPayload) == Payload);
        AssertPrivateModes();
    }

    [Fact]
    public void Startup_CreatesAndRestrictsOnlyItsOwnRing()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        var sibling = Path.Combine(_root, "data", "unrelated.txt");
        File.WriteAllText(sibling, "unrelated");
        UnixFileMode? siblingMode = OperatingSystem.IsWindows() ? null : File.GetUnixFileMode(sibling);
        using var first = Build();
        PersistentAdminKeyRing.Validate(first);
        AssertPrivateModes();
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(RingPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead);
            foreach (var path in Directory.GetFiles(RingPath, "*.xml"))
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
            }

            using var second = Build();
            PersistentAdminKeyRing.Validate(second);
            AssertPrivateModes();
            Assert.Equal(siblingMode, File.GetUnixFileMode(sibling));
        }
    }

    [Theory]
    [InlineData("<key>synthetic-secret-marker")]
    [InlineData("<key id='invalid'>synthetic-secret-marker</key>")]
    public void Startup_CorruptKeyFailsWithSafeDiagnostic(string contents)
    {
        Directory.CreateDirectory(RingPath);
        File.WriteAllText(Path.Combine(RingPath, "key-invalid.xml"), contents);
        using var services = Build();
        var exception = Assert.Throws<InvalidOperationException>(() => PersistentAdminKeyRing.Validate(services));
        Assert.Equal(PersistentAdminKeyRing.StartupFailureMessage, exception.Message);
        Assert.Null(exception.InnerException);
        Assert.Single(Directory.GetFiles(RingPath, "*.xml"));
    }

    [Fact]
    public void Startup_CorruptRetainedDescriptorCannotBeHiddenByValidKey()
    {
        using var first = Build();
        PersistentAdminKeyRing.Validate(first);
        var invalid = $"<key id='{Guid.NewGuid()}' version='1'><creationDate>2000-01-01T00:00:00Z</creationDate><activationDate>2000-01-01T00:00:00Z</activationDate><expirationDate>2000-01-02T00:00:00Z</expirationDate><descriptor deserializerType='invalid'>synthetic-secret-marker</descriptor></key>";
        File.WriteAllText(Path.Combine(RingPath, "key-expired-invalid.xml"), invalid);
        using var second = Build();
        var exception = Assert.Throws<InvalidOperationException>(() => PersistentAdminKeyRing.Validate(second));
        Assert.Equal(PersistentAdminKeyRing.StartupFailureMessage, exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void Startup_UnavailableDirectoryDoesNotFallBackToMemory()
    {
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        File.WriteAllText(RingPath, "not-a-directory");
        using var services = Build();
        var exception = Assert.Throws<InvalidOperationException>(() => PersistentAdminKeyRing.Validate(services));
        Assert.Equal(PersistentAdminKeyRing.StartupFailureMessage, exception.Message);
        Assert.False(Directory.Exists(RingPath));
    }

    [Fact]
    public void Startup_SymbolicLinkRingIsRejected()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Windows symlink creation requires privileges; Unix is the deployment target.
        }

        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        Directory.CreateSymbolicLink(RingPath, target);
        using var services = Build();
        var exception = Assert.Throws<InvalidOperationException>(() => PersistentAdminKeyRing.Validate(services));
        Assert.Equal(PersistentAdminKeyRing.StartupFailureMessage, exception.Message);
        Assert.Empty(Directory.GetFiles(target));
    }

    [Fact]
    public async Task IndependentHosts_CanUseTheSameRingAcrossRestarts()
    {
        string protectedPayload;
        using (var first = new VocabularyWebApplicationFactory("Testing", true,
                   keyContentRoot: _root))
        {
            using var client = first.CreateClient();
            Assert.True((await client.GetAsync("/health", TestContext.Current.CancellationToken)).IsSuccessStatusCode);
            protectedPayload = Protector(first.Services).Protect(Payload);
        }

        using var second = new VocabularyWebApplicationFactory("Testing", true,
            keyContentRoot: _root);
        using var secondClient = second.CreateClient();
        Assert.True((await secondClient.GetAsync("/health", TestContext.Current.CancellationToken)).IsSuccessStatusCode);
        Assert.True(Protector(second.Services).Unprotect(protectedPayload) == Payload);
    }

    [Fact]
    public void HostStartup_InvalidKeyPreventsRequests()
    {
        Directory.CreateDirectory(RingPath);
        File.WriteAllText(Path.Combine(RingPath, "key-invalid.xml"), "<key>synthetic-secret-marker");
        using var host = new VocabularyWebApplicationFactory("Testing", true, keyContentRoot: _root);
        var exception = Assert.ThrowsAny<InvalidOperationException>(() => host.CreateClient());
        Assert.DoesNotContain("synthetic-secret-marker", exception.ToString());
    }

    private void AssertPrivateModes()
    {
        Assert.NotEmpty(Directory.GetFiles(RingPath, "*.xml"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(RingPath));
            foreach (var path in Directory.GetFiles(RingPath, "*.xml"))
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
