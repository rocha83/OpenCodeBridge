using Microsoft.Data.Sqlite;

namespace Rochas.OpenCodeBridge.Web.Data;

// Bootstrap do SQLite (DDL + seed admin). DapperRepository cuida do CRUD.
public static class AppDb
{
    public static string Path { get; private set; } = "web.db";
    public static string ConnectionString => $"Data Source={Path};Cache=Shared";

    public static void Init(string path)
    {
        Path = path;
        using var conn = new SqliteConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS users (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, email TEXT NOT NULL UNIQUE, password_hash TEXT NOT NULL, active INTEGER NOT NULL DEFAULT 1, is_admin INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS agents (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, model TEXT NOT NULL DEFAULT 'qwen3-8b-awq', bridge_url TEXT NOT NULL DEFAULT 'http://127.0.0.1:4124', temperature REAL NOT NULL DEFAULT 0.2, thinking TEXT NOT NULL DEFAULT 'events', system_prompt TEXT NOT NULL DEFAULT '', active INTEGER NOT NULL DEFAULT 1);
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
    }
}
