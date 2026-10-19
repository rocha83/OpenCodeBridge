using System.ComponentModel.DataAnnotations.Schema;
using Rochas.AI.Manager.Domain.Entities.Base;

namespace Rochas.AI.Manager.Domain.Harness;

[Table("bridge_captures")]
public sealed class BridgeCapture : BaseEntity
{
    [Column("mode")]
    public string Mode { get; set; } = string.Empty;
    [Column("request_json")]
    public string? RequestJson { get; set; }
    [Column("response_json")]
    public string? ResponseJson { get; set; }
    [Column("request_hash")]
    public string? RequestHash { get; set; }
}
