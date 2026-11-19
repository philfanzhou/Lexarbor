using System.ComponentModel.DataAnnotations.Schema;

namespace Lexarbor.Learning.Database.Entities;

[Table("learner_identity")]
public sealed class LearnerIdentity
{
    [Column("id")]
    public Guid Id { get; set; }
}
