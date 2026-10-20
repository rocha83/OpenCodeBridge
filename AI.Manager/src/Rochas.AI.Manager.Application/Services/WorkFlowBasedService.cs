using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Rochas.AI.Manager.Domain.Entities.Management;
using Rochas.AI.Manager.Domain.Interfaces;
using Rochas.AI.Manager.Domain.Interfaces.Services;
using Rochas.Data.Specification.Interfaces;

namespace Rochas.AI.Manager.Application.Services;

public class WorkFlowBasedService<T> : IWorkFlowBasedService<T> where T : class
{
    private readonly IGenericRepository<WorkflowStep> _stepRepo;
    private readonly IGenericRepository<WorkFlowTransition> _transitionRepo;
    private readonly uint _entityHashCode;

    public WorkFlowBasedService(IGenericRepository<WorkflowStep> stepRepo, IGenericRepository<WorkFlowTransition> transitionRepo, uint entityHashCode)
    {
        _stepRepo = stepRepo;
        _transitionRepo = transitionRepo;
        _entityHashCode = entityHashCode;
    }

    public async Task<List<WorkflowStep>> GetEntityStepsAsync(uint entityHashCode)
    {
        var all = await _stepRepo.Query(new WorkflowStep());
        return all.Where(s => s.EntityHashCode == entityHashCode && s.IsActive).OrderBy(s => s.SortOrder).ToList();
    }

    public async Task<bool> CanAdvanceAsync(T entity, int userId)
    {
        try { await AdvanceAsync(entity, userId); return true; } catch { return false; }
    }

    public async Task AdvanceAsync(T entity, int userId, string? note = null)
    {
        var wf = entity as Rochas.AI.Manager.Domain.Interfaces.Base.IWorkflowBasedEntity;
        if (wf is null) throw new InvalidOperationException($"{typeof(T).Name} does not implement IWorkflowBasedEntity.");
        var steps = await GetEntityStepsAsync(_entityHashCode);
        var current = steps.FirstOrDefault(s => s.StatusId == wf.StatusId);
        if (current is null) { wf.Approve(userId, "No step configured — auto approved."); return; }
        var nextStatus = current.NextStatusId;
        wf.SetStatus(nextStatus);
        var transition = new WorkFlowTransition
        {
            EntityHashCode = _entityHashCode,
            EntityId = wf.Id,
            EntityStatusFrom = current.StatusId,
            EntityStatusTo = nextStatus,
            Action = "Submit",
            ApproverId = userId,
            Note = note,
            Name = $"transition-{wf.Id}"
        };
        await _transitionRepo.Add(transition);
    }

    public async Task<List<WorkFlowTransition>> GetTransitionsAsync(uint entityHashCode, int entityId)
    {
        var all = await _transitionRepo.Query(new WorkFlowTransition());
        return all.Where(t => t.EntityHashCode == entityHashCode && t.EntityId == entityId).OrderByDescending(t => t.CreatedAt).ToList();
    }
}
