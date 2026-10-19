using Rochas.Data.Specification.Interfaces;
using Rochas.AI.Manager.Application.Abstractions;
using Rochas.AI.Manager.Domain.Entities;

namespace Rochas.AI.Manager.Application.Services;

public sealed class UserService(
    IGenericRepository<User> repo,
    IPersistenceRepository<User> write) : IUserService
{
    private readonly IGenericRepository<User> _repo = repo;
    private readonly IPersistenceRepository<User> _write = write;

    public async Task<int> CreateAsync(string name, string email, string passwordHash, bool isAdmin, CancellationToken ct = default)
    {
        var u = new User { Name = name, Email = email, PasswordHash = passwordHash, IsAdmin = isAdmin, IsActive = true };
        await _write.Add(u);
        return u.Id;
    }

    public async Task<User?> GetAsync(int id, CancellationToken ct = default)
    {
        var list = await _repo.QueryRaw("SELECT * FROM users WHERE id=@id", new Dictionary<string, object> { ["@id"] = id });
        return list.FirstOrDefault();
    }

    public async Task<IReadOnlyList<User>> ListAsync(CancellationToken ct = default)
    {
        var list = await _repo.QueryRaw("SELECT * FROM users", new Dictionary<string, object>());
        return list.OrderBy(u => u.Name).ToList();
    }

    public async Task UpdateAsync(int id, string name, string email, bool isAdmin, bool active, CancellationToken ct = default)
    {
        var u = await GetAsync(id, ct) ?? throw new KeyNotFoundException($"User {id} not found.");
        u.Name = name;
        u.Email = email;
        u.IsAdmin = isAdmin;
        u.IsActive = active;
        await _write.Update(u, new User { Id = id, CreatedAt = default });
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await _write.Remove(new User { Id = id, CreatedAt = default });
    }
}
