using System.Reflection;

namespace Lexarbor.Host;

/// <summary>
/// The version this build was stamped with. The release workflow passes the
/// version tag into the container build, so a published image reports the tag
/// it was released under rather than the SDK default that every build would
/// otherwise share.
/// </summary>
internal static class ApplicationVersion
{
    /// <summary>
    /// The informational version, without the source revision the SDK appends
    /// when the build has a repository available. Container builds exclude
    /// <c>.git</c>, so keeping the suffix would make the same release report a
    /// different value depending on where it was built.
    /// </summary>
    public static string Current { get; } = Read();

    private static string Read()
    {
        var informational = typeof(ApplicationVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return "unknown";
        }

        var revisionSeparator = informational.IndexOf('+');
        return revisionSeparator < 0
            ? informational
            : informational[..revisionSeparator];
    }
}
