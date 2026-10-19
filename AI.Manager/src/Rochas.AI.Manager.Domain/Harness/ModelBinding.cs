using System.ComponentModel.DataAnnotations.Schema;
using Rochas.AI.Manager.Domain.Entities.Base;

namespace Rochas.AI.Manager.Domain.Harness;

[Table("model_bindings")]
public sealed class ModelBinding : BaseEntity
{
    [Column("provider")]
    public string Provider { get; set; } = string.Empty;
    [Column("model_id")]
    public string ModelId { get; set; } = string.Empty;
    [Column("max_tokens")]
    public int MaxTokens { get; set; } = 4096;
    [Column("context_window")]
    public int ContextWindow { get; set; } = 32768;
    [Column("thinking")]
    public string Thinking { get; set; } = "off";
    [Column("temperature")]
    public double Temperature { get; set; } = 0.5;
}
