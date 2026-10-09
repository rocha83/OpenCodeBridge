using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Rochas.Data.Specification.Interfaces;
using Rochas.OpenCodeBridge.Web.Models;

namespace Rochas.OpenCodeBridge.Web.Services;

// Serviço de sessões de chat (CRUD + mensagens). Comentários pt-BR.
public sealed class SessionService(
    IGenericRepository<Session> sessions,
    IPersistenceRepository<Session> sessionsWrite,
    IGenericRepository<SessionMessage> messages,
    IPersistenceRepository<SessionMessage> messagesWrite) : ISessionService
{
    private readonly IGenericRepository<Session> _sessions = sessions;
    private readonly IPersistenceRepository<Session> _sessionsWrite = sessionsWrite;
    private readonly IGenericRepository<SessionMessage> _messages = messages;
    private readonly IPersistenceRepository<SessionMessage> _messagesWrite = messagesWrite;

    public async Task<Session> CreateAsync(int userId, int agentId, string title)
    {
        var s = new Session { UserId = userId, AgentId = agentId, Title = title };
        await _sessionsWrite.Add(s);
        // Obter o ID gerado (last_insert_rowid)
        var created = await _sessions.Query(new Session { UserId = userId, AgentId = agentId, Title = title });
        return created.OrderByDescending(x => x.Id ?? 0).First();
    }

    public async Task<List<Session>> GetByUserAsync(int userId)
    {
        var filter = new Session { UserId = userId };
        var list = await _sessions.Query(filter);
        return list.OrderByDescending(x => x.UpdatedAt).ToList();
    }

    public async Task<Session?> GetAsync(int sessionId, int userId)
    {
        var filter = new Session { Id = (int?)sessionId, UserId = userId };
        var list = await _sessions.Query(filter);
        return list.FirstOrDefault();
    }

    public async Task UpdateTitleAsync(int sessionId, string title)
    {
        var s = await _sessions.Get(new Session { Id = (int?)sessionId });
        if (s is not null)
        {
            s.Title = title;
            s.UpdatedAt = System.DateTime.UtcNow;
            await _sessionsWrite.Update(s, new Session { Id = (int?)sessionId });
        }
    }

    public async Task UpdateAgentAsync(int sessionId, int agentId)
    {
        var s = await _sessions.Get(new Session { Id = (int?)sessionId });
        if (s is not null)
        {
            s.AgentId = agentId;
            s.UpdatedAt = System.DateTime.UtcNow;
            await _sessionsWrite.Update(s, new Session { Id = (int?)sessionId });
        }
    }

    public async Task DeleteAsync(int sessionId, int userId)
    {
        var s = await GetAsync(sessionId, userId);
        if (s is not null)
        {
            // Mensagens deletadas em cascata via FK ON DELETE CASCADE
            await _sessionsWrite.Remove(new Session { Id = (int?)sessionId });
        }
    }

    public async Task<List<SessionMessage>> GetMessagesAsync(int sessionId, int limit = 50)
    {
        var filter = new SessionMessage { SessionId = sessionId };
        var list = await _messages.Query(filter);
        return list.OrderBy(x => x.CreatedAt).TakeLast(limit).ToList();
    }

    public async Task AddMessageAsync(int sessionId, string role, string content, string thinking, int? promptTokens, int? completionTokens)
    {
        var msg = new SessionMessage
        {
            SessionId = sessionId,
            Role = role,
            Content = content,
            Thinking = thinking,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens
        };
        await _messagesWrite.Add(msg);
    }

    public async Task<int> CountMessagesAsync(int sessionId)
    {
        var filter = new SessionMessage { SessionId = (int?)sessionId };
        var list = await _messages.Query(filter);
        return list.Count;
    }

    public async Task TouchAsync(int sessionId)
    {
        var s = await _sessions.Get(new Session { Id = (int?)sessionId });
        if (s is not null)
        {
            s.UpdatedAt = System.DateTime.UtcNow;
            await _sessionsWrite.Update(s, new Session { Id = (int?)sessionId });
        }
    }
}