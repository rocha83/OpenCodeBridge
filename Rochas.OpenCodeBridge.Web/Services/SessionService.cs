using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Rochas.Data.Specification.Interfaces;
using Rochas.OpenCodeBridge.Web.Data;
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

    // Contorno das limitações do SQLite sob acesso concorrente (leitores do polling +
    // escritores dos executores): retry com backoff só em falhas transitórias de handle
    // (SafeHandle null, stmt descartado, busy/locked). Não mascara erro de SQL ou schema.
    private static bool IsTransientDb(Exception ex) =>
        ex is ObjectDisposedException
        || ex is ArgumentNullException
        || (ex is Microsoft.Data.Sqlite.SqliteException se
            && (se.SqliteErrorCode == 5 || se.SqliteErrorCode == 6)); // busy, locked

    private static async Task<T> DbRetryAsync<T>(Func<Task<T>> op, int tries = 5)
    {
        for (int i = 1; ; i++)
        {
            try { return await op(); }
            catch (Exception ex) when (i < tries && IsTransientDb(ex))
            {
                await Task.Delay(100 * i * i);
            }
        }
    }

    private static Task DbRetryAsync(Func<Task> op, int tries = 5) =>
        DbRetryAsync(async () => { await op(); return 0; }, tries);

    // WORKAROUND (remover quando SqlWrapper>=1.5.1 chegar via DapperRepository):
    // o parser emite DateTime como data em UPDATE (hotfix rocha83/SqlWrapper 1.5.1).
    // Carimbo com precisão de hora via SQL direto.
    private static async Task StampAsync(string sql, params (string Name, object Value)[] args)
    {
        await DbRetryAsync(async () =>
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(AppDb.ConnectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in args)
                cmd.Parameters.AddWithValue(name, value);
            await cmd.ExecuteNonQueryAsync();
            return 0;
        });
    }

    private static string Iso(System.DateTime dt) =>
        dt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss");

    public async Task<Session> CreateAsync(int userId, int agentId, string title, int? executorAgentId = null)
    {
        var now = System.DateTime.UtcNow;
        var s = new Session { UserId = userId, AgentId = agentId, ExecutorAgentId = executorAgentId, Title = title, CreatedAt = now, UpdatedAt = now };
        await DbRetryAsync(async () => await _sessionsWrite.Add(s));
        // Obter o ID gerado (last_insert_rowid)
        var created = await DbRetryAsync(async () => await _sessions.Query(new Session { UserId = userId, AgentId = agentId, Title = title }));
        var first = created.OrderByDescending(x => x.Id ?? 0).First();
        await StampAsync("UPDATE sessions SET created_at=@c, updated_at=@u WHERE id=@id",
            ("@c", Iso(now)), ("@u", Iso(now)), ("@id", first.Id ?? 0));
        first.CreatedAt = now;
        first.UpdatedAt = now;
        return first;
    }

    public async Task<List<Session>> GetByUserAsync(int userId)
    {
        var filter = new Session { UserId = userId };
        var list = await DbRetryAsync(async () => await _sessions.Query(filter));
        return list.OrderByDescending(x => x.UpdatedAt).ToList();
    }

    public async Task<Session?> GetAsync(int sessionId, int userId)
    {
        var filter = new Session { Id = (int?)sessionId, UserId = userId };
        var list = await DbRetryAsync(async () => await _sessions.Query(filter));
        return list.FirstOrDefault();
    }

    public async Task UpdateTitleAsync(int sessionId, string title)
    {
        var s = await _sessions.Get(new Session { Id = (int?)sessionId });
        if (s is not null)
        {
            s.Title = title;
            var stamp = System.DateTime.UtcNow;
            s.UpdatedAt = stamp;
            await _sessionsWrite.Update(s, new Session { Id = (int?)sessionId });
            await StampAsync("UPDATE sessions SET updated_at=@t WHERE id=@id",
                ("@t", Iso(stamp)), ("@id", sessionId));
        }
    }

    public async Task UpdateAgentAsync(int sessionId, int agentId)
    {
        var s = await _sessions.Get(new Session { Id = (int?)sessionId });
        if (s is not null)
        {
            s.AgentId = agentId;
            var stamp = System.DateTime.UtcNow;
            s.UpdatedAt = stamp;
            await _sessionsWrite.Update(s, new Session { Id = (int?)sessionId });
            await StampAsync("UPDATE sessions SET updated_at=@t WHERE id=@id",
                ("@t", Iso(stamp)), ("@id", sessionId));
        }
    }

    public async Task UpdateExecutorAsync(int sessionId, int? executorAgentId)
    {
        var s = await _sessions.Get(new Session { Id = (int?)sessionId });
        if (s is not null)
        {
            s.ExecutorAgentId = executorAgentId;
            var stamp = System.DateTime.UtcNow;
            s.UpdatedAt = stamp;
            await _sessionsWrite.Update(s, new Session { Id = (int?)sessionId });
            await StampAsync("UPDATE sessions SET updated_at=@t WHERE id=@id",
                ("@t", Iso(stamp)), ("@id", sessionId));
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
        var list = await DbRetryAsync(async () => await _messages.Query(filter));
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
            CompletionTokens = completionTokens,
            CreatedAt = System.DateTime.UtcNow
        };
        await DbRetryAsync(async () => await _messagesWrite.Add(msg));
    }

    public async Task<int> CountMessagesAsync(int sessionId)
    {
        var filter = new SessionMessage { SessionId = (int?)sessionId };
        var list = await _messages.Query(filter);
        return list.Count;
    }

    public async Task TouchAsync(int sessionId)
    {
        var s = await DbRetryAsync(async () => await _sessions.Get(new Session { Id = (int?)sessionId }));
        if (s is not null)
        {
            // SQLite tem precisão de segundo: avança +1s do valor gravado
            // (sempre visível no banco; now em ms seria truncado para igual).
            s.UpdatedAt = s.UpdatedAt.AddSeconds(1);
            await _sessionsWrite.Update(s, new Session { Id = (int?)sessionId });
            await StampAsync("UPDATE sessions SET updated_at=@t WHERE id=@id",
                ("@t", Iso(s.UpdatedAt)), ("@id", sessionId));
        }
    }

    // Trilha de tools no SQLite (como o thinking): best-effort, nunca quebra o pipeline.
    public async Task LogToolAsync(int sessionId, string agent, string name, string args, bool ok, string output, long ms)
    {
        try
        {
            string a = args.Length > 2000 ? args[..2000] + "\n[truncado]" : args;
            string o = output.Length > 2000 ? output[..2000] + "\n[truncado]" : output;
            await DbRetryAsync(async () =>
            {
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection(AppDb.ConnectionString);
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO tool_calls (session_id, agent, name, args, ok, output, ms) VALUES (@s,@a,@n,@g,@o,@u,@m)";
                cmd.Parameters.AddWithValue("@s", sessionId);
                cmd.Parameters.AddWithValue("@a", agent ?? "");
                cmd.Parameters.AddWithValue("@n", name ?? "");
                cmd.Parameters.AddWithValue("@g", a);
                cmd.Parameters.AddWithValue("@o", ok ? 1 : 0);
                cmd.Parameters.AddWithValue("@u", o);
                cmd.Parameters.AddWithValue("@m", ms);
                await cmd.ExecuteNonQueryAsync();
                return 0;
            });
        }
        catch { /* log nunca quebra o pipeline */ }
    }

    public async Task<List<ToolCall>> GetToolCallsAsync(int sessionId, int limit = 200)
    {
        var list = new List<ToolCall>();
        await DbRetryAsync(async () =>
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(AppDb.ConnectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, session_id, agent, name, args, ok, output, ms, created_at FROM tool_calls WHERE session_id=@s ORDER BY id LIMIT @l";
            cmd.Parameters.AddWithValue("@s", sessionId);
            cmd.Parameters.AddWithValue("@l", Math.Max(1, limit));
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                list.Add(new ToolCall
                {
                    Id = r.IsDBNull(0) ? null : r.GetInt32(0),
                    SessionId = r.IsDBNull(1) ? null : r.GetInt32(1),
                    Agent = r.IsDBNull(2) ? "" : r.GetString(2),
                    Name = r.IsDBNull(3) ? "" : r.GetString(3),
                    Args = r.IsDBNull(4) ? "" : r.GetString(4),
                    Ok = !r.IsDBNull(5) && r.GetInt32(5) != 0,
                    Output = r.IsDBNull(6) ? "" : r.GetString(6),
                    Ms = r.IsDBNull(7) ? 0 : r.GetInt64(7),
                });
            }
            return 0;
        });
        return list;
    }
}