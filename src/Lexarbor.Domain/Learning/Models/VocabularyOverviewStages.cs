namespace Lexarbor.Learning.Domain.Models;

// New describes only the current scope; the other stages describe all historical cards.
public sealed record VocabularyOverviewStages(int New, int Learning, int Familiar, int Mastered);
