using System.ComponentModel.DataAnnotations.Schema;
using Rochas.AI.Manager.Domain.Entities.Base;

namespace Rochas.AI.Manager.Domain.Profiles;

[Table("training_patterns")]
public sealed class TrainingPattern : BaseEntity
{
    [Column("profile_id")]
    public int? ProfileId { get; set; }
    [Column("model_artifact_id")]
    public int? ModelArtifactId { get; set; }
    [Column("pattern_json")]
    public string? PatternJson { get; set; }
}
