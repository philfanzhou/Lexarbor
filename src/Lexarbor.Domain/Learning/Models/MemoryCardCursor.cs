namespace Lexarbor.Learning.Domain.Models;

public sealed record MemoryCardCursor(DateTimeOffset DueAt, string WordKey, string MeaningKey);
