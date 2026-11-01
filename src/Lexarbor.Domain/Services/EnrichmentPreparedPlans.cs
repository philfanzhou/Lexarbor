using Lexarbor.Domain.Models;

namespace Lexarbor.Domain.Services;

public static class EnrichmentPreparedPlans
{
    public static EnrichmentPlan Store(PreparedPronunciation plan, string? ownAssetId) => plan.Content with
    {
        Preparation = new(plan.Input, plan.InputVersion, plan.ConfigurationVersion, plan.EntryKind,
            plan.ClassificationBasis, plan.RequiredPhonetic, plan.SpeechPhonetic, plan.Control, ownAssetId)
    };

    public static PreparedPronunciation? Restore(EnrichmentPlan? plan) => plan?.Preparation is not { } saved ? null :
        new(plan with { Preparation = null }, saved.Input, saved.InputVersion, saved.ConfigurationVersion,
            saved.EntryKind, saved.ClassificationBasis, saved.RequiredPhonetic, saved.SpeechPhonetic, saved.Control);
}
