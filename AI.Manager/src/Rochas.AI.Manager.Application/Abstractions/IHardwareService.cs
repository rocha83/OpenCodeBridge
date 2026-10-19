namespace Rochas.AI.Manager.Application.Abstractions;

public interface IHardwareService
{
    Task<object> DetectAsync(CancellationToken ct = default);
}
