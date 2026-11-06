namespace Lexarbor.Domain.Models;

public sealed record VocabularyExpansionInput(string WordId, string MeaningId, string Word,
    string? PartOfSpeech, string Meaning, string? Example, EnrichmentInputVersion Version);
public sealed record VocabularyExpansionCandidate(string Word, string Type, string PartOfSpeech,
    string Meaning, string? Example, string UsageNote);
public sealed record VocabularyExpansionPlan(string Model, string BookId,
    IReadOnlyList<VocabularyExpansionCandidate> Candidates);

public static class VocabularyExpansionValidation
{
    public const int MaximumCandidates = 12;
    public static bool Valid(VocabularyExpansionPlan plan) => Text(plan.Model, 2048)
        && Text(plan.BookId, 128) && plan.Candidates is { Count: <= MaximumCandidates }
        && plan.Candidates.All(c => c != null && Text(c.Word, 200) && VocabularyRelationTypes.IsValid(c.Type)
            && Text(c.PartOfSpeech, 32) && Text(c.Meaning, 500) && (c.Example == null || Text(c.Example, 500))
            && Text(c.UsageNote, 500))
        && plan.Candidates.Select(c => (c.Word.Trim().ToLowerInvariant(), c.Type)).Distinct().Count() == plan.Candidates.Count;
    private static bool Text(string? value, int max) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= max && !value.Any(char.IsControl);
}
