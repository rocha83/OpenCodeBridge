using System.ComponentModel.DataAnnotations.Schema;
using Rochas.AI.Manager.Domain.Entities.Base;

namespace Rochas.AI.Manager.Domain.Harness;

[Table("tool_policies")]
public sealed class ToolPolicy : BaseEntity
{
    [Column("role")]
    public string Role { get; set; } = "general";
    [Column("allow")]
    public string Allow { get; set; } = string.Empty;
    [Column("deny")]
    public string Deny { get; set; } = string.Empty;
    [Column("require_call")]
    public bool RequireCall { get; set; }
    [Column("max_turns")]
    public int MaxTurns { get; set; } = 10;
    [Column("stall_limit")]
    public int StallLimit { get; set; } = 3;
}
