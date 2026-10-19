namespace Rochas.AI.Manager.Domain.Harness;

public sealed class ModelBinding
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Provider { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public int MaxTokens { get; set; } = 4096;
    public int ContextWindow { get; set; } = 32768;
    public string Thinking { get; set; } = "off";
    public double Temperature { get; set; } = 0.5;
}
