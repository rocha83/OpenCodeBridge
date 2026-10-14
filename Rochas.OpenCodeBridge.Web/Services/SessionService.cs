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

    public async Task<Session> CreateAsync(int userId, int agentId, string title, int? executorAgentId = null)
    {
        var now = System.DateTime.UtcNow;
        var s = new Session { UserId = userId, AgentId = agentId, ExecutorAgentId = executorAgentId, Title = title, CreatedAt = now, UpdatedAt = now };
        await DbRetryAsync(async () => await _sessionsWrite.Add(s));
        // Obter o ID gerado (last_insert_rowid)
        var created = await DbRetryAsync(async () => await _sessions.Query(new Session { UserId = userId, AgentId = agentId, Title = title }));
        var first = created.OrderByDescending(x => x.Id ?? 0).First();
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

    // Updates via ORM (DapperRepository 2.0.5): busca, altera e salva a
    // entidade completa, preservando created_at.
    private async Task UpdateSessionAsync(int sessionId, Action<Session> change)
    {
        var list = await DbRetryAsync(async () => await _sessions.Query(new Session { Id = (int?)sessionId }));
        var s = list.FirstOrDefault();
        if (s is null) return;
        change(s);
        await DbRetryAsync(async () => await _sessionsWrite.Update(s, new Session { Id = (int?)sessionId }));
    }

    public async Task UpdateTitleAsync(int sessionId, string title)
    {
        var stamp = System.DateTime.UtcNow;
        await UpdateSessionAsync(sessionId, s => { s.Title = title; s.UpdatedAt = stamp; });
    }

    public async Task UpdateAgentAsync(int sessionId, int agentId)
    {
        var stamp = System.DateTime.UtcNow;
        await UpdateSessionAsync(sessionId, s => { s.AgentId = agentId; s.UpdatedAt = stamp; });
    }

    public async Task UpdateExecutorAsync(int sessionId, int? executorAgentId)
    {
        var stamp = System.DateTime.UtcNow;
        await UpdateSessionAsync(sessionId, s => { s.ExecutorAgentId = executorAgentId; s.UpdatedAt = stamp; });
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

    // Rótulo de status p/ o rodapé da bolha (tabela message_statuses; nulo deriva).
    public static string MessageStatus(int? statusId, string role, int archived) =>
        statusId switch
        {
            1 => "Enviada",
            2 => "Respondida",
            3 => "Arquivada",
            _ => archived != 0 ? "Arquivada" : (role == "user" ? "Enviada" : "Respondida"),
        };

    public async Task<int> AddMessageAsync(int sessionId, string role, string content, string thinking, int? promptTokens, int? completionTokens, long? elapsedMs = null, double? tps = null, int? statusId = null)
    {
        // INSERT direto + last_insert_rowid na mesma conexão: id confiável
        // p/ vincular as tools do turno a esta mensagem (bolha dona).
        int st = statusId ?? (role == "user" ? 1 : 2);
        return await DbRetryAsync(async () =>
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(AppDb.ConnectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO session_messages (session_id, role, content, thinking, prompt_tokens, completion_tokens, elapsed_ms, tps, status_id) VALUES (@s,@r,@c,@t,@p,@e,@m,@v,@y); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("@s", sessionId);
            cmd.Parameters.AddWithValue("@r", role ?? "");
            cmd.Parameters.AddWithValue("@c", content ?? "");
            cmd.Parameters.AddWithValue("@t", thinking ?? "");
            cmd.Parameters.AddWithValue("@p", (object?)promptTokens ?? System.DBNull.Value);
            cmd.Parameters.AddWithValue("@e", (object?)completionTokens ?? System.DBNull.Value);
            cmd.Parameters.AddWithValue("@m", (object?)elapsedMs ?? System.DBNull.Value);
            cmd.Parameters.AddWithValue("@v", (object?)tps ?? System.DBNull.Value);
            cmd.Parameters.AddWithValue("@y", st);
            var id = await cmd.ExecuteScalarAsync();
            return System.Convert.ToInt32(id);
        });
    }

    public async Task UpdateMessageAsync(int id, string content, string thinking, int? promptTokens = null, int? completionTokens = null, long? elapsedMs = null, double? tps = null)
    {
        // UPDATE progressivo da bolha viva: texto cresce por turno; usage só no DONE
        // (COALESCE preserva o que já está gravado quando o parâmetro é nulo).
        await DbRetryAsync(async () =>
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(AppDb.ConnectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE session_messages SET content=@c, thinking=@t, prompt_tokens=COALESCE(@p,prompt_tokens), completion_tokens=COALESCE(@e,completion_tokens), elapsed_ms=COALESCE(@m,elapsed_ms), tps=COALESCE(@v,tps) WHERE id=@id";
            cmd.Parameters.AddWithValue("@c", content ?? "");
            cmd.Parameters.AddWithValue("@t", thinking ?? "");
            cmd.Parameters.AddWithValue("@p", (object?)promptTokens ?? System.DBNull.Value);
            cmd.Parameters.AddWithValue("@e", (object?)completionTokens ?? System.DBNull.Value);
            cmd.Parameters.AddWithValue("@m", (object?)elapsedMs ?? System.DBNull.Value);
            cmd.Parameters.AddWithValue("@v", (object?)tps ?? System.DBNull.Value);
            cmd.Parameters.AddWithValue("@id", id);
            await cmd.ExecuteNonQueryAsync();
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
                    Thinking = m.Thinking, CreatedAt = m.CreatedAt, Archived = 1, StatusId = 3,
                });
            }
            await _messagesWrite.Add(new SessionMessage
            {
                SessionId = sessionId, Role = "assistant",
                Content = "[Resumo da conversa até aqui] " + summary!.Trim(),
                Thinking = "", CreatedAt = summaryAt, Archived = 0, StatusId = 2,
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
            var now = System.DateTime.UtcNow;
            var sec = new System.DateTime(now.Ticks - now.Ticks % System.TimeSpan.TicksPerSecond, System.DateTimeKind.Utc);
            s.UpdatedAt = sec > s.UpdatedAt ? sec : (s.UpdatedAt > now ? sec : s.UpdatedAt.AddSeconds(1));
            await DbRetryAsync(async () => await _sessionsWrite.Update(s, new Session { Id = (int?)sessionId }));
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

    // Artefato da sessão (ex.: zip dos scripts): upsert por nome.
    public async Task SaveArtifactAsync(int sessionId, string name, string mime, string b64)
    {
        try
        {
            await DbRetryAsync(async () =>
            {
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection(AppDb.ConnectionString);
                await conn.OpenAsync();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO session_artifacts (session_id, name, mime, b64) VALUES (@s,@n,@m,@b) " +
                    "ON CONFLICT(session_id, name) DO UPDATE SET mime=excluded.mime, b64=excluded.b64, created_at=CURRENT_TIMESTAMP";
                cmd.Parameters.AddWithValue("@s", sessionId);
                cmd.Parameters.AddWithValue("@n", name ?? "");
                cmd.Parameters.AddWithValue("@m", mime ?? "");
                cmd.Parameters.AddWithValue("@b", b64 ?? "");
                await cmd.ExecuteNonQueryAsync();
                return 0;
            });
        }
        catch { /* best-effort */ }
    }

    public async Task<(string Mime, string B64)?> GetArtifactAsync(int sessionId, string name)
    {
        (string, string)? found = null;
        await DbRetryAsync(async () =>
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(AppDb.ConnectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT mime, b64 FROM session_artifacts WHERE session_id=@s AND name=@n LIMIT 1";
            cmd.Parameters.AddWithValue("@s", sessionId);
            cmd.Parameters.AddWithValue("@n", name ?? "");
            using var r = await cmd.ExecuteReaderAsync();
            if (await r.ReadAsync()) found = (r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1));
            return 0;
        });
        return found;
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