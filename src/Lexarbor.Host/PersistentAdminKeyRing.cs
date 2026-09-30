using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lexarbor.Host;

/// <summary>Single-instance keys on a trusted local filesystem, owned by the runtime user.</summary>
public sealed class PersistentAdminKeyRing(string contentRootPath)
{
    public const string ApplicationName = "Lexarbor";
    public const string AdminSessionPurpose = "Lexarbor.AdminSession.v1";
    public const string StartupFailureMessage =
        "Administrator key storage is unavailable or invalid. Check data/admin-keys, its ownership and permissions, and restore a complete valid key ring if necessary.";

    public string DirectoryPath { get; } = Path.Combine(contentRootPath, "data", "admin-keys");

    private const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static void Register(IServiceCollection services)
    {
        services.AddSingleton(provider => new PersistentAdminKeyRing(
            provider.GetRequiredService<IHostEnvironment>().ContentRootPath));
        services.AddDataProtection().SetApplicationName(ApplicationName);
        services.AddOptions<KeyManagementOptions>()
            .Configure<PersistentAdminKeyRing>((options, ring) =>
            {
                options.XmlRepository = new FileSystemXmlRepository(
                    new DirectoryInfo(ring.DirectoryPath), NullLoggerFactory.Instance);
            });
        // Framework error diagnostics can include the XML element being processed.
        // Emit only our safe startup diagnostic; never send key XML to host logs.
        services.AddLogging(logging =>
            logging.AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.None));
    }

    public static void Validate(IServiceProvider services)
    {
        try
        {
            var ring = services.GetRequiredService<PersistentAdminKeyRing>();
            ring.PrepareDirectory();
            // Check all retained keys, including expired keys, before the provider can
            // select a usable one and hide a corrupt historical descriptor.
            var repository = services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository!;
            var elements = repository.GetAllElements();
            var keys = services.GetRequiredService<IKeyManager>().GetAllKeys();
            var persistedKeyIds = new HashSet<Guid>();
            foreach (var element in elements)
            {
                if (element.Name == "key")
                {
                    if (!Guid.TryParse((string?)element.Attribute("id"), out var id)
                        || !persistedKeyIds.Add(id)
                        || !keys.Any(key => key.KeyId == id))
                    {
                        throw new CryptographicException();
                    }
                }
                else if (element.Name != "revocation")
                {
                    throw new CryptographicException();
                }
            }

            foreach (var key in keys)
            {
                if (key.CreateEncryptor() is null)
                {
                    throw new CryptographicException();
                }
            }

            var protector = services.GetRequiredService<IDataProtectionProvider>()
                .CreateProtector("Lexarbor.AdminKeys.StartupProbe.v1");
            const string probe = "lexarbor-key-storage-probe";
            if (protector.Unprotect(protector.Protect(probe)) != probe)
            {
                throw new CryptographicException();
            }

            ring.RestrictFiles();
        }
        catch (Exception)
        {
            // No inner exception: parsing failures can contain key material.
            throw new InvalidOperationException(StartupFailureMessage);
        }
    }

    private void PrepareDirectory()
    {
        if (new DirectoryInfo(DirectoryPath).LinkTarget is not null)
        {
            throw new IOException();
        }

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(DirectoryPath);
        }
        else
        {
            Directory.CreateDirectory(DirectoryPath, DirectoryMode);
            File.SetUnixFileMode(DirectoryPath, DirectoryMode);
        }

        RestrictFiles();
        // An existing usable key needs no write. Prove persistence is writable
        // even on that startup, without writing any sensitive material.
        var probePath = Path.Combine(DirectoryPath, $".probe-{Guid.NewGuid():N}");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = System.IO.FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = FileMode;
            }

            using var stream = new FileStream(probePath, options);
            stream.WriteByte(1);
            stream.Flush(flushToDisk: true);
            stream.Position = 0;
            if (stream.ReadByte() != 1)
            {
                throw new IOException();
            }
        }
        finally
        {
            File.Delete(probePath);
        }
    }

    private void RestrictFiles()
    {
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.xml", SearchOption.TopDirectoryOnly))
        {
            if (new FileInfo(path).LinkTarget is not null)
            {
                throw new IOException();
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, FileMode);
            }
        }
    }
}
