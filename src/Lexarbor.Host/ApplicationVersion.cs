using System.Reflection;

namespace Lexarbor.Host;

/// <summary>
/// The version this build was stamped with. The release workflow passes the
/// version tag into the container build, so a published image reports the tag
/// it was released under rather than the SDK default that every build would
/// otherwise share.
///
/// Written to the startup log and nowhere else. It is not served over HTTP:
/// the only endpoint that could carry it is the anonymous <c>/health</c>, and
/// telling an unauthenticated caller which release it is talking to tells it
/// which published issues to try.
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
