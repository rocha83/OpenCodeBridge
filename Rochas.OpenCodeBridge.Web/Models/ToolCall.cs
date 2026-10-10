using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Rochas.Data.Specification.Annotations;

namespace Rochas.OpenCodeBridge.Web.Models;

// Chamada de tool executada (trilha de avaliação da interação com o modelo local,
// como o thinking: args/resultado para melhoria posterior). Comentários pt-BR.
[Table("tool_calls")]
public sealed class ToolCall
{
    [Key]
    public int? Id { get; set; }

    [Column("session_id")]
    public int? SessionId { get; set; }

    [Column("agent")]
    public string Agent { get; set; } = "";

    [Column("name")]
    public string Name { get; set; } = "";

    [Column("args")]
    public string Args { get; set; } = "";

    [Column("ok")]
    public bool Ok { get; set; }

    [Column("output")]
    public string Output { get; set; } = "";

    [Column("ms")]
    public long Ms { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
