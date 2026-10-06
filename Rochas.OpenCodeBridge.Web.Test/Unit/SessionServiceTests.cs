// Testes unitários de SessionService (usa SQLite arquivo temporário para isolamento).
// Execução: dotnet run -c Release --project Rochas.OpenCodeBridge.Web.Test -- --web http://127.0.0.1:4130

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using Rochas.DapperRepository;
using Rochas.Data.Specification.Enums;
using Rochas.OpenCodeBridge.Web.Data;
using Rochas.OpenCodeBridge.Web.Models;
using Rochas.OpenCodeBridge.Web.Services;

namespace Rochas.OpenCodeBridge.Web.Test.Unit;

internal static class SessionServiceTests
{
    private static int Failures;

    internal static async Task RunAsync()
    {
        string db = Path.Combine(Path.GetTempPath(), $"sessiontest-{Guid.NewGuid():N}.db");
        try
        {
            AppDb.Init(db);

            var usersRepo = new GenericRepository<User>(DatabaseEngine.SQLite, AppDb.ConnectionString);
            var agentsRepo = new GenericRepository<Agent>(DatabaseEngine.SQLite, AppDb.ConnectionString);

            // Seed user + agent - capture actual IDs
            var user = new User { Name = "Test User", Email = "test@test.com", PasswordHash = "hash", Active = true, IsAdmin = false };
            await usersRepo.Add(user);
            var agent = new Agent { Name = "Test Agent", Model = "test-model", BridgeUrl = "http://localhost:4124", Active = true };
            await agentsRepo.Add(agent);

            // Refresh to get IDs
            var userFromDb = (await usersRepo.Query(new User { Email = "test@test.com" })).FirstOrDefault();
            var agentFromDb = (await agentsRepo.Query(new Agent { Name = "Test Agent" })).FirstOrDefault();
            int testUserId = userFromDb?.Id ?? 1;
            int testAgentId = agentFromDb?.Id ?? 1;

            var sessionsRepo = new GenericRepository<Session>(DatabaseEngine.SQLite, AppDb.ConnectionString);
            var messagesRepo = new GenericRepository<SessionMessage>(DatabaseEngine.SQLite, AppDb.ConnectionString);
            var svc = new SessionService(sessionsRepo, messagesRepo);

            await CreateSessionAsync(svc, testUserId, testAgentId);
            await GetByUserAsync(svc, testUserId);
            await GetAsync(svc, testUserId);
            await UpdateTitleAsync(svc);
            await UpdateAgentAsync(svc);
            await DeleteAsync(svc, testUserId);
            await GetMessagesAsync(svc, testUserId, testAgentId);
            await AddMessageAsync(svc, testUserId, testAgentId);
            await CountMessagesAsync(svc, testUserId, testAgentId);
            await TouchAsync(svc, testUserId, testAgentId);

            System.Console.WriteLine($"=== SessionService Unit: {10 - Failures}/10 PASS, {Failures} FAIL ===");
        }
        finally
        {
            try { File.Delete(db); } catch { }
        }
    }

