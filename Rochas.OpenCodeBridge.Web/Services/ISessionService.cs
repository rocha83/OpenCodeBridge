using Rochas.OpenCodeBridge.Web.Models;

namespace Rochas.OpenCodeBridge.Web.Services;

// Contrato do serviço de sessões de chat (domínio; implementação via repositórios).
public interface ISessionService
{
    Task<Session> CreateAsync(int userId, int agentId, string title, int? executorAgentId = null);
    Task<List<Session>> GetByUserAsync(int userId);
    Task<Session?> GetAsync(int sessionId, int userId);
    Task UpdateTitleAsync(int sessionId, string title);
    Task UpdateAgentAsync(int sessionId, int agentId);
    Task UpdateExecutorAsync(int sessionId, int? executorAgentId);
    Task DeleteAsync(int sessionId, int userId);
    Task<List<SessionMessage>> GetMessagesAsync(int sessionId, int limit = 50);
    Task AddMessageAsync(int sessionId, string role, string content, string thinking, int? promptTokens, int? completionTokens);
    Task<int> CountMessagesAsync(int sessionId);
    Task TouchAsync(int sessionId);
}
