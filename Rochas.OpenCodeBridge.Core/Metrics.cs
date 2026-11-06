// ===========================================================================
//  Metrics da OpenCodeBridge — anel em memoria + dump SQLite.
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

namespace Rochas.OpenCodeBridge.Core;

public static class Metrics
{
    const int Cap = 8192;
    static readonly ConcurrentQueue<JsonObject> Ring = new();
    static int _dropped;

    public static bool Active = false;
    public static int Count => Ring.Count;
    public static int Dropped => _dropped;

    // Contadores por anel: sempre ativos (mesmo sem --diagnostic), porque o
    // desenho "catalogo cheio, deny so no risco" tem que ser medido, nao
    // presumido. Anel 2 = chamada barrada por policy de tool/role; anel 3 =
    // argv barrado no inspetor de shell; nudge = argumentos guiados (chamada
    // recusada antes de executar e pedida de novo com o JSON completo).
    static long _ring2Deny, _ring3Deny, _nudgeArgs;
    static long _breakerNudge, _breakerTrip;
    static long _antiRepeatContent, _antiRepeatTools;
    static long _upstreamAbort, _upstreamAbortRetry;
    static long _toolArgGuard, _upstreamArgRetry, _gitRepair, _upstreamDegenerateRetry;
    public static long Ring2Deny => System.Threading.Interlocked.Read(ref _ring2Deny);
    public static long Ring3Deny => System.Threading.Interlocked.Read(ref _ring3Deny);
    public static long NudgeArgs => System.Threading.Interlocked.Read(ref _nudgeArgs);
    public static long BreakerNudge => System.Threading.Interlocked.Read(ref _breakerNudge);
    public static long BreakerTripCount => System.Threading.Interlocked.Read(ref _breakerTrip);
    public static long AntiRepeatContent => System.Threading.Interlocked.Read(ref _antiRepeatContent);
    public static long AntiRepeatTools => System.Threading.Interlocked.Read(ref _antiRepeatTools);
    public static long UpstreamAbortCount => System.Threading.Interlocked.Read(ref _upstreamAbort);
    public static long UpstreamAbortRetryCount => System.Threading.Interlocked.Read(ref _upstreamAbortRetry);
    public static long RejectedArgsCount => System.Threading.Interlocked.Read(ref _toolArgGuard);
    public static long ArgsRetryAfterUpstreamErrorCount => System.Threading.Interlocked.Read(ref _upstreamArgRetry);
    public static long GitCommandRepairCount => System.Threading.Interlocked.Read(ref _gitRepair);
    /// <summary>Retry de turno degenerado: fechou em max_tokens sem call util (texto infinito ou args gigantes).</summary>
    public static long DegenerateTurnRetryCount => System.Threading.Interlocked.Read(ref _upstreamDegenerateRetry);

    /// <summary>
    /// Portao de argumento de tool: recusa args com lixo de template acima do
    /// teto. Emite alerta guiado ao modelo e conta no painel de portoes.
    /// </summary>
    public static void CountRejectedArguments()
    {
        System.Threading.Interlocked.Increment(ref _toolArgGuard);
        if (Active) Record("tool-arg-guard", new JsonObject());
    }

    /// <summary>Portao do upstream: 500 de parse de tool call virou turno retryavel.</summary>
    public static void CountArgsRetryAfterUpstreamError()
    {
        System.Threading.Interlocked.Increment(ref _upstreamArgRetry);
        if (Active) Record("upstream-arg-retry", new JsonObject());
    }

    /// <summary>
    /// Portao de turno degenerado: com tools no request, resposta que fecha em
    /// max_tokens sem call util vira retry unico. Dois sintomas medidos no
    /// E-tools-full (2026-11-06): texto infinito sem call (16.384 tok / 377 s) e
    /// call com args de 57.288 chars que o upstream nem parseia (16.384 tok /
    /// 386 s). Custa minutos e nao entrega passo; conta no painel de portoes.
    /// </summary>
    public static void CountDegenerateTurnRetry()
    {
        System.Threading.Interlocked.Increment(ref _upstreamDegenerateRetry);
        if (Active) Record("upstream-degenerate-retry", new JsonObject());
    }

    /// <summary>Portao de vocabulario do git: normalizacao aplicada antes de executar.</summary>
    public static void CountGitCommandRepair()
    {
        System.Threading.Interlocked.Increment(ref _gitRepair);
        if (Active) Record("git-repair", new JsonObject());
    }

    /// <summary>
    /// Portoes que emitem alerta (ao modelo ou ao cliente). Fonte unica do
    /// painel: /api/metrics publica DenyCounters e o dashboard lista estas
    /// chaves. Portao novo entra aqui e no painel, senao o unit acusa.
    /// </summary>
    public static readonly string[] AlertGateKeys =
    {
        "anti_repeat_content", "anti_repeat_tools",
        "nudge_args", "breaker_nudge", "breaker_trip",
        "ring2_deny", "ring3_deny",
        "tool_arg_guard", "upstream_arg_retry",
        "upstream_abort", "upstream_abort_retry", "git_repair",
        "upstream_degenerate_retry",
    };

