using Rochas.AI.Manager.Domain.Interfaces;

namespace Rochas.AI.Manager.Application.Services;

public sealed class HardwareService : IHardwareService
{
    public Task<object> DetectAsync(CancellationToken ct = default)
    {
        var info = new
        {
            MachineName = Environment.MachineName,
            OS = Environment.OSVersion.ToString(),
            ProcessorCount = Environment.ProcessorCount,
            Is64Bit = Environment.Is64BitOperatingSystem,
            WorkingSetMb = Environment.WorkingSet / (1024 * 1024)
        };
        return Task.FromResult<object>(info);
    }
}
