using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.IdentityModel.Tokens;

namespace Lexarbor.Host.Integration;

/// <summary>
/// The startup snapshot of the one integration client allowed to submit
/// meanings: a fixed issuer, audience and subject, and a ring of RSA public keys
/// selected by key id. Lexarbor never fetches keys, contacts an identity provider
/// or stores a client secret; rotating keys means editing the ring and restarting.
/// </summary>
public sealed partial class IntegrationServiceTrust
{
    public const string Section = "MeaningSubmissions:Integration";
    public const string DefaultAudience = "urn:lexarbor:integration";

    public string Issuer { get; private init; } = string.Empty;
    public string Audience { get; private init; } = DefaultAudience;
    public string Subject { get; private init; } = string.Empty;
    internal IReadOnlyDictionary<string, SecurityKey> Keys { get; private init; } = new Dictionary<string, SecurityKey>();

    public bool IsConfigured => Keys.Count > 0;

    public static IntegrationServiceTrust Read(IConfiguration configuration)
    {
        var section = configuration.GetSection(Section);
        var keys = new Dictionary<string, SecurityKey>(StringComparer.Ordinal);
        if (section.GetSection("PublicKeys").Value is not null
            || section.AsEnumerable().Any(entry => entry.Key.Contains("PrivateKey", StringComparison.OrdinalIgnoreCase)))
        {
            throw Invalid("PublicKeys");
        }

        foreach (var entry in section.GetSection("PublicKeys").GetChildren())
        {
            var kid = entry["KeyId"];
            if (kid is null || !KeyIdPattern().IsMatch(kid) || keys.ContainsKey(kid))
            {
                throw Invalid("PublicKeys:KeyId");
            }

            try
            {
                var pem = File.ReadAllText(entry["PublicKeyPath"] ?? string.Empty);
                if (pem.Contains("PRIVATE KEY", StringComparison.Ordinal))
                {
                    throw new CryptographicException();
                }

                using var rsa = RSA.Create();
                rsa.ImportFromPem(pem);
                if (rsa.KeySize < 2048)
                {
                    throw new CryptographicException();
                }

                keys.Add(kid, new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = kid });
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or CryptographicException)
            {
                throw Invalid("PublicKeys:PublicKeyPath");
            }
        }

        if (keys.Count == 0)
        {
            return new IntegrationServiceTrust();
        }

        return new IntegrationServiceTrust
        {
            Issuer = Text(section, "Issuer", null),
            Audience = Text(section, "Audience", DefaultAudience),
            Subject = Text(section, "Subject", null),
            Keys = keys
        };
    }

    private static string Text(IConfiguration section, string key, string? fallback)
    {
        var value = section[key] ?? fallback;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace))
        {
            throw Invalid(key);
        }

        return value;
    }

    // The message names the setting only: never the value, a path or key material.
    private static InvalidOperationException Invalid(string key) =>
        new($"{Section}:{key} is invalid or incomplete.");

    [GeneratedRegex("^[A-Za-z0-9._-]{1,128}$")]
    private static partial Regex KeyIdPattern();
}
