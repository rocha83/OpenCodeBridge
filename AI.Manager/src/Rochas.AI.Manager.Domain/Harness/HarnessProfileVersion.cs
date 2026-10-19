using System.ComponentModel.DataAnnotations.Schema;
using Rochas.AI.Manager.Domain.Entities.Base;

namespace Rochas.AI.Manager.Domain.Harness;

[Table("harness_profile_versions")]
public sealed class HarnessProfileVersion : BaseEntity
{
    [Column("profile_id")]
    public int ProfileId { get; set; }
    [Column("version")]
    public int Version { get; set; }
    [Column("mode")]
    public string Mode { get; set; } = "ClientNative";
    [Column("checksum")]
    public string? Checksum { get; set; }
    [Column("approved_by")]
    public string? ApprovedBy { get; set; }
    [Column("approved_at")]
    public DateTime? ApprovedAt { get; set; }
}
