using Rochas.AI.Manager.Domain.Entities;

namespace Rochas.AI.Manager.Domain.Interfaces;

public interface IUserService
{
    Task<int> CreateAsync(string name, string email, string passwordHash, bool isAdmin, CancellationToken ct = default);
    Task<User?> GetAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<User>> ListAsync(CancellationToken ct = default);
    Task UpdateAsync(int id, string name, string email, bool isAdmin, bool active, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
}
