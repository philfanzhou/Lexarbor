using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Lexarbor.Domain.Models;

namespace Lexarbor.Database.Entities;

[Table("meaning_submission")]
public sealed class MeaningSubmissionEntity
{
    [Key]
    [Column("id")]
    public string Id { get; set; } = string.Empty;

    [Column("source")]
    public string Source { get; set; } = string.Empty;

    [Column("word")]
    public string Word { get; set; } = string.Empty;

    [Column("normalized_word")]
    public string NormalizedWord { get; private set; } = string.Empty;

    [Column("part_of_speech")]
    public string? PartOfSpeech { get; set; }

    [Column("normalized_part_of_speech")]
    public string NormalizedPartOfSpeech { get; private set; } = string.Empty;

    [Column("meaning")]
    public string Meaning { get; set; } = string.Empty;

    [Column("normalized_meaning")]
    public string NormalizedMeaning { get; private set; } = string.Empty;

    [Column("example")]
    public string? Example { get; set; }

    [Column("status")]
    public MeaningSubmissionStatus Status { get; set; }

    [Column("result_book_id")]
    public string? ResultBookId { get; set; }

    [Column("result_word_id")]
    public string? ResultWordId { get; set; }

    [Column("result_meaning_id")]
    public string? ResultMeaningId { get; set; }

    [Column("result_word")]
    public string? ResultWord { get; set; }

    [Column("result_meaning")]
    public string? ResultMeaning { get; set; }

    [Column("result_part_of_speech")]
    public string? ResultPartOfSpeech { get; set; }

    // Unix milliseconds, like the audio tables: the SQLite provider cannot order
    // by DateTimeOffset, and both review queues are ordered by time.
    [Column("created_at_unix_ms")]
    public long CreatedAtUnixMs { get; set; }

    [Column("updated_at_unix_ms")]
    public long UpdatedAtUnixMs { get; set; }

    [Column("reviewed_at_unix_ms")]
    public long? ReviewedAtUnixMs { get; set; }
}
