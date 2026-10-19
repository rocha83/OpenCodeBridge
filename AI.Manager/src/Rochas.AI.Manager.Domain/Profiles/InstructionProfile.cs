using System.ComponentModel.DataAnnotations.Schema;
using Rochas.AI.Manager.Domain.Entities.Base;

namespace Rochas.AI.Manager.Domain.Profiles;

[Table("instruction_profiles")]
public sealed class InstructionProfile : BaseEntity
{
    [Column("description")]
    public string? Description { get; set; }
    [Column("instructions_json")]
    public string? InstructionsJson { get; set; }
    [Column("version")]
    public string? Version { get; set; }
    [Column("status")]
    public string Status { get; set; } = "Draft";
}
