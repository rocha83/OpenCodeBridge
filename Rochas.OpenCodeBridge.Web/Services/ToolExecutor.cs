using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Web.Services
{
    // Tool executor reutilizando lógica do Runner (allowlist, execução, log).
    public sealed class ToolExecutor
    {
        private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
        {
            "ls", "cat", "head", "tail", "echo", "sed", "grep", "find", "wc", "diff",
            "file", "pwd", "date", "git", "dotnet", "python", "python3", "node",
            "npm", "curl", "head", "tail", "wc", "diff", "file",
        };

        private static readonly List<string> SudoExact = new()
        {
            "systemctl restart opencode-bridge.service",
            "systemctl restart opencode-bridge-cpu.service",
            "systemctl status opencode-bridge.service",
            "systemctl status opencode-bridge-cpu.service",
        };

        private readonly string _repoPath;
        private readonly string _logPath;
        private readonly string _verbosity;

        public ToolExecutor(string repoPath, string logPath = "/tmp/tool-executor.log", string verbosity = "normal")
        {
            _repoPath = repoPath;
            _logPath = logPath;
            _verbosity = verbosity;
        }

        public ToolResult Execute(string name, string arguments, int timeoutSeconds = 120)
        {
            if (!Validate(name, arguments, out string why, out string[] argv))
            {
                return new ToolResult { Success = false, Error = why };
            }

            try
            {
                var psi = new ProcessStartInfo(argv[0])
                {
                    WorkingDirectory = _repoPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                };
                foreach (string a in argv.Skip(1)) psi.ArgumentList.Add(a);

                using var proc = Process.Start(psi);
                if (proc is null) return new ToolResult { Success = false, Error = "Processo não iniciou" };

                try { proc.StandardInput.Close(); } catch { }

                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                if (!proc.WaitForExit(120_000))
                {
                    KillTree(proc.Id);
                    proc.WaitForExit(5000);
                    return new ToolResult { Success = false, Error = "TIMEOUT (árvore morta)", Output = stdout.ToString().Trim() };
                }

                string output = stdout.ToString().Trim();
                string err = stderr.ToString().Trim();
                if (err.Length > 0) output += "\n[stderr]\n" + err;

                Audit("exec", output);
                return new ToolResult { Success = true, Output = output };
            }
            catch (Exception ex)
            {
                return new ToolResult { Success = false, Error = ex.Message };
            }
        }

        private bool Validate(string name, string arguments, out string why, out string[] argv)
        {
            why = ""; argv = Array.Empty<string>();

            if (name == "sudo")
            {
                string full = ("sudo " + arguments).Trim();
                if (new[] { "systemctl restart opencode-bridge.service", "systemctl restart opencode-bridge-cpu.service",
                            "systemctl status opencode-bridge.service", "systemctl status opencode-bridge-cpu.service" }.Contains(full))
                {
                    var parts = SplitArgs(arguments);
                    argv = new[] { "sudo" }.Concat(parts).ToArray();
                    return true;
                }
                why = "sudo fora da lista exata"; return false;
            }

            if (!new[] { "ls", "cat", "head", "tail", "echo", "sed", "grep", "find", "wc", "diff", "file", "pwd", "date", "git", "dotnet", "python", "python3", "node", "npm", "curl", "head", "tail", "wc", "diff", "file" }.Contains(name))
            {
                return Fail($"comando '{name}' fora do allowlist", out why, out argv);
            }

            string low = " " + arguments + " ";
            foreach (char c in new[] { ';', '&', '|', '>', '<', '$', '`', '!', '\r', '\n' })
                if (arguments.Contains(c)) return Fail($"metacaractere '{c}' negado", out why, out argv);

            foreach (string bad in new[] { "rm", "kill", "pkill", "killall", "reboot", "shutdown", "halt", "mkfs", "dd", "fdisk", "mount", "systemctl", "service", "crontab", "ssh", "scp", "wget", "chmod", "chown", "nohup", "setsid", "disown", "exec", "eval", "su", "mkfifo" })
            {
                string pattern = $@"[\s\""]{Regex.Escape(bad)}[\s\""]";
                if (Regex.IsMatch(" " + arguments + " ", pattern, RegexOptions.IgnoreCase))
                    return Fail($"token '{bad}' negado", out why, out argv);
            }

            var sudoParts = SplitArgs(arguments);
            argv = new[] { name }.Concat(ExpandGlobs(sudoParts)).ToArray();
            return true;
        }

        private bool Fail(string msg, out string why, out string[] argv) { why = msg; argv = Array.Empty<string>(); return false; }

        private string[] SplitArgs(string s)
        {
            var parts = new List<string>();
            var cur = new StringBuilder();
            char quote = '\0';
            foreach (char c in s.Trim())
            {
                if (quote != '\0') { if (c == quote) quote = '\0'; else cur.Append(c); }
                else if (c is '"' or '\'') quote = c;
                else if (char.IsWhiteSpace(c)) { if (cur.Length > 0) { parts.Add(cur.ToString()); cur.Clear(); } }
                else cur.Append(c);
            }
            if (cur.Length > 0) parts.Add(cur.ToString());
            return parts.ToArray();
        }

        private string[] ExpandGlobs(string[] parts)
        {
            var expanded = new List<string>();
            foreach (string p in parts)
            {
                if (p.IndexOfAny(new[] { '*', '?', '[' }) < 0) { expanded.Add(p); continue; }
                try
                {
                    string dir = Path.GetDirectoryName(p) ?? ".";
                    string absDir = Path.Combine(Directory.GetCurrentDirectory(), dir);
                    string[] hits = Directory.GetFileSystemEntries(absDir, Path.GetFileName(p));
                    if (hits.Length == 0) expanded.Add(p);
                    else expanded.AddRange(hits.OrderBy(h => h).Select(h => Path.GetRelativePath(Directory.GetCurrentDirectory(), h)));
                }
                catch { expanded.Add(p); }
            }
            return expanded.ToArray();
        }

        private void Audit(string evt, string output)
        {
            try
            {
                File.AppendAllText(_logPath, new JsonObject
                {
                    ["event"] = "exec",
                    ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ["output_bytes"] = output?.Length ?? 0,
                    ["output_head"] = Truncate(output, 1000),
                }.ToJsonString() + "\n");
            }
            catch { }
        }

        private void KillTree(int pid)
        {
            try { Process.GetProcessById(pid).Kill(true); } catch { }
            try { RunInternal("pkill", "-9", "-P", pid.ToString()); } catch { }
            try { RunInternal("kill", "-9", pid.ToString()); } catch { }
        }

        static void RunInternal(string exe, params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var proc = Process.Start(psi);
                proc?.WaitForExit(5000);
            }
            catch { }
        }

        private string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "\n[truncado]";
    }

// // public class ToolResult
// {
// // public bool Success { get; init; }
// public string Output { get: init; } = "";
// public string Error { get: init; } = "";
// }
    public class ToolResult
    {
        public bool Success { get; set; }
        public string Output { get; set; } = "";
        public string Error { get; set; } = "";
    }
}
