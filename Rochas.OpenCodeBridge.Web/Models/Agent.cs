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
    [Filterable]
    public string Name { get; set; } = "";

    [Column("model")]
    [Filterable]
    public string Model { get; set; } = "";

    [Column("bridge_url")]
    public string BridgeUrl { get; set; } = "";

    [Column("temperature")]
    public double Temperature { get; set; }

    [Column("thinking")]
    [Filterable]
    public string Thinking { get; set; } = "";

    [Column("system_prompt")]
    [Filterable]
    public string SystemPrompt { get; set; } = "";

    [Column("mode")]
    [Filterable]
    public string Mode { get; set; } = "";

    [Column("measured_tps")]
    public double MeasuredTps { get; set; }

    [Column("active")]
    public bool Active { get; set; }

    // Harness de tools: CSV com as tools liberadas (vazio = nenhuma).
    // Presets: navegação=glob, leitura=read+grep, escrita=write+edit,
    // repos=git, internet=fetch, validação=shell. subagent segue o Mode.
    // SEMPRE vazio por padrão: valor preenchido aqui vazaria p/ o WHERE de
    // todo filtro `new Agent()` (o parser inclui strings não-vazias).
    [Column("tools")]
    public string Tools { get; set; } = "";

    // Normaliza vazio (linhas antigas) para build.
    public static string EffectiveMode(Agent a) => string.IsNullOrEmpty(a.Mode) ? "build" : a.Mode;
}