    /// <summary>
    /// Conta um aborto do upstream (conexão encerrada antes da resposta).
    /// Sem esse contador o caso só existia no log do arquivo — invisível no
    /// painel, indistinguível de erro de modelo.
    /// </summary>
    /// <param name="retried">true = a bridge repetiu o turno; false = devolveu 502.</param>
    public static void CountUpstreamAbort(bool retried)
    {
        if (retried) System.Threading.Interlocked.Increment(ref _upstreamAbortRetry);
        else System.Threading.Interlocked.Increment(ref _upstreamAbort);
        if (!Active) return;
        Record("upstream-abort", new JsonObject { ["retried"] = retried });
    }

    /// <summary>Conta uma recusa no anel e (se diagnostic ligado) grava o evento.</summary>
    /// <param name="ring">"ring2", "ring3" ou "nudge".</param>
    /// <param name="tool">Tool alvo da recusa.</param>
    /// <param name="why">Motivo objetivo (vai para o anel/SQLite).</param>
    public static void CountToolDenial(string ring, string tool, string why)
    {
        switch (ring)
        {
            case "ring2": System.Threading.Interlocked.Increment(ref _ring2Deny); break;
            case "ring3": System.Threading.Interlocked.Increment(ref _ring3Deny); break;
            default: System.Threading.Interlocked.Increment(ref _nudgeArgs); break;
        }
        if (!Active) return;
        Record(ring + "-deny", new JsonObject { ["tool"] = tool, ["reason"] = why });
    }

    /// <summary>Instantaneo dos contadores por anel (para /api/metrics e testes).</summary>
    public static JsonObject DenyCounters() => new()
    {
        ["ring2_deny"] = Ring2Deny,
        ["ring3_deny"] = Ring3Deny,
        ["nudge_args"] = NudgeArgs,
        ["breaker_nudge"] = BreakerNudge,
        ["breaker_trip"] = BreakerTripCount,
        ["anti_repeat_content"] = AntiRepeatContent,
        ["anti_repeat_tools"] = AntiRepeatTools,
        ["upstream_abort"] = UpstreamAbortCount,
        ["upstream_abort_retry"] = UpstreamAbortRetryCount,
        ["tool_arg_guard"] = RejectedArgsCount,
        ["upstream_arg_retry"] = ArgsRetryAfterUpstreamErrorCount,
        ["git_repair"] = GitCommandRepairCount,
        ["upstream_degenerate_retry"] = DegenerateTurnRetryCount,
    };

    /// <summary>Conta um disparo do gate anti-repeat (conteudo ou tools).</summary>
    /// <param name="tools">true = args/loop de tool; false = conteudo/thinking/stream.</param>
    public static void CountAntiRepeatAlert(bool toolLoop)
    {
        if (toolLoop) System.Threading.Interlocked.Increment(ref _antiRepeatTools);
        else System.Threading.Interlocked.Increment(ref _antiRepeatContent);
        if (!Active) return;
        Record("anti-repeat-gate", new JsonObject { ["gate"] = toolLoop ? "tools" : "content" });
    }

    /// <summary>Conta um veredito do breaker de loop agentico (nudge|trip).</summary>
    /// <param name="verdict">"nudge" ou "trip".</param>
    public static void CountLoopBreakerVerdict(string verdict)
    {
        if (string.Equals(verdict, "trip", StringComparison.OrdinalIgnoreCase))
            System.Threading.Interlocked.Increment(ref _breakerTrip);
        else
            System.Threading.Interlocked.Increment(ref _breakerNudge);
        if (!Active) return;
        Record("tool-error-breaker", new JsonObject { ["verdict"] = verdict });
    }

    /// <summary>Instantaneo dos contadores do breaker (para /api/metrics e testes).</summary>
    public static JsonObject BreakerCounters() => new()
    {
        ["breaker_nudge"] = BreakerNudge,
        ["breaker_trip"] = BreakerTripCount,
    };

    public static void Record(string kind, JsonObject fields)
    {        if (!Active) return;
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

    public static List<JsonObject> Snapshot()
    {
        var list = new List<JsonObject>();
        foreach (var e in Ring) list.Add((JsonObject)e.DeepClone());
        return list;
    }

    public static void Clear()
    {
        while (Ring.TryDequeue(out _)) { }
        System.Threading.Interlocked.Exchange(ref _dropped, 0);
        System.Threading.Interlocked.Exchange(ref _ring2Deny, 0);
        System.Threading.Interlocked.Exchange(ref _ring3Deny, 0);
        System.Threading.Interlocked.Exchange(ref _nudgeArgs, 0);
        System.Threading.Interlocked.Exchange(ref _breakerNudge, 0);
        System.Threading.Interlocked.Exchange(ref _breakerTrip, 0);
        System.Threading.Interlocked.Exchange(ref _antiRepeatContent, 0);
        System.Threading.Interlocked.Exchange(ref _antiRepeatTools, 0);
        System.Threading.Interlocked.Exchange(ref _upstreamAbort, 0);
        System.Threading.Interlocked.Exchange(ref _upstreamAbortRetry, 0);
        System.Threading.Interlocked.Exchange(ref _toolArgGuard, 0);
        System.Threading.Interlocked.Exchange(ref _upstreamArgRetry, 0);
        System.Threading.Interlocked.Exchange(ref _gitRepair, 0);
    }

    // Amostrador de GPU em background (5s; best-effort, sem nvidia-smi = mudo).
    public static void StartGpuSampler()
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
    public static int DumpSqlite(string path)
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
