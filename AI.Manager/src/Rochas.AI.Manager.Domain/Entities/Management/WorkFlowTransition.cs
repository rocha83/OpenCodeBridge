using System.ComponentModel.DataAnnotations.Schema;
using Rochas.AI.Manager.Domain.Entities.Base;
using Rochas.AI.Manager.Domain.Helpers;

namespace Rochas.AI.Manager.Domain.Entities.Management;

[Table("workflow_transitions")]
public class WorkFlowTransition : MultiEntityReferrer
{
    [Column("entity_id")]
    public int EntityId { get; set; }
    [Column("entity_status_from")]
    public int EntityStatusFrom { get; set; }
    [Column("entity_status_to")]
    public int EntityStatusTo { get; set; }
    [Column("action")]
    public string Action { get; set; } = "Submit";
    [Column("approver_id")]
    public int ApproverId { get; set; }
    [Column("note")]
    public string? Note { get; set; }

    public WorkFlowTransition() { }

    public WorkFlowTransition(uint entityHashCode, int entityId, int entityStatusFrom, int entityStatusTo, string action, int approverId, string? note = null)
        : base(entityHashCode)
    {
        if (entityId <= 0) throw new ArgumentException("Valid Entity ID is required.", nameof(entityId));
        EntityId = entityId;
        EntityStatusFrom = entityStatusFrom;
        EntityStatusTo = entityStatusTo;
        Action = action;
        ApproverId = approverId;
        Note = note;
    }
}

public class WorkFlowTransition<T> : WorkFlowTransition where T : class
{
    protected WorkFlowTransition() : base() { }

    public WorkFlowTransition(int entityId, int entityStatusFrom, int entityStatusTo, string action, int approverId, string? note = null)
        : base(Rochas.AI.Manager.Domain.Helpers.EntityHashCode.FromType<T>(), entityId, entityStatusFrom, entityStatusTo, action, approverId, note) { }
}
