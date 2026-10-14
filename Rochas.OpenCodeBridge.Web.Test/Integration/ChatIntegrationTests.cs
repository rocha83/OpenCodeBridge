// Testes de integração do Chat (HTTP real contra servidor web).
// Requer servidor rodando em --web http://host:port
// Execução: dotnet run -c Release --project Rochas.OpenCodeBridge.Web.Test -- --web http://127.0.0.1:4150

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Rochas.DapperRepository;
using Rochas.Data.Specification.Enums;
using Rochas.OpenCodeBridge.Web.Data;
using Rochas.OpenCodeBridge.Web.Models;
using Rochas.OpenCodeBridge.Web.Services;

namespace Rochas.OpenCodeBridge.Web.Test.Integration;

internal static class ChatIntegrationTests
{
    private static HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false });
    private static string WebBase = "http://127.0.0.1:4150";
    private static CookieContainer Cookies = new();
    private static int Failures;
    private static string AfToken = "";

    private static async Task FetchAfToken()
    {
        try
        {
            var html = await Http.GetStringAsync($"{WebBase}/Account/Login");
            var m = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
            if (m.Success) AfToken = m.Groups[1].Value;
        }
        catch { }
    }

    private static Task<HttpResponseMessage> JPost(string url, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (!string.IsNullOrEmpty(AfToken)) req.Headers.Add("RequestVerificationToken", AfToken);
        return Http.SendAsync(req);
    }

    private static Task<HttpResponseMessage> JPut(string url, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
        if (!string.IsNullOrEmpty(AfToken)) req.Headers.Add("RequestVerificationToken", AfToken);
        return Http.SendAsync(req);
    }

    private static Task<HttpResponseMessage> JDelete(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Delete, url);
        if (!string.IsNullOrEmpty(AfToken)) req.Headers.Add("RequestVerificationToken", AfToken);
        return Http.SendAsync(req);
    }

    private static Task<HttpResponseMessage> JPostForm(string url, HttpContent content)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (!string.IsNullOrEmpty(AfToken)) req.Headers.Add("RequestVerificationToken", AfToken);
        return Http.SendAsync(req);
    }

    private static async Task<HttpResponseMessage> JPostFormAdmin(HttpClient admin, string url, HttpContent content)
    {
        string tok = "";
        try
        {
            var html = await admin.GetStringAsync($"{WebBase}/Account/Login");
            var m = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
            if (m.Success) tok = m.Groups[1].Value;
        }
        catch { }
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        if (!string.IsNullOrEmpty(tok)) req.Headers.Add("RequestVerificationToken", tok);
        return await admin.SendAsync(req);
    }
    private static readonly List<int> CreatedSessions = new();

    internal static async Task RunAsync(string webBase)
    {
        WebBase = webBase;
        Http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = Cookies });

        // Login admin
        await LoginAdmin();

        // Cookie antiforgery gira no login: reler o token pós-login.
        await FetchAfToken();

        Check(await AnonRedirectsToLogin(), "I-chat-login-redirect");
        Check(await AdminSeesAgentsUsersLinks(), "I-chat-admin-sees-agents-users");
        Check(await SessionsCrud(), "I-chat-sessions-crud");
        Check(await StreamSseWithSession(), "I-chat-stream-sse");
        Check(await AbortKeepsMessageThenResume(), "I-chat-abort-resume");
        Check(await ClientLogAcceptsBatch(), "I-chat-clientlog");
        Check(await PingEngine(), "I-chat-ping-engine");
        Check(await DiagnosticsEndpoint(), "I-chat-diagnostics");
        Check(await ToolProgressEvents(), "I-chat-tool-progress");

        // Não suja a UI real: apaga as sessões criadas pelos testes.
        foreach (int sid in CreatedSessions.Distinct())
        {
            try { await JDelete($"{WebBase}/Chat/Sessions/{sid}"); } catch { }
        }

        Console.WriteLine($"=== Chat Integration: {9 - Failures}/9 PASS, {Failures} FAIL ===");
    }

    private static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) Failures++;
    }

    private static async Task<bool> LoginAdmin()
    {
        // Bateria isolada no usuário de teste (nunca polui as sessões do admin na UI).
        // Token antes do login (cookie+form); FetchAfToken renova o pós-login.
        await FetchAfToken();
        var resp = await JPostForm($"{WebBase}/Account/Login",
            new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("email", "teste@e2e.local"), new KeyValuePair<string, string>("password", "Teste@123") }));
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
        // Links de admin exigem o admin de verdade (bateria roda no usuário de teste).
        using var admin = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = new CookieContainer() });
        var login = await JPostFormAdmin(admin,$"{WebBase}/Account/Login",
            new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("email", "admin@bridge.pcss.com.br"), new KeyValuePair<string, string>("password", "123") }));
        if (login.StatusCode != HttpStatusCode.Redirect) return false;
        var html = await admin.GetStringAsync($"{WebBase}/Chat");
        return html.Contains("/Agents") && html.Contains("/Users");
    }

    private static async Task<bool> SessionsCrud()
    {
        // Create
        var createResp = await JPost($"{WebBase}/Chat/Sessions", new { agentId = 1, title = "Teste Integração" });
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
        var putResp = await JPut($"{WebBase}/Chat/Sessions/{id}/Title", new { title = "Atualizado" });
        if (!putResp.IsSuccessStatusCode) return false;

        // Update agent
        var putAgentResp = await JPut($"{WebBase}/Chat/Sessions/{id}/Agent", new { agentId = 2 });
        if (!putAgentResp.IsSuccessStatusCode) return false;

        // Delete
        var delResp = await JDelete($"{WebBase}/Chat/Sessions/{id}");
        if (!delResp.IsSuccessStatusCode) return false;

        // Verify deleted
        var listAfter = await Http.GetFromJsonAsync<JsonArray>($"{WebBase}/Chat/Sessions");
        return listAfter == null || !listAfter.Any(x => x?["id"]?.GetValue<int>() == id);
    }

    private static async Task<bool> StreamSseWithSession()
    {
        // Create session
        var createResp = await JPost($"{WebBase}/Chat/Sessions", new { agentId = 1, title = "Stream Test" });
        if (!createResp.IsSuccessStatusCode) return false;
        var session = await createResp.Content.ReadFromJsonAsync<JsonObject>();
        var id = session?["id"]?.GetValue<int>();
        if (id == null || id <= 0) return false;
        CreatedSessions.Add(id.Value);

        // Stream
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{WebBase}/Chat/Stream");
        if (!string.IsNullOrEmpty(AfToken)) req.Headers.Add("RequestVerificationToken", AfToken);
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

    private static async Task<bool> AbortKeepsMessageThenResume()
    {
        // 1) Mensagem garantida sem resposta: stream abortado no início.
        var createResp = await JPost($"{WebBase}/Chat/Sessions", new { agentId = 1, title = "Resume Test" });
        if (!createResp.IsSuccessStatusCode) return false;
        var session = await createResp.Content.ReadFromJsonAsync<JsonObject>();
        var id = session?["id"]?.GetValue<int>();
        if (id == null || id <= 0) return false;
        CreatedSessions.Add(id.Value);

        using (var cts = new CancellationTokenSource())
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{WebBase}/Chat/Stream");
        if (!string.IsNullOrEmpty(AfToken)) req.Headers.Add("RequestVerificationToken", AfToken);
            req.Content = JsonContent.Create(new { sessionId = id, agentId = 1, messages = new[] { new { role = "user", content = "ping resume" } } });
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            try { using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token); } catch { }
        }
        await Task.Delay(1500);
        var mid = await Http.GetFromJsonAsync<JsonArray>($"{WebBase}/Chat/Sessions/{id}/Messages");
        if (mid == null) return false;
        var userRows = mid.Where(x => x?["role"]?.GetValue<string>() == "user").ToList();
        // Pergunta persistida e marcada Enviada, sem duplicação; última é user ou parcial.
        if (userRows.Count != 1 || userRows[0]?["content"]?.GetValue<string>() != "ping resume") return false;
        if (userRows[0]?["status"]?.GetValue<string>() != "Enviada") return false;
        var lastRole = mid.LastOrDefault()?["role"]?.GetValue<string>();
        if (lastRole != "user" && lastRole != "assistant") return false;

        // 2) Retomada: stream completo continua do histórico.
        using (var req2 = new HttpRequestMessage(HttpMethod.Post, $"{WebBase}/Chat/Stream"))
        {
            req2.Content = JsonContent.Create(new { sessionId = id, agentId = 1, messages = new[] { new { role = "user", content = "continue" } } });
            if (!string.IsNullOrEmpty(AfToken)) req2.Headers.Add("RequestVerificationToken", AfToken);
            using var resp2 = await Http.SendAsync(req2, HttpCompletionOption.ResponseHeadersRead);
            if (!resp2.IsSuccessStatusCode) return false;
            using var stream = await resp2.Content.ReadAsStreamAsync();
            using var reader = new System.IO.StreamReader(stream);
            string? line; bool done = false;
            while ((line = await reader.ReadLineAsync()) != null)
                if (line.Trim() == "data: [DONE]") { done = true; break; }
            if (!done) return false;
        }
        var fin = await Http.GetFromJsonAsync<JsonArray>($"{WebBase}/Chat/Sessions/{id}/Messages");
        if (fin == null) return false;
        // Sem duplicação da pergunta; última é resposta marcada Respondida; tudo com status.
        if (fin.Count(x => x?["role"]?.GetValue<string>() == "user") != 2) return false;
        var flast = fin.LastOrDefault();
        if (flast?["role"]?.GetValue<string>() != "assistant") return false;
        if (flast?["status"]?.GetValue<string>() != "Respondida") return false;
        return fin.All(x => !string.IsNullOrEmpty(x?["status"]?.GetValue<string>()));
    }

    private static async Task<bool> ClientLogAcceptsBatch()
    {
        var resp = await JPost($"{WebBase}/Chat/ClientLog",
            new { sessionId = (int?)null, events = new[] { new { t = "2026-10-13T00:00:00Z", source = "ui:test", level = "info", msg = "tracer ok", data = (string?)null } } });
        if (!resp.IsSuccessStatusCode) return false;
        var j = await resp.Content.ReadFromJsonAsync<JsonObject>();
        return j?["ok"]?.GetValue<bool>() == true && j?["kept"]?.GetValue<int>() == 1;
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
        var createResp = await JPost($"{WebBase}/Chat/Sessions", new { agentId = 1, title = "Progress Test" });
        if (!createResp.IsSuccessStatusCode) return false;
        var session = await createResp.Content.ReadFromJsonAsync<JsonObject>();
        var id = session?["id"]?.GetValue<int>();
        if (id == null || id <= 0) return false;
        CreatedSessions.Add(id.Value);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{WebBase}/Chat/Stream");
        if (!string.IsNullOrEmpty(AfToken)) req.Headers.Add("RequestVerificationToken", AfToken);
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