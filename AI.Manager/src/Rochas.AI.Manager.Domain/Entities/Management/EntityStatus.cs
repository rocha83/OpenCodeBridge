using System.ComponentModel.DataAnnotations.Schema;
using Rochas.AI.Manager.Domain.Entities.Base;

namespace Rochas.AI.Manager.Domain.Entities.Management;

[Table("entity_statuses")]
public sealed class EntityStatus : BaseEntity
{
    [Column("code")]
    public new string Code { get; set; } = string.Empty;
    [Column("description")]
    public string? Description { get; set; }
}
