using ServiceMantle.Diagnostics;

namespace Lexarbor.Host;

/// <summary>
/// Dispatches OTLP authentication queries to the shared fixed resolvers, one
/// per configured signal header name. The value is read from the process
/// environment once, here at construction, and never travels through
/// configuration, is never logged, and is never included in an exception. The
/// header's NAME is configuration; the secret is the environment.
/// </summary>
/// <remarks>
/// <para>
/// One shared <see cref="FixedRemoteTelemetryAuthenticationResolver"/> serves
/// each distinct configured <c>AuthenticationHeaderName</c>, so the traces and
/// metrics signals may carry different header names. The three-branch behavior
/// matrix of the retired in-house resolver is kept exactly: with no header name
/// configured nothing is queried and no header is exported; with a header name
/// configured and the environment value present, the query answers with
/// <c>(that configured name, the environment value)</c>; with the value absent
/// or blank, every query answers false and the ServiceMantle runtime fails
/// startup closed (<c>otlp.authentication_missing</c>) instead of exporting
/// silently without authentication.
/// </para>
/// </remarks>
public sealed class ConfiguredOtlpAuthenticationResolver : IRemoteTelemetryAuthenticationResolver
{
    public const string ValueEnvironmentVariable = "LEXARBOR_TELEMETRY_OTLP_AUTHORIZATION";

    private readonly IReadOnlyList<FixedRemoteTelemetryAuthenticationResolver> resolvers;

    public ConfiguredOtlpAuthenticationResolver(IEnumerable<string?> headerNames)
    {
        ArgumentNullException.ThrowIfNull(headerNames);

        // Read once, at construction: the runtime resolves headers once at
        // startup, so the per-query read of the retired resolver bought
        // nothing, and a host under test sees its environment override the
        // same way a deployment's container variable is seen.
        var value = Environment.GetEnvironmentVariable(ValueEnvironmentVariable);
        resolvers = string.IsNullOrWhiteSpace(value)
            ? []
            : headerNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .Select(name => new FixedRemoteTelemetryAuthenticationResolver(name!, name!, value))
                .ToList()
                .AsReadOnly();
    }

    public bool TryResolve(string name, out RemoteTelemetryAuthenticationHeader? header)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        foreach (var resolver in resolvers)
        {
            if (resolver.TryResolve(name, out header))
            {
                return true;
            }
        }

        header = null;
        return false;
    }
}
