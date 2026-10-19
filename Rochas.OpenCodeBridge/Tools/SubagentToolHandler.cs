using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Tools;

// Handler da tool "subagent"/"task": delega um prompt a um modelo (upstream)
// e devolve o conteudo da resposta. Permite ao Build/Orch segmentar trabalho
// em sub-tarefas executadas por um modelo menor (E2B) ou pelo proprio 12B.
public sealed class SubagentToolHandler : IToolHandler
{
    public string ToolName => "subagent";

    public ToolResult Handle(string argumentsJson, ToolContext context)
    {
        JsonObject args;
        try { args = JsonNode.Parse(argumentsJson)?.AsObject() ?? new JsonObject(); }
        catch { args = new JsonObject(); }

        string prompt = args["prompt"]?.GetValue<string>() ?? args["task"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(prompt))
            return ToolResult.Fail("subagent: argumento 'prompt' obrigatorio");

        string system = args["system"]?.GetValue<string>() ?? "Voce e um agente desenvolvedor senior. Execute a tarefa e responda de forma concisa em pt-br.";
        string model = args["model"]?.GetValue<string>() ?? "";
        double temp = args["temperature"]?.GetValue<double>() ?? 0.4;

        string upstream = string.IsNullOrWhiteSpace(context.UpstreamUrl) ? "http://127.0.0.1:4100" : context.UpstreamUrl;
        // se model nao informado, usa o model do upstream (default do bridge)
        var body = new JsonObject
        {
            ["temperature"] = temp,
            ["stream"] = false,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = prompt })
        };
        if (!string.IsNullOrWhiteSpace(model)) body["model"] = model;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Clamp(context.TimeoutSeconds, 30, 600)) };
            if (!string.IsNullOrWhiteSpace(context.ApiKey))
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", context.ApiKey);
            var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            var resp = http.PostAsync(upstream + "/v1/chat/completions", content).GetAwaiter().GetResult();
            string raw = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
                return ToolResult.Fail($"subagent: upstream HTTP {(int)resp.StatusCode}: {raw[..Math.Min(300, raw.Length)]}");
            using var doc = JsonDocument.Parse(raw);
            string outText = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            return ToolResult.Ok(outText.Length > 0 ? outText : "(sem resposta)");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"subagent: {ex.Message}");
        }
    }
}
