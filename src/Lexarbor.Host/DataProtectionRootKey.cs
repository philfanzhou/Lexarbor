using Microsoft.Extensions.Configuration;
using ServiceMantle.Configuration;

namespace Lexarbor.Host;

/// <summary>
/// The root key protecting the ServiceMantle Data Protection key repository.
/// A deployment injects <c>DataProtection:RootKey</c> (container variable
/// <c>LEXARBOR_DATA_PROTECTION_ROOT_KEY</c>); without an injected value the
/// first start atomically creates a random root-key file under <c>data/</c>
/// and reuses it, so a single-container deployment stays zero-configuration.
/// The reading, creation, and validation are the shared ServiceMantle
/// <see cref="RootKeySource"/> semantics; this type keeps only the two
/// configuration keys and the content-root default path.
/// </summary>
/// <remarks>
/// <para>
/// The shared source is stricter than the retired in-house implementation in
/// three ways, all of them fail-closed: the key file's parent directory must
/// carry exactly the owner-only mode bits on Unix (the deployment's
/// <c>data/</c> directory is created 0700 for exactly this reason), every
/// ancestor of the file must be a real directory — a path resolving through a
/// symbolic link is refused — and the file content must be the canonical
/// 44-character Base64 form of 32 bytes (the exact shape both implementations
/// generate). A custom <c>DataProtection:RootKeyFile</c> must therefore be a
/// fully qualified path.
/// </para>
/// </remarks>
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

        var configuredPath = configuration[KeyFileSetting];
        var filePath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(contentRootPath, "data", DefaultKeyFileName)
            : configuredPath;

        try
        {
            // The injected value wins without any file inspection; otherwise
            // the shared source reads or atomically publishes the key file.
            // Its one fixed diagnostic — which never carries a path or the
            // key — is rethrown as this repository's public startup message.
            return new RootKeySource(configuration[InjectedKeySetting], filePath).Resolve();
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(StartupFailureMessage, exception);
        }
    }
}
