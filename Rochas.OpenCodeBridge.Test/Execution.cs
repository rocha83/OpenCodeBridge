// ===========================================================================
//  Execution — primitiva de execucao de processos do TestCircuitDriver.
//  Portavel (Windows/Linux), sem shell, sem P/Invoke, com timeout e kill
//  de arvore. O TestCircuitDriver usa Execution; o WarTemplate so descreve
//  os CircuitCommand.
// ===========================================================================

using System.Diagnostics;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>Executor de processos portavel (sem shell, sem P/Invoke).</summary>
internal static class Execution
{
    public sealed record Result(int Code, string Output, long Seconds);

    public static Task<Result> RunAsync(
        CircuitCommand command, int timeoutSeconds, CancellationToken ct = default)
        => Task.Run(() => Run(command, timeoutSeconds, ct), ct);

    public static Result Run(
        CircuitCommand command, int timeoutSeconds, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var (file, args) = CommandLine(command);
            var psi = new ProcessStartInfo(file)
            {
                WorkingDirectory = command.WorkingDirectory ?? Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (string a in args) psi.ArgumentList.Add(a);
            if (command.Environment is not null)
                foreach (var kv in command.Environment)
                    psi.Environment[kv.Key] = kv.Value;

            using var process = new Process { StartInfo = psi };
            if (!process.Start())
                return new Result(127, $"nao iniciou: {command.FileName}", 0);

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            int code;
            using var timeout = new CancellationTokenSource();
            if (timeoutSeconds > 0)
                timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                process.WaitForExitAsync(linked.Token).GetAwaiter().GetResult();
                code = process.ExitCode;
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch { }
                try { process.WaitForExit(5000); }
                catch { }
                code = ct.IsCancellationRequested ? 130 : 124;
            }

            string output;
            try { output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult(); }
            catch { output = ""; }
            sw.Stop();
            return new Result(code, output, (long)sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new Result(127, ex.Message, (long)sw.Elapsed.TotalSeconds);
        }
    }

    private static (string File, IReadOnlyList<string> Args) CommandLine(CircuitCommand command)
    {
        string file = Resolve(command.FileName);
        if (OperatingSystem.IsWindows()
            && (file.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)))
        {
            var wrapped = new List<string> { "/c", file };
            wrapped.AddRange(command.Args);
            return ("cmd.exe", wrapped);
        }
        return (file, command.Args);
    }

    /// <summary>Resolve executavel no PATH (considera PATHEXT no Windows).</summary>
    public static string Resolve(string file)
    {
        if (string.IsNullOrWhiteSpace(file)) return file;
        if (Path.IsPathRooted(file)
            || file.Contains(Path.DirectorySeparatorChar)
            || file.Contains(Path.AltDirectorySeparatorChar))
            return file;

        string? found = FindOnPath(file);
        return found ?? file;
    }

    public static string? FindOnPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        var names = new List<string> { name };
        if (OperatingSystem.IsWindows() && !Path.HasExtension(name))
        {
            string exts = Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT";
            foreach (string ext in exts.Split(';', StringSplitOptions.RemoveEmptyEntries))
                names.Add(name + ext.Trim());
        }

        foreach (string dir in path.Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string candidateName in names)
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim('"'), candidateName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                    // Entrada de PATH invalida.
                }
            }
        }
        return null;
    }

    /// <summary>PIDs vivos com o nome de processo informado (sem /proc).</summary>
    public static List<int> FindProcessIds(string processName)
    {
        var pids = new List<int>();
        try
        {
            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    if (process.Id != Environment.ProcessId
                        && process.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase))
                        pids.Add(process.Id);
                }
                catch
                {
                    // Processo morreu entre a listagem e a leitura.
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            // Sem permissao para enumerar processos: evidencia vazia.
        }
        return pids;
    }
}
