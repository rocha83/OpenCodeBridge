namespace Rochas.AI.Manager.Domain.Profiles;

public sealed class TrainingPattern
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public Guid? ProfileId { get; set; }
    public Guid? ModelArtifactId { get; set; }
    public string? PatternJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
