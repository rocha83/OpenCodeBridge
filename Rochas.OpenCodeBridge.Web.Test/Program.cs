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
        var belts = new List<string>();
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--web") web = args[i + 1];
            if (args[i] == "--mode") mode = args[i + 1];
            if (args[i] == "--belts") belts.AddRange(args[i + 1].Split(',', StringSplitOptions.RemoveEmptyEntries));
        }
        ui = args.Contains("--ui");

        UnitHash();
        UnitAgent();
        UnitContextWindow();
        UnitSessionService().GetAwaiter().GetResult();
        Failures += Rochas.OpenCodeBridge.Web.Test.Unit.ToolExecutorTests.Run();
        Failures += Rochas.OpenCodeBridge.Web.Test.Unit.DiagnosticsTests.Run();
        Failures += Rochas.OpenCodeBridge.Web.Test.Unit.OrchestrationTests.Run();
        Failures += Rochas.OpenCodeBridge.Web.Test.Unit.SessionTaskPanelTests.Run();
        IntegrationCrud().GetAwaiter().GetResult();
        IntegrationHttp(web).GetAwaiter().GetResult();
        Integration.ChatIntegrationTests.RunAsync(web).GetAwaiter().GetResult();
        if (ui)
            Failures += Ui.BeltUiTests.RunAsync(web, belts.ToArray(), mode).GetAwaiter().GetResult();

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
        var login = await client.PostAsync(web + "/Account/Login",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = "admin@mova.com", ["password"] = "Admin@123" }));
        Check(login.StatusCode == HttpStatusCode.Redirect, "I-http-login");
        var chat = await client.GetAsync(web + "/Chat");
        Check(chat.IsSuccessStatusCode, "I-http-chat");
        var agents = await client.GetAsync(web + "/Agents");
        Check(agents.IsSuccessStatusCode, "I-http-agents");
        var bad = await client.PostAsync(web + "/Chat/Stream",
            JsonContent.Create(new { agentId = 999999, messages = new[] { new { role = "user", content = "oi" } } }));
        Check(bad.StatusCode == HttpStatusCode.NotFound, "I-http-chat-404");
    }
}
