using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Web.Services;

// Handler da tool "git": leitura para qualquer role; escrita só para roles que
// executam (build/exec). O role "plan" (e role desconhecido/omitido) é somente
// leitura por design — planejar não grava no repo.
public sealed class GitToolHandler : IToolHandler
{
    public string ToolName => "git";

    private static readonly HashSet<string> ReadAllowed = new(StringComparer.Ordinal)
    {
        "status", "log", "diff", "show", "branch", "ls-files", "rev-parse",
    };

    private static readonly HashSet<string> WriteAllowed = new(StringComparer.Ordinal)
    {
        "init", "add", "commit", "rm", "mv", "tag",
    };

    // Roles que executam a tarefa no disco. Plan/orch coordenam e não gravam.
    private static readonly HashSet<string> WriteRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "build", "exec",
    };

    private static readonly string[] Denied =
    {
        "--upload-pack", "--receive-pack", "--exec=", "upload-archive",
        "push", "reset", "clean", "rebase", "merge", "checkout", "filter-branch", "--force",
    };

    public ToolResult Handle(string argumentsJson, ToolContext context)
    {
        JsonObject? obj;
        try { obj = JsonNode.Parse(argumentsJson)?.AsObject(); }
        catch (Exception ex) { return ToolResult.Fail($"argumentos inválidos: {ex.Message}"); }
        if (obj is null) return ToolResult.Fail("argumentos inválidos: objeto JSON esperado");

        string command = obj["command"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(command)) return ToolResult.Fail("command é obrigatório (ex.: 'git status')");
        string repoDir = context.WorkspaceRoot;
        string wantPath = obj["path"]?.GetValue<string>() ?? "";
        if (!string.IsNullOrWhiteSpace(wantPath))
        {
            if (!WorkspaceGuard.TryResolve(context.WorkspaceRoot, wantPath, out string resolved, out string whyPath))
                return ToolResult.Fail(whyPath);
            repoDir = resolved;
        }
        bool canWrite = WriteRoles.Contains(context.Role ?? "");
        var allowed = canWrite
            ? new HashSet<string>(ReadAllowed.Concat(WriteAllowed), StringComparer.Ordinal)
            : ReadAllowed;
        string allowedList = string.Join(", ", allowed);

        string norm = command.Replace('\t', ' ');
        foreach (string bad in Denied)
            if (norm.Contains(bad, StringComparison.Ordinal))
                return ToolResult.Fail($"git negado: '{bad.Trim()}' não é permitido (escrita local via init/add/commit)");

        // -c é aceito só para a identidade do commit (user.name/user.email);
        // qualquer outra configuração inline (-c core.*, upload-pack) fica negada.
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(norm, @"(?:^|\s)-c\s+(\S+)"))
        {
            string kv = m.Groups[1].Value;
            if (!kv.StartsWith("user.name=", StringComparison.Ordinal) && !kv.StartsWith("user.email=", StringComparison.Ordinal))
                return ToolResult.Fail("git -c: só user.name/user.email são aceitos");
        }

        string[] argv = Split(command);
        if (argv.Length > 0 && argv[0] != "git" && allowed.Contains(argv[0]))
            argv = (new[] { "git" }).Concat(argv).ToArray();
        if (argv.Length == 0 || argv[0] != "git")
            return ToolResult.Fail($"command deve começar com 'git' (role '{context.Role}' permite: {allowedList})");
        if (argv.Length < 2 || !allowed.Contains(argv[1]))
            return ToolResult.Fail($"subcomando git não permitido para o role '{context.Role}' (permitidos: {allowedList})");

        try
        {
            var r = ProcessRunner.Run(argv[0], argv[1..], repoDir, context.TimeoutSeconds);
            return r.Success ? ToolResult.Ok(r.Output) : ToolResult.Fail(r.Error ?? "falha");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"falha ao rodar git: {ex.Message.Split('\n')[0]}");
        }
    }

    private static string[] Split(string command)
    {
        var parts = new System.Collections.Generic.List<string>();
        var cur = new System.Text.StringBuilder();
        char quote = '\0';
        foreach (char ch in command.Trim())
        {
            if (quote != '\0')
            {
                if (ch == quote) quote = '\0';
                else cur.Append(ch);
            }
            else if (ch == '"' || ch == '\'')
            {
                quote = ch;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (cur.Length > 0) { parts.Add(cur.ToString()); cur.Clear(); }
            }
            else cur.Append(ch);
        }
        if (cur.Length > 0) parts.Add(cur.ToString());
        return parts.ToArray();
    }
}
