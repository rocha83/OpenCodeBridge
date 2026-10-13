using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Rochas.Data.Specification.Annotations;

namespace Rochas.OpenCodeBridge.Web.Models;

// Mensagem de uma sessão de chat. Comentários pt-BR.
[Table("session_messages")]
public sealed class SessionMessage
{
    [Key]
    public int? Id { get; set; }

    [Column("session_id")]
    public int? SessionId { get; set; }

    [Column("role")]
    public string Role { get; set; } = ""; // "user" | "assistant" | "system"

    [Column("content")]
    public string Content { get; set; } = "";

    [Column("thinking")]
    public string Thinking { get; set; } = "";

    [Column("prompt_tokens")]
    public int? PromptTokens { get; set; }

    [Column("completion_tokens")]
    public int? CompletionTokens { get; set; }

    // Métrica do rodapé da bolha: duração da execução em ms + throughput em t/s.
    [Column("elapsed_ms")]
    public long? ElapsedMs { get; set; }

    [Column("tps")]
    public double? Tps { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
    // Status: 1 Enviada (user), 2 Respondida (assistant/subagent), 3 Arquivada (compactação).
    [Column("status_id")]
    public int? StatusId { get; set; }
    // Compactação: 1 = passado (fora do contexto, visível no histórico).
    public int Archived { get; set; } = 0;
}