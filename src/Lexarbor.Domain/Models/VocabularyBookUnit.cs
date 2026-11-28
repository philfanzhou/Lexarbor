namespace Lexarbor.Domain.Models;

public class VocabularyBookUnit
{
    public string Id { get; set; } = string.Empty;
    public string BookId { get; set; } = string.Empty;
    public int Number { get; set; }
    public string? Title { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
