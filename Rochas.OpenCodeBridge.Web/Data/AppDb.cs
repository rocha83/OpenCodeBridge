using Microsoft.Data.Sqlite;
using System.IO;

namespace Rochas.OpenCodeBridge.Web.Data;

// Bootstrap do SQLite (DDL + seed admin). DapperRepository cuida do CRUD.
// Usa banco compartilhado na raiz da solução bridge para persistência unificada.
public static class AppDb
{
    private static string _dbPath = "";
    public static string Path 
    { 
        get => _dbPath; 
        private set => _dbPath = value; 
    }
    
    public static string ConnectionString => $"Data Source={_dbPath};Cache=Shared";

    // Caminho padrão: banco compartilhado na raiz da solução bridge
    private static string DefaultPath => System.IO.Path.GetFullPath(
        System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Rochas.OpenCodeBridge", "bridge.db"));

    public static void Init(string? path = null)
    {
        Path = path ?? DefaultPath;

        // Garante diretório
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
            System.IO.Directory.CreateDirectory(dir);

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        // Concorrencia sob executores paralelos: WAL + NORMAL evita SafeHandle/disposed
        // e NREs transitórios do rollback-journal em writes simultâneos.
        using (var prag = conn.CreateCommand())
        {
            prag.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=10000;";
            prag.ExecuteNonQuery();
        }
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS users (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, email TEXT NOT NULL UNIQUE, password_hash TEXT NOT NULL, active INTEGER NOT NULL DEFAULT 1, is_admin INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS agents (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, model TEXT NOT NULL DEFAULT 'qwen3.5-9b', bridge_url TEXT NOT NULL DEFAULT 'http://127.0.0.1:4124', temperature REAL NOT NULL DEFAULT 0.4, thinking TEXT NOT NULL DEFAULT 'events', system_prompt TEXT NOT NULL DEFAULT '', role TEXT NOT NULL DEFAULT '', mode TEXT NOT NULL DEFAULT 'build', measured_tps REAL NOT NULL DEFAULT 0, active INTEGER NOT NULL DEFAULT 1, tools TEXT NOT NULL DEFAULT 'shell,read,write,edit,grep,glob,fetch,git');
            CREATE TABLE IF NOT EXISTS sessions (id INTEGER PRIMARY KEY AUTOINCREMENT, user_id INTEGER NOT NULL, agent_id INTEGER NOT NULL, executor_agent_id INTEGER, title TEXT NOT NULL DEFAULT '', created_at DATETIME DEFAULT CURRENT_TIMESTAMP, updated_at DATETIME DEFAULT CURRENT_TIMESTAMP, FOREIGN KEY(user_id) REFERENCES users(id), FOREIGN KEY(agent_id) REFERENCES agents(id));
            CREATE TABLE IF NOT EXISTS message_statuses (id INTEGER PRIMARY KEY, code TEXT NOT NULL UNIQUE, label TEXT NOT NULL);
            INSERT OR IGNORE INTO message_statuses (id, code, label) VALUES (1, 'enviada', 'Enviada'), (2, 'respondida', 'Respondida'), (3, 'arquivada', 'Arquivada');
            CREATE TABLE IF NOT EXISTS session_messages (id INTEGER PRIMARY KEY AUTOINCREMENT, session_id INTEGER NOT NULL, role TEXT NOT NULL, content TEXT NOT NULL, thinking TEXT NOT NULL DEFAULT '', prompt_tokens INTEGER, completion_tokens INTEGER, created_at DATETIME DEFAULT CURRENT_TIMESTAMP, status_id INTEGER REFERENCES message_statuses(id), FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS refinement_lessons (id INTEGER PRIMARY KEY AUTOINCREMENT, session_id INTEGER NOT NULL, task_index INTEGER NOT NULL DEFAULT 0, kind TEXT NOT NULL DEFAULT '', detail TEXT NOT NULL DEFAULT '', created_at DATETIME DEFAULT CURRENT_TIMESTAMP, FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS tool_calls (id INTEGER PRIMARY KEY AUTOINCREMENT, session_id INTEGER NOT NULL, agent TEXT NOT NULL DEFAULT '', name TEXT NOT NULL DEFAULT '', args TEXT NOT NULL DEFAULT '', ok INTEGER NOT NULL DEFAULT 0, output TEXT NOT NULL DEFAULT '', ms INTEGER NOT NULL DEFAULT 0, message_id INTEGER, sub TEXT NOT NULL DEFAULT '', created_at DATETIME DEFAULT CURRENT_TIMESTAMP, FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS session_artifacts (id INTEGER PRIMARY KEY AUTOINCREMENT, session_id INTEGER NOT NULL, name TEXT NOT NULL DEFAULT '', mime TEXT NOT NULL DEFAULT '', b64 TEXT NOT NULL DEFAULT '', created_at DATETIME DEFAULT CURRENT_TIMESTAMP, FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_artifacts_session_name ON session_artifacts(session_id, name);
            CREATE TABLE IF NOT EXISTS prompt_controls (key TEXT PRIMARY KEY, label TEXT NOT NULL DEFAULT '', value TEXT NOT NULL DEFAULT '', updated_at DATETIME DEFAULT CURRENT_TIMESTAMP);
            CREATE INDEX IF NOT EXISTS idx_sessions_user ON sessions(user_id);
            CREATE INDEX IF NOT EXISTS idx_sessions_agent ON sessions(agent_id);
            CREATE INDEX IF NOT EXISTS idx_messages_session ON session_messages(session_id);
            CREATE INDEX IF NOT EXISTS idx_messages_created ON session_messages(created_at);
            CREATE INDEX IF NOT EXISTS idx_toolcalls_session ON tool_calls(session_id);
            """;
        cmd.ExecuteNonQuery();

        // Migração: adiciona coluna is_admin se não existir (para DBs antigos)
        using var check = conn.CreateCommand();
        check.CommandText = "PRAGMA table_info(users);";
        using var r = check.ExecuteReader();
        var hasIsAdmin = false;
        while (r.Read()) if (r.GetString(1) == "is_admin") { hasIsAdmin = true; break; }
        if (!hasIsAdmin)
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE users ADD COLUMN is_admin INTEGER NOT NULL DEFAULT 0;";
            alter.ExecuteNonQuery();
        }

        // Migração: papel do agente (orchestrator/executor) p/ agentes híbridos.
        EnsureColumn(conn, "agents", "role", "ALTER TABLE agents ADD COLUMN role TEXT NOT NULL DEFAULT '';");
        // Migração: modo do agente (plan/build).
        EnsureColumn(conn, "agents", "mode", "ALTER TABLE agents ADD COLUMN mode TEXT NOT NULL DEFAULT 'build';");
        // Migração: throughput medido do executor (tok/s) p/ estimativas.
        EnsureColumn(conn, "agents", "measured_tps", "ALTER TABLE agents ADD COLUMN measured_tps REAL NOT NULL DEFAULT 0;");
        // Migração: executor selecionado na sessão.
        EnsureColumn(conn, "sessions", "executor_agent_id", "ALTER TABLE sessions ADD COLUMN executor_agent_id INTEGER;");
        // Migração: passado compactado (fora do contexto, visível no histórico).
        EnsureColumn(conn, "session_messages", "archived", "ALTER TABLE session_messages ADD COLUMN archived INTEGER NOT NULL DEFAULT 0;");
        // Migração: vínculo tool->bolha (mensagem dona) + id do subagente dono.
        EnsureColumn(conn, "tool_calls", "message_id", "ALTER TABLE tool_calls ADD COLUMN message_id INTEGER;");
        EnsureColumn(conn, "tool_calls", "sub", "ALTER TABLE tool_calls ADD COLUMN sub TEXT NOT NULL DEFAULT '';");
        // Migração: métrica por bolha (rodapé): duração em ms + throughput t/s.
        EnsureColumn(conn, "session_messages", "elapsed_ms", "ALTER TABLE session_messages ADD COLUMN elapsed_ms INTEGER;");
        EnsureColumn(conn, "session_messages", "tps", "ALTER TABLE session_messages ADD COLUMN tps REAL;");
        // Migração: status da mensagem (1 Enviada, 2 Respondida, 3 Arquivada) + backfill.
        EnsureColumn(conn, "session_messages", "status_id", "ALTER TABLE session_messages ADD COLUMN status_id INTEGER REFERENCES message_statuses(id);");
        using (var backfill = conn.CreateCommand())
        {
            backfill.CommandText = "UPDATE session_messages SET status_id = CASE WHEN archived != 0 THEN 3 WHEN role = 'user' THEN 1 ELSE 2 END WHERE status_id IS NULL;";
            try { backfill.ExecuteNonQuery(); } catch { }
        }
        // Migração: modo canônico Plan/Build (primeira maiúscula).
        using (var modeq = conn.CreateCommand())
        {
            modeq.CommandText = "UPDATE agents SET mode = CASE WHEN LOWER(mode) = 'plan' THEN 'Plan' ELSE 'Build' END WHERE LOWER(mode) IN ('plan', 'build') AND mode NOT IN ('Plan', 'Build');";
            try { modeq.ExecuteNonQuery(); } catch { }
        }
        // Migração: Plan sem escrita/execução (remove write,edit,shell legados; fetch navega, fica).
        using (var planq = conn.CreateCommand())
        {
            planq.CommandText = "UPDATE agents SET tools = TRIM(REPLACE(REPLACE(REPLACE(REPLACE(',' || COALESCE(tools,'') || ',', ',write,', ','), ',edit,', ','), ',shell,', ','), ',,', ','), ',') WHERE LOWER(mode) = 'plan';";
            try { planq.ExecuteNonQuery(); } catch { }
        }
        // Migração: Plan navega com fetch (repõe se ausente).
        using (var fetchq = conn.CreateCommand())
        {
            fetchq.CommandText = "UPDATE agents SET tools = CASE WHEN COALESCE(tools,'') = '' THEN 'fetch' ELSE tools || ',fetch' END WHERE LOWER(mode) = 'plan' AND (',' || COALESCE(tools,'') || ',') NOT LIKE '%,fetch,%';";
            try { fetchq.ExecuteNonQuery(); } catch { }
        }
        using (var subq = conn.CreateCommand())
        {
            subq.CommandText = "UPDATE agents SET tools = CASE WHEN COALESCE(tools,'') = '' THEN 'subagent' ELSE tools || ',subagent' END WHERE LOWER(mode) = 'plan' AND (',' || COALESCE(tools,'') || ',') NOT LIKE '%,subagent,%';";
            try { subq.ExecuteNonQuery(); } catch { }
        }
        // Migração: controle de tentativas de login (lockout).
        EnsureColumn(conn, "users", "failed_attempts", "ALTER TABLE users ADD COLUMN failed_attempts INTEGER NOT NULL DEFAULT 0;");
        EnsureColumn(conn, "users", "locked_until", "ALTER TABLE users ADD COLUMN locked_until TEXT NOT NULL DEFAULT '';");
        // Migração: harness de tools por agente (CSV; vazio = nenhuma).
        EnsureColumn(conn, "agents", "tools", "ALTER TABLE agents ADD COLUMN tools TEXT NOT NULL DEFAULT 'shell,read,write,edit,grep,glob,fetch,git';");
        // Seed dos agentes revisados (só o que falta por id, nunca sobrescreve).
        AgentSeeder.SeedIfEmpty(conn);
        // Seed dos controles de repeticao/calda (só o que falta por key, nunca sobrescreve).
        using (var seedpc = conn.CreateCommand())
        {
            seedpc.CommandText = """
                INSERT OR IGNORE INTO prompt_controls (key, label, value) VALUES
                ('system_tail', 'Calda de system prompt (anexada a todo agente)', ''),
                ('anti_repeat_enabled', 'AntiRepeat ligado (bridge)', 'true'),
                ('anti_repeat_window', 'AntiRepeat janela em chars (bridge)', '128'),
                ('anti_repeat_notice', 'AntiRepeat aviso de contencao (bridge)', 'Aqui e a ponte de contencao OpenCodeBridge: pare de enviar o mesmo raciocinio, provavelmente voce entrou em repeticao invalida; quando informada a repeticao, nao responda mais nada, apenas encerre em silencio, obrigado.'),
                ('guardrail', 'Sufixo guardrail da bridge (espelho informativo)', '');
                """;
            try { seedpc.ExecuteNonQuery(); } catch { }
        }
    }

    private static void EnsureColumn(SqliteConnection conn, string table, string column, string alterSql)
    {
        using var check = conn.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table});";
        using var r = check.ExecuteReader();
        while (r.Read()) if (r.GetString(1) == column) return;
        using var alter = conn.CreateCommand();
        alter.CommandText = alterSql;
        alter.ExecuteNonQuery();
    }
}