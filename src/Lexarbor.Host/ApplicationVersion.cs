using System.Reflection;

namespace Lexarbor.Host;

/// <summary>
/// Immutable identity of the running Host build, written to startup logs only.
/// Runtime configuration cannot override these assembly attributes.
/// </summary>
internal static class ApplicationVersion
{
    private static readonly BuildIdentity Identity = Read(typeof(ApplicationVersion).Assembly);

    // Keep the existing pure-version contract for startup log consumers.
    public static string Current => Identity.Version;
    public static string? Revision => Identity.Revision;
    public static string Channel => Identity.Channel;

    internal static BuildIdentity Read(Assembly assembly)
    {
        var versions = assembly.GetCustomAttributes<AssemblyInformationalVersionAttribute>().Take(2).ToArray();
        var informational = versions.Length == 1 ? versions[0].InformationalVersion : null;
        var version = informational?.Split('+', 2)[0];
        if (string.IsNullOrWhiteSpace(version))
        {
            version = "unknown";
        }

        var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToArray();
        var revision = ReadUniqueMetadata(metadata, "BuildRevision");
        revision = revision is { Length: 40 } && revision.All(char.IsAsciiHexDigit)
            ? revision.ToLowerInvariant()
            : null;
        var channel = ReadUniqueMetadata(metadata, "BuildChannel");
        channel = channel is "release" or "edge" or "development" ? channel : "development";

        return new BuildIdentity(version, revision, channel);
    }

    private static string? ReadUniqueMetadata(AssemblyMetadataAttribute[] metadata, string key)
    {
        var matches = metadata.Where(attribute => attribute.Key == key).Take(2).ToArray();
        return matches.Length == 1 ? matches[0].Value : null;
    }

    internal sealed record BuildIdentity(string Version, string? Revision, string Channel);
}
