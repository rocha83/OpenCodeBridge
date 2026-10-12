using System.Text;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Web.Services;

// Executor de tools para modelos DeepSeek sem tool_calls estruturadas.
// Sem mudar prompts: o turno volta como texto e o caminho de parse do padrão
// oficial (DeepSeekToolCoercion: <tool_name>/hermes/fence/tokens) vira Calls,
// executadas no ToolExecutor interno; o retorno volta ao histórico como
// <tool_response> (formato da doc oficial), até esgotar as chamadas.
public sealed class DeepSeekToolExecutor
{
    private readonly ToolExecutor _inner;
    private readonly Func<string, bool> _shellAllowed;

    public DeepSeekToolExecutor(ToolExecutor inner, Func<string, bool> shellAllowed)
    {
        _inner = inner;
        _shellAllowed = shellAllowed;
    }

    public sealed record DsResult(bool Ok, string Text, string Thinking, string Error);

    public async Task<DsResult> RunAsync(IBridgeClient bridge, string bridgeUrl, string model,
        double temperature, string systemPrompt, JsonArray history, List<string> knownTools,
        int maxTurns, Func<string, string, Task>? onChunk, CancellationToken ct)
    {
        var collected = new StringBuilder();
        string thinking = "";
        int nudges = 0;
        bool anyCall = false;
        // Tools reais do sub (mesmas defs anunciadas): sem elas o template não
        // renderiza o bloco # Tools e o required não tem o que forçar.
        var subTools = new JsonArray();
        foreach (var t in ToolDefinitions.GetTools())
        {
            if ((t as JsonObject)?["function"]?["name"]?.GetValue<string>() is not string tn) continue;
            if (!knownTools.Any(k => k.Equals(tn, StringComparison.OrdinalIgnoreCase))) continue;
            subTools.Add(t.DeepClone());
        }
        for (int turn = 0; turn < maxTurns; turn++)
        {
            // Com tools + required no 1º turno (força ação); depois auto.
            // O parse estruturado vale; o texto cai na coerção.
            var t = await BridgeHelper.ChatTurnAsync(bridge, bridgeUrl, model, temperature,
                systemPrompt, history, subTools.Count > 0 ? subTools : null, ct,
                enableThinking: true, maxTokens: null, onChunk: onChunk,
                toolChoice: !anyCall && subTools.Count > 0
                    ? System.Text.Json.Nodes.JsonValue.Create("required") : null);
            if (!t.Ok) return new DsResult(false, collected.ToString().Trim(), thinking, t.Error);
            if (!string.IsNullOrWhiteSpace(t.Thinking)) thinking = t.Thinking;
            if (!string.IsNullOrWhiteSpace(t.Content)) collected.AppendLine(t.Content);
            var calls = DeepSeekToolCoercion.Coerce(model, t.Content, out _, knownTools);
            if (calls.Count == 0)
            {
                // Em prosa sem calls e sem nenhum trabalho feito: cutucada com
                // o formato exato (máx 2). Texto após trabalho = conclusão.
                if (!anyCall && nudges < 2 && !string.IsNullOrWhiteSpace(t.Content))
                {
                    nudges++;
                    history.Add(new JsonObject { ["role"] = "assistant", ["content"] = t.Content });
                    history.Add(new JsonObject { ["role"] = "user", ["content"] =
                        $"[DeepSeek-tools {nudges}/2] Sem tool calls. Emita no formato exato, sem prosa junto, ex.: " +
                        "<｜tool▁calls▁begin｜><｜tool▁call▁begin｜>function<｜tool▁sep｜>write\n" +
                        "```json\n{\"path\": \"fase.sh\", \"content\": \"...\"}\n```<｜tool▁call▁end｜>" +
                        "<｜tool▁calls▁end｜> (write cria o script; shell só 'bash -n <arq>')." });
                    continue;
                }
                break;
            }
            history.Add(new JsonObject { ["role"] = "assistant", ["content"] = t.Content });
            var responses = new StringBuilder();
            foreach (var c in calls)
            {
                anyCall = true;
                if (c.Name.Equals("subagent", StringComparison.OrdinalIgnoreCase))
                {
                    collected.AppendLine("[subagent] ignorado: subagente não delega.");
                    responses.Append("<tool_response>ignorado: subagente não delega.</tool_response>\n");
                    continue;
                }
                if (c.Name is "shell" or "bash" or "sh" && !_shellAllowed(c.Args))
                {
                    const string msg = "negado: shell do subagente só valida ('bash -n <arquivo>'), compacta (zip/unzip) ou navega (ls/cat/pwd); não execute scripts.";
                    collected.AppendLine($"[shell] FALHOU: {msg}");
                    responses.Append($"<tool_response>Error: {msg}</tool_response>\n");
                    continue;
                }
                var r = _inner.Execute(c.Name, c.Args, 300);
                string o = r.Success ? r.Output : $"Error: {r.Error}";
                collected.AppendLine($"[{c.Name}] {(r.Success ? "ok" : "FALHOU")}: {o.Replace("\n", " / ")}");
                responses.Append($"<tool_response>{o}</tool_response>\n");
            }
            history.Add(new JsonObject { ["role"] = "user", ["content"] = responses.ToString() });
        }
        return new DsResult(true, collected.ToString().Trim(), thinking, "");
    }
}
