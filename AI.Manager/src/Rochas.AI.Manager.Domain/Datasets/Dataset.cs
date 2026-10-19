using System.ComponentModel.DataAnnotations.Schema;
using Rochas.AI.Manager.Domain.Entities.Base;

namespace Rochas.AI.Manager.Domain.Datasets;

[Table("datasets")]
public sealed class Dataset : BaseEntity
{
    [Column("format")]
    public string Format { get; set; } = "jsonl";
    [Column("version")]
    public string? Version { get; set; }
    [Column("file_path")]
    public string? FilePath { get; set; }
    [Column("file_hash")]
    public string? FileHash { get; set; }
    [Column("is_validated")]
    public bool IsValidated { get; set; }
}
