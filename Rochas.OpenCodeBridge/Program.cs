// ===========================================================================
//  Qwen3-Bridge  (C# / .NET console)  —  v1.1
// ---------------------------------------------------------------------------
//  PROBLEMA
//  --------
//  O opencode (v2.0.21) SEMPRE chama POST /v1/responses (API Responses) e
//  ignora o campo "api" do opencode.json. Nesse fluxo o vLLM nao faz o parse
//  de tool calls: devolve texto puro com finish_reason=stop e nenhuma tool
//  nasce (foi a causa de todos os "executed=False").
//
//  SOLUCAO (propositalmente simples)
//  ---------------------------------
//  Bridge que recebe /v1/responses e traduz para /v1/chat/completions do
//  vLLM — o MESMO caminho que ja funciona no qwen CLI. Depois converte a
//  resposta (tool_calls + thinking) de volta para o formato Responses.
//
//  O QUE NAO ESTA AQUI (de proposito — heranca problematica do Qwen2.5-7B):
//  coercion de paths, grounding, extracao de pseudo-tool-call em texto,
//  retry com nudge, footer de tokens, strip agressivo de template.
//  Se surgir algo especifico do Qwen3-8B, amadurecemos nesta versao.
//
//  CONVENCAO DE IDIOMA: identificadores em en-US, comentarios em pt-BR.
//
//  USO:  dotnet run -c Release -- --port 4143 --upstream http://127.0.0.1:4100
// ===========================================================================

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge;

internal static partial class Program
{
    // ---------------------------------------------------------------------
    // 1) CONFIGURACAO (linhas de comando) — tudo sobrescrevivel sem recompilar
    // ---------------------------------------------------------------------
    static int Port = 4124;                                        // porta da bridge
    static string Listen = "127.0.0.1";                            // interface de escuta (0.0.0.0 = rede toda)
    static string Upstream = "http://127.0.0.1:4100";              // vLLM
    static string Model = "qwen3-8b-awq";                          // id servido pelo vLLM
    static string Thinking = "events";                             // "events" = mostra | "off" = descarta
    static double Temperature = 0.2;                                 // direto e objetivo (0.1-0.4); tool calling continua deterministico via tool_choice
    static int MaxTokens = 4096;                                   // teto de saida padrao (fabrica 4k); pedido menor e honrado, nunca maior
    static int CpuMaxTokens = 2048;                                // teto p/ upstream sem parser server-side (llama/CPU): decode ~5 tok/s, teto alto = divagacao de minutos. GPU usa MaxTokens.
    static int ToolOutputLimit = 8000;                             // corte por resultado de tool (ctx 28672)
    static string LogPath = "/tmp/qwen3-bridge.log";
    static readonly string Version = "2.0";

    // Coerção texto->tool_calls p/ upstream sem parser server-side (llama.cpp
    // na CPU): extrai ```json{"name"..} ou <tool_call>..</tool_call> do content
    // e sintetiza tool_calls. Liga via --coerce-text-tools ou auto quando o
    // model contém "cpu". Qwen3/AWQ (parsers nativos) ficam intactos.
    static bool CoerceTextTools;
    static bool CoerceFor(string modelId) => CoerceTextTools
        || (modelId?.Contains("cpu", StringComparison.OrdinalIgnoreCase) ?? false);

