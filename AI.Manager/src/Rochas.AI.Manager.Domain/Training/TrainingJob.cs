using System.ComponentModel.DataAnnotations.Schema;
using Rochas.AI.Manager.Domain.Entities.Base;

namespace Rochas.AI.Manager.Domain.Training;

[Table("training_jobs")]
public sealed class TrainingJob : BaseEntity
{
    [Column("status")]
    public string Status { get; set; } = "Queued";
    [Column("model_artifact_id")]
    public int? ModelArtifactId { get; set; }
    [Column("dataset_id")]
    public int? DatasetId { get; set; }
    [Column("experiment_id")]
    public int? ExperimentId { get; set; }
    [Column("profile_name")]
    public string? ProfileName { get; set; }
    [Column("advanced_mode")]
    public bool AdvancedMode { get; set; }
    [Column("training_method")]
    public string TrainingMethod { get; set; } = "SFT";
    [Column("configuration_json")]
    public string? ConfigurationJson { get; set; }
    [Column("started_at")]
    public DateTime? StartedAt { get; set; }
    [Column("completed_at")]
    public DateTime? CompletedAt { get; set; }
    [Column("error_message")]
    public string? ErrorMessage { get; set; }
    [Column("last_loss")]
    public double? LastLoss { get; set; }
    [Column("vram_used_mb")]
    public double? VramUsedMb { get; set; }
    [Column("epochs_completed")]
    public int EpochsCompleted { get; set; }
    [Column("best_accuracy")]
    public double? BestAccuracy { get; set; }
    [Column("throughput_tokens_per_sec")]
    public double ThroughputTokensPerSec { get; set; }
}
