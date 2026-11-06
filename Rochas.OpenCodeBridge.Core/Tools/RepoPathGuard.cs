namespace Rochas.OpenCodeBridge.Core.Tools;

/// <summary>
/// Trava de caminho INTERNO do repositorio: `write`/`edit` recusam gravar dentro
/// de `.git/`. Medido no E-tools-full L2 de 2026-11-06: o modelo gravou
/// `user.name=...` em `.git/config`, o repo ficou invalido e `git add`/`commit`
/// falharam em TODAS as tentativas — o commit nunca acontecia.
/// </summary>
/// <remarks>
/// A recusa devolve a forma que funciona (a tool `git`), porque o modelo copia o
/// literal da mensagem: nada de escrever o mapa do config de novo.
/// </remarks>
public static class RepoPathGuard
{
    /// <summary>Mensagem de recusa quando o caminho cai em `.git/`, ou null.</summary>
    /// <param name="path">Caminho pedido na call (relativo ao workspace).</param>
    /// <param name="tool">Nome da tool, para a mensagem.</param>
    public static string? GitInternal(string? path, string tool)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string p = path.Replace('\\', '/').Trim();
        while (p.StartsWith("./", System.StringComparison.Ordinal)) p = p[2..];
        foreach (string seg in p.Split('/'))
        {
            if (!string.Equals(seg, ".git", System.StringComparison.OrdinalIgnoreCase)) continue;
            return $"{tool}: '{path}' fica dentro de .git/ (area interna do repositorio). "
                 + "Gravar ali deixa o repo invalido e `git add`/`commit` passam a falhar. "
                 + "A identidade se grava pela tool git, ANTES do primeiro commit: "
                 + "git {\"command\":\"config user.name \\\"Seu Nome\\\"\"} e "
                 + "git {\"command\":\"config user.email \\\"voce@exemplo.com\\\"\"}; "
                 + "depois git {\"command\":\"add .\"} e git {\"command\":\"commit -m \\\"<mensagem>\\\"\"}.";
        }
        return null;
    }
}
