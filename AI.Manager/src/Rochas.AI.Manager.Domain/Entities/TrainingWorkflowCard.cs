using System.ComponentModel.DataAnnotations.Schema;
using Rochas.AI.Manager.Domain.Entities.Base;

namespace Rochas.AI.Manager.Domain.Entities;

[Table("training_workflow_cards")]
public sealed class TrainingWorkflowCard : WorkFlowBasedEntity
{
    [Column("title")]
    public string Title { get; set; } = string.Empty;

    [Column("description")]
    public string? Description { get; set; }

    [Column("experiment_id")]
    public int? ExperimentId { get; set; }

    [Column("accuracy")]
    public double? Accuracy { get; set; }

    [Column("target_accuracy")]
    public double TargetAccuracy { get; set; } = 0.9;
    [Column("rejection_reason")]
    public string? RejectionReason { get; set; }
}
