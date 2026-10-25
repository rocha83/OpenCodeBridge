// Testes unitários do despachante de tools (handlers em workspace temporário).
// Execução: dotnet run -c Release --project Rochas.OpenCodeBridge.Web.Test -- --web http://127.0.0.1:4130

using System.IO;
using Rochas.OpenCodeBridge.Web.Services;
using Rochas.OpenCodeBridge.Core.Tools;

namespace Rochas.OpenCodeBridge.Web.Test.Unit;

internal static class ToolExecutorTests
{
    private static int Failures;

    internal static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), $"tools-{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var exec = new ToolExecutor(root, Path.Combine(root, "audit.log"));
            var ctx = new ToolContext { WorkspaceRoot = root, TimeoutSeconds = 30 };

            ShellEcho(exec);
            ShellDenied(exec);
            ShellMetacharDenied(exec);
            UnknownTool(exec);
            BashAlias(exec);
            ReadWriteEditRoundtrip(exec);
            TraversalDenied(exec, ctx);
            TrailingSlashRoot();
            GrepFinds(exec);
            GlobFinds(exec);
            EdgeCases(exec);
            AgentFlags(exec);
            PlanToolsFilter();
            ShellInspectorBlocks();
            ShellProcExecGates(exec);
            ScripterVsExec(exec);
            GitRepoPath(exec, ctx);
            GlobAbsoluteOutside();
            CpuEtaTps();
            ScriptEtaParse();
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        System.Console.WriteLine($"=== ToolExecutor Unit: {Total - Failures}/{Total} PASS, {Failures} FAIL ===");
        return Failures;
    }

    private static int Total;

    private static void Check(bool ok, string name)
    {
        Total++;
        System.Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) Failures++;
    }

    internal static void AgentFlags(ToolExecutor exec)
    {
        var none = new Models.Agent { Name = "n", Mode = "build", Tools = "" };
        Check(!Services.ToolDefinitions.Allowed(none, "shell") && !Services.ToolDefinitions.Allowed(none, "read"),
            "U-tool-flags-empty-deny");
        var disp = new Models.Agent { Name = "d", Mode = "build", Tools = "read,display" };
        Check(Services.ToolDefinitions.Allowed(disp, "shell") && Services.ToolDefinitions.Allowed(disp, "read")
            && !Services.ToolDefinitions.Allowed(disp, "write"), "U-tool-flags-display-shell");
        var echo = exec.Execute("shell", "{\"command\": \"echo oi\"}", 30, "build", disp);
        Check(echo.Success, "U-tool-flags-display-echo-ok");
        var bad = exec.Execute("shell", "{\"command\": \"ls -la\"}", 30, "build", disp);
        Check(!bad.Success && bad.Error.Contains("exibição"), "U-tool-flags-display-ls-deny");
        var plan = new Models.Agent { Name = "p", Mode = "plan", Tools = "shell,read" };
        var planSub = new Models.Agent { Name = "ps", Mode = "plan", Tools = "shell,read,subagent" };
        var buildSub = new Models.Agent { Name = "bs", Mode = "build", Tools = "shell,subagent" };
        Check(Services.ToolDefinitions.Allowed(planSub, "subagent") && Services.ToolDefinitions.Allowed(buildSub, "subagent")
            && !Services.ToolDefinitions.Allowed(plan, "subagent") && !Services.ToolDefinitions.Allowed(none, "subagent"),
            "U-tool-flags-subagent-checkbox");
        // Escrita anunciada, permissão no CSV: plan sem write/edit não anuncia;
        // build com write/edit anuncia (backstop confirma o modo).
        var buildWrite = new Models.Agent { Name = "bw", Mode = "build", Tools = "shell,write,edit" };
        Check(!Services.ToolDefinitions.Allowed(plan, "write") && !Services.ToolDefinitions.Allowed(plan, "edit")
            && Services.ToolDefinitions.Allowed(buildWrite, "write") && Services.ToolDefinitions.Allowed(buildWrite, "edit"),
            "U-tool-flags-write-csv");
        // Catálogo cheio: `*` anuncia tudo; `-tool` nega (denylist). O CSV antigo
        // (lista de permitidas) continua válido para as linhas já gravadas.
        var full = new Models.Agent { Name = "f", Mode = "build", Tools = "*" };
        var fullMinus = new Models.Agent { Name = "fm", Mode = "build", Tools = "*,-write,-edit" };
        Check(Services.ToolDefinitions.Allowed(full, "write") && Services.ToolDefinitions.Allowed(full, "shell")
            && Services.ToolDefinitions.Allowed(full, "question") && Services.ToolDefinitions.Allowed(full, "skill"),
            "U-tool-flags-catalog-full");
        Check(!Services.ToolDefinitions.Allowed(fullMinus, "write") && !Services.ToolDefinitions.Allowed(fullMinus, "edit")
            && Services.ToolDefinitions.Allowed(fullMinus, "shell") && Services.ToolDefinitions.Allowed(fullMinus, "read"),
            "U-tool-flags-denylist");
    }

    private static void PlanToolsFilter()
    {
        var plan = ToolDefinitions.GetTools("plan");
        var names = plan.Select(t => (t as System.Text.Json.Nodes.JsonObject)?["function"]?["name"]?.GetValue<string>()).ToList();
        Check(plan.Count == 11 && !names.Contains("execute") && names.Contains("shell") && names.Contains("write") && names.Contains("edit")
            && names.Contains("read") && names.Contains("grep") && names.Contains("glob"), "U-exec-plan-tools");
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"plan-{System.Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(root);
        try
        {
            var exec = new ToolExecutor(root, System.IO.Path.Combine(root, "audit.log"));
            var nav = exec.Execute("shell", "{\"command\": \"echo x\"}", 30, "plan");
            var denied = exec.Execute("write", "{\"path\": \"a.txt\", \"content\": \"x\"}", 30, "plan");
            var allowed = exec.Execute("glob", "{\"pattern\": \"*.cs\"}", 30, "plan");
            Check(nav.Success && !denied.Success && denied.Error.Contains("Não permitido") && allowed.Success, "U-exec-plan-backstop");
        }
        finally
        {
            try { System.IO.Directory.Delete(root, true); } catch { }
        }
    }

    private static void ShellInspectorBlocks()
    {
        // Negação uniforme: direto, cadeia e payload bash -c (furo antigo).
        Check(ShellInspector.Validate("rm -rf /tmp/x") is not null, "U-insp-rm");
        Check(ShellInspector.Validate("sh -c \"rm -rf /tmp/x\"") is not null, "U-insp-bash-rm");
        Check(ShellInspector.Validate("echo ok; kill 123") is not null, "U-insp-chain-kill");
        Check(ShellInspector.Validate("mv a b") is not null, "U-insp-mv");
        Check(ShellInspector.Validate("rmdir sub") is not null, "U-insp-rmdir");
        Check(ShellInspector.Validate("echo x > /dev/sda") is not null, "U-insp-dev");
        Check(ShellInspector.Validate("find /tmp -name \"x*\" 2>/dev/null") is null, "U-insp-devnull-ok");
        Check(ShellInspector.Validate("top") is not null, "U-insp-top-interactive");
        // Liberados: printf/cp (só cp entre os destrutivos), ps, top batch.
        Check(ShellInspector.Validate("printf '%s\\n' oi") is null, "U-insp-printf-ok");
        Check(ShellInspector.Validate("cp a b") is null, "U-insp-cp-ok");
        Check(ShellInspector.Validate("ps aux") is null, "U-insp-ps-ok");
        Check(ShellInspector.Validate("top -b -n 1") is null, "U-insp-top-batch-ok");
        Check(ShellInspector.Validate("bash -n run.sh") is null, "U-insp-bashn-ok");
        // Sem falso-positivo em palavra contida.
        Check(ShellInspector.Validate("echo firmware ok") is null, "U-insp-substring-ok");
        Check(ShellInspector.Classify("bash run.sh") == ShellInspector.ShellClass.Execution, "U-insp-class-exec");
        Check(ShellInspector.Classify("bash -n run.sh") == ShellInspector.ShellClass.Validation, "U-insp-class-validation");
        Check(ShellInspector.Classify("ps aux") == ShellInspector.ShellClass.Proc, "U-insp-class-proc");
        Check(ShellInspector.Classify("ls -la") == ShellInspector.ShellClass.Other, "U-insp-class-other");
    }

    private static void ShellProcExecGates(ToolExecutor exec)
    {
        var shellOnly = new Models.Agent { Name = "so", Mode = "build", Tools = "shell" };
        var full = new Models.Agent { Name = "full", Mode = "build", Tools = "shell,proc,exec" };
        var noExec = exec.Execute("shell", "{\"command\": \"bash run.sh\"}", 30, "build", shellOnly);
        Check(!noExec.Success && noExec.Error.Contains("exec"), "U-gate-exec");
        var noProc = exec.Execute("shell", "{\"command\": \"ps aux\"}", 30, "build", shellOnly);
        Check(!noProc.Success && noProc.Error.Contains("proc"), "U-gate-proc");
        var okPrintf = exec.Execute("shell", "{\"command\": \"printf oi\"}", 30, "build", shellOnly);
        Check(okPrintf.Success, "U-gate-printf-ok");
        var okPs = exec.Execute("shell", "{\"command\": \"ps aux\"}", 30, "build", full);
        Check(okPs.Success, "U-gate-ps-ok");
        var okBashN = exec.Execute("shell", "{\"command\": \"bash -n run.sh\"}", 30, "build", shellOnly);
        Check(okBashN.Success || okBashN.Error.Contains("run.sh"), "U-gate-bashn-validation");
    }

    // Scripter cria+valida ('bash -n') sem 'exec'; Exec valida e executa.
    private static void ScripterVsExec(ToolExecutor exec)
    {
        var scripter = new Models.Agent { Name = "scripter", Mode = "build",
            Tools = "glob,read,grep,git,fetch,display,write,edit,proc,shell,install" };
        var execRole = new Models.Agent { Name = "exec", Mode = "build",
            Tools = "glob,read,grep,git,fetch,display,write,edit,proc,shell,exec" };
        exec.Execute("write", "{\"path\": \"run.sh\", \"content\": \"echo exec-ok\"}");
        var v = exec.Execute("shell", "{\"command\": \"bash -n run.sh\"}", 30, "build", scripter);
        Check(v.Success || v.Error.Contains("run.sh"), "U-scripter-bashn-validation");
        var deny = exec.Execute("shell", "{\"command\": \"bash run.sh\"}", 30, "build", scripter);
        Check(!deny.Success && deny.Error.Contains("exec"), "U-scripter-no-exec");
        var run = exec.Execute("shell", "{\"command\": \"bash run.sh\"}", 30, "build", execRole);
        Check(run.Success && run.Output.Contains("exec-ok"), "U-exec-runs-script");
        var runC = exec.Execute("shell", "{\"command\": \"bash -c \\\"echo via-c\\\"\"}", 30, "build", execRole);
        Check(runC.Success && runC.Output.Contains("via-c"), "U-exec-bash-c");
        var denyC = exec.Execute("shell", "{\"command\": \"bash -c \\\"echo via-c\\\"\"}", 30, "build", scripter);
        Check(!denyC.Success && denyC.Error.Contains("exec"), "U-scripter-bash-c-no-exec");
    }

    // Primeira base gravável (sonda com escrita real; volume externo vive travando).
    private static string WritableBase(params string[] candidates)
    {
        foreach (var c in candidates)
        {
            try
            {
                string probe = System.IO.Path.Combine(c, $"probe-{System.Guid.NewGuid():N}");
                System.IO.Directory.CreateDirectory(probe);
                System.IO.Directory.Delete(probe);
                return c;
            }
            catch { }
        }
        return candidates[^1];
    }

    private static void GlobAbsoluteOutside()
    {
        // Fora do workspace: absoluto (sem cadeia "../../.."); dentro: relativo.
        // Base no volume de dados (gravável); cai p/ temp se o volume travar.
        string wsbase = WritableBase("/media/mint/3686C649614854E6", System.IO.Path.GetTempPath());
        string ws = System.IO.Path.Combine(wsbase, $"globws-{System.Guid.NewGuid():N}");
        string other = System.IO.Path.Combine(wsbase, $"globout-{System.Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(ws);
        System.IO.Directory.CreateDirectory(other);
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(other, "alvo.txt"), "x");
            var h = new GlobToolHandler();
            var r = h.Handle("{\"pattern\": \"*.txt\", \"path\": \"" + other + "\"}",
                new ToolContext { WorkspaceRoot = ws, TimeoutSeconds = 30 });
            // Absoluto fora do workspace só resolve sob /media ou /mnt
            // (WorkspaceGuard); fora dali o jail nega — ambos comportamentos ok.
            bool extra = other.StartsWith("/media/", StringComparison.Ordinal)
                || other.StartsWith("/mnt/", StringComparison.Ordinal);
            Check(extra ? (r.Success && r.Output.StartsWith(other))
                : (!r.Success && r.Error.Contains("fora do workspace")), "U-glob-absolute-outside");
            var r2 = h.Handle("{\"pattern\": \"*.txt\", \"path\": \".\"}",
                new ToolContext { WorkspaceRoot = other, TimeoutSeconds = 30 });
            Check(r2.Success && !r2.Output.StartsWith("/"), "U-glob-relative-inside");
        }
        finally
        {
            try { System.IO.Directory.Delete(ws, true); } catch { }
            try { System.IO.Directory.Delete(other, true); } catch { }
        }
    }

    private static void GitRepoPath(ToolExecutor exec, ToolContext ctx)
    {
        // Tool git com path: roda no repo certo; escrita segue o ROLE (plan le,
        // build grava) — a regra de escrita mora no handler, nao no texto.
        string sub = System.IO.Path.Combine(ctx.WorkspaceRoot, "repo-path-test");
        System.IO.Directory.CreateDirectory(sub);
        var init = exec.Execute("shell", "{\"command\": \"cd repo-path-test && git init\"}", 30);
        Check(init.Success, "U-git-path-init");
        var h = new GitToolHandler();
        var ctxPlan = new ToolContext { WorkspaceRoot = ctx.WorkspaceRoot, TimeoutSeconds = 30, Role = "plan" };
        var st = h.Handle("{\"command\": \"status\", \"path\": \"repo-path-test\"}", ctxPlan);
        Check(st.Success, "U-git-path-status");
        var add = h.Handle("{\"command\": \"add x\", \"path\": \"repo-path-test\"}", ctxPlan);
        Check(!add.Success && add.Error.Contains("role 'plan'"), "U-git-write-hint");
        System.IO.File.WriteAllText(System.IO.Path.Combine(sub, "x.txt"), "x");
        var ctxBuild = new ToolContext { WorkspaceRoot = ctx.WorkspaceRoot, TimeoutSeconds = 30, Role = "build" };
        var addOk = h.Handle("{\"command\": \"add .\", \"path\": \"repo-path-test\"}", ctxBuild);
        Check(addOk.Success, "U-git-build-writes");
    }

    // ETA estimada pelo modelo no cabeçalho do script; ausente = vazio.
    private static void ScriptEtaParse()
    {
        Check(Services.ScriptEta.ParseMinutes("#!/bin/bash\n# ETA: ~12 min\necho oi") == 12, "U-eta-header");
        Check(Services.ScriptEta.ParseMinutes("# ETA: 2,5 min") == 2.5, "U-eta-decimal");
        Check(Services.ScriptEta.ParseMinutes("echo sem header") is null, "U-eta-missing");
        Check(Services.ScriptEta.ParseMinutes("# ETA: ?") is null, "U-eta-unknown");
    }
    private static void CpuEtaTps()
    {
        var cpu = new Models.Agent { BridgeUrl = "http://127.0.0.1:4125", Model = "Gemma4-E4B-UD-Q3-build" };
        var gpu = new Models.Agent { BridgeUrl = "http://127.0.0.1:4124", Model = "Gemma4-E4B-Q4-build" };
        var probed = new Models.Agent { BridgeUrl = "http://127.0.0.1:4125", Model = "cpu", MeasuredTps = 3.1 };
        Check(Services.ExecutorTps.ResolveTps(cpu, 3) == 3, "U-exec-cpu-tps");
        Check(Services.ExecutorTps.ResolveTps(gpu, 2.5) == 30, "U-exec-gpu-tps");
        Check(Services.ExecutorTps.ResolveTps(probed, 3) == 3.1, "U-exec-probed-tps");
    }

    private static void ShellEcho(ToolExecutor exec)
    {
        var r = exec.Execute("shell", "{\"command\": \"echo ola\"}");
        Check(r.Success && r.Output == "ola", "U-tool-shell-echo");
    }

    private static void ShellDenied(ToolExecutor exec)
    {
        var r = exec.Execute("shell", "{\"command\": \"rm -rf /tmp/x\"}");
        Check(!r.Success && r.Error.Contains("negado"), "U-tool-shell-denied");
    }

    private static void ShellMetacharDenied(ToolExecutor exec)
    {
        var h = new Rochas.OpenCodeBridge.Web.Services.ShellToolHandler();
        var ctx = new ToolContext { WorkspaceRoot = Path.GetTempPath(), TimeoutSeconds = 30 };
        // Cadeia com ';' executa em sequência (set -e); pipe continua negado.
        var r = h.Handle("echo a; echo b", ctx);
        Check(r.Success && r.Output.Contains("a") && r.Output.Contains("b"), "U-tool-shell-chain");
        // ';' dentro de aspas (ex.: script sed) não é separador nem motivo de nega.
        var q = h.Handle("echo \"a;b\"", ctx);
        Check(q.Success && q.Output.Contains("a;b"), "U-tool-shell-quoted");
        var p = h.Handle("echo x | cat", ctx);
        Check(p.Success && p.Output.Contains("x"), "U-tool-shell-pipe-ok");
    }

    private static void UnknownTool(ToolExecutor exec)
    {
        var r = exec.Execute("task", "{\"agent\": \"general\"}");
        Check(!r.Success && r.Error.Contains("Não permitido"), "U-tool-unknown");
    }

    private static void BashAlias(ToolExecutor exec)
    {
        var r = exec.Execute("bash", "{\"command\": \"echo via-bash\"}");
        Check(r.Success && r.Output == "via-bash", "U-tool-bash-alias");
    }

    private static void ReadWriteEditRoundtrip(ToolExecutor exec)
    {
        var w = exec.Execute("write", "{\"path\": \"a.txt\", \"content\": \"linha1\"}");
        var r = exec.Execute("read", "{\"path\": \"a.txt\"}");
        Check(w.Success && r.Success && r.Output == "linha1", "U-tool-write-read");

        var e = exec.Execute("edit", "{\"path\": \"a.txt\", \"oldString\": \"linha1\", \"newString\": \"linha2\"}");
        var r2 = exec.Execute("read", "{\"path\": \"a.txt\"}");
        Check(e.Success && r2.Output == "linha2", "U-tool-edit");

        var dup = exec.Execute("write", "{\"path\": \"b.txt\", \"content\": \"x x x\"}");
        var amb = exec.Execute("edit", "{\"path\": \"b.txt\", \"oldString\": \"x\", \"newString\": \"y\"}");
        Check(dup.Success && !amb.Success && amb.Error.Contains("replaceAll"), "U-tool-edit-ambiguous");
    }

    private static void TraversalDenied(ToolExecutor exec, ToolContext ctx)
    {
        var r = exec.Execute("read", "{\"path\": \"../fora.txt\"}");
        Check(!r.Success && r.Error.Contains("workspace"), "U-tool-traversal");
    }

    private static void TrailingSlashRoot()
    {
        // Regressão: AppContext.BaseDirectory termina com separador; nada dentro
        // do workspace pode ser negado por causa disso.
        string root = Path.Combine(Path.GetTempPath(), $"tools-slash-{System.Guid.NewGuid():N}") + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        try
        {
            var exec = new ToolExecutor(root, Path.Combine(root, "audit.log"));
            var w = exec.Execute("write", "{\"path\": \"./dentro.txt\", \"content\": \"ok\"}");
            var g = exec.Execute("glob", "{\"pattern\": \"*.txt\", \"path\": \".\"}");
            Check(w.Success && g.Success, "U-tool-trailing-slash");
        }
        finally
        {
            try { Directory.Delete(root.TrimEnd(Path.DirectorySeparatorChar), true); } catch { }
        }
    }

    private static void GrepFinds(ToolExecutor exec)
    {
        exec.Execute("write", "{\"path\": \"g.txt\", \"content\": \"alpha\\nbeta marcador\\ngamma\"}");
        var r = exec.Execute("grep", "{\"pattern\": \"marcador\"}");
        Check(r.Success && r.Output.Contains("g.txt:2"), "U-tool-grep");
    }

    private static void GlobFinds(ToolExecutor exec)
    {
        var r = exec.Execute("glob", "{\"pattern\": \"*.txt\"}");
        Check(r.Success && r.Output.Contains("g.txt"), "U-tool-glob");
        var r2 = exec.Execute("glob", "{\"pattern\": \"**/*.txt\"}");
        Check(r2.Success && r2.Output.Contains("g.txt"), "U-tool-glob-starstar");
    }

    // Casos de borda (cobertura dos handlers).
    internal static void EdgeCases(ToolExecutor exec)
    {
        var tools = ToolDefinitions.GetTools();
        bool hasTask = tools.Any(t => (t as System.Text.Json.Nodes.JsonObject)?["function"]?["name"]?.GetValue<string>() == "task");
        var names = tools.Select(t => (t as System.Text.Json.Nodes.JsonObject)?["function"]?["name"]?.GetValue<string>()).ToList();
        Check(tools.Count == 11 && !hasTask && !names.Contains("execute") && names.Contains("shell") && names.Contains("subagent") && names.Contains("glob") && names.Contains("write") && names.Contains("edit"), "U-tool-definitions");        var missing = exec.Execute("read", "{\"path\": \"nao-existe.txt\"}");
        Check(!missing.Success && missing.Error.Contains("não encontrado"), "U-tool-read-missing");

        exec.Execute("write", "{\"path\": \"sub/ninho.txt\", \"content\": \"l1\\nl2\\nl3\\nl4\"}");
        var slice = exec.Execute("read", "{\"path\": \"sub/ninho.txt\", \"offset\": 1, \"limit\": 2}");
        Check(slice.Success && slice.Output == "l2\nl3", "U-tool-read-slice");

        var empty = exec.Execute("grep", "{\"pattern\": \"zzzz-nao-existe\"}");
        Check(empty.Success && empty.Output.Contains("sem resultados"), "U-tool-grep-empty");

        var badre = exec.Execute("grep", "{\"pattern\": \"([\"}");
        Check(!badre.Success && badre.Error.Contains("regex"), "U-tool-grep-badregex");

        var noglob = exec.Execute("glob", "{\"pattern\": \"*.qqq-sem-match\"}");
        Check(noglob.Success && noglob.Output.Contains("sem resultados"), "U-tool-glob-empty");

        exec.Execute("write", "{\"path\": \"m.txt\", \"content\": \"a a a\"}");
        var all = exec.Execute("edit", "{\"path\": \"m.txt\", \"oldString\": \"a\", \"newString\": \"b\", \"replaceAll\": true}");
        var after = exec.Execute("read", "{\"path\": \"m.txt\"}");
        Check(all.Success && after.Output == "b b b", "U-tool-edit-replaceall");

        var sudo = exec.Execute("shell", "{\"command\": \"sudo ls /root\"}");
        Check(!sudo.Success && sudo.Error.Contains("lista exata"), "U-tool-sudo-denied");

        var abs = exec.Execute("read", "{\"path\": \"/etc/hostname\"}");
        Check(!abs.Success && abs.Error.Contains("workspace"), "U-tool-traversal-abs");

        var noMatch = exec.Execute("edit", "{\"path\": \"m.txt\", \"oldString\": \"qqq-sem-match\", \"newString\": \"z\"}");
        Check(!noMatch.Success && noMatch.Error.Contains("não encontrado"), "U-tool-edit-notfound");

        var slow = exec.Execute("shell", "{\"command\": \"python3 -m http.server 18924\"}", 1);
        Check(!slow.Success && slow.Error.Contains("TIMEOUT"), "U-tool-shell-timeout");
    }
}
