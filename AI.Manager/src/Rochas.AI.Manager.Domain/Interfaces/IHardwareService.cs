namespace Rochas.AI.Manager.Domain.Interfaces;

public interface IHardwareService
{
    Task<object> DetectAsync(CancellationToken ct = default);
}
