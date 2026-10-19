using System.ComponentModel.DataAnnotations.Schema;
using Rochas.AI.Manager.Domain.Entities.Base;

namespace Rochas.AI.Manager.Domain.Models;

[Table("model_artifacts")]
public sealed class ModelArtifact : BaseEntity
{
    [Column("family")]
    public string Family { get; set; } = string.Empty;
    [Column("variant")]
    public string? Variant { get; set; }
    [Column("format")]
    public string Format { get; set; } = "safetensors";
    [Column("file_path")]
    public string? FilePath { get; set; }
    [Column("file_hash")]
    public string? FileHash { get; set; }
    [Column("is_trainable")]
    public bool IsTrainable { get; set; }
    [Column("status")]
    public string Status { get; set; } = "Draft";
}
