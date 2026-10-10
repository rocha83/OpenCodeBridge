using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Web.Services;

// Handler da tool "subagent": delega uma subtarefa ao MESMO modelo (sem tools,
// só texto — sem recursão por construção) e devolve a resposta + thinking.
// É assim que o 8B abre subagentes dele mesmo; aparece na UI como qualquer tool.
public sealed class SubagentToolHandler : IToolHandler
{
    public string ToolName => "subagent";

    private const int DefaultMaxTokens = 2048;
    private const int MaxOutputChars = 4000;
    private const double FixedTemperature = 0.4;
    private const string SubagentSystem =
        "Você é um subagente do mesmo modelo: primeiro resuma a subtarefa em até 3 linhas " +
        "(RESUMO); depois componha a solução completa em scripts sh por fase (SOLUÇÃO). " +
        "Responda só com as duas partes, sem executar nada.";

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
        int maxTokens = obj["maxTokens"]?.GetValue<int>() ?? DefaultMaxTokens;
        maxTokens = Math.Min(Math.Max(maxTokens, 256), 8192);

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
            var tools = ToolDefinitions.GetTools();
            var collected = new StringBuilder();
            string thinking = "";
            for (int turn = 0; turn < 3; turn++)
            {
                var t = BridgeHelper.ChatTurnAsync(_bridge, context.BridgeUrl, context.Model,
                    FixedTemperature, SubagentSystem, history, tools, cts.Token, enableThinking: true)
                    .GetAwaiter().GetResult();
                if (!t.Ok) return ToolResult.Fail($"subagente falhou: {t.Error}");
                if (!string.IsNullOrWhiteSpace(t.Thinking)) thinking = t.Thinking;
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
                    var r = inner.Execute(c.Name, c.Args, 60);
                    string o = r.Success ? r.Output : $"Error: {r.Error}";
                    if (o.Length > 1000) o = o[..1000] + "\n[truncado]";
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
            if (!string.IsNullOrWhiteSpace(thinking))
                text += "\n\n[Raciocínio do subagente]\n" + thinking.Trim();
            if (text.Length > MaxOutputChars) text = text[..MaxOutputChars] + "\n[truncado]";
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
