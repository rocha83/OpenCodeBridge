namespace Rochas.AI.Manager.Domain.Profiles;

public sealed class InstructionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? InstructionsJson { get; set; }
    public string? Version { get; set; }
    public string Status { get; set; } = "Draft"; // Draft, Approved, Deprecated
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
