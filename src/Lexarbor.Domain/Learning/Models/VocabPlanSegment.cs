namespace Lexarbor.Learning.Domain.Models;
public sealed record VocabPlanSegment(string BookId, List<string>? UnitIds)
{

    public bool SameAs(VocabPlanSegment other) =>
        string.Equals(BookId, other.BookId, StringComparison.Ordinal)
        && (UnitIds is null ? other.UnitIds is null : other.UnitIds is not null && UnitIds.SequenceEqual(other.UnitIds, StringComparer.Ordinal));
    public bool Covers(VocabPlanSegment previous)
    {
        if (!string.Equals(BookId, previous.BookId, StringComparison.Ordinal)) return false;
        if (UnitIds is null) return true;
        if (previous.UnitIds is null || UnitIds.Count < previous.UnitIds.Count) return false;
        return UnitIds.Take(previous.UnitIds.Count).SequenceEqual(previous.UnitIds, StringComparer.Ordinal);
    }
}
