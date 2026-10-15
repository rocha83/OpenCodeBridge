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
    private static int Total;

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
            var svc = new SessionService(sessionsRepo, sessionsRepo, messagesRepo, messagesRepo);

            await CreateSessionAsync(svc, testUserId, testAgentId);
            await GetByUserAsync(svc, testUserId);
            await GetAsync(svc, testUserId);
            await UpdateTitleAsync(svc);
            await UpdateAgentAsync(svc);
            await AutoApproveAsync(svc);
            await DeleteAsync(svc, testUserId);
            await GetMessagesAsync(svc, testUserId, testAgentId);
            await AddMessageAsync(svc, testUserId, testAgentId);
            await UpdateMessageAsync(svc, testUserId, testAgentId);
            await CountMessagesAsync(svc, testUserId, testAgentId);
            await MessageStatusAsync(svc, testUserId, testAgentId);
            await TouchAsync(svc, testUserId, testAgentId);
            await ToolCallAsync(svc, testUserId, testAgentId);
            await HistoryTreeAsync(svc, testUserId, testAgentId);
            await MessageSearchAsync(svc, messagesRepo, sessionsRepo, testUserId, testAgentId);

            System.Console.WriteLine($"=== SessionService Unit: {Total - Failures}/{Total} PASS, {Failures} FAIL ===");
        }
        finally
        {
            try { File.Delete(db); } catch { }
        }
    }

    private static void Check(bool ok, string name)
    {
        Total++;
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
            var sessionsRepo2 = new GenericRepository<Session>(DatabaseEngine.SQLite, AppDb.ConnectionString);
            var messagesRepo2 = new GenericRepository<SessionMessage>(DatabaseEngine.SQLite, AppDb.ConnectionString);
            var got = await new SessionService(sessionsRepo2, sessionsRepo2, messagesRepo2, messagesRepo2).GetAsync(id, userId);
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
        var agentsRepo = new GenericRepository<Agent>(DatabaseEngine.SQLite, AppDb.ConnectionString);
        await agentsRepo.Add(new Agent { Name = "Second Agent", Model = "m2" });
        var second = (await agentsRepo.Query(new Agent { Name = "Second Agent" })).FirstOrDefault();
        var list = await new GenericRepository<Session>(DatabaseEngine.SQLite, AppDb.ConnectionString).Query(new Session { UserId = 1 });
        var first = list.FirstOrDefault();
        if (first?.Id is int id && second?.Id is int execId)
        {
            await s.UpdateAgentAsync(id, execId);
            var updated = await s.GetAsync(id, 1);
            Check(updated?.AgentId == execId, "UpdateAgentAsync works");
        }
    }

    private static async Task AutoApproveAsync(SessionService s)
    {
        var list = await new GenericRepository<Session>(DatabaseEngine.SQLite, AppDb.ConnectionString).Query(new Session { UserId = 1 });
        var first = list.FirstOrDefault();
        if (first?.Id is int id)
        {
            Check(first.AutoApprove == false, "AutoApprove default off");
            await s.UpdateAutoApproveAsync(id, true);
            var on = await s.GetAsync(id, 1);
            Check(on?.AutoApprove == true, "UpdateAutoApproveAsync on");
            await s.UpdateAutoApproveAsync(id, false);
            var off = await s.GetAsync(id, 1);
            Check(off?.AutoApprove == false, "UpdateAutoApproveAsync off");
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

    private static async Task UpdateMessageAsync(SessionService s, int userId, int agentId)
    {
        // Bolha viva: INSERT parcial + UPDATE progressivo + usage no fim, sem duplicar.
        var session = await s.CreateAsync(userId, agentId, "Update Msg");
        if (session.Id is int id)
        {
            int mid = await s.AddMessageAsync(id, "assistant", "parcial", "th1", null, null);
            await s.UpdateMessageAsync(mid, "parcial + fim", "th1 th2");
            var msgs = await s.GetMessagesAsync(id, 10);
            Check(msgs.Count == 1, "UpdateMessageAsync sem duplicar");
            Check(msgs[0].Content == "parcial + fim" && msgs[0].Thinking == "th1 th2", "UpdateMessageAsync atualiza texto");
            await s.UpdateMessageAsync(mid, "parcial + fim", "th1 th2", 100, 20, 500, 3.5);
            msgs = await s.GetMessagesAsync(id, 10);
            Check(msgs[0].PromptTokens == 100 && msgs[0].CompletionTokens == 20, "UpdateMessageAsync grava usage");
        }
    }

    private static async Task MessageSearchAsync(SessionService s,
        Rochas.Data.Specification.Interfaces.IGenericRepository<SessionMessage> messagesRepo,
        Rochas.Data.Specification.Interfaces.IGenericRepository<Session> sessionsRepo,
        int userId, int agentId)
    {
        // Search atinge conteúdo de user/assistant/subagent; range de datas filtra sessões.
        var session = await s.CreateAsync(userId, agentId, "Busca Msg");
        if (session.Id is not int id) return;
        await s.AddMessageAsync(id, "user", "zzqmsg pergunta", "", null, null);
        await s.AddMessageAsync(id, "assistant", "zzqmsg resposta", "", null, null);
        await s.AddMessageAsync(id, "subagent", "zzqmsg script", "", null, null);
        var hits = await messagesRepo.Search("zzqmsg");
        var roles = hits.Where(m => m.SessionId == id).Select(m => m.Role).ToHashSet();
        Check(roles.SetEquals(new[] { "user", "assistant", "subagent" }), "MsgSearch atinge 3 papeis");
        var mine = await sessionsRepo.Query(new Session
        {
            UserId = userId,
            CreatedAt = DateTime.UtcNow.AddDays(-1),
            CreatedAte = DateTime.UtcNow.AddDays(1),
        });
        Check(mine.Any(x => x.Id == id), "Session range encontra");
        var fora = await sessionsRepo.Query(new Session
        {
            CreatedAt = DateTime.UtcNow.AddDays(-30),
            CreatedAte = DateTime.UtcNow.AddDays(-20),
        });
        Check(!fora.Any(x => x.Id == id), "Session range exclui");
        var semFiltro = await sessionsRepo.Query(new Session());
        Check(semFiltro.Any(x => x.Id == id), "Session sem filtro inclui");
    }

    private static async Task MessageStatusAsync(SessionService s, int userId, int agentId)
    {
        // Mapeamento puro (tabela message_statuses + fallback).
        Check(SessionService.MessageStatus(1, "assistant", 0) == "Enviada", "status id 1");
        Check(SessionService.MessageStatus(2, "user", 0) == "Respondida", "status id 2");
        Check(SessionService.MessageStatus(3, "user", 0) == "Arquivada", "status id 3");
        Check(SessionService.MessageStatus(null, "user", 0) == "Enviada", "status nulo user");
        Check(SessionService.MessageStatus(null, "assistant", 0) == "Respondida", "status nulo assistant");
        Check(SessionService.MessageStatus(null, "subagent", 0) == "Respondida", "status nulo subagent");
        Check(SessionService.MessageStatus(null, "assistant", 1) == "Arquivada", "status nulo arquivado");
        // Default no INSERT por papel + explícito.
        var session = await s.CreateAsync(userId, agentId, "Status Test");
        if (session.Id is int id)
        {
            await s.AddMessageAsync(id, "user", "oi", "", null, null);
            await s.AddMessageAsync(id, "assistant", "olá", "", null, null);
            await s.AddMessageAsync(id, "assistant", "velha", "", null, null, null, null, 3);
            var msgs = await s.GetMessagesAsync(id, 10);
            Check(msgs.Count == 3, "status 3 mensagens");
            Check(msgs[0].StatusId == 1, "status user=Enviada");
            Check(msgs[1].StatusId == 2, "status assistant=Respondida");
            Check(msgs[2].StatusId == 3, "status explícito Arquivada");
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

    private static async Task ToolCallAsync(SessionService s, int userId, int agentId)
    {
        var session = await s.CreateAsync(userId, agentId, "Tool Log");
        if (session.Id is int id)
        {
            await s.LogToolAsync(id, "agente-teste", "shell", "{\"command\": \"echo oi\"}", true, "oi", 12);
            await s.LogToolAsync(id, "agente-teste", "write", "{\"path\": \"x\"}", false, "negado", 3);
            var calls = await s.GetToolCallsAsync(id);
            Check(calls.Count == 2 && calls[0].Name == "shell" && calls[0].Ok && calls[0].Ms == 12
                && calls[1].Name == "write" && !calls[1].Ok, "ToolCallAsync log+query");
            // Vínculo tool->bolha: turno do agente + internas do sub.
            int turnId = await s.AddMessageAsync(id, "assistant", "turno", "", null, null);
            Check(turnId > 0, "AddMessageAsync returns id");
            int t1 = await s.LogToolAsync(id, "agente-teste", "read", "{}", true, "conteúdo", 5, null, turnId);
            int subId = await s.AddMessageAsync(id, "subagent", "feito", "", null, null);
            await s.LogToolAsync(id, "agente-teste", "shell", "{}", true, "ok", 7, "sub-abc123");
            await s.LinkSubToolsAsync(id, "sub-abc123", subId);
            var all = await s.GetToolCallsAsync(id);
            var turn = all.FirstOrDefault(c => c.Id == t1);
            var inner = all.FirstOrDefault(c => c.Sub == "sub-abc123");
            Check(turn?.MessageId == turnId, "ToolCallAsync outer linked to turn");
            Check(inner?.MessageId == subId, "ToolCallAsync inner linked to sub card");
            // Órfã fecha na mensagem final.
            int orphan = await s.LogToolAsync(id, "agente-teste", "grep", "{}", true, "x", 1);
            int final = await s.AddMessageAsync(id, "assistant", "fim", "", null, null);
            await s.LinkToolsByIdsAsync(id, new[] { orphan }, final);
            var all2 = await s.GetToolCallsAsync(id);
            Check(all2.FirstOrDefault(c => c.Id == orphan)?.MessageId == final, "ToolCallAsync orphan linked to final");
            // Artefato: zip em base64 com upsert por nome.
            await s.SaveArtifactAsync(id, "scripts.zip", "application/zip", "e30=");
            await s.SaveArtifactAsync(id, "scripts.zip", "application/zip", "e31=");
            var art = await s.GetArtifactAsync(id, "scripts.zip");
            Check(art?.B64 == "e31=" && art?.Mime == "application/zip", "ArtifactAsync upsert+get");
            Check(await s.GetArtifactAsync(id, "nada.zip") is null, "ArtifactAsync missing null");
        }
    }

    private static async Task HistoryTreeAsync(SessionService s, int userId, int agentId)
    {
        // Árvore da conversa: user -> turnos assistant -> subagent (texto+thinking separados).
        var session = await s.CreateAsync(userId, agentId, "History Tree");
        if (session.Id is int id)
        {
            await s.AddMessageAsync(id, "user", "faça em fases", "", null, null);
            await s.AddMessageAsync(id, "assistant", "fase 1 pronta", "pensei...", null, null);
            await s.AddMessageAsync(id, "subagent", "script fase 1", "raciocínio sub", null, null);
            await s.AddMessageAsync(id, "assistant", "resumo final", "", null, null);
            var msgs = await s.GetMessagesAsync(id);
            Check(msgs.Count == 4, "HistoryTree keeps all turns");
            Check(msgs[0].Role == "user" && msgs[1].Role == "assistant"
                && msgs[2].Role == "subagent" && msgs[3].Role == "assistant", "HistoryTree order+roles");
            Check(msgs[2].Content == "script fase 1" && msgs[2].Thinking == "raciocínio sub",
                "HistoryTree subagent text+thinking split");
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