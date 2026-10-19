using System.ComponentModel.DataAnnotations.Schema;
using Rochas.AI.Manager.Domain.Entities.Base;
using Rochas.AI.Manager.Domain.Helpers;

namespace Rochas.AI.Manager.Domain.Entities.Management;

[Table("workflow_steps")]
public class WorkflowStep : MultiEntityReferrer
{
    [Column("sort_order")]
    public int SortOrder { get; set; }
    [Column("status_id")]
    public int StatusId { get; set; }
    [Column("next_status_id")]
    public int NextStatusId { get; set; }
    [Column("custom_rule")]
    public string? CustomRule { get; set; }

    public WorkflowStep() { }

    public WorkflowStep(uint entityHashCode, string name, int order, int statusId, int nextStatusId, string? customRule = null, bool isActive = true)
        : base(entityHashCode)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name cannot be empty.", nameof(name));
        Name = name;
        SortOrder = order;
        StatusId = statusId;
        NextStatusId = nextStatusId;
        CustomRule = customRule;
        IsActive = isActive;
    }
}

public class WorkflowStep<T> : WorkflowStep where T : class
{
    protected WorkflowStep() : base() { }

    public WorkflowStep(string name, int order, int statusId, int nextStatusId, string? customRule = null, bool isActive = true)
        : base(Rochas.AI.Manager.Domain.Helpers.EntityHashCode.FromType<T>(), name, order, statusId, nextStatusId, customRule, isActive) { }
}
