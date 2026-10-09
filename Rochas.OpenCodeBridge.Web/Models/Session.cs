using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Rochas.Data.Specification.Annotations;

namespace Rochas.OpenCodeBridge.Web.Models;

// Sessão de chat (pertence a um usuário, usa um agente). Comentários pt-BR.
[Table("sessions")]
public sealed class Session
{
    [Key]
    public int? Id { get; set; }

    [Column("user_id")]
    public int UserId { get; set; }

    [Column("agent_id")]
    public int AgentId { get; set; }

    [Column("executor_agent_id")]
    public int? ExecutorAgentId { get; set; }

    [Column("title")]
    public string Title { get; set; } = "";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}