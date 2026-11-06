using Rochas.OpenCodeBridge.Core.Tools;
using static Rochas.OpenCodeBridge.Core.Tools.ShellGate;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>
/// Gate generico do shell: politica e' BLACKLIST (nega o risco, deixa o resto),
/// nao allowlist de argv; e erro de comando/opcao inexistente (exit 127/129)
/// volta no MESMO envelope de todas as tools. Exit 128 fica intacto (erro de uso
/// real, nao de vocabulario).
/// </summary>
internal static class UnitShellGateTests
{
    public static Task PreIsBlacklist()
    {
        TestContext.Check(CheckCommand("uname -s").Verdict == Verdict.Allow, "comando fora da lista roda (blacklist, nao allowlist)");
        TestContext.Check(CheckCommand("sqlite3 banco.db 'select 1'").Verdict == Verdict.Allow, "sqlite3 (antes fora do allowlist) liberado");
        TestContext.Check(CheckCommand("rm -rf /tmp/x").Verdict == Verdict.Deny, "rm segue negado");
        TestContext.Check(CheckCommand("mv a b").Verdict == Verdict.Deny, "mv segue negado");
        TestContext.Check(CheckCommand("sed -i s/a/b/ f").Verdict == Verdict.Deny, "sed -i segue negado");
        TestContext.Check(CheckCommand("git reset --hard").Verdict == Verdict.Deny, "git reset --hard negado");
        TestContext.Check(CheckCommand("git push origin main").Verdict == Verdict.Deny, "git push negado (publica sem volta)");
        TestContext.Check(CheckCommand("git clean -fdx").Verdict == Verdict.Deny, "git clean negado (apaga nao versionado)");
        return Task.CompletedTask;
    }

    public static Task PostExplainsInvalid()
    {
        var notFound = ExplainInvalidUsage(ToolResult.Fail("exit 127: sqlite3: command not found"), "sqlite3 db");
        TestContext.Check(!notFound.Success && ToolFeedback.IsEnvelope(notFound.Error ?? ""),
            "exit 127 vira envelope (comando nao encontrado)");
        TestContext.Check((notFound.Error ?? "").Contains("sqlite3", StringComparison.Ordinal),
            "envelope nomeia o binario que faltou");

        var badOpt = ExplainInvalidUsage(ToolResult.Fail("exit 129: unknown option '--path'"), "git add . --path ./");
        TestContext.Check(!badOpt.Success && ToolFeedback.IsEnvelope(badOpt.Error ?? ""),
            "exit 129 vira envelope (opcao inventada)");

        var realUse = ExplainInvalidUsage(ToolResult.Fail("exit 128: Author identity unknown"), "git commit -m x");
        TestContext.Check(!realUse.Success && !ToolFeedback.IsEnvelope(realUse.Error ?? ""),
            "exit 128 NAO e' tratado como vocabulario");

        var ok = ExplainInvalidUsage(ToolResult.Ok("saida"), "uname -s");
        TestContext.Check(ok.Success && ok.Output == "saida", "sucesso passa intacto");
        return Task.CompletedTask;
    }

    public static Task UnknownCommandRuns()
    {
        string dir = Path.Combine("/tmp/opencode", "ocb-shellgate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var exec = new BridgeToolExecutor(dir);
            // `uname` estava fora do allowlist antigo; agora roda (blacklist).
            var uname = exec.Execute("shell", "{\"command\":\"uname -s\"}", 10);
            TestContext.Check(uname.Success, "binario fora da lista antiga executa");
            var push = exec.Execute("shell", "{\"command\":\"git push origin main\"}", 10);
            TestContext.Check(!push.Success && ToolFeedback.IsEnvelope(push.Error ?? ""),
                "blacklist responde no envelope unico");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
        return Task.CompletedTask;
    }
}
