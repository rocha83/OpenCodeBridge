using System.Diagnostics;
using System.Text;

namespace Rochas.OpenCodeBridge.Web.Services;

// Execução de processos com timeout e morte da árvore (infra compartilhada).
internal static class ProcessRunner
{
    public static ToolResult Run(string exe, string[] args, string workingDirectory, int timeoutSeconds)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc is null) return ToolResult.Fail("Processo não iniciou");

            try { proc.StandardInput.Close(); } catch { }

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            if (!proc.WaitForExit(Math.Max(1, timeoutSeconds) * 1000))
            {
                KillTree(proc.Id);
                proc.WaitForExit(5000);
                return ToolResult.Fail("TIMEOUT (árvore morta)");
            }
            // Drena handlers assíncronos: o WaitForExit com timeout retorna no fim do
            // processo, mas eventos OutputDataReceived ainda podem estar na fila.
            proc.WaitForExit();

            string output = stdout.ToString().Trim();
            string err = stderr.ToString().Trim();
            if (err.Length > 0) output += "\n[stderr]\n" + err;
            return ToolResult.Ok(output);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(ex.Message);
        }
    }

    private static void KillTree(int pid)
    {
        try { Process.GetProcessById(pid).Kill(true); } catch { }
        try { RunInternal("pkill", "-9", "-P", pid.ToString()); } catch { }
        try { RunInternal("kill", "-9", pid.ToString()); } catch { }
    }

    private static void RunInternal(string exe, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string a in args) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
        }
        catch { }
    }
}
