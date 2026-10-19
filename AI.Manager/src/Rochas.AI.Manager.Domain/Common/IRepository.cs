namespace Rochas.AI.Manager.Domain.Common;

/// <summary>
/// Contrato generico de repositorio (DDD). Uma entidade por agregado.
/// </summary>
public interface IRepository<TEntity, TId> where TEntity : class
{
    Task<TEntity?> GetByIdAsync(TId id, CancellationToken ct = default);
    Task<IReadOnlyList<TEntity>> QueryAsync(TEntity filter, CancellationToken ct = default);
    Task AddAsync(TEntity entity, CancellationToken ct = default);
    void Update(TEntity entity);
    void Remove(TEntity entity);
}
