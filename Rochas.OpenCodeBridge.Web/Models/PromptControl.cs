using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Rochas.Data.Specification.Annotations;

namespace Rochas.OpenCodeBridge.Web.Models;

// Controles de repeticao + calda de system prompt (gerenciado no Flow, so admin).
// Chaves conhecidas: system_tail, anti_repeat_enabled, anti_repeat_window,
// anti_repeat_notice, guardrail (espelho informativo do sufixo da bridge).
[Table("prompt_controls")]
public sealed class PromptControl
{
    [Key]
    [Filterable]
    [Column("key")]
    public string Key { get; set; } = "";

    [Filterable]
    [Column("label")]
    public string Label { get; set; } = "";

    [Column("value")]
    public string Value { get; set; } = "";

    [Column("updated_at")]
    public string UpdatedAt { get; set; } = "";
}
