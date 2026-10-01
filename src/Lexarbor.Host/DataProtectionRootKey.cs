using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;

namespace Lexarbor.Host;

/// <summary>
/// The root key protecting the ServiceMantle Data Protection key repository.
/// A deployment injects <c>DataProtection:RootKey</c> (container variable
/// <c>LEXARBOR_DATA_PROTECTION_ROOT_KEY</c>); without an injected value the
/// first start atomically creates a random root-key file under <c>data/</c>
/// (directory 0700, file 0600, symbolic links refused) and reuses it, so a
/// single-container deployment stays zero-configuration.
/// </summary>
public static class DataProtectionRootKey
{
    public const string InjectedKeySetting = "DataProtection:RootKey";
    public const string KeyFileSetting = "DataProtection:RootKeyFile";
    public const string DefaultKeyFileName = "data-protection-root-key";
    public const string StartupFailureMessage =
        "The Data Protection key repository is unavailable or invalid. Check DataProtection:RootKey " +
        "(or the root-key file under data/) and the database; the key rows and the root key must come " +
        "from the same deployment. Losing the root key only invalidates administrator sessions.";

    /// <summary>
    /// Resolved lazily on first use so every configuration source — including
    /// test-host overrides attached during host construction — is visible.
    /// </summary>
    public static string Resolve(IConfiguration configuration, string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        var injected = configuration[InjectedKeySetting];
        if (!string.IsNullOrWhiteSpace(injected))
        {
            return injected;
        }

        var configuredPath = configuration[KeyFileSetting];
        var filePath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(contentRootPath, "data", DefaultKeyFileName)
            : configuredPath;
        return ReadOrCreateFile(filePath);
    }

    private static string ReadOrCreateFile(string filePath)
    {
        if (File.Exists(filePath))
        {
            RefuseSymbolicLink(filePath);
            return ReadFileWithChecks(filePath);
        }

        var directory = Path.GetDirectoryName(filePath)
            ?? throw new InvalidOperationException(
                "The Data Protection root-key file path has no directory.");
        if (Directory.Exists(directory))
        {
            RefuseSymbolicLink(directory, isDirectory: true);
        }
        else
        {
            if (OperatingSystem.IsWindows())
            {
                Directory.CreateDirectory(directory);
            }
            else
            {
                Directory.CreateDirectory(
                    directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        // Random, printable, single-line: 32 bytes of entropy as base64.
        var rootKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var temporaryPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, rootKey);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    temporaryPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            RefuseSymbolicLink(temporaryPath);
            File.Move(temporaryPath, filePath, overwrite: false);
            return rootKey;
        }
        catch (IOException) when (File.Exists(filePath))
        {
            // Another process initialized the shared directory first; the
            // surviving file is the one to use.
            RefuseSymbolicLink(filePath);
            return ReadFileWithChecks(filePath);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static void RefuseSymbolicLink(string path, bool isDirectory = false)
    {
        var linkTarget = isDirectory
            ? new DirectoryInfo(path).LinkTarget
            : new FileInfo(path).LinkTarget;
        if (linkTarget is not null)
        {
            throw new InvalidOperationException(StartupFailureMessage);
        }
    }

    private static string ReadFileWithChecks(string filePath)
    {
        var content = File.ReadAllText(filePath).Trim();
        if (content.Length == 0)
        {
            throw new InvalidOperationException(StartupFailureMessage);
        }

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(filePath);
            if (mode.HasFlag(UnixFileMode.GroupRead) || mode.HasFlag(UnixFileMode.OtherRead)
                || mode.HasFlag(UnixFileMode.GroupWrite) || mode.HasFlag(UnixFileMode.OtherWrite))
            {
                throw new InvalidOperationException(StartupFailureMessage);
            }
        }

        return content;
    }
}
