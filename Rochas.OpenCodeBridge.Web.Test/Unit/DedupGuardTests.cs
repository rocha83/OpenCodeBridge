// Guarda de DRY: nenhum tipo pode ser declarado ao mesmo tempo no Core e no Web.
// O padrao ja custou caro duas vezes (RepeatLoop copiado do ContentAntiRepeat e os
// 16 tipos de tool copiados em Web/Services). Se este teste ficar vermelho, a
// correcao NAO e apagar o teste: mova o tipo para o Core (fonte unica) e ajuste o
// host pelo contexto/parametro.
using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Web.Test.Unit;

internal static class DedupGuardTests
{
    private static int Failures;

    private const string TypePattern =
        @"^\s*(?:(?:public|internal|private|protected|sealed|static|abstract|partial|readonly|file)\s+)*" +
        @"(?:class|record|struct|interface|enum)\s+(\w+)";

    internal static int Run()
    {
        Failures = 0;
        SemTiposDuplicados();
        return Failures;
    }

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) Failures++;
    }

    private static void SemTiposDuplicados()
    {
        string? root = RepoRoot();
        if (root is null)
        {
            Console.WriteLine("SKIP U-sem-tipos-duplicados (arvore de fontes nao encontrada)");
            return;
        }

        var core = Declared(root, "Rochas.OpenCodeBridge.Core");
        var web = Declared(root, "Rochas.OpenCodeBridge.Web");
        var both = core.Keys.Intersect(web.Keys).OrderBy(t => t, StringComparer.Ordinal).ToList();

        // SubagentToolHandler e ADAPTER de host (precisa do IBridgeClient, dos sinks
        // de progresso e da sessao da Web); o loop com as tools e a unica peca de
        // mecanismo que ainda mora na Web. Qualquer outro tipo duplicado = divida.
        both.RemoveAll(t => t == nameof(SubagentToolHandler));

        foreach (string t in both)
            Console.WriteLine($"  duplicado: {t}  core={core[t]}  web={web[t]}");

        Check(both.Count == 0, $"U-sem-tipos-duplicados (achei {both.Count})");
    }

    private static Dictionary<string, string> Declared(string root, string project)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var pattern = new Regex(TypePattern, RegexOptions.Multiline);
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;
            foreach (Match m in pattern.Matches(File.ReadAllText(file)))
                map.TryAdd(m.Groups[1].Value, Path.GetRelativePath(Path.Combine(root, project), file));
        }
        return map;
    }

    // bin/Release/net<X>/ -> projeto -> repo
    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Rochas.OpenCodeBridge.slnx")) &&
                Directory.Exists(Path.Combine(dir.FullName, "Rochas.OpenCodeBridge.Core")))
                return dir.FullName;
        }
        return null;
    }
}
