namespace Rochas.AI.Manager.Domain.Harness;

public sealed class HarnessProfileVersion
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProfileId { get; set; }
    public int Version { get; set; }
    public string Mode { get; set; } = "ClientNative"; // ClientNative, BridgeBypass, BridgeLegacy, Hybrid
    public string? Checksum { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
