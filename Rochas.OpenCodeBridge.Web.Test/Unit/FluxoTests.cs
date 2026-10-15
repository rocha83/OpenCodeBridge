// Testes do Fluxo: prompt_controls (chave string) + lockout de login + calda.
// Execução: dotnet run -c Release --project Rochas.OpenCodeBridge.Web.Test
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Rochas.DapperRepository;
using Rochas.Data.Specification.Enums;
using Rochas.OpenCodeBridge.Web.Data;
using Rochas.OpenCodeBridge.Web.Models;
using Rochas.OpenCodeBridge.Web.Services;

namespace Rochas.OpenCodeBridge.Web.Test.Unit;

internal static class FluxoTests
{
    private static int Failures;
    private static int Total;

    private static void Check(bool ok, string name)
    {
        Total++;
        Console.WriteLine((ok ? "PASS " : "FAIL ") + "fluxo:" + name);
        if (!ok) Failures++;
    }

    internal static async Task<int> RunAsync()
    {
        string db = Path.Combine(Path.GetTempPath(), $"fluxotest-{Guid.NewGuid():N}.db");
        try
        {
            AppDb.Init(db);
            var repo = new GenericRepository<PromptControl>(DatabaseEngine.SQLite, AppDb.ConnectionString);

            // Seed: 5 controles padrao (janela 128).
            var all = await repo.Query(new PromptControl());
            Check(all.Count == 5, "seed-5-controles");
            Check(all.Any(c => c.Key == "anti_repeat_window" && c.Value == "128"), "seed-janela-128");

            // Get por chave string + Update pela mesma chave.
            var ctl = await repo.Get(new PromptControl { Key = "system_tail" });
            Check(ctl is not null && ctl.Key == "system_tail", "get-chave-string");
            ctl!.Value = "calda de teste";
            await repo.Update(ctl, new PromptControl { Key = "system_tail" });
            var back = await repo.Get(new PromptControl { Key = "system_tail" });
            Check(back?.Value == "calda de teste", "update-chave-string");

            // Busca: OR em memoria (igual ao controller).
            var every = await repo.Query(new PromptControl());
            var found = every.Where(c => (c.Label ?? "").Contains("calda", StringComparison.OrdinalIgnoreCase));
            Check(found.Any(), "search-rotulo");

            // Service: leitura + invalidacao.
            var svc = new PromptControlService(repo);
            Check(await svc.GetAsync("system_tail") == "calda de teste", "service-get");
            ctl.Value = "nova calda";
            await repo.Update(ctl, new PromptControl { Key = "system_tail" });
            Check(await svc.GetAsync("system_tail") == "calda de teste", "service-cache");
            svc.Invalidate();
            Check(await svc.GetAsync("system_tail") == "nova calda", "service-invalidate");

            // Lockout: colunas novas com default zerado.
            var users = new GenericRepository<User>(DatabaseEngine.SQLite, AppDb.ConnectionString);
            await users.Add(new User { Name = "L", Email = "l@l.com", PasswordHash = "h", Active = true });
            var u = (await users.Query(new User { Email = "l@l.com" })).First();
            Check(u.FailedAttempts == 0 && u.LockedUntil == "", "lockout-default");
            u.FailedAttempts = 5;
            u.LockedUntil = "2099-01-01 00:00:00";
            await users.Update(u, new User { Id = u.Id });
            var u2 = (await users.Query(new User { Email = "l@l.com" })).First();
            Check(u2.FailedAttempts == 5 && u2.LockedUntil != "", "lockout-persiste");
        }
        finally { try { File.Delete(db); } catch { } }
        Console.WriteLine($"fluxo: {Total - Failures}/{Total} PASS");
        return Failures;
    }
}
