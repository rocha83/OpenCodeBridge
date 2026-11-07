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
        // Cadeia `add . && commit -m "x"`: o modelo escreve assim naturalmente e,
        // quando recusada, repete a mesma call ate' travar (medido 2026-11-07 no
        // E-tools-full: 3 tentativas, zero commit). Agora roda em sequencia,
        // parando no primeiro erro. Pipe (`|`) continua recusado: exige shell.
        if (HasChain(command))
        {
            if (command.IndexOf('|') >= 0)
                return ToolResult.Fail("pipe (`|`) não é aceito na tool git: rode uma suboperação por chamada.");
            var parts = SplitChain(command);
            if (parts.Count > 6)
                return ToolResult.Fail($"cadeia longa demais ({parts.Count} suboperações): máximo 6.");
            var outs = new System.Collections.Generic.List<string>();
            foreach (string part in parts)
            {
                var one = new JsonObject { ["command"] = part, ["path"] = wantPath };
                var r = Handle(one.ToJsonString(), context);
                if (!r.Success) return r;
                if (!string.IsNullOrWhiteSpace(r.Output)) outs.Add(r.Output.Trim());
            }
            return ToolResult.Ok(string.Join("\n", outs));
        }
        // Normaliza vocabulario de git antes de executar (`--bare`, `.git` como
        // path, caminho de cache/absoluto do workspace). No-op em call valida;
        // cada regra aplicada vira evento `git_repair` para medicao.
        var repaired = GitArgRepair.Repair(command, wantPath, context.WorkspaceRoot);
        if (repaired.Applied.Count > 0)
        {
            Metrics.CountGitCommandRepair();
            context.Log?.Invoke(new JsonObject
            {
                ["event"] = "git_repair",
                ["applied"] = new JsonArray(repaired.Applied.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray()),
                ["command"] = command,
                ["path"] = wantPath
            });
            command = repaired.Command;
            wantPath = repaired.Path;
        }
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
        // Forma estrita: flag inventada (`--path`, `-f`, `--force-include-all`)
        // nao executa, devolve a forma canonica. Medido no E-tools-full de
        // 2026-11-06: o modelo inventava opcao e a call morria no git.
        if (GitCommandSyntax.Validate(argv, subIdx) is string badForm)
            return ToolResult.Fail(badForm);
        if (argv.Length > 2 && argv[1] == "-C")
        {
            if (!WorkspaceGuard.TryResolve(context.WorkspaceRoot, argv[2], out string cdir, out string whyC, context.ExtraRoots))
                return ToolResult.Fail(whyC);
            repoDir = cdir;
        }
        // Path inventado: em 2026-11-06 o modelo copiou `./notas/hoje.txt` do
        // exemplo do help e TODA call de git rodou num diretorio inexistente
        // ("not a git repository"), sem commit no repo real. Falhar aqui (falha
        // de contrato, nao de git) devolve o repo que existe neste workspace.
        // `init`/`clone` criam o diretorio: nesses dois o path pode nao existir.
        if (!string.IsNullOrWhiteSpace(wantPath) && argv[subIdx] is not ("init" or "clone")
            && !Directory.Exists(repoDir))
            return ToolResult.Fail($"path '{wantPath}' nao existe — o repo deste passo e' \"./\" (raiz do workspace). "
                + "Use `path` \"./\" e uma suboperacao por chamada (`add .`, depois `commit -m \"...\"`).");

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
            if (r.Success) return ToolResult.Ok(r.Output);
            string err = r.Error ?? "falha";
            if (err.Contains("not a git repository", StringComparison.OrdinalIgnoreCase)
                || err.Contains("not in a git directory", StringComparison.OrdinalIgnoreCase)
                || err.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                err += "\nDICA: nesta pasta o `path` e' \"./\" e o fluxo e' `init` -> `add .` -> `commit -m \"<msg>\"` " +
                       "(um comando por chamada, sem `--bare`).";
            if (argv[subIdx] == "commit"
                && (err.Contains("Author identity unknown", StringComparison.OrdinalIgnoreCase)
                    || err.Contains("Please tell me who you are", StringComparison.OrdinalIgnoreCase)
                    || err.Contains("unable to auto-detect email address", StringComparison.OrdinalIgnoreCase)))
                err += "\nDICA: identidade local ausente. Chame a tool git DUAS vezes com o `path` do repo, nesta ordem: " +
                       "`config user.name <nome>` e depois `config user.email <email>`; nao use `&&`.";
            return ToolResult.Fail(err);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"falha ao rodar git: {ex.Message.Split('\n')[0]}");
        }
    }

    // Separador de cadeia FORA de aspas: `add . && commit -m "a; b"` tem `;`
    // dentro da mensagem e continua valido como um unico passo.
    private static bool HasChain(string command)
    {
        char quote = '\0';
        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                continue;
            }
            if (c == '"' || c == '\'') { quote = c; continue; }
            if (c == ';' || c == '|') return true;
            if (c == '&' && i + 1 < command.Length && command[i + 1] == '&') return true;
        }
        return false;
    }

    // Separa `add . && commit -m "a; b"` em suboperacoes, respeitando aspas.
    // `;` e `&&` fora de aspas separam; nada mais e' interpretado (pipe e'
    // recusado antes de chegar aqui).
    private static System.Collections.Generic.List<string> SplitChain(string command)
    {
        var parts = new System.Collections.Generic.List<string>();
        var cur = new System.Text.StringBuilder();
        char quote = '\0';
        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            if (quote != '\0')
            {
                cur.Append(c);
                if (c == quote) quote = '\0';
                continue;
            }
            if (c == '"' || c == '\'') { quote = c; cur.Append(c); continue; }
            if (c == ';' || (c == '&' && i + 1 < command.Length && command[i + 1] == '&'))
            {
                if (c == '&') i++;
                parts.Add(cur.ToString().Trim());
                cur.Clear();
                continue;
            }
            cur.Append(c);
        }
        parts.Add(cur.ToString().Trim());
        return parts.FindAll(p => p.Length > 0);
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
