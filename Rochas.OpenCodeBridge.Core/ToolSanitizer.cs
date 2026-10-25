using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Sanitização do catálogo de tools antes de ir ao modelo — regra do núcleo,
/// usada por qualquer interface (API da bridge, console, Web UI, testes).
/// </summary>
public static class ToolSanitizer
{
    /// <summary>Nota que substitui o bloco nativo Code Mode do opencode.</summary>
    public const string CodeModeNote =
        "# Tools (harness da bridge)\n" +
        "As tools do catalogo (shell, write, read, edit, grep, glob, webfetch, websearch, ...) sao chamadas DIRETAS. " +
        "Esta bridge NAO oferece `execute` nem sandbox JavaScript: Code Mode nao existe aqui. " +
        "Nao empacote varias acoes em um script; emita uma tool call por acao (paralelas quando independentes).\n";

    /// <summary>
    /// Remove <c>pattern</c> não ancorado (schema que o parser gramatical do llama rejeita com 400).
    /// </summary>
    /// <param name="node">Tools ou sub-schema (mutado no lugar).</param>
    /// <param name="path">Caminho do schema (usado no log da remoção).</param>
    /// <param name="hits">Acumulador dos caminhos removidos.</param>
    /// <returns>Quantas chaves removidas.</returns>
    public static int StripUnanchoredPatterns(JsonNode? node, string path = "$", List<string>? hits = null)
    {
        int dropped = 0;
        if (node is JsonObject obj)
        {
            if (obj["pattern"] is JsonValue pv && pv.TryGetValue<string>(out string? pat)
                && !string.IsNullOrEmpty(pat) && !(pat.StartsWith('^') && pat.EndsWith('$')))
            {
                obj.Remove("pattern");
                hits?.Add(path + ".pattern");
                dropped++;
            }
            foreach (var kv in obj)
                dropped += StripUnanchoredPatterns(kv.Value, path + "." + kv.Key, hits);
        }
        else if (node is JsonArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
                dropped += StripUnanchoredPatterns(arr[i], $"{path}[{i}]", hits);
        }
        return dropped;
    }

    /// <summary>Remove a tool <c>execute</c> (Code Mode do opencode) do catálogo.</summary>
    /// <param name="tools">Array de tools (chat ou responses).</param>
    /// <param name="enabled">Estratégia de tools ligada.</param>
    /// <param name="mode">Rótulo do caminho, só para log.</param>
    /// <param name="log">Sink de log do host (opcional).</param>
    /// <returns>Quantidade removida.</returns>
    public static int StripExecuteTools(JsonNode? tools, bool enabled, string mode, Action<JsonObject>? log = null)
    {
        if (!enabled || tools is not JsonArray arr) return 0;
        int removed = 0;
        for (int i = arr.Count - 1; i >= 0; i--)
        {
            string? name = arr[i]?["function"]?["name"]?.GetValue<string>() ?? arr[i]?["name"]?.GetValue<string>();
            if (!string.Equals(name, "execute", StringComparison.Ordinal)) continue;
            arr.RemoveAt(i);
            removed++;
        }
        if (removed > 0)
            log?.Invoke(new JsonObject { ["event"] = "tool-strip", ["mode"] = mode, ["tool"] = "execute", ["removed"] = removed, ["left"] = arr.Count });
        return removed;
    }

    /// <summary>Troca o bloco nativo Code Mode pelo aviso de tools diretas.</summary>
    /// <param name="node">Request chat (usa "messages").</param>
    /// <param name="enabled">Estratégia de tools ligada.</param>
    /// <param name="log">Sink de log do host (opcional).</param>
    /// <returns>Quantidade de mensagens reescritas.</returns>
    public static int NeutralizeCodeMode(JsonNode? node, bool enabled, Action<JsonObject>? log = null)
    {
        if (!enabled || node?["messages"] is not JsonArray msgs) return 0;
        int hits = 0;
        foreach (var m in msgs)
        {
            if (m is not JsonObject mo || mo["content"] is not JsonValue cv || !cv.TryGetValue<string>(out string? text)) continue;
            int start = text.IndexOf("# Code Mode", StringComparison.Ordinal);
            if (start < 0) continue;
            int end = text.IndexOf("Skills provide specialized instructions", start + 1, StringComparison.Ordinal);
            if (end < 0) end = text.IndexOf("\n# ", start + 1, StringComparison.Ordinal);
            string tail = end >= 0 ? text[end..] : "";
            mo["content"] = text[..start] + CodeModeNote + tail;
            hits++;
        }
        if (hits > 0) log?.Invoke(new JsonObject { ["event"] = "harness-strip", ["mode"] = "code-mode", ["hits"] = hits });
        return hits;
    }
}
