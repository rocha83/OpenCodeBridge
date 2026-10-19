namespace Rochas.AI.Manager.Domain.Experiments;

public sealed class Experiment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Hypothesis { get; set; }
    public Guid? BaseArtifactId { get; set; }
    public Guid? TunedArtifactId { get; set; }
    public string? MetricsJson { get; set; }
    public string Status { get; set; } = "Planned"; // Planned, Running, Approved, Rejected
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
