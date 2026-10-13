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
    // Retry se 0 linhas afetadas (race INSERT vs UPDATE em conexões diferentes).
    private static async Task StampAsync(string sql, params (string Name, object Value)[] args)
    {
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            using (var conn1 = new Microsoft.Data.Sqlite.SqliteConnection(AppDb.ConnectionString))
            {
                await conn1.OpenAsync();
                using (var cmd1 = conn1.CreateCommand())
                {
                    cmd1.CommandText = sql;
                    foreach (var (name, value) in args)
                        cmd1.Parameters.AddWithValue(name, value);
                    int rows = await cmd1.ExecuteNonQueryAsync();
                    if (rows > 0) return;
                }
            }
            // 0 linhas: race INSERT/UPDATE - espera e tenta de novo
            await Task.Delay(50 * attempt);
        }
        // última tentativa sem checar linhas (pode falhar silenciosamente)
        using (var conn2 = new Microsoft.Data.Sqlite.SqliteConnection(AppDb.ConnectionString))
        {
            await conn2.OpenAsync();
            using (var cmd2 = conn2.CreateCommand())
            {
                cmd2.CommandText = sql;
                foreach (var (name, value) in args)
                    cmd2.Parameters.AddWithValue(name, value);
                await cmd2.ExecuteNonQueryAsync();
            }
        }
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
        // created_at nunca atualizado: vem só do INSERT; aqui só updated_at.
        await StampAsync("UPDATE sessions SET updated_at=@u WHERE id=@id",
            ("@u", Iso(now)), ("@id", first.Id ?? 0));
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
        // created_at nunca atualizado: SQL direto só em title + updated_at (ORM truncaria).
        var stamp = System.DateTime.UtcNow;
        await StampAsync("UPDATE sessions SET title=@title, updated_at=@t WHERE id=@id",
            ("@title", title), ("@t", Iso(stamp)), ("@id", sessionId));
    }

    public async Task UpdateAgentAsync(int sessionId, int agentId)
    {
        // created_at nunca atualizado: SQL direto só em agent_id + updated_at (ORM truncaria).
        var stamp = System.DateTime.UtcNow;
        await StampAsync("UPDATE sessions SET agent_id=@a, updated_at=@t WHERE id=@id",
            ("@a", agentId), ("@t", Iso(stamp)), ("@id", sessionId));
    }

    public async Task UpdateExecutorAsync(int sessionId, int? executorAgentId)
    {
        // created_at nunca atualizado: SQL direto só em executor + updated_at (ORM truncaria).
        var stamp = System.DateTime.UtcNow;
        await StampAsync("UPDATE sessions SET executor_agent_id=@e, updated_at=@t WHERE id=@id",
            ("@e", (object?)executorAgentId ?? System.DBNull.Value), ("@t", Iso(stamp)), ("@id", sessionId));
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
        // Desempate por Id: SQLite trunca CreatedAt em segundos e turnos
        // rápidos empatam — sem isto o histórico volta embaralhado.
        return list.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).TakeLast(limit).ToList();
    }

    public async Task<int> AddMessageAsync(int sessionId, string role, string content, string thinking, int? promptTokens, int? completionTokens)
    {
        // INSERT direto + last_insert_rowid na mesma conexão: id confiável
        // p/ vincular as tools do turno a esta mensagem (bolha dona).
        return await DbRetryAsync(async () =>
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(AppDb.ConnectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO session_messages (session_id, role, content, thinking, prompt_tokens, completion_tokens) VALUES (@s,@r,@c,@t,@p,@e); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("@s", sessionId);
            cmd.Parameters.AddWithValue("@r", role ?? "");
            cmd.Parameters.AddWithValue("@c", content ?? "");
            cmd.Parameters.AddWithValue("@t", thinking ?? "");
            cmd.Parameters.AddWithValue("@p", (object?)promptTokens ?? System.DBNull.Value);
            cmd.Parameters.AddWithValue("@e", (object?)completionTokens ?? System.DBNull.Value);
            var id = await cmd.ExecuteScalarAsync();
            return System.Convert.ToInt32(id);
        });
    }

    public async Task<int> CountMessagesAsync(int sessionId)
    {
        var filter = new SessionMessage { SessionId = (int?)sessionId };
        var list = await _messages.Query(filter);
        return list.Count;
    }

    public async Task<int> CompactAsync(int sessionId, Func<string, Task<string?>> summarize, int keepLast = 8)
    {
        var all = (await GetMessagesAsync(sessionId, int.MaxValue)).Where(m => m.Archived == 0).ToList();
        if (all.Count <= keepLast + 1) return 0;
        var head = all[..^keepLast];
        var sb = new System.Text.StringBuilder();
        foreach (var m in head)
            sb.AppendLine($"[{m.Role}] {(m.Content ?? "").Trim()}");
        string? summary = null;
        try { summary = await summarize(sb.ToString()); } catch { }
        if (string.IsNullOrWhiteSpace(summary)) return 0;
        // Marca o antigo como passado (não apaga: segue visível no histórico,
        // fora do contexto). Resumo entra entre o passado e a cauda.
        // Resumo entra entre o passado e a cauda. -1s (não ticks: SQLite
        // trunca em segundos e o resumo cairia no mesmo segundo da cauda).
        var summaryAt = all[^keepLast].CreatedAt.AddSeconds(-1);
        await DbRetryAsync(async () =>
        {
            foreach (var m in head)
            {
                if (!m.Id.HasValue) continue;
                await _messagesWrite.Remove(new SessionMessage { Id = m.Id });
                await _messagesWrite.Add(new SessionMessage
                {
                    SessionId = sessionId, Role = m.Role, Content = m.Content,
                    Thinking = m.Thinking, CreatedAt = m.CreatedAt, Archived = 1,
                });
            }
            await _messagesWrite.Add(new SessionMessage
            {
                SessionId = sessionId, Role = "assistant",
                Content = "[Resumo da conversa até aqui] " + summary!.Trim(),
                Thinking = "", CreatedAt = summaryAt, Archived = 0,
            });
        });
        await TouchAsync(sessionId);
        return head.Count;
    }

    public async Task TouchAsync(int sessionId)
    {
        var s = await DbRetryAsync(async () => await _sessions.Get(new Session { Id = (int?)sessionId }));
        if (s is not null)
        {
            // Precisão de segundo do SQLite + relógio pode saltar (dual-boot/RTC):
            // trunca o now (determinístico p/ teste) e nunca deixa futuro para trás.
            // created_at nunca atualizado: só updated_at via SQL direto (ORM truncaria).
            var now = System.DateTime.UtcNow;
            var sec = new System.DateTime(now.Ticks - now.Ticks % System.TimeSpan.TicksPerSecond, System.DateTimeKind.Utc);
            var stamp = sec > s.UpdatedAt ? sec : (s.UpdatedAt > now ? sec : s.UpdatedAt.AddSeconds(1));
            await StampAsync("UPDATE sessions SET updated_at=@t WHERE id=@id",
                ("@t", Iso(stamp)), ("@id", sessionId));
        }
    }

    // Trilha de tools no SQLite (como o thinking): best-effort, nunca quebra o pipeline.
    public async Task<int> LogToolAsync(int sessionId, string agent, string name, string args, bool ok, string output, long ms, string? sub = null, int? messageId = null)
    {
        try
        {
            string a = args;
            string o = output;
            return await DbRetryAsync(async () =>
            {
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection(AppDb.ConnectionString);
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO tool_calls (session_id, agent, name, args, ok, output, ms, sub, message_id) VALUES (@s,@a,@n,@g,@o,@u,@m,@b,@d); SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("@s", sessionId);
                cmd.Parameters.AddWithValue("@a", agent ?? "");
                cmd.Parameters.AddWithValue("@n", name ?? "");
                cmd.Parameters.AddWithValue("@g", a);
                cmd.Parameters.AddWithValue("@o", ok ? 1 : 0);
                cmd.Parameters.AddWithValue("@u", o);
                cmd.Parameters.AddWithValue("@m", ms);
                cmd.Parameters.AddWithValue("@b", sub ?? "");
                cmd.Parameters.AddWithValue("@d", (object?)messageId ?? System.DBNull.Value);
                var id = await cmd.ExecuteScalarAsync();
                return System.Convert.ToInt32(id);
            });
        }
        catch { return 0; /* log nunca quebra o pipeline */ }
    }

    // Liga órfãs (turno sem texto / card que falhou) à mensagem final.
    public async Task LinkToolsByIdsAsync(int sessionId, IEnumerable<int> toolIds, int messageId)
    {
        try
        {
            var ids = toolIds.Where(i => i > 0).Distinct().ToList();
            if (ids.Count == 0) return;
            await DbRetryAsync(async () =>
            {
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection(AppDb.ConnectionString);
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"UPDATE tool_calls SET message_id=@m WHERE session_id=@s AND message_id IS NULL AND id IN ({string.Join(",", ids)})";
                cmd.Parameters.AddWithValue("@m", messageId);
                cmd.Parameters.AddWithValue("@s", sessionId);
                await cmd.ExecuteNonQueryAsync();
                return 0;
            });
        }
        catch { /* best-effort */ }
    }

    // Liga as tools internas do sub (gravadas sem message_id) ao card dele.
    public async Task LinkSubToolsAsync(int sessionId, string sub, int messageId)
    {
        try
        {
            await DbRetryAsync(async () =>
            {
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection(AppDb.ConnectionString);
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE tool_calls SET message_id=@m WHERE session_id=@s AND sub=@b AND message_id IS NULL";
                cmd.Parameters.AddWithValue("@m", messageId);
                cmd.Parameters.AddWithValue("@s", sessionId);
                cmd.Parameters.AddWithValue("@b", sub ?? "");
                await cmd.ExecuteNonQueryAsync();
                return 0;
            });
        }
        catch { /* best-effort */ }
    }

    public async Task<List<ToolCall>> GetToolCallsAsync(int sessionId, int limit = 200)
    {
        var list = new List<ToolCall>();
        await DbRetryAsync(async () =>
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(AppDb.ConnectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, session_id, agent, name, args, ok, output, ms, message_id, sub, created_at FROM tool_calls WHERE session_id=@s ORDER BY id LIMIT @l";
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
                    MessageId = r.IsDBNull(8) ? null : r.GetInt32(8),
                    Sub = r.IsDBNull(9) ? "" : r.GetString(9),
                });
            }
            return 0;
        });
        return list;
    }
}