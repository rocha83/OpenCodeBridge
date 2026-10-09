// Testes de integração do Chat (HTTP real contra servidor web).
// Requer servidor rodando em --web http://host:port
// Execução: dotnet run -c Release --project Rochas.OpenCodeBridge.Web.Test -- --web http://127.0.0.1:4130

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rochas.DapperRepository;
using Rochas.Data.Specification.Enums;
using Rochas.OpenCodeBridge.Web.Data;
using Rochas.OpenCodeBridge.Web.Models;
using Rochas.OpenCodeBridge.Web.Services;

namespace Rochas.OpenCodeBridge.Web.Test.Integration;

internal static class ChatIntegrationTests
{
    private static HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false });
    private static string WebBase = "http://127.0.0.1:4130";
    private static CookieContainer Cookies = new();
    private static int Failures;

    internal static async Task RunAsync(string webBase)
    {
        WebBase = webBase;
        Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = Cookies });

        // Login admin
        await LoginAdmin();

        Check(await AnonRedirectsToLogin(), "I-chat-login-redirect");
        Check(await AdminSeesAgentsUsersLinks(), "I-chat-admin-sees-agents-users");
        Check(await SessionsCrud(), "I-chat-sessions-crud");
        Check(await StreamSseWithSession(), "I-chat-stream-sse");
        Check(await PingEngine(), "I-chat-ping-engine");
        Check(await DiagnosticsEndpoint(), "I-chat-diagnostics");
        Check(await ToolProgressEvents(), "I-chat-tool-progress");

        Console.WriteLine($"=== Chat Integration: {16 - Failures}/16 PASS, {Failures} FAIL ===");
    }

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) Failures++;
    }

    private static async Task<bool> LoginAdmin()
    {
        var resp = await Http.PostAsync($"{WebBase}/Account/Login",
            new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("email", "admin@mova.com"), new KeyValuePair<string, string>("password", "x") }));
        return resp.StatusCode == HttpStatusCode.Redirect;
    }

    private static async Task<bool> AnonRedirectsToLogin()
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var resp = await client.GetAsync($"{WebBase}/Chat");
        return resp.StatusCode == HttpStatusCode.Redirect && resp.Headers.Location?.ToString().Contains("/Account/Login") == true;
    }

    private static async Task<bool> AdminSeesAgentsUsersLinks()
    {
        var html = await Http.GetStringAsync($"{WebBase}/Chat");
        return html.Contains("/Agents") && html.Contains("/Users");
    }

    private static async Task<bool> SessionsCrud()
    {
        // Create
        var createResp = await Http.PostAsJsonAsync($"{WebBase}/Chat/Sessions", new { agentId = 1, title = "Teste Integração" });
        if (!createResp.IsSuccessStatusCode) return false;
        var session = await createResp.Content.ReadFromJsonAsync<JsonObject>();
        var id = session?["id"]?.GetValue<int>();
        if (id == null || id <= 0) return false;

        // List
        var listResp = await Http.GetFromJsonAsync<JsonArray>($"{WebBase}/Chat/Sessions");
        if (listResp == null || listResp.Count == 0) return false;
        var found = listResp.Any(x => x?["id"]?.GetValue<int>() == id);
        if (!found) return false;

        // Get messages (empty)
        var msgResp = await Http.GetFromJsonAsync<JsonArray>($"{WebBase}/Chat/Sessions/{id}/Messages");
        if (msgResp == null || msgResp.Count != 0) return false;

        // Update title
        var putResp = await Http.PutAsJsonAsync($"{WebBase}/Chat/Sessions/{id}/Title", new { title = "Atualizado" });
        if (!putResp.IsSuccessStatusCode) return false;

        // Update agent
        var putAgentResp = await Http.PutAsJsonAsync($"{WebBase}/Chat/Sessions/{id}/Agent", new { agentId = 2 });
        if (!putAgentResp.IsSuccessStatusCode) return false;

        // Delete
        var delResp = await Http.DeleteAsync($"{WebBase}/Chat/Sessions/{id}");
        if (!delResp.IsSuccessStatusCode) return false;

        // Verify deleted
        var listAfter = await Http.GetFromJsonAsync<JsonArray>($"{WebBase}/Chat/Sessions");
        return listAfter == null || !listAfter.Any(x => x?["id"]?.GetValue<int>() == id);
    }

    private static async Task<bool> StreamSseWithSession()
    {
        // Create session
        var createResp = await Http.PostAsJsonAsync($"{WebBase}/Chat/Sessions", new { agentId = 1, title = "Stream Test" });
        if (!createResp.IsSuccessStatusCode) return false;
        var session = await createResp.Content.ReadFromJsonAsync<JsonObject>();
        var id = session?["id"]?.GetValue<int>();
        if (id == null || id <= 0) return false;

        // Stream
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{WebBase}/Chat/Stream");
        req.Content = JsonContent.Create(new { sessionId = id, agentId = 1, messages = new[] { new { role = "user", content = "ping" } } });
        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        if (!resp.IsSuccessStatusCode) return false;

        using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new System.IO.StreamReader(stream);
        string? line;
        bool gotReasoning = false, gotContent = false, gotDone = false;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (line.StartsWith("data: "))
            {
                var data = line[6..].Trim();
                if (data == "[DONE]") { gotDone = true; break; }
                try
                {
                    var chunk = JsonNode.Parse(data);
                    var delta = chunk?["choices"]?[0]?["delta"];
                    if (delta?["reasoning_content"] != null) gotReasoning = true;
                    if (delta?["content"] != null) gotContent = true;
                }
                catch { }
            }
        }

        // Verify user message persisted
        var msgResp = await Http.GetFromJsonAsync<JsonArray>($"{WebBase}/Chat/Sessions/{id}/Messages");
        var userMsg = msgResp?.Any(x => x?["role"]?.GetValue<string>() == "user" && x?["content"]?.GetValue<string>() == "ping") == true;
        var assistantMsg = msgResp?.Any(x => x?["role"]?.GetValue<string>() == "assistant") == true;

        return gotReasoning && gotContent && gotDone && userMsg && assistantMsg;
    }

    private static async Task<bool> PingEngine()
    {
        var resp = await Http.GetFromJsonAsync<JsonObject>($"{WebBase}/Chat/Ping?agentId=1");
        return resp?["ok"]?.GetValue<bool>() == true;
    }

    private static async Task<bool> DiagnosticsEndpoint()
    {
        // Formato vale com diagnóstico on/off; conteúdo varia com o modo.
        var resp = await Http.GetFromJsonAsync<JsonObject>($"{WebBase}/Chat/Diagnostics");
        return resp?.ContainsKey("enabled") == true && resp?["snapshots"] is JsonArray;
    }

    private static async Task<bool> ToolProgressEvents()
    {
        // Pede um ls: o servidor deve emitir tool_start + tool_done no SSE.
        var createResp = await Http.PostAsJsonAsync($"{WebBase}/Chat/Sessions", new { agentId = 1, title = "Progress Test" });
        if (!createResp.IsSuccessStatusCode) return false;
        var session = await createResp.Content.ReadFromJsonAsync<JsonObject>();
        var id = session?["id"]?.GetValue<int>();
        if (id == null || id <= 0) return false;

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{WebBase}/Chat/Stream");
        req.Content = JsonContent.Create(new { sessionId = id, agentId = 1, messages = new[] { new { role = "user", content = "liste os arquivos com ls" } } });
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        if (!resp.IsSuccessStatusCode) return false;

        using var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new System.IO.StreamReader(stream);
        string? line;
        bool started = false, done = false;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            if (!line.StartsWith("data: ")) continue;
            var data = line[6..].Trim();
            if (data == "[DONE]") break;
            try
            {
                var evt = JsonNode.Parse(data)?.AsObject();
                if (evt?["progress"]?.GetValue<string>() == "tool_start") started = true;
                if (evt?["progress"]?.GetValue<string>() == "tool_done") done = true;
            }
            catch { }
        }
        return started && done;
    }
}