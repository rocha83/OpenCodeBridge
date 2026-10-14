// Suite unit + integracao da UI web (console, exit 0 PASS / 1 FAIL).
// Unit: hash, validacao de Agent. Integracao: CRUD sqlite real + HTTP (login, chat proxy).
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Rochas.DapperRepository;
using Rochas.Data.Specification.Enums;
using Rochas.OpenCodeBridge.Web.Data;
using Rochas.OpenCodeBridge.Web.Models;
using Rochas.OpenCodeBridge.Web.Services;

namespace Rochas.OpenCodeBridge.Web.Test;

internal static class Program
{
    static int Failures;
    static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false });

    static void Check(bool ok, string name)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) Failures++;
    }

    static int Main(string[] args)
    {
        string web = "http://127.0.0.1:4130";
        bool ui = false;
        string mode = "plan";
        string phase = "full";
        int exec = 0;
        var belts = new List<string>();
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--web") web = args[i + 1];
            if (args[i] == "--mode") mode = args[i + 1];
            if (args[i] == "--phase") phase = args[i + 1];
            if (args[i] == "--exec" && int.TryParse(args[i + 1], out int e)) exec = e;
            if (args[i] == "--belts") belts.AddRange(args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries));
        }
        ui = args.Contains("--ui");
        bool unitsOnly = args.Contains("--units-only");
        bool integrationOnly = args.Contains("--integration-only");
        bool skipUnits = args.Contains("--skip-units");
        bool skipIntegration = args.Contains("--skip-integration");
        if (exec > 0) Ui.BeltUiTests.SetExecOverride(exec);

        if (!integrationOnly && !skipUnits)
        {
            UnitHash();
            UnitAgent();
            UnitContextWindow();
            UnitSessionService().GetAwaiter().GetResult();
            Failures += Rochas.OpenCodeBridge.Web.Test.Unit.ToolExecutorTests.Run();
            Failures += Rochas.OpenCodeBridge.Web.Test.Unit.DeepSeekToolCoercionTests.Run();
            Failures += Rochas.OpenCodeBridge.Web.Test.Unit.DiagnosticsTests.Run();
            Failures += Rochas.OpenCodeBridge.Web.Test.Unit.ResponsesBypassTests.Run();
            Failures += Rochas.OpenCodeBridge.Web.Test.Unit.SessionTaskPanelTests.Run();
            IntegrationCrud().GetAwaiter().GetResult();
        }
        if (!unitsOnly && !skipIntegration)
        {
            IntegrationHttp(web).GetAwaiter().GetResult();
            Integration.ChatIntegrationTests.RunAsync(web).GetAwaiter().GetResult();
        }
        if (ui)
            Failures += Ui.BeltUiTests.RunAsync(web, belts.ToArray(), mode, phase).GetAwaiter().GetResult();
        if (args.Contains("--simple"))
            Failures += Ui.SimpleHelloTests.RunAsync(web).GetAwaiter().GetResult();
        if (args.Contains("--ui-behavior"))
            Failures += Ui.ChatBehaviorTests.RunAsync(web).GetAwaiter().GetResult();

        Console.WriteLine(Failures == 0 ? "[test] PASS" : $"[test] FAIL={Failures}");
        return Failures == 0 ? 0 : 1;
    }

    // Unit: hash verifica certo e rejeita errado; formato com 3 partes.
    static void UnitHash()
    {
        string h = PasswordHasher.Hash("Admin@123");
        Check(h.Split('.').Length == 3, "U-hash-format");
        Check(PasswordHasher.Verify("Admin@123", h), "U-hash-ok");
        Check(!PasswordHasher.Verify("errada", h), "U-hash-reject");
        Check(!PasswordHasher.Verify("x", "lixo"), "U-hash-malformed");
    }

    // Unit: defaults do Agent apontam p/ linha GPU.
    static void UnitAgent()
    {
        // Defaults zerados de propósito: props não-default poluem o WHERE do Dapper.
        var a = new Agent();
        Check(a.BridgeUrl == "" && a.Model == "" && a.Temperature == 0 && !a.Active, "U-agent-blank-defaults");
        Check(Agent.EffectiveMode(a) == "build", "U-agent-effective-mode");
    }

    static void UnitContextWindow()
    {
        Rochas.OpenCodeBridge.Web.Test.Unit.ContextWindowTests.Run();
    }

    static async Task UnitSessionService()
    {
        await Rochas.OpenCodeBridge.Web.Test.Unit.SessionServiceTests.RunAsync();
    }

    // Integracao: CRUD real no sqlite temporario via GenericRepository.
    static async Task IntegrationCrud()
    {
        string db = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"webtest-{Guid.NewGuid():N}.db");
        try
        {
            AppDb.Init(db);
            var users = new GenericRepository<User>(DatabaseEngine.SQLite, AppDb.ConnectionString);
            await users.Add(new User { Name = "T", Email = "t@t.com", PasswordHash = PasswordHasher.Hash("x") });
            var got = (await users.Query(new User { Email = "t@t.com" })).FirstOrDefault();
            Check(got is not null && got.Name == "T", "I-crud-add-get");
            if (got is null) return;
            got.Name = "T2";
            await users.Update(got, new User { Id = got.Id });
            var upd = await users.Get(new User { Id = got.Id });
            Check(upd?.Name == "T2", "I-crud-update");
            await users.Remove(new User { Id = got.Id });
            Check(await users.Get(new User { Id = got.Id }) is null, "I-crud-remove");

            var agents = new GenericRepository<Agent>(DatabaseEngine.SQLite, AppDb.ConnectionString);
            await agents.Add(new Agent { Name = "gpu", Model = "qwen3-8b-awq" });
            var list = await agents.Query(new Agent());
            Check(list.Any(a => a.Name == "gpu"), "I-crud-agent");
            // ORM: Active=true filtra no WHERE; Search usa as [Filterable].
            await agents.Add(new Agent { Name = "zzq-ativo", Model = "zzq-model", Mode = "Plan", Thinking = "off", SystemPrompt = "zzq prompt shell linux", Active = true });
            await agents.Add(new Agent { Name = "zzq-inativo", Model = "zzq-model", Active = false });
            var actives = await agents.Query(new Agent { Active = true });
            Check(actives.All(a => a.Active) && actives.Any(a => a.Name == "zzq-ativo") && !actives.Any(a => a.Name == "zzq-inativo"), "I-orm-active-true");
            var byModel = await agents.Query(new Agent { Model = "zzq-model" });
            Check(byModel.Count() == 2, "I-orm-query-model");
            var byMode = await agents.Query(new Agent { Mode = "Plan" });
            Check(byMode.Any(a => a.Name == "zzq-ativo"), "I-orm-query-mode");
            var byThinking = await agents.Query(new Agent { Thinking = "off" });
            Check(byThinking.Any(a => a.Name == "zzq-ativo"), "I-orm-query-thinking");
            var byName = await agents.Search("zzq-ativo");
            Check(byName.Any(a => a.Name == "zzq-ativo"), "I-orm-search-nome");
            var byPrompt = await agents.Search("zzq prompt shell");
            Check(byPrompt.Any(a => a.Name == "zzq-ativo"), "I-orm-search-prompt");
        }
        finally
        {
            try { System.IO.File.Delete(db); } catch { }
        }
    }

    // Integracao: HTTP no servidor web (anonimo redireciona; login admin entra; chat exige agente).
    static async Task IntegrationHttp(string web)
    {
        try
        {
            using var anon = await Http.GetAsync(web + "/Chat");
            Check(anon.StatusCode == HttpStatusCode.Redirect || anon.StatusCode == HttpStatusCode.Unauthorized, "I-http-anon-guard");
        }
        catch (Exception ex)
        {
            Check(false, "I-http-anon-guard (" + ex.Message + ")");
            return;
        }
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        using var client = new HttpClient(handler);
        // Antiforgery: token da página de login (cookie + form).
        var loginPage = await client.GetStringAsync(web + "/Account/Login");
        var m = System.Text.RegularExpressions.Regex.Match(loginPage, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Check(m.Success, "I-http-af-token");
        var form = new Dictionary<string, string> { ["email"] = "admin@bridge.pcss.com.br", ["password"] = "123" };
        if (m.Success) form["__RequestVerificationToken"] = m.Groups[1].Value;
        var login = await client.PostAsync(web + "/Account/Login", new FormUrlEncodedContent(form));
        Check(login.StatusCode == HttpStatusCode.Redirect, "I-http-login");
        var chat = await client.GetAsync(web + "/Chat");
        Check(chat.IsSuccessStatusCode, "I-http-chat");
        // Token embute a identidade: reler o pós-login (anônimo não vale autenticado).
        var chatHtml = await chat.Content.ReadAsStringAsync();
        var m2 = System.Text.RegularExpressions.Regex.Match(chatHtml, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        var streamTok = m2.Success ? m2.Groups[1].Value : (m.Success ? m.Groups[1].Value : "");
        var agents = await client.GetAsync(web + "/Agents");
        Check(agents.IsSuccessStatusCode, "I-http-agents");
        var streamReq = new HttpRequestMessage(HttpMethod.Post, web + "/Chat/Stream");
        streamReq.Content = JsonContent.Create(new { agentId = 999999, messages = new[] { new { role = "user", content = "oi" } } });
        if (!string.IsNullOrEmpty(streamTok)) streamReq.Headers.Add("RequestVerificationToken", streamTok);
        var bad = await client.SendAsync(streamReq);
        Check(bad.StatusCode == HttpStatusCode.NotFound, "I-http-chat-404");
        await AntiforgeryEnforced(client, web, m.Success ? m.Groups[1].Value : "", streamTok);
    }

    // Antiforgery nativo: sem token 400; token anônimo pós-login 400
    // (username embutido difere); token fresco autenticado 200.
    static async Task AntiforgeryEnforced(HttpClient client, string web, string anonTok, string freshTok)
    {
        static HttpRequestMessage Mut(string web, string tok)
        {
            var r = new HttpRequestMessage(HttpMethod.Post, web + "/Chat/Sessions")
            {
                Content = JsonContent.Create(new { agentId = 1, title = "AF-TESTE" })
            };
            if (!string.IsNullOrEmpty(tok)) r.Headers.Add("RequestVerificationToken", tok);
            return r;
        }
        var noTok = await client.SendAsync(Mut(web, ""));
        Check(noTok.StatusCode == HttpStatusCode.BadRequest, "I-af-sem-token-400");
        int created = 0;
        if (!string.IsNullOrEmpty(anonTok) && anonTok != freshTok)
        {
            var stale = await client.SendAsync(Mut(web, anonTok));
            Check(stale.StatusCode == HttpStatusCode.BadRequest, "I-af-token-anonimo-400");
        }
        if (!string.IsNullOrEmpty(freshTok))
        {
            var okResp = await client.SendAsync(Mut(web, freshTok));
            Check(okResp.IsSuccessStatusCode, "I-af-token-fresco-200");
            try
            {
                var jo = await okResp.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>();
                created = jo?["id"]?.GetValue<int>() ?? 0;
            }
            catch { }
        }
        if (created > 0)
        {
            var del = new HttpRequestMessage(HttpMethod.Delete, web + $"/Chat/Sessions/{created}");
            if (!string.IsNullOrEmpty(freshTok)) del.Headers.Add("RequestVerificationToken", freshTok);
            await client.SendAsync(del);
        }
    }
}
