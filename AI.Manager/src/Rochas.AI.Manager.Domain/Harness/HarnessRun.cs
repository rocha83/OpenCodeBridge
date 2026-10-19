namespace Rochas.AI.Manager.Domain.Harness;

public sealed class HarnessRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid EvidenceId { get; set; }
    public Guid ProfileVersionId { get; set; }
    public Guid ModelBindingId { get; set; }
    public string? RequestHash { get; set; }
    public int ToolCalls { get; set; }
    public int FilesCreated { get; set; }
    public int ResultCode { get; set; }
    public long DurationMs { get; set; }
    public double TokensPerSecond { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
