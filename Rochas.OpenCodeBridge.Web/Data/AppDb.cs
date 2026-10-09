using Microsoft.Data.Sqlite;
using System.IO;

namespace Rochas.OpenCodeBridge.Web.Data;

// Bootstrap do SQLite (DDL + seed admin). DapperRepository cuida do CRUD.
// Usa banco compartilhado na raiz da solução bridge para persistência unificada.
public static class AppDb
{
    private static string _dbPath;
    public static string Path 
    { 
        get => _dbPath; 
        private set => _dbPath = value; 
    }
    
    public static string ConnectionString => $"Data Source={_dbPath};Cache=Shared";

    // Caminho padrão: banco compartilhado na raiz da solução bridge
    private static string DefaultPath => System.IO.Path.GetFullPath(
        System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Rochas.OpenCodeBridge", "bridge.db"));

    public static void Init(string path = null)
    {
        Path = path ?? DefaultPath;
        
        // Garante diretório
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!System.IO.Directory.Exists(dir))
            System.IO.Directory.CreateDirectory(dir);

        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS users (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, email TEXT NOT NULL UNIQUE, password_hash TEXT NOT NULL, active INTEGER NOT NULL DEFAULT 1, is_admin INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS agents (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, model TEXT NOT NULL DEFAULT 'qwen3-8b-awq', bridge_url TEXT NOT NULL DEFAULT 'http://127.0.0.1:4124', temperature REAL NOT NULL DEFAULT 0.2, thinking TEXT NOT NULL DEFAULT 'events', system_prompt TEXT NOT NULL DEFAULT '', role TEXT NOT NULL DEFAULT '', mode TEXT NOT NULL DEFAULT 'build', measured_tps REAL NOT NULL DEFAULT 0, active INTEGER NOT NULL DEFAULT 1);
            CREATE TABLE IF NOT EXISTS sessions (id INTEGER PRIMARY KEY AUTOINCREMENT, user_id INTEGER NOT NULL, agent_id INTEGER NOT NULL, executor_agent_id INTEGER, title TEXT NOT NULL DEFAULT '', created_at DATETIME DEFAULT CURRENT_TIMESTAMP, updated_at DATETIME DEFAULT CURRENT_TIMESTAMP, FOREIGN KEY(user_id) REFERENCES users(id), FOREIGN KEY(agent_id) REFERENCES agents(id));
            CREATE TABLE IF NOT EXISTS session_messages (id INTEGER PRIMARY KEY AUTOINCREMENT, session_id INTEGER NOT NULL, role TEXT NOT NULL, content TEXT NOT NULL, thinking TEXT NOT NULL DEFAULT '', prompt_tokens INTEGER, completion_tokens INTEGER, created_at DATETIME DEFAULT CURRENT_TIMESTAMP, FOREIGN KEY(session_id) REFERENCES sessions(id) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS idx_sessions_user ON sessions(user_id);
            CREATE INDEX IF NOT EXISTS idx_sessions_agent ON sessions(agent_id);
            CREATE INDEX IF NOT EXISTS idx_messages_session ON session_messages(session_id);
            CREATE INDEX IF NOT EXISTS idx_messages_created ON session_messages(created_at);
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