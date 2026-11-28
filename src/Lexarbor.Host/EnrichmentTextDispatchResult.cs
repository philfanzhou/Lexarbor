namespace Lexarbor.Host;

public sealed record EnrichmentTextDispatchResult(IReadOnlyList<string> Ready, IReadOnlyList<string> Deferred,
    long NextCheckAtUnixMs = 0, int Requests = 0, double RequestMilliseconds = 0)
{
    public static readonly EnrichmentTextDispatchResult Empty = new([], []);
}
