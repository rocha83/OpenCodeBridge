using Rochas.OpenCodeBridge.Core.Tools;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>
/// Trava de `.git/`: `write`/`edit` nao podem gravar dentro do repositorio.
/// Medido no E-tools-full L2 de 2026-11-06 — o modelo gravou `.git/config` e
/// `git add`/`commit` falharam em todas as tentativas.
/// </summary>
internal static class UnitRepoPathGuardTests
{
    public static Task RefusesGitInternals()
    {
        foreach (string p in new[] { ".git/config", "./.git/HEAD", ".git", "notas/.git/config", "./notas/.git/refs/heads/main" })
            TestContext.Check(RepoPathGuard.GitInternal(p, "write") is not null, $"recusa caminho dentro de .git ('{p}')");

        foreach (string p in new[] { "data.txt", "notas/hoje.txt", "gitignore", ".github/workflows/ci.yml", ".gitignore" })
            TestContext.Check(RepoPathGuard.GitInternal(p, "write") is null, $"libera caminho normal ('{p}')");

        string msg = RepoPathGuard.GitInternal(".git/config", "write") ?? "";
        TestContext.Check(msg.Contains("config user.name", StringComparison.Ordinal)
            && msg.Contains("add .", StringComparison.Ordinal),
            "recusa ensina a tool git (config + add)");
        return Task.CompletedTask;
    }
}
