using System.ComponentModel.DataAnnotations.Schema;
using Rochas.AI.Manager.Domain.Entities.Base;

namespace Rochas.AI.Manager.Domain.Harness;

[Table("harness_attachments")]
public sealed class HarnessAttachment : BaseEntity
{
    [Column("version_id")]
    public int VersionId { get; set; }
    [Column("kind")]
    public string Kind { get; set; } = "Core";
    [Column("role")]
    public string Role { get; set; } = "general";
    [Column("content")]
    public string Content { get; set; } = string.Empty;
    [Column("char_count")]
    public int CharCount { get; set; }
}
