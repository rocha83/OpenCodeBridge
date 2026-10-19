namespace Rochas.AI.Manager.Domain.Harness;

public sealed class HarnessProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Status { get; set; } = "Draft"; // Draft, Approved, Deprecated
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<HarnessProfileVersion> Versions { get; set; } = new();
}
