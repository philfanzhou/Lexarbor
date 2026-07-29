using System;

namespace Ruoyu.Study.Vocabulary.Domain.Models;

public class VocabularyMeaningModel
{
    public string Id { get; set; } = string.Empty;
    public string VocabularyId { get; set; } = string.Empty;
    public string BookId { get; set; } = string.Empty;
    public string? PartOfSpeech { get; set; }
    public string Meaning { get; set; } = string.Empty;
    public string? Example { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
