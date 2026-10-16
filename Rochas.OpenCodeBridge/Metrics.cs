// ===========================================================================
//  Metrics da Qwen3-Bridge — anel em memoria + dump SQLite.
// ---------------------------------------------------------------------------
//  So atua com DIAGNOSTIC ligado (--diagnostic / appsettings diagnostic /
//  env DIAGNOSTIC=1): cada request chat, login, guarda e amostra de GPU vira
//  um evento no anel (cap 8192, descarta o mais antigo). POST
//  /api/metrics/dump despeja em SQLite no LAYOUT do sink Serilog.Sinks.SQLite
//  (tabela logs: Timestamp, Level, MessageTemplate, Message, Exception,
//  Properties) para analise posterior. BCL + Microsoft.Data.Sqlite.
//  Identificadores em en-US, comentarios em pt-BR.
// ===========================================================================

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Rochas.OpenCodeBridge;

static class Metrics
{
    const int Cap = 8192;
    static readonly ConcurrentQueue<JsonObject> Ring = new();
    static int _dropped;

    internal static bool Active = false;
    internal static int Count => Ring.Count;
    internal static int Dropped => _dropped;

    internal static void Record(string kind, JsonObject fields)
    {
        if (!Active) return;
        try
        {
            fields["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            fields["kind"] = kind;
            Ring.Enqueue(fields);
            while (Ring.Count > Cap && Ring.TryDequeue(out _))
                System.Threading.Interlocked.Increment(ref _dropped);
        }
        catch { /* metrica nunca derruba o pedido */ }
    }

    internal static List<JsonObject> Snapshot()
    {
        var list = new List<JsonObject>();
        foreach (var e in Ring) list.Add((JsonObject)e.DeepClone());
        return list;
    }

    internal static void Clear()
    {
        while (Ring.TryDequeue(out _)) { }
        System.Threading.Interlocked.Exchange(ref _dropped, 0);
    }

    // Amostrador de GPU em background (5s; best-effort, sem nvidia-smi = mudo).
    internal static void StartGpuSampler()
    {
        if (!Active) return;
        var t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    var psi = new ProcessStartInfo("nvidia-smi",
                        "--query-gpu=memory.used,temperature.gpu,power.draw,utilization.gpu --format=csv,noheader,nounits")
                    { RedirectStandardOutput = true, UseShellExecute = false };
                    using var p = Process.Start(psi);
                    string o = p?.StandardOutput.ReadToEnd().Trim() ?? "";
                    p?.WaitForExit(4000);
                    var parts = o.Split(',');
                    if (parts.Length == 4)
                        Record("gpu", new JsonObject
                        {
                            ["vram_mib"] = (int)double.Parse(parts[0].Trim(), System.Globalization.CultureInfo.InvariantCulture),
                            ["temp_c"] = (int)double.Parse(parts[1].Trim(), System.Globalization.CultureInfo.InvariantCulture),
                            ["watt"] = (int)double.Parse(parts[2].Trim(), System.Globalization.CultureInfo.InvariantCulture),
                            ["util"] = (int)double.Parse(parts[3].Trim(), System.Globalization.CultureInfo.InvariantCulture),
                        });
                }
                catch { }
                Thread.Sleep(5000);
            }
        })
        { IsBackground = true, Name = "metrics-gpu" };
        t.Start();
    }

    // Dump no layout do Serilog.Sinks.SQLite (tabela logs). Retorna linhas.
    internal static int DumpSqlite(string path)
    {
        var rows = Snapshot();
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS logs (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, Timestamp TEXT, Level TEXT,
                    MessageTemplate TEXT, Message TEXT, Exception TEXT, Properties TEXT);
                DELETE FROM logs;
                """;
            cmd.ExecuteNonQuery();
        }
        int n = 0;
        using var tx = conn.BeginTransaction();
        foreach (var e in rows)
        {
            string kind = e["kind"]?.GetValue<string>() ?? "event";
            long ts = e["ts"]?.GetValue<long>() ?? 0;
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO logs (Timestamp, Level, MessageTemplate, Message, Exception, Properties) VALUES (@t,@l,@mt,@m,'',@p);";
            cmd.Parameters.AddWithValue("@t", DateTimeOffset.FromUnixTimeMilliseconds(ts).ToString("o"));
            cmd.Parameters.AddWithValue("@l", "Information");
            cmd.Parameters.AddWithValue("@mt", "bridge-{kind}");
            cmd.Parameters.AddWithValue("@m", $"bridge-{kind}");
            cmd.Parameters.AddWithValue("@p", e.ToJsonString());
            cmd.ExecuteNonQuery();
            n++;
        }
        tx.Commit();
        return n;
    }
}
