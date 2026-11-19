namespace Lexarbor.Learning.Domain.Models;

/// <summary>One calendar date and its state: checkedIn, missed, noPlan, pending (today, not yet checked in) or future.</summary>
public sealed record VocabularyCheckInDay(DateOnly Date, string State);
