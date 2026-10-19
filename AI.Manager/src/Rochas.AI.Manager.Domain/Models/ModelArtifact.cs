namespace Rochas.AI.Manager.Domain.Models;

public sealed class ModelArtifact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Family { get; set; } = string.Empty; // Gemma4, Llama, Qwen, DeepSeek
    public string? Variant { get; set; }
    public string Format { get; set; } = "safetensors"; // safetensors, gguf
    public string? FilePath { get; set; }
    public string? FileHash { get; set; }
    public bool IsTrainable { get; set; }
    public string Status { get; set; } = "Draft"; // Draft, Approved, Deprecated
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
