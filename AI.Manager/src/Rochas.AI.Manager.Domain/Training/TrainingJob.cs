namespace Rochas.AI.Manager.Domain.Training;

/// <summary>
/// Agregado: estado do job de treinamento.
/// </summary>
public sealed class TrainingJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Status { get; set; } = "Queued"; // Queued, Running, Completed, Failed, Cancelled
    public Guid? ModelArtifactId { get; set; }
    public Guid? DatasetId { get; set; }
    public Guid? ExperimentId { get; set; }
    public string? ProfileName { get; set; }
    public bool AdvancedMode { get; set; }
    public string? ConfigurationJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public double? LastLoss { get; set; }
    public double? VramUsedMb { get; set; }
}
