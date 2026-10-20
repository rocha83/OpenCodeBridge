using System.Threading;
using System.Threading.Tasks;

namespace Rochas.AI.Manager.Domain.Interfaces;

public interface ITrainingRunner
{
    Task<(int ExitCode, string Output, string Error)> RunAsync(string fileName, string arguments, string? workingDirectory = null, CancellationToken ct = default);
}
