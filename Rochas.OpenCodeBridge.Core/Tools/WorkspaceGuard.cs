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

    /// <summary>
    /// Dica quando o arquivo nao existe no path pedido: procura o MESMO nome de
    /// arquivo (BFS, depth 4, acha 3) e devolve o caminho relativo real; vazio
    /// quando nao ha achado util (o chamador entao responde o erro original).
    /// Motivo medido: em E-tools-full o modelo fez `cd temp_dir` no shell e chamou
    /// edit com "random_file.txt" (arquivo estava em temp_dir/), edit falhou e o
    /// loop gastou os steps ate acabar a bateria sem a linha EDITADO.
    /// </summary>
    public static string Locate(string root, string path, string[]? extraRoots = null)
    {
        try
        {
            string nome = Path.GetFileName(path ?? "");
            if (nome.Length == 0) return "";

            string baseRoot = Path.GetFullPath(root ?? "")
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var raizes = new List<string>();
            if (Directory.Exists(baseRoot)) raizes.Add(baseRoot);
            foreach (string r in extraRoots ?? ToolContext.DefaultExtraRoots)
            {
                string full = Path.GetFullPath(r);
                if (Directory.Exists(full) && !raizes.Contains(full, StringComparer.Ordinal)) raizes.Add(full);
            }

            const int DepthMax = 4, AchaMax = 3, VisitaMax = 4000;
            var achados = new List<string>();
            foreach (string raiz in raizes)
            {
                int vistos = 0;
                var fila = new Queue<(string Dir, int Depth)>();
                fila.Enqueue((raiz, 0));
                while (fila.Count > 0 && achados.Count < AchaMax && vistos < VisitaMax)
                {
                    var (dir, depth) = fila.Dequeue();
                    vistos++;
                    try
                    {
                        foreach (string f in Directory.EnumerateFiles(dir))
                        {
                            vistos++;
                            if (!Path.GetFileName(f).Equals(nome, StringComparison.OrdinalIgnoreCase)) continue;
                            string rel = f.StartsWith(baseRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                                ? f[(baseRoot.Length + 1)..]
                                : f;
                            if (!achados.Contains(rel, StringComparer.Ordinal)) achados.Add(rel);
                            if (achados.Count >= AchaMax) break;
                        }
                        if (achados.Count >= AchaMax || depth >= DepthMax) continue;
                        foreach (string sub in Directory.EnumerateDirectories(dir))
                        {
                            string nomeSub = Path.GetFileName(sub);
                            if (nomeSub is ".git" or "node_modules" or "bin" or "obj" or ".vs"
                                or "__pycache__" or ".gradle" or "target") continue;
                            fila.Enqueue((sub, depth + 1));
                        }
                    }
                    catch { /* sem permissao ou link quebrado: segue */ }
                }
                if (achados.Count >= AchaMax) break;
            }
            return string.Join(", ", achados);
        }
        catch { return ""; }
    }
}
