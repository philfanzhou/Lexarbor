using System.ComponentModel.DataAnnotations.Schema;
using Lexarbor.Domain.Models;

namespace Lexarbor.Database.Entities;

[Table("meaning_phonetic_epoch")]
public sealed class MeaningPhoneticEpochEntity
{
    [Column("meaning_id")] public string MeaningId { get; set; } = string.Empty;
    [Column("word_id")] public string WordId { get; set; } = string.Empty;
    [Column("locale")] public AudioLocale Locale { get; set; }
    [Column("epoch")] public long Epoch { get; set; }
}
