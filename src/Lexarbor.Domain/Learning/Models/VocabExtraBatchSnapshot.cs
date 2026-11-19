namespace Lexarbor.Learning.Domain.Models;

/// <summary>Answer-free progress for one extra-learning batch, counted by distinct words.</summary>
public sealed record VocabExtraBatchSnapshot(Guid Id, int Target, int Required, int Completed, DateTimeOffset? CompletedAt);
