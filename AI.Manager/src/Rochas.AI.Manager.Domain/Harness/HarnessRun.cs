using System.ComponentModel.DataAnnotations.Schema;
using Rochas.AI.Manager.Domain.Entities.Base;

namespace Rochas.AI.Manager.Domain.Harness;

[Table("harness_runs")]
public sealed class HarnessRun : BaseEntity
{
    [Column("evidence_id")]
    public int EvidenceId { get; set; }
    [Column("profile_version_id")]
    public int ProfileVersionId { get; set; }
    [Column("model_binding_id")]
    public int ModelBindingId { get; set; }
    [Column("request_hash")]
    public string? RequestHash { get; set; }
    [Column("tool_calls")]
    public int ToolCalls { get; set; }
    [Column("files_created")]
    public int FilesCreated { get; set; }
    [Column("result_code")]
    public int ResultCode { get; set; }
    [Column("duration_ms")]
    public long DurationMs { get; set; }
    [Column("tokens_per_second")]
    public double TokensPerSecond { get; set; }
}
