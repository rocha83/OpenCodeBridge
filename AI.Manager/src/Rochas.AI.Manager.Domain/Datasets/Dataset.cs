namespace Rochas.AI.Manager.Domain.Datasets;

public sealed class Dataset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Format { get; set; } = "jsonl";
    public string? Version { get; set; }
    public string? FilePath { get; set; }
    public string? FileHash { get; set; }
    public bool IsValidated { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
