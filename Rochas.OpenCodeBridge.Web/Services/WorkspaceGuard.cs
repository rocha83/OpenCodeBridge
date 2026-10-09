namespace Rochas.OpenCodeBridge.Web.Services;

// Trava de workspace: resolve caminho e garante que fica dentro da raiz.
internal static class WorkspaceGuard
{
    public static bool TryResolve(string root, string path, out string full, out string why)
    {
        full = "";
        why = "";
        try
        {
            // AppContext.BaseDirectory termina com separador: normaliza para o
            // prefixo não virar "//" e negar tudo dentro do workspace.
            string baseRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string candidate = Path.GetFullPath(Path.Combine(baseRoot, path ?? ""));
            if (candidate != baseRoot && !candidate.StartsWith(baseRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
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
}
