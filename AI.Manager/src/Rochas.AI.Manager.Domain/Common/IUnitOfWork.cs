namespace Rochas.AI.Manager.Domain.Common;

/// <summary>
/// Unidade de trabalho. Implementacao via Rochas.DapperRepository (Dapper).
/// </summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
