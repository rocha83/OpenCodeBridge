namespace Rochas.AI.Manager.Domain.Harness;

public sealed class HarnessAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid VersionId { get; set; }
    public string Kind { get; set; } = "Core"; // Core, RoleTail, BehaviorTail, ToolsTail, SkillHint
    public string Role { get; set; } = "general";
    public string Content { get; set; } = string.Empty;
    public int CharCount { get; set; }
}
