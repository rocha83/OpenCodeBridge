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
    // Retorna o id inserido (vínculo tool->bolha). statusId nulo deriva do papel (user=1, demais=2).
    Task<int> AddMessageAsync(int sessionId, string role, string content, string thinking, int? promptTokens, int? completionTokens, long? elapsedMs = null, double? tps = null, int? statusId = null);
    // Progressiva: atualiza a bolha viva do turno (texto acumulado + usage no DONE).
    Task UpdateMessageAsync(int id, string content, string thinking, int? promptTokens = null, int? completionTokens = null, long? elapsedMs = null, double? tps = null);
    Task<int> CountMessagesAsync(int sessionId);
    // Compactação: resume o histórico antigo via summarize, apaga as antigas,
    // insere o resumo como mensagem do agente e reordena a cauda. Retorna
    // quantas mensagens antigas viraram resumo (0 = nada a fazer/falhou).
    Task<int> CompactAsync(int sessionId, Func<string, Task<string?>> summarize, int keepLast = 8);
    Task TouchAsync(int sessionId);
    // Retorna o id inserido. sub = id do subagente dono (só internas);
    // messageId = mensagem dona (turno do agente; internas ligam depois).
    Task<int> LogToolAsync(int sessionId, string agent, string name, string args, bool ok, string output, long ms, string? sub = null, int? messageId = null);
    Task<List<ToolCall>> GetToolCallsAsync(int sessionId, int limit = 200);
    // Liga as tools internas do sub à mensagem do card dele (pós-save).
    Task LinkSubToolsAsync(int sessionId, string sub, int messageId);
    // Liga órfãs (turno sem texto / card que falhou) à mensagem final.
    Task LinkToolsByIdsAsync(int sessionId, IEnumerable<int> toolIds, int messageId);
    // Artefato da sessão (ex.: zip dos scripts): upsert por nome.
    Task SaveArtifactAsync(int sessionId, string name, string mime, string b64);
    // Lê artefato (nome exato) ou null.
    Task<(string Mime, string B64)?> GetArtifactAsync(int sessionId, string name);
}
