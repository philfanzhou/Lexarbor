using System;

namespace Lexarbor.Domain.Models;

public class VocabularyBookModel
{
    public string Id { get; set; } = string.Empty;
    public string BookName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Publisher { get; set; }
    public string? EducationLevel { get; set; }
    public string? Grade { get; set; }
    public string? Category { get; set; }
    public int DisplayOrder { get; set; }
    public bool Status { get; set; }
    public string? IconUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
