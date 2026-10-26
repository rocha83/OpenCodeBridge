using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Core.Tools;

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
        "init", "add", "commit", "rm", "mv", "tag", "config",
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
            if (!WorkspaceGuard.TryResolve(context.WorkspaceRoot, wantPath, out string resolved, out string whyPath, context.ExtraRoots))
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
        // Flags globais (-C <dir>, -c chave=valor) vem antes do subcomando; o
        // subcomando real e o primeiro token que nao e flag.
        int subIdx = 1;
        while (subIdx < argv.Length && (argv[subIdx] == "-C" || argv[subIdx] == "-c"))
            subIdx += 2;
        if (subIdx >= argv.Length || !allowed.Contains(argv[subIdx]))
            return ToolResult.Fail($"subcomando git não permitido para o role '{context.Role}' (permitidos: {allowedList})");
        if (argv.Length > 2 && argv[1] == "-C")
        {
            if (!WorkspaceGuard.TryResolve(context.WorkspaceRoot, argv[2], out string cdir, out string whyC, context.ExtraRoots))
                return ToolResult.Fail(whyC);
            repoDir = cdir;
        }

        try
        {
            // A bridge roda como root e o workspace da tarefa pode ter outro dono
            // (ex.: dir criado pelo teste/usuário). Sem safe.directory o git nega
            // o repo ("dubious ownership"/"not in a git directory"); o caminho é
            // exato (nunca `*`) e só vale para o repo já resolvido.
            string[] runArgs = argv[1..];
            if (argv[subIdx] != "init")
                runArgs = (new[] { "-c", "safe.directory=" + Path.GetFullPath(repoDir) }).Concat(runArgs).ToArray();
            var r = ProcessRunner.Run(argv[0], runArgs, repoDir, context.TimeoutSeconds);
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
