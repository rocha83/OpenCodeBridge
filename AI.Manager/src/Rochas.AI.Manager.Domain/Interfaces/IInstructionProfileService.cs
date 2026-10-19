using Rochas.AI.Manager.Domain.Profiles;

namespace Rochas.AI.Manager.Domain.Interfaces;

public interface IInstructionProfileService
{
    Task<int> CreateAsync(string name, string? description, string? instructionsJson, string? version, CancellationToken ct = default);
    Task<InstructionProfile?> GetAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<InstructionProfile>> ListAsync(CancellationToken ct = default);
    Task ApproveAsync(int id, CancellationToken ct = default);
    Task DeprecateAsync(int id, CancellationToken ct = default);
}
