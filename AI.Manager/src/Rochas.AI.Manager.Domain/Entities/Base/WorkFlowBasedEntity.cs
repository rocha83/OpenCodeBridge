using System.ComponentModel.DataAnnotations.Schema;

namespace Rochas.AI.Manager.Domain.Entities.Base;

public abstract class WorkFlowBasedEntity : AuditableEntity
{
    [Column("status_id")]
    public int StatusId { get; set; }

    [Column("next_status_id")]
    public int? NextStatusId { get; set; }

    [Column("requester_id")]
    public int? RequesterId { get; set; }

    [Column("approver_id")]
    public int? ApproverId { get; set; }

    [Column("request_note")]
    public string? RequestNote { get; set; }

    [Column("approval_note")]
    public string? ApprovalNote { get; set; }

    [Column("approved")]
    public bool Approved { get; set; }

    protected WorkFlowBasedEntity() { }

    protected WorkFlowBasedEntity(int statusId, int? parentId = null) : base(parentId)
    {
        StatusId = statusId;
    }

    public virtual void SetStatus(int statusId, int? nextStatusId = null)
    {
        StatusId = statusId;
        NextStatusId = nextStatusId;
    }
}
