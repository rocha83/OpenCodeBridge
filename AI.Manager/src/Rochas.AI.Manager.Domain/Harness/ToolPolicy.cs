namespace Rochas.AI.Manager.Domain.Harness;

public sealed class ToolPolicy
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Role { get; set; } = "general";
    public string Allow { get; set; } = string.Empty; // comma-separated
    public string Deny { get; set; } = string.Empty;
    public bool RequireCall { get; set; }
    public int MaxTurns { get; set; } = 10;
    public int StallLimit { get; set; } = 3;
}