    // Perfis por sufixo no model (ex.: openai/qwen3-8b-awq-plan): plan =
    // thinking ligado + temp alta p/ planejar; build = thinking fundido +
    // temp baixa p/ executar; orch = coordena 3B; moe = uso geral hibrido
    // (Qwen3 decide sozinho quando delegar). Request sem sufixo usa os
    // globais. O caminho Qwen3 padrao (sem sufixo) fica byte-identico.
    static readonly Dictionary<string, (double Temp, string Think)> Profiles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["plan"] = (0.6, "events"),
            ["build"] = (0.2, "off"),
            ["orch"] = (0.2, "events"),
            ["moe"] = (0.4, "events"),
        };

    /// <summary>Descobre o perfil pelo sufixo do model (-plan/-build).</summary>
    static bool TryProfile(string requestedModel, out double profTemp, out string profThink)
    {
        profTemp = double.NaN; profThink = "";
        if (string.IsNullOrEmpty(requestedModel)) return false;
        foreach (var kv in Profiles)
            if (requestedModel.EndsWith("-" + kv.Key, StringComparison.OrdinalIgnoreCase))
            { profTemp = kv.Value.Temp; profThink = kv.Value.Think; return true; }
        return false;
    }

    /// <summary>Arquivo opcional ao lado do DLL (CLI vence o arquivo).</summary>
    static void LoadAppSettings()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(path)) return;
            var cfg = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
            if (cfg is null) return;
            if (cfg["listen"]?.GetValue<string>() is string listen && listen.Length > 0) Listen = listen;
            if (cfg["port"]?.GetValue<int>() is int port && port > 0) Port = port;
            if (cfg["upstream"]?.GetValue<string>() is string up && up.Length > 0) Upstream = up.TrimEnd('/');
            if (cfg["model"]?.GetValue<string>() is string model && model.Length > 0) Model = model;
            if (cfg["thinking"]?.GetValue<string>() is string thinking && thinking is "events" or "off") Thinking = thinking;
            if (cfg["temperature"]?.GetValue<double>() is double temp) Temperature = temp;
            if (cfg["maxTokens"]?.GetValue<int>() is int mt && mt > 0) MaxTokens = mt;
            if (cfg["cpuMaxTokens"]?.GetValue<int>() is int cmt && cmt > 0) CpuMaxTokens = cmt;
            if (cfg["logPath"]?.GetValue<string>() is string log && log.Length > 0) LogPath = log;
            if (cfg["coerceTextTools"]?.GetValue<bool>() is bool coerce) CoerceTextTools = coerce;
            if (cfg["profiles"]?.AsObject() is JsonObject profs)
                foreach (var kv in profs)
                {
                    var o = kv.Value?.AsObject();
                    if (o is null) continue;
                    double t = o["temperature"]?.GetValue<double>() ?? double.NaN;
                    string th = o["thinking"]?.GetValue<string>() ?? "";
                    Profiles.TryGetValue(kv.Key, out var cur);
                    if (double.IsNaN(t)) t = cur.Temp != 0 ? cur.Temp : Temperature;
                    if (th is not ("events" or "off")) th = cur.Think ?? Thinking;
                    Profiles[kv.Key] = (t, th);
                }
            Log(new JsonObject { ["event"] = "appsettings", ["path"] = path });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[qwen3-bridge] appsettings ignorado: {ex.Message}");
        }
    }

    // Perfil paralelo p/ Qwen2.5-Coder: com tool_choice "auto" ele improvisa
    // o call em texto (bloco ```json ou XML <response><function_call>);
    // com "required" emite tool_calls nativo (atestado no vLLM 0.19.1).
    // Quando o Model nao contem "coder", o caminho Qwen3 fica byte-identico.
    static bool IsCoder => Model.Contains("coder", StringComparison.OrdinalIgnoreCase);

    // Telemetria basica p/ a API de gerenciamento (/api/status)
    static DateTime StartedAt = DateTime.UtcNow;
    static long TotalRequests;
    static long FailedRequests;

    // Acumuladores p/ /api/metrics (tokens/s). Lock porque streams concorrem.
    static long TotalInputTokens;
    static long TotalOutputTokens;
    static double TotalElapsedSecs;
    static readonly object MetricsLock = new();

    /// <summary>Soma uma medicao aos acumuladores de metrica.</summary>
    static void AddMetrics(int inputTokens, int outputTokens, double elapsedSecs)
    {
        lock (MetricsLock)
        {
            TotalInputTokens += inputTokens;
            TotalOutputTokens += outputTokens;
            TotalElapsedSecs += elapsedSecs;
        }
    }

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    // JSON sem escape unicode -> acentos do pt-BR chegam inteiros no cliente
    static readonly JsonSerializerOptions SerOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // Marcadores de template que as vezes escapam para o texto final.
    static readonly Regex ReTemplate = new(@"<\|im_(start|end)\|>", RegexOptions.Compiled);

    // Coerção: fenced ```json{"name":..}, tags <tool_call>..</tool_call> e
    // o formato cru do 3B na CPU: <{"name": ..}}> (sem fences/tags).
    static readonly Regex ReFencedCall = new(@"```(?:json)?\s*(\{\s*""name""\s*:[\s\S]*?\})\s*```", RegexOptions.Compiled);
    static readonly Regex ReToolTag = new(@"<tool_call>([\s\S]*?)</tool_call(?:\|>)?", RegexOptions.Compiled);

    /// <summary>Extrai tool calls de texto (fenced JSON ou tags) p/ coercão.</summary>
    static List<(string Name, string Arguments)> ExtractTextToolCalls(string content)
    {
        var list = new List<(string, string)>();
        if (string.IsNullOrEmpty(content)) return list;
        foreach (Match m in ReToolTag.Matches(content)) TryAddTextCall(m.Groups[1].Value, list);
        if (list.Count == 0)
            foreach (Match m in ReFencedCall.Matches(content)) TryAddTextCall(m.Groups[1].Value, list);
        if (list.Count == 0)
            foreach (string json in ExtractAngleCalls(content, false)) TryAddTextCall(json, list);
        if (list.Count == 0)
        {
            // Ultimo recurso: resposta INTEIRA e um objeto {"name":..} cru
            // (3B alterna formatos entre runs). Exige "arguments" p/ nao
            // confundir com codigo que mencione "name".
            string t = content.TrimStart();
            if (t.StartsWith("{\"name\"", StringComparison.Ordinal) && t.Contains("\"arguments\""))
                foreach (string json in ExtractAngleCalls(t, true)) TryAddTextCall(json, list);
        }
        return list;
    }

    /// <summary>Extrai chamadas no formato cru do 3B <c>&lt;{"name":..}&gt;</c> (ou objeto cru no inicio), com chaves balanceadas.</summary>
    /// <summary>Extrai chamadas no formato cru do 3B: <c>&lt;{"name":..}&gt;</c> em qualquer posicao, ou objeto cru SOMENTE no inicio.</summary>
    static List<string> ExtractAngleCalls(string content, bool bareStartOnly)
    {
        var out_ = new List<string>();
        // Objeto cru: tenta uma vez na posicao 0 (conteudo ja vem trimado).
        if (bareStartOnly)
        {
            if (content.Length > 0 && content[0] == '{' && TryBalanced(content, 0, out string one) && one.Contains("\"name\""))
                out_.Add(one);
            return out_;
        }
        int i = 0;
        while (i < content.Length)
        {
            int lt = content.IndexOf('<', i);
            if (lt < 0 || lt + 1 >= content.Length) break;
            int j = lt + 1;
            if (content[j] != '{') { i = j; continue; }
            if (TryBalanced(content, j, out string one) && one.Contains("\"name\""))
            { out_.Add(one); i = j + one.Length; }
            else i = j + 1;
        }
        return out_;
    }

    /// <summary>Extrai objeto JSON com chaves balanceadas a partir de um <c>{</c> (respeita strings).</summary>
    static bool TryBalanced(string s, int start, out string json)
    {
        json = "";
        int depth = 0;
        bool inStr = false, esc = false;
        for (int k = start; k < s.Length; k++)
        {
            char c = s[k];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
            }
            else
            {
                if (c == '"') inStr = true;
                else if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) { json = s.Substring(start, k - start + 1); return true; }
                }
            }
        }
        return false;
    }

    static void TryAddTextCall(string json, List<(string Name, string Arguments)> list)
    {
        try
        {
            var o = JsonNode.Parse(json.Trim())?.AsObject();
            if (o is null) return;
            string name = o["name"]?.GetValue<string>() ?? "";
            if (name.Length == 0) return;
            list.Add((name, JsonToString(o["arguments"])));
        }
        catch { /* texto que parece call mas nao parseia: ignora */ }
    }

    /// <summary>Ponto de entrada: le CLI, sobe o HttpListener e atende em loop.</summary>
    /// <param name="cliArgs">Argumentos --chave valor.</param>
    static int Main(string[] cliArgs)
    {
        // Modo portao: helper no mesmo binario (sem projeto separado).
        // Uso: vllm-ocode-bridge.dll gate <repo> "<prompt>" [texto ...] [--review]
        if (cliArgs.Length > 0 && cliArgs[0] == "gate")
            return Gate.Run(cliArgs[1..]);

        LoadAppSettings();                                       // 1.1 arquivo (CLI vence)
        ParseArgs(cliArgs);                                        // 1.2 parse CLI
        try
        {
            var logDir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(logDir)) Directory.CreateDirectory(logDir);
            File.AppendAllText(LogPath, $"{{\"event\":\"start\",\"port\":{Port},\"upstream\":\"{Upstream}\",\"model\":\"{Model}\",\"thinking\":\"{Thinking}\"}}\n");
        }
        catch { /* log de startup nunca derruba a bridge (ex.: /tmp owned by root) */ }

        // HttpListener e o servidor HTTP embutido da BCL (sem Kestrel/NuGet)
        using var listener = new HttpListener();
        // "*" = todas as interfaces (modo remoto). IP especifico = so local.
        string prefixHost = Listen is "0.0.0.0" or "*" or "+" ? "*" : Listen;
        listener.Prefixes.Add($"http://{prefixHost}:{Port}/");
        listener.Start();
        StartedAt = DateTime.UtcNow;
        Console.WriteLine($"[qwen3-bridge] {Listen}:{Port} -> {Upstream} (model={Model}, thinking={Thinking})");

        // Loop infinito de aceitacao; cada requisicao roda na thread do pool
        while (true)
        {
            var ctx = listener.GetContext();
            _ = Task.Run(() => HandleRequest(ctx));
        }
    }
    /// <summary>Interpreta a linha de comando para a configuracao estatica.</summary>
    /// <param name="cliArgs">Vetor de argumentos.</param>
    // 1.2) Argumentos simples: --chave valor
    static void ParseArgs(string[] cliArgs)
    {
        for (int i = 0; i < cliArgs.Length - 1; i++)
        {
            switch (cliArgs[i])
            {
                case "--port": Port = int.Parse(cliArgs[i + 1]); break;
                // --listen 0.0.0.0 expõe a bridge na rede (opencode remoto)
                case "--listen": case "--host": Listen = cliArgs[i + 1]; break;
                case "--upstream": Upstream = cliArgs[i + 1].TrimEnd('/'); break;
                case "--model": Model = cliArgs[i + 1]; break;
                case "--thinking": Thinking = cliArgs[i + 1]; break;
                // Invariante: em locale pt-BR, Parse("0.7") quebraria sem isso
                case "--temperature": Temperature = double.Parse(cliArgs[i + 1], CultureInfo.InvariantCulture); break;
                case "--max-tokens": MaxTokens = int.Parse(cliArgs[i + 1]); break;
                case "--cpu-max-tokens": CpuMaxTokens = int.Parse(cliArgs[i + 1]); break;
                case "--log": LogPath = cliArgs[i + 1]; break;
                case "--coerce-text-tools": CoerceTextTools = cliArgs[i + 1] is not "false" and not "0"; break;
            }
        }
    }

    // =====================================================================
    // 2) ROTEAMENTO — GET /health, POST /v1/responses, demais = passa direto
    // =====================================================================
    /// <summary>Roteia cada HTTP: docs, saude, /v1/responses, passthrough.</summary>
    /// <param name="ctx">Contexto HTTP.</param>
    static void HandleRequest(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        try
        {
            Interlocked.Increment(ref TotalRequests);

            // ---- Documentacao Swagger (pagina black autocontida + OpenAPI JSON)
            if (ctx.Request.HttpMethod == "GET" && (path == "/swagger" || path == "/swagger/" || path == "/docs"))
            {
                ServeSwaggerPage(ctx);
                return;
            }

            if (ctx.Request.HttpMethod == "GET" && path == "/api/swagger.json")
            {
                ServeSwaggerJson(ctx);
                return;
            }

            // Metricas de vazao (tokens/s) p/ o e2e exibir no console/arquivo.
            if (ctx.Request.HttpMethod == "GET" && path == "/api/metrics")
            {
                double elapsed, tpsOut, tpsTotal;
                long inTok, outTok;
                lock (MetricsLock)
                {
                    inTok = TotalInputTokens; outTok = TotalOutputTokens; elapsed = TotalElapsedSecs;
                }
                tpsOut = elapsed > 0 ? outTok / elapsed : 0;
                tpsTotal = elapsed > 0 ? (inTok + outTok) / elapsed : 0;
                WriteJson(ctx, 200, new JsonObject
                {
                    ["requests"] = TotalRequests, ["failed_requests"] = FailedRequests,
                    ["input_tokens"] = inTok, ["output_tokens"] = outTok,
                    ["elapsed_seconds"] = Math.Round(elapsed, 2),
                    ["tps_output"] = Math.Round(tpsOut, 2),
                    ["tps_total"] = Math.Round(tpsTotal, 2)
                });
                return;
            }

            // ---- API de gerenciamento (permite operar a bridge remotamente)
            if (ctx.Request.HttpMethod == "GET" && path == "/api/status")
            {
                WriteJson(ctx, 200, BuildStatus());
                return;
            }

            if (ctx.Request.HttpMethod == "POST" && path == "/api/translate")
            {
                // Dry-run: mostra o /v1/chat/completions que SERIA enviado ao
                // vLLM, sem chamar o modelo. Util p/ depurar a traducao.
                var req = JsonNode.Parse(ReadBody(ctx))?.AsObject() ?? new JsonObject();
                var chat = BuildChatRequest(req, out string requestedModel, out double temperature, out string thinking, out int toolCount);
                WriteJson(ctx, 200, new JsonObject
                {
                    ["requested_model"] = requestedModel,
                    ["temperature"] = temperature,
                    ["thinking"] = thinking,
                    ["message_count"] = chat["messages"]!.AsArray().Count,
                    ["tool_count"] = toolCount,
                    ["chat_request"] = chat
                });
                return;
            }

            if (ctx.Request.HttpMethod == "POST" && path == "/api/convert")
            {
                // Converte um /v1/chat/completions PRONTO em objeto Responses,
                // sem chamar o modelo. Corpo: {model, temperature, completion}.
                var req = JsonNode.Parse(ReadBody(ctx))?.AsObject() ?? new JsonObject();
                var completion = req["completion"]?.AsObject() ?? req;
                string reqModel = req["model"]?.GetValue<string>() ?? Model;
                string reqThink = TryProfile(reqModel, out _, out string pThink) && pThink.Length > 0 ? pThink : Thinking;
                var responseObj = ChatToResponses(
                    completion,
                    reqModel,
                    req["temperature"]?.GetValue<double>() ?? Temperature,
                    reqThink);
                WriteJson(ctx, 200, responseObj);
                return;
            }

            if (ctx.Request.HttpMethod == "POST" && path == "/api/config")
            {
                // Ajuste remoto de parametros (parcial: so o que vier no corpo).
                var req = JsonNode.Parse(ReadBody(ctx))?.AsObject() ?? new JsonObject();
                if (req["thinking"]?.GetValue<string>() is string thinking && thinking is "events" or "off")
                    Thinking = thinking;
                if (req["temperature"]?.GetValue<double>() is double tempValue)
                    Temperature = tempValue;
                if (req["max_tokens"]?.GetValue<int>() is int maxTokens && maxTokens > 0)
                    MaxTokens = maxTokens;
                if (req["cpu_max_tokens"]?.GetValue<int>() is int cpuMax && cpuMax > 0)
                    CpuMaxTokens = cpuMax;
                Log(new JsonObject { ["event"] = "config", ["thinking"] = Thinking, ["temperature"] = Temperature, ["max_tokens"] = MaxTokens, ["cpu_max_tokens"] = CpuMaxTokens });
                WriteJson(ctx, 200, BuildStatus());
                return;
            }

            if (ctx.Request.HttpMethod == "GET" && (path == "/health" || path == "/" || path == "/v1/health"))
            {
                WriteJson(ctx, 200, new JsonObject
                {
                    ["status"] = "ok", ["bridge"] = "qwen3-csharp",
                    ["upstream"] = Upstream, ["model"] = Model, ["thinking"] = Thinking
                });
                return;
            }

            if (ctx.Request.HttpMethod == "POST" && (path == "/v1/responses" || path == "/responses"))
            {
                string body = ReadBody(ctx);
                HandleResponses(ctx, body);
                return;
            }

            // ---- Compatibilidade de SAIDA (padrao antigo e novo no mesma porta)
            // Antigo: /v1/chat/completions e /v1/completions (legado) = repasse
            //         cru, o cliente recebe o formato OpenAI original do vLLM.
            // Novo:   /v1/responses = traducao com thinking + tool calling.
            if (ctx.Request.HttpMethod == "POST" && (path == "/v1/chat/completions" || path == "/chat/completions"
                || path == "/v1/completions" || path == "/completions"))
            {
                // PASSA-DIRETO: util pra testar o vLLM sem passar pela traducao
                // (e pra qwen CLI e clientes antigos apontarem aqui sem quebrar nada).
                Passthrough(ctx, ReadBody(ctx));
                return;
            }

            // GET de compatibilidade (ex.: /v1/models p/ clientes listarem o modelo)
            if (ctx.Request.HttpMethod == "GET" && (path == "/v1/models" || path == "/models"))
            {
                Passthrough(ctx, "");
                return;
            }

            // Qualquer outra rota (models, embeddings, ...) tambem passa direto
            if (ctx.Request.HttpMethod == "POST")
            {
                Passthrough(ctx, ReadBody(ctx));
                return;
            }

            WriteJson(ctx, 404, new JsonObject { ["error"] = "rota nao suportada: " + path });
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref FailedRequests);
            // Cliente desconectou no meio (opencode abortou: Esc/timeout). Nao e
            // falha da bridge: registra como cancel e nao tenta responder (morto).
            string msg = ex.ToString();
            if (msg.Contains("Connection reset") || msg.Contains("forcibly closed")
                || msg.Contains("Broken pipe") || msg.Contains("Unable to write data"))
            {
                Log(new JsonObject { ["event"] = "cancel", ["path"] = path });
                try { ctx.Response.Abort(); } catch { }
                return;
            }
            Log(new JsonObject { ["event"] = "error", ["path"] = path, ["error"] = Truncate(msg, 800) });
            try { WriteJson(ctx, 500, new JsonObject { ["error"] = ex.Message }); } catch { }
        }
    }

    // Foto da configuracao + telemetria p/ quem opera a bridge remotamente.
    /// <summary>Foto de config + telemetria p/ operacao remota.</summary>
    /// <returns>Status JSON.</returns>
    static JsonObject BuildStatus() => new()
    {
        ["status"] = "ok", ["bridge"] = "qwen3-csharp", ["version"] = Version,
        ["listen"] = Listen, ["port"] = Port,
        ["upstream"] = Upstream, ["model"] = Model, ["thinking"] = Thinking,
        ["temperature"] = Temperature, ["max_tokens"] = MaxTokens,
        ["cpu_max_tokens"] = CpuMaxTokens,
        ["uptime_seconds"] = (long)(DateTime.UtcNow - StartedAt).TotalSeconds,
        ["total_requests"] = TotalRequests, ["failed_requests"] = FailedRequests
    };

    /// <summary>Le o corpo da requisicao como texto UTF-8.</summary>
    /// <param name="ctx">Contexto HTTP.</param>
    /// <returns>Corpo bruto.</returns>
    static string ReadBody(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Responde JSON com status e Content-Length.</summary>
    /// <param name="ctx">Contexto HTTP.</param>
    /// <param name="status">HTTP status.</param>
    /// <param name="obj">Payload.</param>
    static void WriteJson(HttpListenerContext ctx, int status, JsonNode obj)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(obj.ToJsonString(SerOpts));
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    // =====================================================================
    // 3) PASSA-DIRETO — encaminha o corpo cru pro vLLM e devolve o que vier
    // =====================================================================
    /// <summary>Encaminha o corpo cru ao vLLM e devolve o stream intacto.</summary>
    /// <param name="ctx">Contexto HTTP.</param>
    /// <param name="body">Corpo original.</param>
    static void Passthrough(HttpListenerContext ctx, string body)
    {
        // opencode 1.x fala chat/completions direto (sem /v1/responses): o id
        // com sufixo de perfil (-plan/-build/-orch) precisa virar o Model
        // servido, senao o vLLM responde 404. Temperatura do perfil entra
        // quando o cliente nao informa nenhuma. llama.cpp aceita qualquer
        // id, mas normalizar aqui mantem o log/telemetria coerentes.
        string outBody = body;
        try
        {
            var node = JsonNode.Parse(body)?.AsObject();
            if (node is not null && node["model"]?.GetValue<string>() is string mid && mid != Model)
            {
                bool hasProfile = TryProfile(mid, out double profTemp, out _);
                if (hasProfile || CoerceFor(mid))
                {
                    node["model"] = Model;
                    if (node["temperature"] is null && !double.IsNaN(profTemp))
                        node["temperature"] = profTemp;
                    // Linha CPU: o 3B entra em loop de filler ([END_OF_TEXT])
                    // quando divaga — stop dedicado (só aqui; Responses/chat
                    // nativo do vLLM jamais recebe stop automático).
                    if (node["stop"] is null && CoerceFor(mid))
                        node["stop"] = new JsonArray("[END_OF_TEXT]");
                    outBody = node.ToJsonString(SerOpts);
                }
            }
        }
        catch { outBody = body; }
        // Preserva o verbo original (GET p/ models, POST p/ completions).
        // Linha CPU (llama sem parser server-side): forca upstream sem stream
        // p/ coagir o texto em tool_calls e devolver SSE sintetizado — senao o
        // opencode 1.x recebe texto puro, nenhuma tool executa e o agente entra
        // em loop re-planejando. Linha GPU segue em streaming real.
        bool isChat = ctx.Request.Url!.AbsolutePath.Contains("chat/completions");
        bool coerceLine = CoerceFor(Model) && isChat
            && ctx.Request.HttpMethod is "POST" or "PUT" or "PATCH";
        bool wantStream = false;
        if (coerceLine)
        {
            try
            {
                var n2 = JsonNode.Parse(outBody)?.AsObject();
                wantStream = n2?["stream"]?.GetValue<bool>() ?? false;
                if (n2 is not null) { n2["stream"] = false; outBody = n2.ToJsonString(SerOpts); }
            }
            catch { wantStream = false; }
        }
        var forward = new HttpRequestMessage(new HttpMethod(ctx.Request.HttpMethod), Upstream + ctx.Request.Url!.AbsolutePath);
        if (ctx.Request.HttpMethod is "POST" or "PUT" or "PATCH")
            forward.Content = new StringContent(outBody, Encoding.UTF8, "application/json");
        using var upstreamResponse = Http.Send(forward, HttpCompletionOption.ResponseHeadersRead);
        if (coerceLine && upstreamResponse.IsSuccessStatusCode)
        {
            CoercedChatResponse(ctx, upstreamResponse, wantStream);
            return;
        }
        ctx.Response.StatusCode = (int)upstreamResponse.StatusCode;
        ctx.Response.ContentType = upstreamResponse.Content.Headers.ContentType?.ToString() ?? "application/json";
        using var upstreamStream = upstreamResponse.Content.ReadAsStream();
        string ct = ctx.Response.ContentType ?? "";
        if (ct.Contains("text/event-stream"))
        {
            // Renomeia o campo de reasoning do vLLM ("reasoning") para o nome
            // que o opencode/openai-chat espera ("reasoning_content"), preservando
            // o streaming linha a linha (sem bufferizar o SSE).
            using var reader = new StreamReader(upstreamStream, new UTF8Encoding(false));
            using var writer = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false)) { AutoFlush = true };
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Contains("\"reasoning\":"))
                    line = line.Replace("\"reasoning\":", "\"reasoning_content\":");
                writer.WriteLine(line);
            }
        }
        else
        {
            // JSON unico: mesma renomeacao no corpo completo.
            using var reader = new StreamReader(upstreamStream, new UTF8Encoding(false));
            string json = reader.ReadToEnd();
            if (json.Contains("\"reasoning\":"))
                json = json.Replace("\"reasoning\":", "\"reasoning_content\":");
            var bytes = new UTF8Encoding(false).GetBytes(json);
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }
        ctx.Response.Close();
    }

    /// <summary>Resposta chat da linha CPU: coage texto em tool_calls e devolve JSON ou SSE sintetizado.</summary>
    static void CoercedChatResponse(HttpListenerContext ctx, HttpResponseMessage upstreamResponse, bool wantStream)
    {
        string json;
        using (var reader = new StreamReader(upstreamResponse.Content.ReadAsStream(), new UTF8Encoding(false)))
            json = reader.ReadToEnd();
        int coerced = 0;
        try
        {
            var root = JsonNode.Parse(json)?.AsObject();
            var msg = root?["choices"]?.AsArray().FirstOrDefault()?["message"]?.AsObject();
            if (msg is not null && msg["tool_calls"] is null)
            {
                string content = msg["content"]?.GetValue<string>() ?? "";
                var calls = ExtractTextToolCalls(content);
                if (calls.Count > 0)
                {
                    var arr = new JsonArray();
                    foreach (var (Name, Arguments) in calls)
                        arr.Add(new JsonObject
                        {
                            ["index"] = arr.Count,
                            ["id"] = "call_" + Guid.NewGuid().ToString("N")[..8],
                            ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = Name, ["arguments"] = Arguments }
                        });
                    msg["tool_calls"] = arr;
                    var choices = root!["choices"]!.AsArray();
                    if (choices[0]?.AsObject() is JsonObject first)
                        first["finish_reason"] = "tool_calls";
                    coerced = calls.Count;
                    json = root!.ToJsonString(SerOpts);
                }
            }
        }
        catch { /* corpo inesperado: devolve cru */ }
        Log(new JsonObject { ["event"] = "coerced-chat", ["model"] = Model, ["count"] = coerced });
        if (!wantStream)
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            var bytes = new UTF8Encoding(false).GetBytes(json);
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
            return;
        }
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/event-stream";
        using var writer = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false)) { AutoFlush = true };
        try
        {
            var root = JsonNode.Parse(json)?.AsObject();
            var msg = root?["choices"]?.AsArray().FirstOrDefault();
            string? finish = msg?["finish_reason"]?.GetValue<string>();
            var m = msg?["message"]?.AsObject();
            var delta = new JsonObject { ["role"] = "assistant" };
            if (m?["content"]?.GetValue<string>() is string c && c.Length > 0) delta["content"] = c;
            if (m?["tool_calls"] is JsonArray tc) delta["tool_calls"] = (JsonNode?)JsonNode.Parse(tc.ToJsonString(SerOpts));
            var chunk = new JsonObject
            {
                ["id"] = root?["id"]?.GetValue<string>() ?? ("chatcmpl-" + Guid.NewGuid().ToString("N")[..8]),
                ["object"] = "chat.completion.chunk",
                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["model"] = Model,
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish
                })
            };
            writer.WriteLine("data: " + chunk.ToJsonString(SerOpts));
            writer.WriteLine();
            writer.WriteLine("data: [DONE]");
            writer.WriteLine();
        }
        catch { writer.WriteLine("data: [DONE]"); writer.WriteLine(); }
        ctx.Response.Close();
    }

    // =====================================================================
    // 4) NUCLEO — /v1/responses  =>  /v1/chat/completions  =>  /v1/responses
    // =====================================================================
    // 4.1-4.4) Monta o body do /v1/chat/completions a partir do request
    // Responses. Metodo exposto tambem via POST /api/translate (dry-run).
    /// <summary>Monta o chat request a partir do Responses (usado no fluxo e no dry-run).</summary>
    /// <param name="req">Request Responses.</param>
    /// <param name="requestedModel">Model id do cliente (eco).</param>
    /// <param name="temperature">Temperatura efetiva.</param>
    /// <param name="toolCount">No. de tools convertidas.</param>
    /// <returns>Body p/ /v1/chat/completions.</returns>
    static JsonObject BuildChatRequest(JsonObject req, out string requestedModel, out double temperature, out string thinking, out int toolCount)
    {
        // O opencode manda "openai/qwen3-8b-awq" mas o vLLM so conhece o id
        // servido ("qwen3-8b-awq") e responde 404 p/ outro id. Por isso o
        // upstream SEMPRE usa o Model configurado; o id original so volta
        // como eco no campo "model" da resposta.
        requestedModel = req["model"]?.GetValue<string>() ?? Model;
        // Perfil (-plan/-build): temperatura+thinking do perfil; temperatura
        // explicita no request vence o perfil; sem perfil valem os globais.
        bool hasProfile = TryProfile(requestedModel, out double profTemp, out string profThink);
        thinking = hasProfile && !string.IsNullOrEmpty(profThink) ? profThink : Thinking;
        // Temperatura: a do opencode tem prioridade; a da linha de comando
        // e so o padrao quando o request nao informa nenhuma.
        temperature = req["temperature"]?.GetValue<double>()
            ?? (hasProfile && !double.IsNaN(profTemp) ? profTemp : Temperature);

        // Converte a conversa Responses -> messages do formato chat
        var messages = InputToMessages(req["input"], req["instructions"]);
        // Converte as tools Responses (formato "chato") -> formato chat
        var tools = ToolsToChat(req["tools"]);
        toolCount = tools?.Count ?? 0;

        // Monta o body do /v1/chat/completions
        // Teto 4k de fabrica (8k no remaster); CPU sem parser usa CpuMaxTokens
        // (decode lento: teto alto = divagacao de minutos sem tool call).
        // Honra pedido menor, nunca maior que o teto da linha.
        int cap = CoerceFor(requestedModel) ? CpuMaxTokens : MaxTokens;
        int asked = req["max_tokens"]?.GetValue<int>()
            ?? req["max_output_tokens"]?.GetValue<int>() ?? cap;
        if (asked <= 0) asked = cap;
        var chat = new JsonObject
        {
            ["model"] = Model,
            ["messages"] = messages,
            ["temperature"] = temperature,
            ["max_tokens"] = Math.Min(asked, cap),
            ["stream"] = false,          // upstream sem stream: resposta unica, sintetizamos o SSE
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = thinking == "events" }
        };
        if (tools is { Count: > 0 })
        {
            chat["tools"] = tools;
            // Qwen3 decide sozinho ("auto", bake-off 3/3); Coder precisa de
            // "required" p/ emitir tool_calls nativo em vez de texto.
            chat["tool_choice"] = IsCoder ? "required" : "auto";
        }
        // Stop sequences: repasse opcional (string ou array). ATENCAO: com
        // tools ativas, stop pode truncar o JSON da tool call no meio.
        if (req["stop"] is JsonNode stop)
        {
            bool hasStop = stop is JsonArray stopArr ? stopArr.Count > 0
                : stop.GetValueKind() == System.Text.Json.JsonValueKind.String
                  && !string.IsNullOrEmpty(stop.GetValue<string>());
            if (hasStop) chat["stop"] = stop.DeepClone();
        }
        return chat;
    }

    /// <summary>Nucleo: Responses -> chat -> vLLM -> Responses (JSON ou SSE).</summary>
    /// <param name="ctx">Contexto HTTP.</param>
    /// <param name="body">Request Responses.</param>
    static void HandleResponses(HttpListenerContext ctx, string body)
    {
        var startedAt = DateTime.UtcNow;

        // 4.1-4.4) Le o request Responses e monta o chat request
        var req = JsonNode.Parse(body)?.AsObject() ?? new JsonObject();
        bool wantStream = req["stream"]?.GetValue<bool>() ?? false;
        var chat = BuildChatRequest(req, out string requestedModel, out double temperature, out string thinking, out int toolCount);

        // Stream real sai por caminho proprio (sem a chamada unica abaixo).
        if (wantStream)
        {
            HandleResponsesStream(ctx, chat, requestedModel, temperature, thinking, startedAt);
            return;
        }

        // 4.5) CHAMADA AO vLLM
        var chatRequest = new HttpRequestMessage(HttpMethod.Post, Upstream + "/v1/chat/completions")
        {
            Content = new StringContent(chat.ToJsonString(SerOpts), Encoding.UTF8, "application/json")
        };
        using var upstreamResponse = Http.Send(chatRequest);
        string responseBody = upstreamResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!upstreamResponse.IsSuccessStatusCode)
        {
            // Repassa o erro do vLLM tal qual (opencode mostra a mensagem real)
            ctx.Response.StatusCode = (int)upstreamResponse.StatusCode;
            byte[] errorBytes = Encoding.UTF8.GetBytes(responseBody);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = errorBytes.Length;
            ctx.Response.OutputStream.Write(errorBytes);
            ctx.Response.Close();
            return;
        }

        // 4.6) Converte a resposta chat -> objeto Responses
        var chatJson = JsonNode.Parse(responseBody)?.AsObject() ?? new JsonObject();
        var responseObj = ChatToResponses(chatJson, requestedModel, temperature, thinking);

        double elapsed = (DateTime.UtcNow - startedAt).TotalSeconds;
        int inTok = responseObj["usage"]?["input_tokens"]?.GetValue<int>() ?? 0;
        int outTok = responseObj["usage"]?["output_tokens"]?.GetValue<int>() ?? 0;
        Log(new JsonObject
        {
            ["event"] = "response", ["model"] = requestedModel, ["stream"] = wantStream,
            ["message_count"] = chat["messages"]!.AsArray().Count, ["tool_count"] = toolCount,
            ["has_tool_call"] = responseObj["output"]!.AsArray().Any(i => i?["type"]?.GetValue<string>() == "function_call"),
            ["has_reasoning"] = responseObj["output"]!.AsArray().Any(i => i?["type"]?.GetValue<string>() == "reasoning"),
            ["elapsed"] = Math.Round(elapsed, 2),
            ["input_tokens"] = inTok, ["output_tokens"] = outTok,
            ["tps_output"] = Math.Round(elapsed > 0 ? outTok / elapsed : 0, 1)
        });
        AddMetrics(inTok, outTok, elapsed);

        // 4.7) Sem stream: devolve JSON direto.
        WriteJson(ctx, 200, responseObj);
    }

    /// <summary>Streaming real: repassa deltas do vLLM na hora, fecha com completed.</summary>
    /// <param name="ctx">Contexto HTTP.</param>
    /// <param name="chat">Chat request ja montado.</param>
    /// <param name="requestedModel">Model id p/ eco.</param>
    /// <param name="temperature">Efetiva.</param>
    /// <param name="startedAt">Inicio p/ elapsed.</param>
    static void HandleResponsesStream(HttpListenerContext ctx, JsonObject chat,
        string requestedModel, double temperature, string thinking, DateTime startedAt)
    {
        // Pede stream ao upstream (com usage no fim p/ fechar os tokens).
        chat["stream"] = true;
        chat["stream_options"] = new JsonObject { ["include_usage"] = true };

        var chatRequest = new HttpRequestMessage(HttpMethod.Post, Upstream + "/v1/chat/completions")
        {
            Content = new StringContent(chat.ToJsonString(SerOpts), Encoding.UTF8, "application/json")
        };

        // Headers do upstream ANTES de abrir o SSE: se der erro aqui,
        // devolvemos JSON de erro limpo (nada foi enviado ao cliente ainda).
        using var upstreamResponse = Http.Send(chatRequest, HttpCompletionOption.ResponseHeadersRead);
        if (!upstreamResponse.IsSuccessStatusCode)
        {
            string errorBody = upstreamResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Log(new JsonObject { ["event"] = "error", ["mode"] = "live", ["status"] = (int)upstreamResponse.StatusCode });
            ctx.Response.StatusCode = (int)upstreamResponse.StatusCode;
            byte[] errorBytes = Encoding.UTF8.GetBytes(errorBody);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = errorBytes.Length;
            ctx.Response.OutputStream.Write(errorBytes);
            ctx.Response.Close();
            return;
        }

        // Abre o SSE em chunked: cada Emit desce na hora p/ o opencode.
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/event-stream; charset=utf-8";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.SendChunked = true;
        using var writer = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false)) { AutoFlush = true };
        int sequence = 0;
        void Emit(string type, JsonObject payload)
        {
            payload["sequence_number"] = sequence++;
            writer.Write("event: " + type + "\ndata: " + payload.ToJsonString(SerOpts) + "\n\n");
            writer.Flush();
        }

        string respId = "resp_" + NewId();
        var shell = new JsonObject
        {
            ["id"] = respId, ["object"] = "response",
            ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["status"] = "in_progress", ["model"] = requestedModel,
            ["output"] = new JsonArray(), ["parallel_tool_calls"] = true,
            ["tool_choice"] = "auto", ["tools"] = new JsonArray(),
            ["temperature"] = temperature, ["service_tier"] = "auto",
            ["usage"] = null
        };
        Emit("response.created", new JsonObject
            { ["type"] = "response.created", ["response"] = shell.DeepClone() });
        Emit("response.in_progress", new JsonObject
            { ["type"] = "response.in_progress", ["response"] = shell });

        // ---- Estado acumulado durante o stream
        var thinkingText = new StringBuilder();
        var contentText = new StringBuilder();
        string reasoningId = "";
        string messageId = "";
        bool thinkOpen = false, msgOpen = false, inThink = false;
        string carry = "";   // texto ainda nao classificado (tag pode partir no meio do chunk)
        var toolAccum = new SortedDictionary<int, (string Id, string Name, StringBuilder Args)>();
        int inputTokens = 0, outputTokens = 0;

        // Abre o item reasoning no primeiro delta de thinking.
        void FlushThinking(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (!thinkOpen && thinking == "events")
            {
                reasoningId = "rs_" + NewId();
                Emit("response.output_item.added", new JsonObject
                {
                    ["type"] = "response.output_item.added", ["output_index"] = 0,
                    ["item"] = new JsonObject { ["id"] = reasoningId, ["type"] = "reasoning", ["summary"] = new JsonArray() }
                });
                Emit("response.reasoning_summary_part.added", new JsonObject
                {
                    ["type"] = "response.reasoning_summary_part.added", ["item_id"] = reasoningId,
                    ["output_index"] = 0, ["content_index"] = 0, ["summary_index"] = 0,
                    ["part"] = new JsonObject { ["type"] = "summary_text", ["text"] = "" }
                });
                thinkOpen = true;
            }
            // Modo "off": sem item reasoning; o raciocinio vira texto comum.
            if (!thinkOpen && thinking != "events") { FlushMessage(text); return; }
            if (!thinkOpen) return;
            thinkingText.Append(text);
            Emit("response.reasoning_summary_text.delta", new JsonObject
            {
                ["type"] = "response.reasoning_summary_text.delta", ["item_id"] = reasoningId,
                ["output_index"] = 0, ["content_index"] = 0, ["summary_index"] = 0, ["delta"] = text
            });
        }

        // Abre o item message no primeiro delta de texto.
        void FlushMessage(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (!msgOpen)
            {
                messageId = "msg_" + NewId();
                Emit("response.output_item.added", new JsonObject
                {
                    ["type"] = "response.output_item.added", ["output_index"] = thinkOpen ? 1 : 0,
                    ["item"] = new JsonObject
                    {
                        ["id"] = messageId, ["type"] = "message", ["role"] = "assistant",
                        ["status"] = "in_progress", ["content"] = new JsonArray(), ["phase"] = null
                    }
                });
                Emit("response.content_part.added", new JsonObject
                {
                    ["type"] = "response.content_part.added", ["output_index"] = thinkOpen ? 1 : 0,
                    ["content_index"] = 0, ["item_id"] = messageId,
                    ["part"] = new JsonObject { ["type"] = "output_text", ["text"] = "", ["annotations"] = new JsonArray() }
                });
                msgOpen = true;
            }
            contentText.Append(text);
            Emit("response.output_text.delta", new JsonObject
            {
                ["type"] = "response.output_text.delta", ["item_id"] = messageId,
                ["output_index"] = thinkOpen ? 1 : 0, ["content_index"] = 0, ["delta"] = text,
                ["logprobs"] = new JsonArray()
            });
        }

        // Maquina de estados do hibrido: <think> decide o destino de cada
        // trecho. Guarda cauda de 16 chars p/ tag partida entre chunks.
        void RouteContent(string chunk)
        {
            carry += chunk;
            while (true)
            {
                if (!inThink)
                {
                    int tag = carry.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
                    if (tag >= 0)
                    {
                        FlushMessage(carry[..tag]);
                        carry = carry[(tag + 7)..];
                        inThink = true;
                        continue;
                    }
                    if (carry.Length > 256) { FlushMessage(carry[..^16]); carry = carry[^16..]; }
                    break;
                }
                else
                {
                    int end = carry.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                    if (end >= 0)
                    {
                        FlushThinking(carry[..end]);
                        carry = carry[(end + 8)..];
                        inThink = false;
                        continue;
                    }
                    if (carry.Length > 256) { FlushThinking(carry[..^16]); carry = carry[^16..]; }
                    break;
                }
            }
        }

        // ---- Le o SSE do upstream e traduz delta a delta
        using var stream = upstreamResponse.Content.ReadAsStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (!line.StartsWith("data:")) continue;
            string data = line["data:".Length..].Trim();
            if (data == "[DONE]") break;
            JsonObject? delta;
            try { delta = JsonNode.Parse(data)?.AsObject(); }
            catch { continue; }   // chunk parcial: ignora, o proximo completa
            if (delta is null) continue;

            if (delta["usage"]?.AsObject() is JsonObject usage)
            {
                inputTokens = usage["prompt_tokens"]?.GetValue<int>() ?? inputTokens;
                outputTokens = usage["completion_tokens"]?.GetValue<int>() ?? outputTokens;
            }

            var choices = delta["choices"]?.AsArray();
            // Chunk final de usage vem com choices vazio: protege o indice.
            var choice = choices is { Count: > 0 } ? choices[0]?.AsObject() : null;
            if (choice is null) continue;
            var deltaMsg = choice["delta"]?.AsObject() ?? choice["message"]?.AsObject();
            if (deltaMsg is null) continue;

            var reasoningChunk = deltaMsg["reasoning_content"]?.GetValue<string>()
                                 ?? deltaMsg["reasoning"]?.GetValue<string>();
            if (reasoningChunk is not null)
                FlushThinking(reasoningChunk);
            if (deltaMsg["content"]?.GetValue<string>() is string contentChunk)
                RouteContent(contentChunk);

            if (deltaMsg["tool_calls"] is JsonArray calls)
            {
                foreach (var callNode in calls)
                {
                    if (callNode is not JsonObject callObj) continue;
                    int index = callObj["index"]?.GetValue<int>() ?? 0;
                    if (!toolAccum.TryGetValue(index, out var acc))
                    {
                        acc = (callObj["id"]?.GetValue<string>() ?? ("call_" + NewId()), "", new StringBuilder());
                        toolAccum[index] = acc;
                    }
                    var func = callObj["function"]?.AsObject();
                    if (func?["name"]?.GetValue<string>() is string funcName && funcName.Length > 0)
                        toolAccum[index] = (acc.Id, funcName, acc.Args);
                    if (func?["arguments"]?.GetValue<string>() is string argsChunk)
                        acc.Args.Append(argsChunk);
                }
            }
        }

        // ---- Fecho: descarrega o resto, fecha itens, emite tool calls
        if (inThink) FlushThinking(carry); else FlushMessage(carry);

        int msgIndex = thinkOpen ? 1 : 0;
        if (thinkOpen)
        {
            Emit("response.reasoning_summary_text.done", new JsonObject
            {
                ["type"] = "response.reasoning_summary_text.done", ["item_id"] = reasoningId,
                ["output_index"] = 0, ["content_index"] = 0, ["summary_index"] = 0, ["text"] = thinkingText.ToString()
            });
            // Sem o part.done o opencode nao consolida os deltas (text fica "").
            Emit("response.reasoning_summary_part.done", new JsonObject
            {
                ["type"] = "response.reasoning_summary_part.done", ["item_id"] = reasoningId,
                ["output_index"] = 0, ["content_index"] = 0, ["summary_index"] = 0,
                ["part"] = new JsonObject { ["type"] = "summary_text", ["text"] = thinkingText.ToString() }
            });
            Emit("response.output_item.done", new JsonObject
            {
                ["type"] = "response.output_item.done", ["output_index"] = 0,
                ["item"] = new JsonObject
                {
                    ["id"] = reasoningId, ["type"] = "reasoning",
                    ["summary"] = new JsonArray(new JsonObject { ["type"] = "summary_text", ["text"] = thinkingText.ToString() })
                }
            });
        }
        if (msgOpen)
        {
            string fullText = ReTemplate.Replace(contentText.ToString(), "");
            Emit("response.output_text.done", new JsonObject
            {
                ["type"] = "response.output_text.done", ["item_id"] = messageId,
                ["output_index"] = msgIndex, ["content_index"] = 0, ["text"] = fullText,
                ["logprobs"] = new JsonArray()
            });
            Emit("response.content_part.done", new JsonObject
            {
                ["type"] = "response.content_part.done", ["output_index"] = msgIndex,
                ["content_index"] = 0, ["item_id"] = messageId,
                ["part"] = new JsonObject { ["type"] = "output_text", ["text"] = fullText, ["annotations"] = new JsonArray() }
            });
            Emit("response.output_item.done", new JsonObject
            {
                ["type"] = "response.output_item.done", ["output_index"] = msgIndex,
                ["item"] = new JsonObject
                {
                    ["id"] = messageId, ["type"] = "message", ["role"] = "assistant",
                    ["status"] = "completed",
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "output_text", ["text"] = fullText,
                        ["annotations"] = new JsonArray(), ["logprobs"] = null
                    })
                }
            });
        }

        // Coerção no fecho do stream (perfil CPU/llama): sem deltas de call,
        // extrai do texto acumulado. vLLM nativo nao passa por aqui com calls.
        if (CoerceFor(requestedModel) && toolAccum.Count == 0)
        {
            foreach (var (name, args) in ExtractTextToolCalls(contentText.ToString()))
                toolAccum[toolAccum.Count] = ("call_" + NewId(), name, new StringBuilder(args));
        }

        int callIndex = (thinkOpen ? 1 : 0) + (msgOpen ? 1 : 0);
        var toolList = new List<(string Id, string Name, string Arguments)>();
        foreach (var (_, acc) in toolAccum)
        {
            string arguments = acc.Args.ToString();
            toolList.Add((acc.Id, acc.Name, arguments));
            var item = new JsonObject
            {
                ["type"] = "function_call", ["id"] = acc.Id, ["call_id"] = acc.Id,
                ["name"] = acc.Name, ["arguments"] = arguments
            };
            Emit("response.output_item.added", new JsonObject
            {
                ["type"] = "response.output_item.added", ["output_index"] = callIndex,
                ["item"] = (JsonNode)item.DeepClone()
            });
            Emit("response.function_call_arguments.delta", new JsonObject
            {
                ["type"] = "response.function_call_arguments.delta", ["item_id"] = acc.Id,
                ["output_index"] = callIndex, ["delta"] = arguments
            });
            Emit("response.function_call_arguments.done", new JsonObject
            {
                ["type"] = "response.function_call_arguments.done", ["item_id"] = acc.Id,
                ["output_index"] = callIndex, ["arguments"] = arguments
            });
            Emit("response.output_item.done", new JsonObject
            {
                ["type"] = "response.output_item.done", ["output_index"] = callIndex, ["item"] = item
            });
            callIndex++;
        }

        // Reaproveita o montador bufferizado p/ gerar o objeto final do
        // completed (usage + ids consistentes com o que ja foi emitido).
        var synthetic = new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject
            {
                ["message"] = new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = (inThink || thinkOpen) && thinkingText.Length > 0
                        ? "<think>" + thinkingText.ToString() + "</think>\n" + contentText.ToString()
                        : contentText.ToString(),
                    ["tool_calls"] = new JsonArray(toolList.Select(t => new JsonObject
                    {
                        ["id"] = t.Id, ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = t.Name, ["arguments"] = t.Arguments }
                    }).ToArray())
                }
            }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = inputTokens, ["completion_tokens"] = outputTokens }
        };
        var finalObj = ChatToResponses(synthetic, requestedModel, temperature, thinking);
        finalObj["id"] = respId;
        // Ids do completed precisam casar com os itens ja anunciados no stream.
        foreach (var outputNode in finalObj["output"]!.AsArray())
        {
            if (outputNode is not JsonObject outputItem) continue;
            string itemType = outputItem["type"]?.GetValue<string>() ?? "";
            if (itemType == "reasoning" && thinkOpen) outputItem["id"] = reasoningId;
            if (itemType == "message" && msgOpen) outputItem["id"] = messageId;
        }

        Emit("response.completed", new JsonObject
            { ["type"] = "response.completed", ["response"] = finalObj });

        double liveElapsed = (DateTime.UtcNow - startedAt).TotalSeconds;
        Log(new JsonObject
        {
            ["event"] = "response", ["mode"] = "live", ["model"] = requestedModel, ["stream"] = true,
            ["message_count"] = chat["messages"]!.AsArray().Count, ["tool_count"] = toolList.Count,
            ["has_tool_call"] = toolList.Count > 0, ["has_reasoning"] = thinkOpen,
            ["elapsed"] = Math.Round(liveElapsed, 2),
            ["input_tokens"] = inputTokens, ["output_tokens"] = outputTokens,
            ["tps_output"] = Math.Round(liveElapsed > 0 ? outputTokens / liveElapsed : 0, 1)
        });
        AddMetrics(inputTokens, outputTokens, liveElapsed);
        ctx.Response.Close();
    }

    // ---------------------------------------------------------------------
    // 4.2) input (Responses) -> messages (chat)
    //   item message           -> {role, content}
    //   item function_call     -> assistant com tool_calls (id preservado!)
    //   item function_call_output -> role "tool" ligado ao call_id
    // ---------------------------------------------------------------------
    /// <summary>Converte input/instructions Responses em messages do chat.</summary>
    /// <param name="input">Itens Responses.</param>
    /// <param name="instructions">Sistema.</param>
    /// <returns>Messages.</returns>
    static JsonArray InputToMessages(JsonNode? input, JsonNode? instructions)
    {
        var messages = new JsonArray();

        // "instructions" do Responses vira a mensagem de sistema
        if (instructions is JsonValue instructionNode && instructionNode.TryGetValue<string>(out string? systemText) && !string.IsNullOrWhiteSpace(systemText))
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = systemText });

        if (input is JsonValue stringValue && stringValue.TryGetValue<string>(out string? directText))
        {
            // input pode ser uma string pura em vez de lista
            if (!string.IsNullOrWhiteSpace(directText))
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = directText });
            return messages;
        }

        if (input is not JsonArray list) return messages;

        foreach (var entryNode in list)
        {
            if (entryNode is null) continue;

            // input as vezes chega como string simples
            if (entryNode is JsonValue textValue && textValue.TryGetValue<string>(out string? s))
            {
                if (!string.IsNullOrWhiteSpace(s))
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = s });
                continue;
            }
            if (entryNode is not JsonObject entry) continue;

            string type = entry["type"]?.GetValue<string>() ?? "message";

            if (type == "message")
            {
                string role = entry["role"]?.GetValue<string>() ?? "user";
                string content = ExtractText(entry["content"]);
                if (string.IsNullOrWhiteSpace(content)) continue;
                if (role == "developer") role = "system";
                if (role is not ("user" or "system" or "assistant")) role = "user";
                messages.Add(new JsonObject { ["role"] = role, ["content"] = content });
            }
            else if (type == "function_call")
            {
                // O opencode reenvia a tool call que ele EXECUTOU: preservamos o
                // call_id original para o role="tool" abaixo casar com ele.
                string callId = entry["call_id"]?.GetValue<string>() ?? entry["id"]?.GetValue<string>() ?? ("call_" + NewId());
                string name = entry["name"]?.GetValue<string>() ?? "";
                string arguments = JsonToString(entry["arguments"]);
                messages.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = null,
                    ["tool_calls"] = new JsonArray(new JsonObject
                    {
                        ["id"] = callId, ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments }
                    })
                });
            }
            else if (type is "function_call_output" or "custom_tool_call_output" or "shell_call_output"
                     or "computer_call_output" or "local_shell_call_output" or "apply_patch_call_output")
            {
                string callId = entry["call_id"]?.GetValue<string>() ?? entry["id"]?.GetValue<string>() ?? "";
                string resultText = ExtractText(entry["output"] ?? entry["result"] ?? entry["content"]);
                if (resultText.Length > ToolOutputLimit) resultText = resultText[..ToolOutputLimit] + "\n[truncado]";
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = callId, ["content"] = resultText });
            }
            // Outros tipos (reasoning, item_reference, calls ainda nao executados)
            // sao ignorados de proposito: nao fazem falta no fluxo chat.
        }
        return messages;
    }

    // Extrai texto de content/output seja string, lista de partes ou objeto.
    /// <summary>Extrai texto de no string, lista de partes ou objeto.</summary>
    /// <param name="node">No qualquer.</param>
    /// <returns>Texto puro.</returns>
    static string ExtractText(JsonNode? node)
    {
        if (node is null) return "";
        if (node is JsonValue value)
            return value.TryGetValue<string>(out string? s) ? s : value.ToJsonString(SerOpts);
        if (node is JsonArray parts)
        {
            var builder = new StringBuilder();
            foreach (var part in parts)
            {
                if (part is JsonValue partValue && partValue.TryGetValue<string>(out string? partText)) { builder.Append(partText); continue; }
                if (part is JsonObject partObj && partObj["text"] is JsonValue textNode && textNode.TryGetValue<string>(out string? nestedText))
                    builder.Append(nestedText);
            }
            return builder.ToString();
        }
        if (node is JsonObject obj && obj["text"] is JsonValue text && text.TryGetValue<string>(out string? objText))
            return objText;
        return node.ToJsonString(SerOpts);
    }

    /// <summary>Serializa no; string passa direto, nulo vira {}.</summary>
    /// <param name="node">No qualquer.</param>
    /// <returns>JSON textual.</returns>
    static string JsonToString(JsonNode? node) =>
        node is null ? "{}"
        : node is JsonValue value && value.TryGetValue<string>(out string? s) ? s
        : node.ToJsonString(SerOpts);

    // Um JsonNode so pode ter UM pai: reaproveitar no do request na arvore
    // nova lanca "The node already has a parent". Por isso clonamos.
    /// <summary>Clona no (JsonNode exige pai unico) ou schema vazio.</summary>
    /// <param name="node">No qualquer.</param>
    /// <returns>Clone ou schema vazio.</returns>
    static JsonNode CloneOrDefault(JsonNode? node) =>
        node is null ? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() } : node.DeepClone();

    // ---------------------------------------------------------------------
    // 4.3) tools Responses -> tools chat
    //   formato Responses: {type:"function", name, description, inputSchema}
    //   formato chat:      {type:"function", function:{name, description, parameters}}
    //   (tambem aceita o formato chat pronto e "namespace" do opencode)
    // ---------------------------------------------------------------------
    /// <summary>Converte tools Responses (flat/chat/namespace) p/ formato chat.</summary>
    /// <param name="tools">Array de tools.</param>
    /// <returns>Tools chat ou nulo.</returns>
    static JsonArray? ToolsToChat(JsonNode? tools)
    {
        if (tools is not JsonArray list || list.Count == 0) return null;
        var converted = new JsonArray();
        foreach (var toolNode in list)
        {
            if (toolNode is not JsonObject entry) continue;
            string type = entry["type"]?.GetValue<string>() ?? "";

            if (type == "function" && entry["function"] is JsonObject)
            { converted.Add(entry.DeepClone()); continue; }

            if (type == "function" && entry["name"] is not null)
            {
                converted.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = entry["name"]?.GetValue<string>(),
                        ["description"] = entry["description"]?.GetValue<string>() ?? "",
                        ["parameters"] = CloneOrDefault(entry["parameters"] ?? entry["inputSchema"])
                    }
                });
                continue;
            }

            if (type == "namespace" && entry["tools"] is JsonArray nested)
            {
                foreach (var nestedNode in nested)
                {
                    if (nestedNode is not JsonObject nestedTool || nestedTool["name"] is null) continue;
                    converted.Add(new JsonObject
                    {
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = nestedTool["name"]?.GetValue<string>(),
                            ["description"] = nestedTool["description"]?.GetValue<string>() ?? "",
                            ["parameters"] = CloneOrDefault(nestedTool["parameters"] ?? nestedTool["inputSchema"])
                        }
                    });
                }
            }
        }
        return converted.Count > 0 ? converted : null;
    }

    // ---------------------------------------------------------------------
    // 4.6) resposta chat -> objeto Responses
    //   reasoning (thinking do Qwen3-8B) -> item "reasoning"   [modo events]
    //   content                          -> item "message"
    //   tool_calls                       -> item "function_call" (SEM status:
    //                                        o opencode conduz in_progress->completed)
    // ---------------------------------------------------------------------
    /// <summary>Converte resposta chat em objeto Responses (reasoning+message+calls).</summary>
    /// <param name="chat">Chat completion.</param>
    /// <param name="model">Model id p/ eco.</param>
    /// <param name="temperature">Efetiva.</param>
    /// <returns>Objeto Responses.</returns>
    static JsonObject ChatToResponses(JsonObject chat, string model, double temperature, string? thinkMode = null)
    {
        thinkMode ??= Thinking;
        var choice = chat["choices"]?[0]?.AsObject() ?? new JsonObject();
        var message = choice["message"]?.AsObject() ?? new JsonObject();

        string content = message["content"]?.GetValue<string>() ?? "";

        // 4.6.1) THINKING: o vLLM devolve o raciocinio dentro do proprio content
        // (bloco <think>...</think>) ou no campo reasoning_content. Separamos:
        // thinking vira item "reasoning", o resto vira "message".
        // No modo "off" descartamos o thinking.
        string thinking = "";
        var reasoningNode = message["reasoning_content"] ?? message["reasoning"];
        if (reasoningNode is JsonValue rn && rn.TryGetValue<string>(out string? reasoningText))
            thinking = reasoningText ?? "";
        var (thinkingBlock, cleanContent) = SplitThinking(content);
        if (thinkingBlock.Length > thinking.Length) thinking = thinkingBlock;
        content = cleanContent;

        // 4.6.2) Limpeza minima: so remove marcador de template que porventura
        // escape (nao fazemos nenhuma heuristica de texto).
        content = ReTemplate.Replace(content, "");

        var toolCalls = new List<(string Id, string Name, string Arguments)>();
        if (message["tool_calls"] is JsonArray callArray)
        {
            foreach (var callNode in callArray)
            {
                if (callNode is not JsonObject callObj) continue;
                string id = callObj["id"]?.GetValue<string>() ?? ("call_" + NewId());
                var funcObj = callObj["function"]?.AsObject() ?? new JsonObject();
                toolCalls.Add((id, funcObj["name"]?.GetValue<string>() ?? "", JsonToString(funcObj["arguments"])));
            }
        }
        // Coerção (só perfil CPU/llama): sem tool_calls nativos, extrai do texto.
        // Caminho vLLM (hermes/qwen3_coder/gemma4) nunca entra aqui com calls.
        if (toolCalls.Count == 0 && CoerceFor(model))
        {
            foreach (var (name, args) in ExtractTextToolCalls(content))
                toolCalls.Add(("call_" + NewId(), name, args));
            if (toolCalls.Count > 0)
            {
                // Remove os blocos consumidos p/ nao duplicar texto + call.
                content = ReToolTag.Replace(content, "");
                content = ReFencedCall.Replace(content, "");
                Log(new JsonObject { ["event"] = "coerced", ["model"] = model, ["count"] = toolCalls.Count });
            }
        }

        // 4.6.3) Uso de tokens: chat usa prompt/completion, Responses usa input/output
        var chatUsage = chat["usage"]?.AsObject() ?? new JsonObject();
        int inputTokens = chatUsage["prompt_tokens"]?.GetValue<int>() ?? 0;
        int outputTokens = chatUsage["completion_tokens"]?.GetValue<int>() ?? 0;
        if (outputTokens == 0) outputTokens = Math.Max(content.Length / 4 + toolCalls.Count * 8, 1);

        var output = new JsonArray();

        // thinking primeiro (coerente com a ordem em que o modelo "pensou")
        // Modo "off": sem item reasoning separado; o raciocinio vira texto
        // comum no inicio da message (senao a resposta sai vazia quando o
        // modelo pensa tudo e nao escreve nada fora do thinking).
        if (thinkMode != "events" && !string.IsNullOrWhiteSpace(thinking))
            content = string.IsNullOrEmpty(content) ? thinking : thinking + "\n\n" + content;
        if (thinkMode == "events" && !string.IsNullOrWhiteSpace(thinking))
        {
            output.Add(new JsonObject
            {
                ["id"] = "rs_" + NewId(), ["type"] = "reasoning",
                ["summary"] = new JsonArray(new JsonObject { ["type"] = "summary_text", ["text"] = thinking })
            });
        }

        if (!string.IsNullOrWhiteSpace(content))
        {
            output.Add(new JsonObject
            {
                ["id"] = "msg_" + NewId(), ["type"] = "message", ["role"] = "assistant",
                ["status"] = "completed",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "output_text", ["text"] = content,
                    ["annotations"] = new JsonArray(), ["logprobs"] = null
                })
            });
        }

        foreach (var (id, name, arguments) in toolCalls)
        {
            output.Add(new JsonObject
            {
                ["type"] = "function_call", ["id"] = id, ["call_id"] = id,
                ["name"] = name, ["arguments"] = arguments
            });
        }

        return new JsonObject
        {
            ["id"] = "resp_" + NewId(),
            ["object"] = "response",
            ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["status"] = "completed",
            ["model"] = model,
            ["output"] = output,
            ["parallel_tool_calls"] = true,
            ["tool_choice"] = "auto",
            ["tools"] = new JsonArray(),
            ["temperature"] = temperature,
            ["service_tier"] = "auto",
            ["incomplete_details"] = null,
            ["instructions"] = null,
            ["metadata"] = null,
            ["usage"] = new JsonObject
            {
                ["input_tokens"] = inputTokens, ["output_tokens"] = outputTokens,
                ["total_tokens"] = inputTokens + outputTokens,
                ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 0 },
                ["output_tokens_details"] = new JsonObject()
            }
        };
    }

    // Separa o bloco de thinking (<think>...</think>, modo hibrido do Qwen3)
    // do texto final. Sem tag de fechamento (corte por max_tokens), tudo apos
    // a abertura e considerado thinking.
    /// <summary>Separa o bloco &lt;think&gt; do texto final.</summary>
    /// <param name="text">Conteudo bruto.</param>
    /// <returns>Tupla (thinking, limpo).</returns>
    static (string thinking, string clean) SplitThinking(string text)
    {
        if (string.IsNullOrEmpty(text)) return ("", "");
        int openIdx = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        if (openIdx < 0) return ("", text);
        int closeIdx = text.IndexOf("</think>", openIdx, StringComparison.OrdinalIgnoreCase);
        if (closeIdx < 0) return (text[(openIdx + 7)..].Trim(), text[..openIdx].Trim());

        string before = text[..openIdx];
        string thought = text[(openIdx + 7)..closeIdx];
        string after = text[(closeIdx + 8)..];
        return (thought.Trim(), (before + after).Trim());
    }

    // ---------------------------------------------------------------------
    // 4.7) Sintetiza o SSE no formato Responses que o opencode aceita.
    //      A ordem dos eventos importa: created -> in_progress -> [reasoning]
    //      -> message -> function_call -> completed, todos com sequence_number
    //      crescente.
    // ---------------------------------------------------------------------
    /// <summary>Sintetiza o SSE Responses com sequence crescente.</summary>
    /// <param name="obj">Objeto Responses.</param>
    /// <returns>Bytes do evento-stream.</returns>
    static byte[] BuildSse(JsonObject obj, string? thinkMode = null)
    {
        thinkMode ??= Thinking;
        var sse = new StringBuilder();
        int sequence = 0;

        // Cabecalho: a resposta nasce "in_progress" e sem output
        var baseResponse = (JsonObject)obj.DeepClone();
        baseResponse["output"] = new JsonArray();
        baseResponse["status"] = "in_progress";

        WriteEvent(sse, "response.created", new JsonObject { ["type"] = "response.created", ["sequence_number"] = sequence++, ["response"] = baseResponse.DeepClone() });
        WriteEvent(sse, "response.in_progress", new JsonObject { ["type"] = "response.in_progress", ["sequence_number"] = sequence++, ["response"] = baseResponse });

        int outputIndex = 0;
        foreach (var outputNode in obj["output"]!.AsArray())
        {
            // Copia de trabalho: o original tem pai (o array output) e nao pode
            // ser anexado a outro objeto. O original fica intacto p/ o completed.
            if (outputNode is not JsonObject originalItem) continue;
            var item = (JsonObject)originalItem.DeepClone();
            string type = item["type"]?.GetValue<string>() ?? "";

            if (type == "reasoning" && thinkMode == "events")
            {
                // Raciocinio: item aberto -> delta do resumo -> item fechado
                string thinkingText = item["summary"]?[0]?["text"]?.GetValue<string>() ?? "";
                string reasoningId = item["id"]?.GetValue<string>() ?? ("rs_" + NewId());
                var reasoningOpen = new JsonObject { ["id"] = reasoningId, ["type"] = "reasoning", ["summary"] = new JsonArray() };
                WriteEvent(sse, "response.output_item.added", new JsonObject
                {
                    ["type"] = "response.output_item.added", ["output_index"] = outputIndex,
                    ["sequence_number"] = sequence++, ["item"] = reasoningOpen
                });
                WriteEvent(sse, "response.reasoning_summary_part.added", new JsonObject
                {
                    ["type"] = "response.reasoning_summary_part.added", ["item_id"] = reasoningId,
                    ["output_index"] = outputIndex, ["content_index"] = 0, ["summary_index"] = 0, ["sequence_number"] = sequence++,
                    ["part"] = new JsonObject { ["type"] = "summary_text", ["text"] = "" }
                });
                WriteEvent(sse, "response.reasoning_summary_text.delta", new JsonObject
                {
                    ["type"] = "response.reasoning_summary_text.delta", ["item_id"] = reasoningId,
                    ["output_index"] = outputIndex, ["content_index"] = 0, ["summary_index"] = 0, ["delta"] = thinkingText,
                    ["sequence_number"] = sequence++
                });
                WriteEvent(sse, "response.reasoning_summary_text.done", new JsonObject
                {
                    ["type"] = "response.reasoning_summary_text.done", ["item_id"] = reasoningId,
                    ["output_index"] = outputIndex, ["content_index"] = 0, ["summary_index"] = 0, ["text"] = thinkingText,
                    ["sequence_number"] = sequence++
                });
                // Sem o part.done o opencode nao consolida os deltas (text fica "").
                WriteEvent(sse, "response.reasoning_summary_part.done", new JsonObject
                {
                    ["type"] = "response.reasoning_summary_part.done", ["item_id"] = reasoningId,
                    ["output_index"] = outputIndex, ["content_index"] = 0, ["summary_index"] = 0, ["sequence_number"] = sequence++,
                    ["part"] = new JsonObject { ["type"] = "summary_text", ["text"] = thinkingText }
                });
                WriteEvent(sse, "response.output_item.done", new JsonObject
                {
                    ["type"] = "response.output_item.done", ["output_index"] = outputIndex,
                    ["sequence_number"] = sequence++, ["item"] = item
                });
                outputIndex++;
                continue;
            }

            if (type == "message")
            {
                string messageId = item["id"]?.GetValue<string>() ?? ("msg_" + NewId());
                string text = item["content"]?[0]?["text"]?.GetValue<string>() ?? "";

                WriteEvent(sse, "response.output_item.added", new JsonObject
                {
                    ["type"] = "response.output_item.added", ["output_index"] = outputIndex,
                    ["sequence_number"] = sequence++,
                    ["item"] = new JsonObject
                    {
                        ["id"] = messageId, ["type"] = "message", ["role"] = "assistant",
                        ["status"] = "in_progress", ["content"] = new JsonArray(), ["phase"] = null
                    }
                });
                WriteEvent(sse, "response.content_part.added", new JsonObject
                {
                    ["type"] = "response.content_part.added", ["output_index"] = outputIndex,
                    ["content_index"] = 0, ["sequence_number"] = sequence++, ["item_id"] = messageId,
                    ["part"] = new JsonObject { ["type"] = "output_text", ["text"] = "", ["annotations"] = new JsonArray() }
                });
                // Delta unico: suficiente pro opencode montar o texto (evita
                // fragmentacao e mantem a saida identica a gerada)
                WriteEvent(sse, "response.output_text.delta", new JsonObject
                {
                    ["type"] = "response.output_text.delta", ["item_id"] = messageId,
                    ["output_index"] = outputIndex, ["content_index"] = 0, ["delta"] = text,
                    ["logprobs"] = new JsonArray(), ["sequence_number"] = sequence++
                });
                WriteEvent(sse, "response.output_text.done", new JsonObject
                {
                    ["type"] = "response.output_text.done", ["item_id"] = messageId,
                    ["output_index"] = outputIndex, ["content_index"] = 0, ["text"] = text,
                    ["logprobs"] = new JsonArray(), ["sequence_number"] = sequence++
                });
                WriteEvent(sse, "response.content_part.done", new JsonObject
                {
                    ["type"] = "response.content_part.done", ["output_index"] = outputIndex,
                    ["content_index"] = 0, ["sequence_number"] = sequence++, ["item_id"] = messageId,
                    ["part"] = new JsonObject { ["type"] = "output_text", ["text"] = text, ["annotations"] = new JsonArray() }
                });
                WriteEvent(sse, "response.output_item.done", new JsonObject
                {
                    ["type"] = "response.output_item.done", ["output_index"] = outputIndex,
                    ["sequence_number"] = sequence++, ["item"] = item
                });
                outputIndex++;
                continue;
            }

            if (type == "function_call")
            {
                string callId = item["call_id"]?.GetValue<string>() ?? item["id"]?.GetValue<string>() ?? "";
                string funcArguments = item["arguments"]?.GetValue<string>() ?? "{}";

                WriteEvent(sse, "response.output_item.added", new JsonObject
                {
                    ["type"] = "response.output_item.added", ["output_index"] = outputIndex,
                    ["sequence_number"] = sequence++, ["item"] = item.DeepClone()
                });
                WriteEvent(sse, "response.function_call_arguments.delta", new JsonObject
                {
                    ["type"] = "response.function_call_arguments.delta", ["item_id"] = callId,
                    ["output_index"] = outputIndex, ["sequence_number"] = sequence++, ["delta"] = funcArguments
                });
                WriteEvent(sse, "response.function_call_arguments.done", new JsonObject
                {
                    ["type"] = "response.function_call_arguments.done", ["item_id"] = callId,
                    ["output_index"] = outputIndex, ["sequence_number"] = sequence++, ["arguments"] = funcArguments
                });
                WriteEvent(sse, "response.output_item.done", new JsonObject
                {
                    ["type"] = "response.output_item.done", ["output_index"] = outputIndex,
                    ["sequence_number"] = sequence++, ["item"] = item
                });
                outputIndex++;
            }
        }

        // Fecho com a resposta completa (opencode le usage daqui)
        WriteEvent(sse, "response.completed", new JsonObject
        {
            ["type"] = "response.completed", ["sequence_number"] = sequence,
            ["response"] = obj
        });
        return Encoding.UTF8.GetBytes(sse.ToString());
    }

    // Escreve um evento SSE no padrao "event: X\ndata: {...}\n\n"
    /// <summary>Anexa um evento SSE ao buffer.</summary>
    /// <param name="sse">Buffer.</param>
    /// <param name="type">Nome do evento.</param>
    /// <param name="payload">Data JSON.</param>
    static void WriteEvent(StringBuilder sse, string type, JsonObject payload)
    {
        sse.Append("event: ").Append(type).Append('\n');
        sse.Append("data: ").Append(payload.ToJsonString(SerOpts)).Append("\n\n");
    }

    // ---------------------------------------------------------------------
    // 5) LOG estruturado (uma linha JSON por evento) p/ diagnostico dos testes
    // ---------------------------------------------------------------------
    /// <summary>Apende linha JSON de diagnostico; nunca derruba o pedido.</summary>
    /// <param name="obj">Evento.</param>
    static void Log(JsonObject obj)
    {
        try
        {
            obj["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            File.AppendAllText(LogPath, obj.ToJsonString(SerOpts) + "\n");
        }
        catch { /* log nunca derruba o pedido */ }
    }

    /// <summary>Id aleatorio hex de 16 chars.</summary>
    /// <returns>Id.</returns>
    static string NewId() => Guid.NewGuid().ToString("N")[..16];
    /// <summary>Corta string no tamanho maximo.</summary>
    /// <param name="s">Texto.</param>
    /// <param name="n">Maximo.</param>
    /// <returns>Texto cortado.</returns>
    static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}
