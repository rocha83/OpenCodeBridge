using System.ComponentModel.DataAnnotations.Schema;
using Rochas.AI.Manager.Domain.Entities.Base;

namespace Rochas.AI.Manager.Domain.Entities;

[Table("agents")]
public sealed class Agent : BaseEntity
{
    [Column("model")]
    public string Model { get; set; } = string.Empty;

    [Column("bridge_url")]
    public string BridgeUrl { get; set; } = string.Empty;

    [Column("temperature")]
    public double Temperature { get; set; } = 0.4;

    [Column("thinking")]
    public string Thinking { get; set; } = "off";

    [Column("system_prompt")]
    public string SystemPrompt { get; set; } = string.Empty;

    [Column("mode")]
    public string Mode { get; set; } = "Build";

    [Column("tools")]
    public string Tools { get; set; } = string.Empty;
}
