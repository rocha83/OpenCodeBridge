using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Rochas.Data.Specification.Annotations;

namespace Rochas.OpenCodeBridge.Web.Models;

// Agente/modelo alvo do chat (aponta p/ bridge GPU ou CPU). Comentarios pt-BR.
[Table("agents")]
public sealed class Agent
{
    [Key]
    public int? Id { get; set; }

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("model")]
    public string Model { get; set; } = "";

    [Column("bridge_url")]
    public string BridgeUrl { get; set; } = "";

    [Column("temperature")]
    public double Temperature { get; set; }

    [Column("thinking")]
    public string Thinking { get; set; } = "";

    [Column("system_prompt")]
    public string SystemPrompt { get; set; } = "";

    [Column("role")]
    public string Role { get; set; } = "";

    [Column("mode")]
    public string Mode { get; set; } = "";

    [Column("measured_tps")]
    public double MeasuredTps { get; set; }

    [Column("active")]
    public bool Active { get; set; }

    // Normaliza vazio (linhas antigas) para build.
    public static string EffectiveMode(Agent a) => string.IsNullOrEmpty(a.Mode) ? "build" : a.Mode;
}
