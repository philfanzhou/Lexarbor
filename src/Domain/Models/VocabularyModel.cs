using System;

namespace Ruoyu.Study.Vocabulary.Domain.Models;

public class VocabularyModel
{
    public string Id { get; set; } = string.Empty;
    public string Word { get; set; } = string.Empty;
    public string? PhoneticUk { get; set; }
    public string? PhoneticUs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
