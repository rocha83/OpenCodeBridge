using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Rochas.OpenCodeBridge.Web.Models;

// Lição aprendida de um refinamento: alucinação/erro do executor + ajuste.
// Alimenta o system prompt do executor de forma progressiva (anti-repetição).
[Table("refinement_lessons")]
public sealed class RefinementLesson
{
    [Key]
    public int? Id { get; set; }

    [Column("session_id")]
    public int SessionId { get; set; }

    [Column("task_index")]
    public int TaskIndex { get; set; }

    // refine | split | tool_denied | needs_tools
    [Column("kind")]
    public string Kind { get; set; } = "";

    [Column("detail")]
    public string Detail { get; set; } = "";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
