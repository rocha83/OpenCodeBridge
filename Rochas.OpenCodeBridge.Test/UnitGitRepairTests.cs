namespace Rochas.OpenCodeBridge.Test;

/// <summary>
/// Reparo deterministico dos args da tool `git` (Core/GitArgRepair): cada regra
/// nasceu de uma falha medida no E-tools-full de 2026-11-06 (`init --bare .git`,
/// `.git` como path, caminho de cache/absoluto). Call valida nao pode ser tocada.
/// </summary>
internal static class UnitGitRepairTests
{
    static (string Command, string Path, IReadOnlyList<string> Applied) Repair(
        string command, string path, string root = "/w")
        => global::Rochas.OpenCodeBridge.Core.Tools.GitArgRepair.Repair(command, path, root);

    public static Task NormalizesVocabulary()
    {
        var a = Repair("git init --bare .", "");
        TestContext.Check(a.Command == "init .", "tira `git ` e `--bare` (vira `init .`)");
        TestContext.Check(a.Applied.Contains("prefix-git") && a.Applied.Contains("drop-bare"),
            "registra as regras aplicadas");

        var b = Repair("init .git --bare", "");
        TestContext.Check(b.Command == "init", "`init .git` perde o argumento .git (nao cria .git/.git)");

        var c = Repair("init", "./repo-temp/data.git");
        TestContext.Check(c.Path == "./", "`init` em subpasta inventada cai no workspace");

        var d = Repair("status", ".git");
        TestContext.Check(d.Path == "./", "`.git` como path vira a pasta do repo");

        var e = Repair("log --oneline", "./node_modules/.cache/toolsfull-agent/workspace/repo/tmp/x");
        TestContext.Check(e.Path == "./", "eco do diretorio de cache vira workspace");

        var f = Repair("log --oneline", "/w/repo");
        TestContext.Check(f.Path == "./repo", "absoluto dentro do workspace vira relativo");

        var g = Repair("config user.name \"Ana\" --local,path:./x", "./");
        TestContext.Check(g.Command == "config user.name \"Ana\"", "descarta sujeira `,path:` do command");

        var h = Repair("add .", "./");
        TestContext.Check(h.Command == "add ." && h.Path == "./" && h.Applied.Count == 0,
            "call valida passa INTACTA (nenhuma regra aplicada)");
        return Task.CompletedTask;
    }
}
