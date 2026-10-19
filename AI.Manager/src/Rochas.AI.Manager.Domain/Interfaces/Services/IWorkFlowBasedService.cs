using System.Threading.Tasks;
using Rochas.AI.Manager.Domain.Entities.Management;

namespace Rochas.AI.Manager.Domain.Interfaces.Services;

public interface IWorkFlowBasedService<T> where T : class
{
    Task<System.Collections.Generic.List<WorkflowStep>> GetEntityStepsAsync(uint entityHashCode);
    Task<bool> CanAdvanceAsync(T entity, int userId);
    Task AdvanceAsync(T entity, int userId, string? note = null);
    Task<System.Collections.Generic.List<WorkFlowTransition>> GetTransitionsAsync(uint entityHashCode, int entityId);
}
