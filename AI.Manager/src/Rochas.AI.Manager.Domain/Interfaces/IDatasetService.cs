using Rochas.AI.Manager.Domain.Datasets;

namespace Rochas.AI.Manager.Domain.Interfaces;

public interface IDatasetService
{
    Task<int> CreateAsync(string name, string format, string? version, string? filePath, string? fileHash, CancellationToken ct = default);
    Task<Dataset?> GetAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<Dataset>> ListAsync(CancellationToken ct = default);
    Task ValidateAsync(int id, bool isValid, CancellationToken ct = default);
    Task<bool> ValidateFormatAsync(int id, string mode, CancellationToken ct = default);
}
