using Rochas.AI.Manager.Domain.Entities.Base;
using System.ComponentModel.DataAnnotations.Schema;

namespace Rochas.AI.Manager.Domain.Harness;

[Table("harness_profiles")]
public sealed class HarnessProfile : BaseEntity
{
    [Column("description")]
    public string? Description { get; set; }
    [Column("status")]
    public string Status { get; set; } = "Draft";

    [NotMapped]
    [Column("versions")]
    public List<HarnessProfileVersion> Versions { get; set; } = new();
}
