using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
namespace Rochas.AI.Manager.Domain.Entities.Base;

public abstract class AuditableEntity : BaseEntity
{
    [Column("owner_login")]
    public string OwnerLogin { get; set; } = string.Empty;

    [Column("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    [Column("updater_id")]
    public int? UpdaterId { get; set; }

    [Column("last_update")]
    public string? LastUpdate { get; set; }

    protected AuditableEntity() { }

    protected AuditableEntity(int? parentId = null) : base(parentId) { }

    public virtual void SetUpdated(int updaterId, string ownerLogin, string? lastUpdate = null)
    {
        UpdatedAt = DateTime.UtcNow;
        UpdaterId = updaterId;
        OwnerLogin = ownerLogin;
        LastUpdate = lastUpdate;
    }
}
