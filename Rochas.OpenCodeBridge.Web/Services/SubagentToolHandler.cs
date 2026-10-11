using System.Text;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Web.Services;

// Handler da tool "subagent": delega uma subtarefa ao MESMO modelo (sem tools,
// só texto — sem recursão por construção) e devolve a resposta + thinking.
// É assim que o 8B abre subagentes dele mesmo; aparece na UI como qualquer tool.
public sealed class SubagentToolHandler : IToolHandler
{
    public string ToolName => "subagent";

    private const double FixedTemperature = 0.4;
    private const string SubagentSystem =
        "Você é um agente desenvolvedor senior .net c# que nao dialoga, compoe script shell sh para implementacao do enunciado em batch";

    private readonly IBridgeClient _bridge;

    public SubagentToolHandler(IBridgeClient bridge)
    {
        _bridge = bridge;
    }

    public ToolResult Handle(string argumentsJson, ToolContext context)
    {
        JsonObject? obj;
        try { obj = JsonNode.Parse(argumentsJson)?.AsObject(); }
        catch (Exception ex) { return ToolResult.Fail($"argumentos inválidos: {ex.Message}"); }
        if (obj is null) return ToolResult.Fail("argumentos inválidos: objeto JSON esperado");

        string prompt = obj["prompt"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(prompt)) return ToolResult.Fail("prompt é obrigatório");
        // Sem teto repassado: ignora pedido menor (o thinking come o orçamento e o texto morre).
        // null = sem max_tokens no body; a bridge aplica o teto 8k.
        int? maxTokens = null;

        if (string.IsNullOrWhiteSpace(context.BridgeUrl) || string.IsNullOrWhiteSpace(context.Model))
            return ToolResult.Fail("subagente sem agente (bridge/modelo ausentes)");

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(30, context.TimeoutSeconds)));
            // Mesmas specs do chamador: mesmo modelo, 0.4, thinking on, mesmas tools.
            // Trava de profundidade estrutural: executor interno sem "subagent".
            var inner = new ToolExecutor(
                ToolExecutor.DefaultHandlers().Where(h => h.ToolName != "subagent"),
                context.WorkspaceRoot, "/tmp/tool-executor-sub.log");
            var history = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = prompt.Trim() } };
            // Sub só gera texto (sem tools anunciadas): mantém o executor só como retaguarda.
            JsonArray? tools = null;
            var collected = new StringBuilder();
            string subthink = "";
            for (int turn = 0; turn < 3; turn++)
            {
                var t = BridgeHelper.ChatTurnAsync(_bridge, context.BridgeUrl, context.Model,
                    FixedTemperature, SubagentSystem, history, tools, cts.Token, enableThinking: true, maxTokens: maxTokens)
                    .GetAwaiter().GetResult();
                if (!t.Ok) return ToolResult.Fail($"subagente falhou: {t.Error}");
                if (!string.IsNullOrWhiteSpace(t.Thinking)) subthink = t.Thinking;
                if (!string.IsNullOrWhiteSpace(t.Content)) collected.AppendLine(t.Content);
                if (t.Calls.Count == 0) break;
                history.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = t.Content,
                    ["tool_calls"] = new JsonArray(t.Calls.Select(c => new JsonObject
                    {
                        ["id"] = c.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Args },
                    }).ToArray()),
                });
                foreach (var c in t.Calls)
                {
                    // Sub não delega: ignora sem erro (não polui o histórico com FALHOU).
                    if (c.Name == "subagent")
                    {
                        collected.AppendLine("[subagent] ignorado: subagente não delega.");
                        history.Add(new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = c.Id,
                            ["content"] = "ignorado: subagente não delega.",
                        });
                        continue;
                    }
                    var r = inner.Execute(c.Name, c.Args, 300);
                    string o = r.Success ? r.Output : $"Error: {r.Error}";
                    collected.AppendLine($"[{c.Name}] {(r.Success ? "ok" : "FALHOU")}: {o.Replace("\n", " / ")}");
                    history.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = c.Id,
                        ["content"] = o,
                    });
                }
            }
            string text = collected.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(subthink))
            {
                string th = subthink.Trim();
                text += "\n<SUBTHINK>" + th + "</SUBTHINK>";
            }
            if (string.IsNullOrWhiteSpace(text)) return ToolResult.Fail("subagente vazio");
            return ToolResult.Ok(text);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail("subagente estourou o tempo");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"subagente errou: {ex.Message.Split('\n')[0]}");
        }
    }
}
