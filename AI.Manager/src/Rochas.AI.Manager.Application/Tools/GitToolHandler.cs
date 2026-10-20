using System.Text.Json.Nodes;

namespace Rochas.AI.Manager.Application.Tools;

// Handler da tool "git": somente leitura (status/log/diff/show).
// Sem commit/push/checkout: nada que escreva no repo.
public sealed class GitToolHandler : IToolHandler
{
    public string ToolName => "git";

    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "status", "log", "diff", "show", "branch", "ls-files", "rev-parse",
    };

    private static readonly string[] Denied =
    {
        "-c ", "--upload-pack", "--receive-pack", "--exec=", "upload-archive",
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
        foreach (string bad in Denied)
            if (command.Contains(bad, StringComparison.Ordinal))
                return ToolResult.Fail($"git somente-leitura: '{bad.Trim()}' negado");

        string[] argv = Split(command);
        if (argv.Length > 0 && argv[0] != "git" && Allowed.Contains(argv[0]))
            argv = (new[] { "git" }).Concat(argv).ToArray();
        if (argv.Length == 0 || argv[0] != "git")
            return ToolResult.Fail("command deve começar com 'git' (leitura aqui; escrita via shell: 'cd <dir> && git add/commit ...')");
        if (argv.Length < 2 || !Allowed.Contains(argv[1]))
            return ToolResult.Fail($"subcomando git permitido: {string.Join(", ", Allowed)} (escrita via shell)");

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
