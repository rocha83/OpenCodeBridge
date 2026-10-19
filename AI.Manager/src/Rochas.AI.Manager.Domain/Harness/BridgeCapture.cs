namespace Rochas.AI.Manager.Domain.Harness;

public sealed class BridgeCapture
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Mode { get; set; } = string.Empty;
    public string? RequestJson { get; set; }
    public string? ResponseJson { get; set; }
    public string? RequestHash { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
