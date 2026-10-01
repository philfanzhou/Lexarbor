using ServiceMantle.Diagnostics;

namespace Lexarbor.Host;

/// <summary>
/// Resolves the OTLP authentication header value from the process environment
/// only: the value never travels through configuration, is never logged, and is
/// never included in an exception. The header's NAME is configuration; the
/// secret is the environment.
/// </summary>
public sealed class LexarborOtlpAuthenticationResolver : IRemoteTelemetryAuthenticationResolver
{
    public const string ValueEnvironmentVariable = "LEXARBOR_TELEMETRY_OTLP_AUTHORIZATION";

    public bool TryResolve(string name, out RemoteTelemetryAuthenticationHeader? header)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var value = Environment.GetEnvironmentVariable(ValueEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            header = null;
            return false;
        }

        header = new RemoteTelemetryAuthenticationHeader(name, value);
        return true;
    }
}
