using Lexarbor.Learning.Domain.Models;

namespace Lexarbor.Learning.Domain.Services;

public sealed record VocabularyPlanScopeEntry(string BookId, bool UnitScoped, VocabularyScopeEntry Entry)
{
    public VocabularySourceKind Source { get; init; } = VocabularySourceKind.Plan;

    /// <summary>Whether the plan's scope also contains this meaning; always true for a plan entry.</summary>
    public bool CoveredByPlan { get; init; } = true;

    public static async Task<IReadOnlyList<VocabularyPlanScopeEntry>> ReadAsync(
        IVocabularyScopeEntryReader reader, IReadOnlyList<VocabPlanSegment> segments, CancellationToken cancellationToken = default)
    {
        var entries = new List<VocabularyPlanScopeEntry>();
        var seen = new HashSet<CardKey>();
        foreach (var segment in segments)
        {
            var unitScoped = segment.UnitIds is { Count: > 0 };
            var segmentEntries = await reader.ReadOrderedScopeEntriesAsync(
                segment.BookId, unitScoped ? segment.UnitIds : null, cancellationToken).ConfigureAwait(false);
            foreach (var entry in segmentEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (seen.Add(new CardKey(entry.NormalizedWord, entry.MeaningKey)))
                {
                    entries.Add(new VocabularyPlanScopeEntry(segment.BookId, unitScoped, entry));
                }
            }
        }

        return entries;
    }
}
