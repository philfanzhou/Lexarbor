namespace Lexarbor.Host;

// Prioritize preparation while both kinds have demand. Expansion remains usable
// when it is the only work, and never occupies both text slots.
internal sealed class EnrichmentTextSchedulingPolicy
{
    private int _preparationRequests;
    private double _preparationMs;
    private double _requiredPreparationMs;

    internal bool CanExpand(bool preparationPending, bool expansionRunning) => !expansionRunning
        && (!preparationPending || _preparationRequests >= 3 && _preparationMs >= _requiredPreparationMs);

    internal void Complete(bool expansion, int requests, double requestMs)
    {
        if (requests == 0) return;
        if (expansion)
        {
            _preparationRequests = 0;
            _preparationMs = 0;
            _requiredPreparationMs = 3 * requestMs;
        }
        else
        {
            _preparationRequests = Math.Min(3, _preparationRequests + requests);
            _preparationMs += requestMs;
        }
    }
}
