namespace Lexarbor.Host;

/// <summary>
/// The container's HEALTHCHECK, run as <c>dotnet Lexarbor.Host.dll --health-check</c>.
///
/// It lives in the application rather than being a shell command because the
/// runtime image ships neither curl nor wget, and the usual fix — installing
/// one — would hand any future remote-code-execution a download tool inside a
/// container this same change is hardening. Reusing the assembly that is
/// already present adds no package, no layer, and no capability the image did
/// not have.
/// </summary>
public static class HealthCheckCommand
{
    public const string Argument = "--health-check";

    /// <summary>
    /// Matches the hardcoded listen port in Program.cs. Loopback specifically:
    /// the check must prove this container serves, not that something on the
    /// network answers.
    /// </summary>
    private const string HealthUri = "http://127.0.0.1:5008/health";

    /// <summary>
    /// Shorter than the HEALTHCHECK timeout in the Dockerfile, so a hung
    /// request is reported by this process as a failure rather than killed by
    /// Docker, which makes the container log say what happened.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public static async Task<int> RunAsync()
    {
        using var client = new HttpClient { Timeout = Timeout };
        try
        {
            using var response = await client.GetAsync(HealthUri);
            if (response.IsSuccessStatusCode)
            {
                return 0;
            }

            await Console.Error.WriteLineAsync(
                $"Health check failed with status {(int)response.StatusCode}.");
            return 1;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException)
        {
            // The two ways a starting, stopped, or wedged application presents
            // itself: connection refused, and a request that never returns.
            await Console.Error.WriteLineAsync($"Health check failed: {exception.Message}");
            return 1;
        }
    }
}
