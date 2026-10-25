namespace Rochas.OpenCodeBridge.Core.Tools;

// Trava de workspace: resolve caminho e garante que fica dentro da raiz.
public static class WorkspaceGuard
{
    public static bool TryResolve(string root, string path, out string full, out string why, string[]? extraRoots = null)
    {
        full = "";
        why = "";
        try
        {
            // AppContext.BaseDirectory termina com separador: normaliza para o
            // prefixo não virar "//" e negar tudo dentro do workspace.
            string baseRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string candidate = Path.GetFullPath(Path.Combine(baseRoot, path ?? ""));
            if (candidate != baseRoot && !candidate.StartsWith(baseRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !UnderExtraRoot(candidate, extraRoots ?? ToolContext.DefaultExtraRoots))
            {
                why = $"caminho '{path}' fora do workspace";
                return false;
            }
            full = candidate;
            return true;
        }
        catch (Exception ex)
        {
            why = $"caminho inválido '{path}': {ex.Message}";
            return false;
        }
    }

    // Raízes extras permitidas além do workspace (politica do host, vem do contexto).
    private static bool UnderExtraRoot(string candidate, string[] roots)
    {
        foreach (string r in roots)
            if (candidate == r || candidate.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return true;
        return false;
    }
}
