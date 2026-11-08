using System.Globalization;

namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Config de agente no MESMO formato do opencode (frontmatter YAML + corpo =
/// system prompt). É a fonte única do que a tool <c>subagent</c> anuncia como
/// agente disponível e do contrato que o subagente recebe ao rodar: sem isso o
/// handler executava "um dev genérico" e a role pedida (ex.: Scripter) não
/// existia no caminho bridge.
/// </summary>
public sealed class AgentSpec
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Model { get; init; } = "";
    public double Temperature { get; init; } = 0.1;
    public string System { get; init; } = "";
    /// <summary>Tools com <c>effect: allow</c> no frontmatter (vazio = todas).</summary>
    public HashSet<string> Tools { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Leitor do diretório de agentes (env <c>BRIDGE_AGENTS_DIR</c> &gt; <c>~/.config/opencode/agents</c>).</summary>
public static class AgentCatalog
{
    public static string Dir()
    {
        string env = Environment.GetEnvironmentVariable("BRIDGE_AGENTS_DIR") ?? "";
        if (env.Length > 0) return env;
        string home = Environment.GetEnvironmentVariable("HOME") ?? "";
        if (home.Length == 0) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return System.IO.Path.Combine(home, ".config", "opencode", "agents");
    }

    public static IReadOnlyList<AgentSpec> List()
    {
        var list = new List<AgentSpec>();
        string dir = Dir();
        if (!System.IO.Directory.Exists(dir)) return list;
        foreach (string f in System.IO.Directory.GetFiles(dir, "*.md").OrderBy(x => x, StringComparer.Ordinal))
        {
            try
            {
                AgentSpec? a = Parse(System.IO.Path.GetFileNameWithoutExtension(f), System.IO.File.ReadAllText(f));
                if (a is not null) list.Add(a);
            }
            catch { /* agent ilegivel nao derruba o catalogo */ }
        }
        return list;
    }

    /// <summary>Nome exato, depois "contém", depois "está contido": o modelo
    /// cita o agente por nome ou por parte dele no enunciado.</summary>
    public static AgentSpec? Resolve(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string want = name.Trim().ToLowerInvariant();
        var all = List();
        return all.FirstOrDefault(a => a.Name.Equals(want, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(a => a.Name.Contains(want, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(a => want.Contains(a.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Lista curta "nome (descricao)" para o schema da tool.</summary>
    public static string Describe()
    {
        var all = List();
        return all.Count == 0 ? "" : string.Join("; ", all.Select(a =>
            a.Description.Length > 0
                ? a.Name + " (" + a.Description.Split('—')[0].Split('-')[0].Trim() + ")"
                : a.Name));
    }

    static AgentSpec? Parse(string name, string raw)
    {
        string body = raw;
        var meta = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (raw.StartsWith("---", StringComparison.Ordinal))
        {
            int end = raw.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end > 0)
            {
                string fm = raw[3..end];
                body = raw[(end + 4)..].TrimStart('\r', '\n');
                string? action = null;
                foreach (string line in fm.Split('\n'))
                {
                    string t = line.Trim();
                    if (t.StartsWith("- ", StringComparison.Ordinal)) t = t[2..].Trim();
                    if (t.StartsWith("action:", StringComparison.OrdinalIgnoreCase))
                    {
                        action = t[7..].Trim().Trim('"');
                        continue;
                    }
                    if (t.StartsWith("effect:", StringComparison.OrdinalIgnoreCase))
                    {
                        if (t[7..].Trim().Equals("allow", StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrWhiteSpace(action) && action != "*")
                            allowed.Add(action!);
                        continue;
                    }
                    int c = t.IndexOf(':');
                    if (c > 0) meta[t[..c].Trim().ToLowerInvariant()] = t[(c + 1)..].Trim().Trim('"');
                }
            }
        }
        double temp = 0.1;
        if (meta.TryGetValue("temperature", out string? ts))
            double.TryParse(ts, NumberStyles.Float, CultureInfo.InvariantCulture, out temp);
        return new AgentSpec
        {
            Name = name,
            Description = meta.TryGetValue("description", out string? d) ? d : "",
            Model = meta.TryGetValue("model", out string? m) ? m : "",
            Temperature = temp,
            System = body.Trim(),
            Tools = allowed,
        };
    }
}