    private static void Check(bool ok, string name)
    {
        System.Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) Failures++;
    }

    private static async Task CreateSessionAsync(SessionService s, int userId, int agentId)
    {
        var session = await s.CreateAsync(userId, agentId, "Teste");
        Check(session.Id.HasValue && session.Id > 0, "CreateAsync returns id");
        Check(session.Title == "Teste", "CreateAsync title");
        Check(session.UserId == userId && session.AgentId == agentId, "CreateAsync ids");
    }

    private static async Task GetByUserAsync(SessionService s, int userId)
    {
        var list = await s.GetByUserAsync(userId);
        Check(list.Count >= 1, "GetByUserAsync returns list");
    }

    private static async Task GetAsync(SessionService s, int userId)
    {
        var list = await new GenericRepository<Session>(DatabaseEngine.SQLite, AppDb.ConnectionString).Query(new Session { UserId = userId });
        var first = list.FirstOrDefault();
        if (first?.Id is int id)
        {
            var got = await new SessionService(
                new GenericRepository<Session>(DatabaseEngine.SQLite, AppDb.ConnectionString),
                new GenericRepository<SessionMessage>(DatabaseEngine.SQLite, AppDb.ConnectionString)
            ).GetAsync(id, userId);
            Check(got is not null && got.Id == id, "GetAsync finds by id+user");
        }
    }

    private static async Task UpdateTitleAsync(SessionService s)
    {
        var list = await new GenericRepository<Session>(DatabaseEngine.SQLite, AppDb.ConnectionString).Query(new Session { UserId = 1 });
        var first = list.FirstOrDefault();
        if (first?.Id is int id)
        {
            await s.UpdateTitleAsync(id, "Novo Título");
            var updated = await s.GetAsync(id, 1);
            Check(updated?.Title == "Novo Título", "UpdateTitleAsync works");
        }
    }

    private static async Task UpdateAgentAsync(SessionService s)
    {
        var list = await new GenericRepository<Session>(DatabaseEngine.SQLite, AppDb.ConnectionString).Query(new Session { UserId = 1 });
        var first = list.FirstOrDefault();
        if (first?.Id is int id)
        {
            await s.UpdateAgentAsync(id, 2);
            var updated = await s.GetAsync(id, 1);
            Check(updated?.AgentId == 2, "UpdateAgentAsync works");
        }
    }

    private static async Task DeleteAsync(SessionService s, int userId)
    {
        var list = await new GenericRepository<Session>(DatabaseEngine.SQLite, AppDb.ConnectionString).Query(new Session { UserId = userId });
        var first = list.FirstOrDefault();
        if (first?.Id is int id)
        {
            await s.DeleteAsync(id, userId);
            var gone = await s.GetAsync(id, userId);
            Check(gone is null, "DeleteAsync removes session");
        }
    }

    private static async Task GetMessagesAsync(SessionService s, int userId, int agentId)
    {
        var session = await s.CreateAsync(userId, agentId, "Msg Test");
        if (session.Id is int id)
        {
            await s.AddMessageAsync(id, "user", "olá", "", null, null);
            await s.AddMessageAsync(id, "assistant", "oi!", "thinking...", 10, 5);
            var msgs = await s.GetMessagesAsync(id);
            Check(msgs.Count == 2, "GetMessagesAsync returns both");
            Check(msgs[0].Role == "user" && msgs[1].Role == "assistant", "GetMessagesAsync order");
            Check(msgs[1].Thinking == "thinking..." && msgs[1].PromptTokens == 10, "GetMessagesAsync preserves metadata");
        }
    }

    private static async Task AddMessageAsync(SessionService s, int userId, int agentId)
    {
        var session = await s.CreateAsync(userId, agentId, "Add Msg");
        if (session.Id is int id)
        {
            await s.AddMessageAsync(id, "user", "teste", "", null, null);
            var count = await s.CountMessagesAsync(id);
            Check(count == 1, "AddMessageAsync increments count");
        }
    }

    private static async Task CountMessagesAsync(SessionService s, int userId, int agentId)
    {
        var session = await s.CreateAsync(userId, agentId, "Count Test");
        if (session.Id is int id)
        {
            Check(await s.CountMessagesAsync(id) == 0, "CountMessagesAsync zero initially");
            await s.AddMessageAsync(id, "user", "a", "", null, null);
            await s.AddMessageAsync(id, "assistant", "b", "", null, null);
            Check(await s.CountMessagesAsync(id) == 2, "CountMessagesAsync after adds");
        }
    }

    private static async Task TouchAsync(SessionService s, int userId, int agentId)
    {
        var session = await s.CreateAsync(userId, agentId, "Touch Test");
        if (session.Id is int id)
        {
            var before = (await new GenericRepository<Session>(DatabaseEngine.SQLite, AppDb.ConnectionString).Get(new Session { Id = (int?)id }))?.UpdatedAt;
            await Task.Delay(10);
            await s.TouchAsync(id);
            var after = (await new GenericRepository<Session>(DatabaseEngine.SQLite, AppDb.ConnectionString).Get(new Session { Id = (int?)id }))?.UpdatedAt;
            Check(after > before, "TouchAsync updates UpdatedAt");
        }
    }
}