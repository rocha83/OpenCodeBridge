using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Rochas.Data.Specification.Annotations;

namespace Rochas.OpenCodeBridge.Web.Models;

// Agente/modelo alvo do chat (aponta p/ bridge GPU ou CPU). Comentarios pt-BR.
[Table("agents")]
public sealed class Agent
{
    [Key]
    public int Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("model")]
    public string Model { get; set; } = "qwen3-8b-awq";

    [Column("bridge_url")]
    public string BridgeUrl { get; set; } = "http://127.0.0.1:4124";

    [Column("temperature")]
    public double Temperature { get; set; } = 0.2;

    [Column("thinking")]
    public string Thinking { get; set; } = "events";

    [Column("system_prompt")]
    public string SystemPrompt { get; set; } = "";

    [Column("active")]
    public bool Active { get; set; } = true;
}
