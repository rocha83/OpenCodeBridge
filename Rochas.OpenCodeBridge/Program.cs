// ===========================================================================
//  OpenCodeBridge  (C# / .NET console)
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
//  Llama — o MESMO caminho que ja funciona no cliente local. Depois converte a
//  resposta (tool_calls + thinking) de volta para o formato Responses.
//
//  O QUE NAO ESTA AQUI (de proposito — heranca problematica legada):
//  coercion de paths, grounding, extracao de pseudo-tool-call em texto,
//  retry com nudge, footer de tokens, strip agressivo de template.
//  Se surgir algo especifico do modelo servido, amadurecemos nesta versao.
//
//  CONVENCAO DE IDIOMA: identificadores em en-US, comentarios em pt-BR.
//
//  USO:  dotnet run -c Release -- --port 4143 --upstream http://127.0.0.1:4100
// ===========================================================================

using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Serilog;
using Rochas.OpenCodeBridge.Core;
using static Rochas.OpenCodeBridge.Core.ResponsesTranslation;
using static Rochas.OpenCodeBridge.Core.TextGrammar;
using static Rochas.OpenCodeBridge.Core.ResponseBody;
using static Rochas.OpenCodeBridge.Core.ToolArgsParser;
using static Rochas.OpenCodeBridge.Core.ToolCoercion;

namespace Rochas.OpenCodeBridge;

internal static partial class Program
{
    // ---------------------------------------------------------------------
    // 1) CONFIGURACAO (linhas de comando) — tudo sobrescrevivel sem recompilar
    // ---------------------------------------------------------------------
    static int Port = 4124;                                        // porta da bridge
    static string Listen = "0.0.0.0";                              // interface de escuta padrao: rede toda (acesso externo; units systemd repetem via --listen)
    static string Upstream = "http://127.0.0.1:4100";              // vLLM
    static string Model = "Gemma4-E4B-Q4";                          // id servido pelo Llama
    static string Thinking = "events";                             // "events" = mostra | "off" = descarta
    static double Temperature = 0.2;                                 // execucao/build (escala baixa 0.1 / media 0.3 / alta 0.5); tool calling continua deterministico via tool_choice
    static double AuxTemp = double.NaN;                            // temperatura AUXILIAR do turno de retry/reconsideracao (--aux-temp); NaN = usa a do role de desvio (plan)
    static int MaxTokens;                                          // teto de saida da linha GPU: 0 = SEM TETO (a infra/llama controla via -c/--n-predict e o cliente decide via max_tokens); >0 = clamp
    static int CpuMaxTokens;                                       // teto da linha CPU: 0 = SEM TETO (deixa o llama controlar); >0 = clamp p/ conter divagacao em decode lento (~5 tok/s)
    static string ToolShimMode = "";                                // "json" = shim generico: remove tools, forca envelope JSON via grammar e converte de volta p/ tool_calls
    static int ToolShimMaxString = 2000;                            // cap de chars por string no grammar do shim (0 = sem cap)
    static int ToolShimMinCalls = 0;                                // calls minimas no historico antes de liberar {"final":...}; 0 = libera sempre
    static int ToolOutputLimit;                                      // corte por resultado de tool: 0 = SEM CORTE (padrao); config via appsettings "toolOutputLimit" / --tool-output-limit
    static string WorkspaceRoot = "";                               // raiz p/ POST /api/tools/execute; vazio = cwd do processo
    static string LogPath = "/tmp/opencodebridge.log";
    static string ApiKey = "";
    static bool PassthroughResponses = false;                    // Bypass p/ upstream com Responses nativo (llama.cpp): so remove itens reasoning, resto passa direto                                     // Bearer p/ upstream com auth (llama --api-key); vazio = so repasse
    static readonly string Version = "2.1";
    // Trava anti-loop no nivel da bridge: complementa QUALQUER system
    // prompt no final (Responses e chat), sem depender do agente.

    // Coerção texto->tool_calls p/ upstream SEM parser server-side (llama.cpp
    // sem --jinja/parser): extrai ```json{"name"..} ou <tool_call>..</tool_call>
    // do content e sintetiza tool_calls. Qwen3/AWQ (parser nativo) ficam intactos.
    //
    // QUEM DECIDE: a CAPACIDADE do upstream (--upstream-parser none) ou o pedido
    // explicito do operador (--coerce-text-tools 1) — nunca o nome do modelo.
    // A heuristica antiga ("cpu" no id) era mentirosa: o id e livre, a suite E2E
    // usa ids "8b" na linha sem parser e a heuristica simplesmente nao disparava.
    static bool CoerceTextTools;
    // Capacidade de parser de tool call do upstream:
    //   native (padrao) = devolve tool_calls estruturados (vLLM / llama + parser)
    //   none            = devolve texto puro; a bridge coagia e sintetiza
    static string UpstreamParser = "native";
    static bool AntiRepeat = true;                               // AntiRepeat: observa o thinking em stream e conteve repeticao
    // Abort em stream: com o detector tripado, corta a geracao no upstream na
    // hora (fecha o SSE) em vez de pagar o turno degenerado inteiro (medido:
    // 16.384 tokens ~ 375 s). Flag propria para poder desligar SO o corte e
    // manter o detector e o retry pos-hoc.
    static bool AntiRepeatAbort = true;
    // Switch das tails (--harness-tails). Nao e o inibidor mestre da bridge:
    // mainHarness e harnessTails sao switches independentes.
    static bool TailsEnabled = true;
    // Coerção de tool call NARRADA: o modelo escreveu a chamada no corpo da mensagem
    // ("[Assistant tool call]: nome({...})") em vez de emitir tool_calls estruturados.
    // Diferente de CoerceFor (que cobre modelo sem parser nenhum): aqui ja houve intencao
    // de tool call, so que no formato errado. Independe do LegacyHarness.
    static bool CoerceNarrated = true;
    // Tubo puro p/ cliente com harness proprio: opencode chama o modelo COM
    // sufixo de perfil (-plan/-build/-orch/-moe) e gerencia as tools sozinho —
    // nesse caminho a bridge nao coagia, nao segura marcador e nao limpa
    // historico (passa reto). A gestao da bridge fica no cliente direto (Web UI,
    // modelo sem sufixo). --bridge-managed true forca gestao em todos.
    static bool BridgeManagedAll;
    // Legado (nunca liga em producao): antigas insercoes da bridge
    // (guardrail/coerce/strip/retry). --legacy-harness 1 reanima.
    static bool LegacyHarness = false;
    // QwenStandard (--qwen-standard): ModelPrompt sintetizado + tools Alibaba.
    static bool QwenStd = false;
    // Formula (harness=true): composta pela bridge p/ cliente web (model sem
    // sufixo). Opencode (sufixo -build/-plan/-orch) passa limpo.
    static string HarnessToolsTail = HarnessTails.DefaultTools;
    // Retry de contencao do anti-repeat: 1 segunda chamada ao modelo com o aviso,
    // descartando a janela repetida. Governado por --anti-repeat-retry, nao pelo
    // LegacyHarness (ver o bloco do retry).
    static bool AntiRepeatRetry = true;
    // Repasse opcional de parametros de amostragem do cliente ao upstream
    // (repeat_penalty, dry_*, top_p/k, min_p, seed...). Desligado por padrao:
    // sem a flag a bridge continua montando o body como antes. Nao e politica:
    // e config do llama que o cliente/experimento decide.
    static bool SamplingPassthrough = false;
    // ToolStrategy: resgate deterministico do padrao Code Mode do opencode
    // (execute{tools.X} -> tool X, strip da tool execute, guia de argumentos).
    static bool ToolStrategy = true;
    // Emissao por delta (efeito "digitando") no content: repassa cada chunk do
    // upstream segurando so o prefixo ambiguo de tag; false = agrega ate 256
    // chars. Liga/desliga por appsettings (streamPerDelta) ou CLI.
    static bool StreamPerDelta = true;
    // Enunciado de inducAO a COMPORTAMENTO (nao e uso de tools): disciplina de execucao,
    // como parar e entregar, como reagir a erro, tom de resposta. Vive separado da
    // ToolsTail de proposito: COMO USAR A TOOL e uma coisa, COMO SE COMPORTAR e outra.
    // Default: enquadramento de WORKSPACE. O usuario costuma apontar um path
    // de fora do namespace no enunciado inicial; sem esta regra o modelo olha
    // so o diretorio literal e conclui "nao existe", ignorando vizinhos e
    // subpastas. Override por appsettings (harnessBehaviorTail) ou CLI.
    static string HarnessBehaviorTail = HarnessTails.DefaultBehavior;
    // O operador DECLAROU uma cauda (CLI/appsettings) ou o valor e o default
    // embutido? Usado so pela validacao, p/ avisar "cauda inerte com
    // --harness-tails 0" sem acusar o default.
    static bool BehaviorTailCustom, ToolsTailCustom;
    // Flag PROPRIA de cada cauda (--harness-behavior / --harness-tools): behavior
    // e tools sao responsabilidades separadas. O behavior entra SEMPRE por padrao
    // (conduta geral); a cauda de tools so entra quando o request traz o array
    // `tools`. Desligar uma nao afeta a outra. Default: ambas ON.
    static bool BehaviorTailEnabled = true, ToolsTailEnabled = true;
    // Texto efetivo da cauda (vazio quando a flag desligou a peca). Fonte unica
    // para TailsAtivas e para a composicao — o Core recebe "" e nao injeta.
    static string BehaviorTailEffective => BehaviorTailEnabled ? HarnessBehaviorTail : "";
    static string ToolsTailEffective => ToolsTailEnabled ? HarnessToolsTail : "";
    // Onde as caudas valem (--tail-scope / appsettings tailScope):
    //   managed = padrao historico: so cliente direto/Web, com --harness-tails 1;
    //   all     = tambem no caminho opencode (model com sufixo de role);
    //   off     = nenhuma cauda, em nenhum caminho.
    // Pedido do operador (2026-10-24): guard de tools ligado nos DOIS caminhos.
    static string TailScope = "managed";
    // Main harness (--main-harness): substitui o system do cliente no caminho
    // gerido (model sem sufixo de role) pelo resumo unico do Core e dispensa as
    // tails nesse caminho. Opencode (sufixo) segue com o system do cliente; para
    // ele, o main harness nao troca o Code Mode do protocolo.
    static bool MainHarnessOn = true;
    // Modo de operacao (Core/BridgeMode): bridge = fixes+anti-repeat+harness+tails;
    // opencode = fixes+anti-repeat (master do cliente). null = auto (heuristica).
    static BridgeMode? Mode;
    // Precedencia da temperatura: CLIENTE (tunel) > perfil do role > global 0.3.
    // Sem inversao e sem flag: a regra e unica e mora no Core (Sampling).
    // Breaker de loop agentico (falhas consecutivas no mesmo alvo).
    static int BreakerSoft = 3;                                  // falhas que injetam o aviso
    static int BreakerHard = 6;                                  // falhas que encerram o turno
    // Ha alguma cauda configurada? O /api/harness reporta isso; a decisao por
    // caminho fica em TailsOn (harnessTails + tailScope).
    static bool TailsAtivas() => BehaviorTailEffective.Length > 0 || ToolsTailEffective.Length > 0;
    // Cliente da requisicao corrente. O CLI do opencode traz harness proprio, entao
    // a cauda ali e opcional; qualquer outro cliente (WAR, Web, SDK) recebe a cauda
    // mesmo com modelo de role (sufixo). O sufixo NAO identifica cliente — era o
    // erro que tirava a cauda do WAR em modo bridge.
    static readonly AsyncLocal<bool> OpencodeClient = new();

    // Caminho de harness bridge DESTE request. O teste WAR declara o modo pelo
    // header X-Bridge-Mode (bridge/opencode); o CLI do opencode continua sendo
    // identificado pelo User-Agent. Em opencode o cliente se auto-gerencia:
    // nao recebe mainHarness nem tails da bridge.
    static readonly AsyncLocal<bool?> BridgeLane = new();

    static bool IsOpencodeClient(HttpListenerContext? ctx) =>
        (ctx?.Request.Headers["User-Agent"] ?? "").Contains("opencode", StringComparison.OrdinalIgnoreCase);

    // Cauda vale NESTE pedido? --harness-tails e o switch; tailScope decide o
    // caminho: "all" injeta TAMBEM na raia opencode, "managed" so no cliente
    // direto/Web (opencode se auto-gerencia). O gate do modo e `Tails` do
    // BridgeMode — nunca o BridgeLane, que discrimina apenas o MainHarness.
    // Cauda de BEHAVIOR: conduta geral, INDEPENDENTE do main harness — o flag
    // --harness-behavior 0 desliga e o default e' ON (com main harness ligado o
    // comportamento tambem entra, por consequencia do default). O escopo/caminho
    // segue o das caudas (off desliga; opencode so com "all").
    static bool BehaviorTailOn() =>
        BehaviorTailEnabled
        && TailScope != "off"
        && (Mode?.Tails ?? true)
        && (TailScope == "all" || !OpencodeClient.Value)
        && BehaviorTailEffective.Length > 0;
    // Cauda de TOOLS: como usar as tools, so quando o request traz o array.
    static bool ToolsTailOn() =>
        ToolsTailEnabled
        && TailScope != "off"
        && (Mode?.Tails ?? true)
        && (TailScope == "all" || !OpencodeClient.Value)
        && ToolsTailEffective.Length > 0;
    // Alguma cauda entra neste request (usado pelo gate do chamador).
    static bool TailsOn(string? modelId) => BehaviorTailOn() || ToolsTailOn();
    static int AntiRepeatWindow = ContentAntiRepeat.DefaultWindow; // janela (chars) da cauda comparada com o historico (64..2048); default unico no Core
    // Janela PROPRIA dos argumentos de tool: `write`/`edit` carrega arquivo inteiro
    // e repete trechos legitimos. Com a janela do texto o abort cortava a call no
    // meio e o salvage recusava os args truncados (medido no L3 de 2026-10-29).
    static int AntiRepeatArgsWindow = ToolArgsAntiRepeatReply.ArgsDefaultWindow;
    // Guarda de ctx (contextGuard): ao atingir o limiar do ctx, a bridge
    // devolve 413 pedindo compactacao em vez da resposta. Parametrizavel
    // via appsettings (contextGuard) ou CLI; ContextTokens=0 = inativo.
    static bool ContextGuard = false;
    static double ContextThreshold = 0.95;                       // fracao do ctx (0.5..0.99)
    static int ContextTokens = 0;                                // tamanho do ctx do modelo servido
    static int ContextSlots = 1;                                 // slots paralelos do llama (-np): divide o ctx total
    static bool Diagnostic = false;                              // sonda Serilog: --diagnostic / appsettings diagnostic / env DIAGNOSTIC=1
    const int SondaMaxChars = 2048;                              // amostra da cauda/duplicata no log de diagnostico
    // Granularidade da sonda (grupo observabilidade):
    //   off     = nenhuma sonda
    //   metrics = evento de trafego com metricas (usage/tps/elapsed), SEM os
    //             corpos req/res (o dump integral passou de 280 MB numa rodada)
    //   dump    = metrics + corpos completos (comportamento historico)
    // --diagnostic 1 (legado) sem nivel declarado implica "dump".
    static string DiagnosticLevel = "off";
    static string AntiRepeatNotice = ContentAntiRepeat.SoftNotice; // texto BRANDO da bridge (default unico no Core)

    // ThinkingOffRoles: roles que NUNCA devem mostrar reasoning, por nome de role
    // ("Build", "Scripter", "Exec"). Vence sobre o perfil/sufixo do model id, para
    // o operador corrigir sem renomear id. Vazio = usa so o Profiles.
    static HashSet<string> ThinkingOffRoles = new(StringComparer.OrdinalIgnoreCase);

    // ToolBlacklist: tools proibidas por role ("Plan" => write, sed, rm). Nome da
    // tool do lado do MODELO (o que ele pede), nao do harness. Vazio = libera.
    // A tool CONTINUA anunciada ao modelo: o bloqueio e na emissao da chamada, nao
    // escondendo o schema. Assim o modelo sabe que a tool existe (e pode explicar ao
    // usuario que ela e de outro role) mas nao consegue executar.    // ToolBlacklist: tools proibidas por role ("Plan" => write, sed, rm). Nome da
    // tool do lado do MODELO (o que ele pede), nao do harness. Vazio = libera.
    static string ToolBlockNotice = "Aqui e a ponte de conteudo OpenCodeBridge: a tool '{0}' nao esta liberada para o role '{1}' (permissao interativa desligada: --risk-ask 0) e nao sera executada; use uma alternativa permitida (shell, read) ou explique ao usuario que essa acao cabe a outro role.";
    static Dictionary<string, HashSet<string>> ToolBlacklist = new(StringComparer.OrdinalIgnoreCase);

    // RiskToolsAlerts: comandos de RISCO que nao sao executados sem confirmacao
    // do usuario na interface. Diferente do ToolBlacklist (que barra a TOOL
    // inteira por role), aqui o alvo e o COMANDO: "git push" casa com o git
    // dentro de um shell, mas "git log" passa livre. O papel do role e o de
    // decidir QUEM pergunta (Plan pergunta; Build tambem, se listado).
    static Dictionary<string, List<Regex>> RiskToolsAlerts = new(StringComparer.OrdinalIgnoreCase);

    // Pergunta em voo: call_id da pergunta -> a chamada de risco original.
    // Cruza o turno: o opencode devolve function_call_output com esse call_id.
    record PendingRisk(string Tool, string Args, string Role);
    static ConcurrentDictionary<string, PendingRisk> PendingRisks = new(StringComparer.Ordinal);

    // Autorizacoes concedidas: assinatura da chamada -> validade. Uma concessao
    // vale para UMA execucao (TryRemove no consume), nao abrePermanente.
    static ConcurrentDictionary<string, DateTime> RiskApprovedOnce = new(StringComparer.Ordinal);

    /// <summary>Envia a pergunta de risco ao usuario (default ligado).</summary>
    static bool RiskAsk = true;

    /// <summary>Tempo que a pergunta de risco fica pendente (minutos).</summary>
    static int RiskAskMinutes = 30;

    /// <summary>Tools que disparam pergunta de risco: padroes regex, por role.</summary>
    static List<Regex> RiskPatternsFor(string? role)
        => role is not null && RiskToolsAlerts.TryGetValue(role, out var pats) ? pats : new List<Regex>();

    /// <summary>Le a resposta do usuario a uma pergunta de risco; concede (uma
    /// vez) a assinatura da chamada ou registra a recusa. Devolve true se a
    /// chamada foi autorizada e deve sair agora.</summary>
    static bool RiskConsumeApproval(string modelId, string tool, string args)
    {
        string sig = ToolSig.Of(tool, args);
        if (!RiskApprovedOnce.TryRemove(sig, out DateTime exp)) return false;
        if (exp < DateTime.UtcNow) return false;
        Log(new JsonObject { ["event"] = "risk-approved", ["tool"] = tool, ["role"] = RoleOfModel(modelId) });
        return true;
    }

    /// <summary>Registra os function_call_output que respondem a uma pergunta de
    /// risco. Roda na entrada do request: o opencode traz a resposta do usuario
    /// no turno seguinte, e e la que a concessao (ou a recusa) e reconhecida.
    /// Devolve o aviso a injetar no historico, para o modelo reemitir a chamada
    /// exata (a concessao vale para uma unica execucao, casada por assinatura).</summary>
    static List<string> RiskScanAnswers(JsonNode? input)
    {
        var notices = new List<string>();
        if (PendingRisks.IsEmpty || input is not JsonArray arr) return notices;
        foreach (var entry in arr)
        {
            if (entry is not JsonObject e) continue;
            string type = e["type"]?.GetValue<string>() ?? "";
            if (type is not ("function_call_output" or "custom_tool_call_output")) continue;
            string callId = e["call_id"]?.GetValue<string>() ?? e["id"]?.GetValue<string>() ?? "";
            if (!PendingRisks.TryRemove(callId, out PendingRisk? risk)) continue;
            string answer = ExtractText(e["output"] ?? e["result"] ?? e["content"]);
            bool approved = RiskAnswerYes(answer);
            Log(new JsonObject { ["event"] = "risk-answer", ["tool"] = risk.Tool,
                                 ["role"] = risk.Role, ["approved"] = approved });
            if (approved)
            {
                RiskApprovedOnce[ToolSig.Of(risk.Tool, risk.Args)] = DateTime.UtcNow.AddMinutes(RiskAskMinutes);
                notices.Add($"Ponte OpenCodeBridge: o usuario AUTORIZOU a chamada de risco pendente. "
                          + $"Reemita agora, sem mudar nada, a tool '{risk.Tool}' com os argumentos: {risk.Args}");
            }
            else
            {
                RiskRefused.Add(ToolSig.Of(risk.Tool, risk.Args));
                notices.Add($"Ponte OpenCodeBridge: o usuario NEGOU a chamada de risco pendente "
                          + $"({risk.Tool}: {Truncate(risk.Args, 200)}). Nao a tente de novo e explique ao usuario "
                          + "o que ficou por fazer.");
            }
        }
        return notices;
    }

    /// <summary>Assinaturas recusadas pelo usuario neste request (o modelo precisa
    /// saber que a acao nao aconteceu, em vez de reemitir em loop).</summary>
    static HashSet<string> RiskRefused = new(StringComparer.Ordinal);

    /// <summary>Resposta do usuario a pergunta de risco: sim/nao. Default e nao
    /// (recusa) — o silencio do usuario nao autoriza.</summary>
    static bool RiskAnswerYes(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return false;
        string a = ContentAntiRepeat.Normalize(answer).ToLowerInvariant();
        if (System.Text.RegularExpressions.Regex.IsMatch(a, @"\b(nao|nunca|negar|recusar|cancelar|deixa|rejeitar)\b"))
            return false;
        return System.Text.RegularExpressions.Regex.IsMatch(a, @"\b(sim|autoriza|autorizado|aprova|aprovado|pode|executa|ok|yes|confirmo|confirmado)\b");
    }

    /// <summary>DECISAO UNICA da politica de tools (fonte de verdade da ponte,
    /// consumida tambem pela Web em /api/permission/check). Devolve:
    /// "allow" (roda), "ask" (pergunta ao usuario; a chamada so sai com aval) ou
    /// "deny" (nao roda e nao se pergunta: autonomia do operador desligada).</summary>
    static (string Action, string Reason) PermissionOf(string? modelId, string tool, string args)
    {
        // Risco declarado: pergunta quando RiskAsk esta ligado; nega quando nao.
        if (IsRiskCall(modelId, tool, args))
            return (RiskAsk ? "ask" : "deny", "risco");
        // Fora do papel: idem — permissao interativa em vez de omissao.
        if (BannedFor(modelId).Contains(tool))
            return (RiskAsk ? "ask" : "deny", "papel");
        return ("allow", "");
    }

    /// <summary>A chamada e de risco e ainda nao foi autorizada: vira pergunta.</summary>
    static bool IsRiskCall(string? modelId, string tool, string args)
    {
        if (!RiskAsk) return false;
        var pats = RiskPatternsFor(RoleOfModel(modelId));
        if (pats.Count == 0) return false;
        string sig = ToolSig.Of(tool, args);
        if (RiskRefused.Contains(sig)) return false;   // ja recusado: nao insiste
        string hay = ContentAntiRepeat.Normalize(tool + " " + args);
        foreach (var rx in pats)
            if (rx.IsMatch(hay)) return true;
        return false;
    }

    /// <summary>Texto legivel da chamada para a pergunta (achata o JSON).</summary>
    static string RiskReadable(string tool, string args)
    {
        string flat;
        try
        {
            if (JsonNode.Parse(args) is JsonObject o)
                flat = string.Join("  ", o.Select(kv => kv.Key + "=" + ExtractText(kv.Value)));
            else flat = args;
        }
        catch { flat = args; }
        return Truncate(flat.Replace('\n', ' ').Trim(), 400);
    }

    /// <summary>Monta a chamada da tool question (nativa do opencode) pedindo a
    /// confirmacao do usuario. E o schema que o opencode abre como formulario.
    /// <paramref name="reason"/> = "risco" (comando perigoso) ou "papel"
    /// (a tool nao e do role). O texto muda, o mecanismo e o mesmo.</summary>
    static string RiskQuestionArgs(string tool, string args, string role, string reason)
    {
        string ask = reason == "papel"
            ? $"O role {role} normalmente nao pode usar a tool '{tool}'. Voce autoriza esta execucao?\n\n"
            : $"O role {role} pediu uma acao de risco, que precisa da sua autorizacao antes de rodar.\n\n";
        var payload = new JsonObject
        {
            ["questions"] = new JsonArray(new JsonObject
            {
                ["header"] = Truncate(reason == "papel" ? "Fora do papel: " + tool : "Risco: " + tool, 30),
                ["question"] = ask + $"{tool}: {RiskReadable(tool, args)}\n\nConfirma a execucao?",
                ["options"] = new JsonArray(
                    new JsonObject { ["label"] = "Autorizar", ["description"] = "Executar agora, com a autorizacao do usuario" },
                    new JsonObject { ["label"] = "Negar", ["description"] = "Nao executar; o agente deve explicar o motivo ao usuario" }),
                ["multiple"] = false
            })
        };
        return payload.ToJsonString(SerOpts);
    }

    /// <summary>Tools bloqueadas para o role do model pedido (vazio = nenhuma).</summary>
    static HashSet<string> BannedFor(string? modelId)
        => ToolBlacklist.TryGetValue(RoleOfModel(modelId), out var set) ? set
           : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Role extraido do sufixo do model id ("...-build" => "Build").</summary>
    static string RoleOfModel(string? modelId) => RoleProfiles.RoleOf(modelId, Profiles);
    /// <summary>A linha servida precisa de coerção texto->tool_calls?</summary>
    /// <remarks>Decidido pela CAPACIDADE do upstream (--upstream-parser none) ou
    /// pelo pedido explicito do operador (--coerce-text-tools 1). Nao olha nome
    /// de modelo (id e livre, e a suite E2E roda ids "8b" na linha sem parser) e
    /// nao depende do LegacyHarness: coerção de tool call e reanimacao de modo
    /// legado sao rotinas distintas, com flags distintas.</remarks>
    /// <returns>True = coagir texto em tool_calls.</returns>
    static bool CoerceFor() => CoerceTextTools || UpstreamParser == "none";

    // Perfis por sufixo no model (ex.: openai/Gemma4-E4B-Q4-plan): plan =
    // thinking ligado + temp media p/ planejar; build = thinking fundido +
    // temp media p/ executar; orch = coordena subagentes; moe = uso geral
    // hibrido; scripter = temp baixa (determinismo) e thinking off.
    // Escala: 0.1 (baixa: build/scripter/exec) / 0.3 (média: plan/orch) / 0.5 (alta: moe).
    // Request sem sufixo usa os
    // globais. O caminho Gemma 4 padrao (sem sufixo) fica byte-identico.
    // Dinamica dos roles (temp/thinking) mora no Core: RoleProfiles.
    static Dictionary<string, RoleProfile> Profiles = RoleProfiles.DefaultCopy();

    /// <summary>Descobre o perfil pelo sufixo do model (-plan/-build).</summary>
    static bool TryProfile(string requestedModel, out double profTemp, out string profThink)
    {
        profTemp = double.NaN; profThink = "";
        if (!RoleProfiles.TryResolve(requestedModel, Profiles, out _, out RoleProfile? rp) || rp is null) return false;
        profTemp = rp.Temperature; profThink = rp.Thinking;
        return true;
    }

    /// <summary>A bridge gerencia tools neste pedido? Cliente com perfil (opencode)
    /// traz o proprio harness: passa reto. Direto/Web (sem sufixo) e gerido.</summary>
    static bool ManagedFor(string? modelId) =>
        BridgeManagedAll || !TryProfile(modelId ?? "", out _, out _);

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
            if (cfg["auxTemp"]?.GetValue<double>() is double at) AuxTemp = at;
            if (cfg["maxTokens"]?.GetValue<int>() is int mt && mt >= 0) MaxTokens = mt;
            if (cfg["cpuMaxTokens"]?.GetValue<int>() is int cmt && cmt >= 0) CpuMaxTokens = cmt;
            if (cfg["toolOutputLimit"]?.GetValue<int>() is int tol && tol >= 0) ToolOutputLimit = tol;
            if (cfg["logPath"]?.GetValue<string>() is string log && log.Length > 0) LogPath = log;
            if (cfg["apiKey"]?.GetValue<string>() is string key && key.Length > 0) ApiKey = key;
            if (cfg["jwt"]?.AsObject() is JsonObject jwt)
            {
                if (jwt["secret"]?.GetValue<string>() is string js && js.Length >= 16) Auth.JwtSecret = js;
                if (jwt["expiryMinutes"]?.GetValue<int>() is int je && je > 0) Auth.JwtExpiryMinutes = je;
            }
            if (cfg["usersDb"]?.GetValue<string>() is string udb && udb.Length > 0) Auth.UsersDbPath = udb;
            if (cfg["passthroughResponses"]?.GetValue<bool>() is bool pt) PassthroughResponses = pt;
            if (cfg["coerceTextTools"]?.GetValue<bool>() is bool coerce) CoerceTextTools = coerce;
            if (cfg["upstreamParser"]?.GetValue<string>() is string usp && usp.Length > 0) UpstreamParser = usp.Trim().ToLowerInvariant();
            if (cfg["maxTokensCoerced"]?.GetValue<int>() is int mtc && mtc >= 0) CpuMaxTokens = mtc;
            // Dois switches separados: mainHarness (texto base) e harnessTails
            // (delta). Nao ha chave alias: so estes dois nomes valem.
            if (cfg["harnessTails"]?.GetValue<bool>() is bool ht)
            { TailsEnabled = ht; BehaviorTailEnabled = ht; ToolsTailEnabled = ht; }
            if (cfg["mode"]?.GetValue<string>() is string md && BridgeMode.TryParse(md, out BridgeMode mcfg)) Mode = mcfg;
            if (cfg["mainHarness"]?.GetValue<bool>() is bool mh) MainHarnessOn = mh;
            // Caudas ajustaveis no appsettings (string vazia desliga a peca).
            if (cfg["harnessToolsTail"]?.GetValue<string>() is string ham) { HarnessToolsTail = ham; ToolsTailCustom = true; }
            if (cfg["harnessBehaviorTail"]?.GetValue<string>() is string bt) { HarnessBehaviorTail = bt; BehaviorTailCustom = true; }
            if (cfg["harnessBehavior"]?.GetValue<bool>() is bool hb) BehaviorTailEnabled = hb;
            if (cfg["harnessTools"]?.GetValue<bool>() is bool htb) ToolsTailEnabled = htb;
            if (cfg["tailScope"]?.GetValue<string>() is string tsp && tsp is "managed" or "all" or "off") TailScope = tsp;
            if (cfg["breakerSoft"]?.GetValue<int>() is int bsoft && bsoft >= 0) BreakerSoft = bsoft;
            if (cfg["breakerHard"]?.GetValue<int>() is int bhard && bhard >= 0) BreakerHard = bhard;
            if (cfg["antiRepeatRetry"]?.GetValue<bool>() is bool arr) AntiRepeatRetry = arr;
            if (cfg["samplingPassthrough"]?.GetValue<bool>() is bool spp) SamplingPassthrough = spp;
            if (cfg["antiRepeatAbort"]?.GetValue<bool>() is bool arA) AntiRepeatAbort = arA;
            if (cfg["diagnosticLevel"]?.GetValue<string>() is string dlg && dlg.Length > 0) DiagnosticLevel = dlg.Trim().ToLowerInvariant();
            if (cfg["streamPerDelta"]?.GetValue<bool>() is bool spd) StreamPerDelta = spd;
            if (cfg["antiRepeat"]?.GetValue<bool>() is bool ar) AntiRepeat = ar;
            if (cfg["coerceNarrated"]?.GetValue<bool>() is bool cn) CoerceNarrated = cn;
            if (cfg["bridgeManaged"]?.GetValue<bool>() is bool bm) BridgeManagedAll = bm;
            if (cfg["riskAsk"]?.GetValue<bool>() is bool ra) RiskAsk = ra;
            if (cfg["riskAskMinutes"]?.GetValue<int>() is int ram && ram > 0) RiskAskMinutes = ram;
            if (cfg["riskToolsAlerts"]?.AsObject() is JsonObject rta)
                foreach (var kv in rta)
                {
                    if (kv.Value is not JsonArray pats) continue;
                    var rx = new List<Regex>();
                    foreach (var p in pats)
                        if (p?.GetValue<string>() is { Length: > 0 } pn && TryRegex(pn, out var compiled)) rx.Add(compiled);
                    if (rx.Count > 0) RiskToolsAlerts[kv.Key] = rx;
                }
            if (cfg["antiRepeatWindow"]?.GetValue<int>() is int arw && arw >= ContentAntiRepeat.WindowMin && arw <= ContentAntiRepeat.WindowMax) AntiRepeatWindow = arw;
            if (cfg["antiRepeatArgsWindow"]?.GetValue<int>() is int arwa && arwa >= ToolArgsAntiRepeatReply.ArgsWindowMin && arwa <= ToolArgsAntiRepeatReply.ArgsWindowMax) AntiRepeatArgsWindow = arwa;
            if (cfg["antiRepeatNotice"]?.GetValue<string>() is string arn && arn.Length >= 20) AntiRepeatNotice = arn;
            if (cfg["diagnostic"]?.GetValue<bool>() is bool dg) Diagnostic = dg;
            if (cfg["toolStrategy"]?.GetValue<bool>() is bool tstrat) ToolStrategy = tstrat;
            if (cfg["toolShim"]?.GetValue<string>() is string tsh) ToolShimMode = tsh.Trim().ToLowerInvariant();
            if (cfg["toolShimMaxString"]?.GetValue<int>() is int tsm && tsm > 0) ToolShimMaxString = tsm;
            if (cfg["toolShimMinCalls"]?.GetValue<int>() is int tsmc && tsmc >= 0) ToolShimMinCalls = tsmc;
            if (cfg["contextGuard"]?.AsObject() is JsonObject cg)
            {
                if (cg["enabled"]?.GetValue<bool>() is bool cge) ContextGuard = cge;
                if (cg["threshold"]?.GetValue<double>() is double cgt && cgt >= 0.5 && cgt <= 0.99) ContextThreshold = cgt;
                if (cg["contextTokens"]?.GetValue<int>() is int cct && cct > 0) ContextTokens = cct;
            }
            if (cfg["qwenStandard"]?.GetValue<bool>() is bool qs) QwenStd = qs;
            if (Environment.GetEnvironmentVariable("DIAGNOSTIC") is "1" or "true" or "True") Diagnostic = true;
            // Dois nomes de ambiente, um por switch.
            if (Environment.GetEnvironmentVariable("HARNESS_TAILS") is "0" or "false" or "False") TailsEnabled = false;
            if (Environment.GetEnvironmentVariable("MAIN_HARNESS") is "0" or "false" or "False") MainHarnessOn = false;
            if (cfg["thinkingOffRoles"] is JsonArray tor)
                foreach (var r in tor)
                    if (r?.GetValue<string>() is { Length: > 0 } rn) ThinkingOffRoles.Add(rn.Trim());
            if (cfg["toolBlacklist"]?.AsObject() is JsonObject tbl)
                foreach (var kv in tbl)
                {
                    if (kv.Value is not JsonArray tools) continue;
                    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var t in tools) if (t?.GetValue<string>() is { Length: > 0 } tn) set.Add(tn.Trim());
                    ToolBlacklist[kv.Key] = set;
                }
            RoleProfiles.Apply(Profiles, cfg["profiles"]?.AsObject(), Temperature, Thinking);
            Log(new JsonObject { ["event"] = "appsettings", ["path"] = path });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[opencodebridge] appsettings ignorado: {ex.Message}");
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
    // Cliente do ultimo request de modelo: "opencode" (User-Agent do CLI) ou
    // "bridge" (qualquer outro cliente). Serve ao painel para nao confundir
    // main_harness global (caminho gerido) com o harness do opencode.
    static long RequestsOpencode;
    static long RequestsBridge;
    // Mensagens reais ao modelo: POST /v1/* conta uma mensagem inteira por
    // request. requests/responses/falhas ficam separados do HTTP total
    // (TotalRequests), que inclui polling do dashboard e health checks.
    static long ModelRequests;
    static long ModelResponses;
    static long ModelFailedRequests;
    static string LastClient = "n/d";
    static long LastRequestMs;
    // Ultimo inicio/fim de request de modelo (nao poll de /api/metrics):
    // gate de frescor dos paineis. Sem fluxo recente => NO DATA.
    static long LastModelActivityMs;
    // Tools executadas pela bridge (POST /api/tools/execute). No modo opencode
    // o CLI executa localmente e este contador fica em 0 de proposito.
    static long ToolsCalls;
    // Amostras da sonda por step (StepProbe -> POST /api/test/probe). Ring em
    // memoria para o painel ler throughput/VRAM/tools sem depender de arquivo.
    static readonly ConcurrentQueue<JsonObject> ProbeSamples = new();
    const int ProbeCap = 4096;
    // System efetivamente enviado ao upstream (harness + cliente + tails).
    // Atestacao honesta: o FsMini do cliente chegou ao modelo? Lido por
    // GET /api/test/last-system. Nao inventa: e' o texto final do request.
    static JsonObject? LastSystemSent;
    static void RecordSystemSent(string path, string? role, string model, string systemText,
        double? temperature = null, bool hasProfile = false,
        bool mainHarness = false, bool hasTools = false)
    {
        LastSystemSent = new JsonObject
        {
            ["event"] = "system-sent",
            ["path"] = path,
            ["role"] = role ?? "",
            ["model"] = model ?? "",
            ["chars"] = systemText.Length,
            ["fsmini"] = systemText.Contains("F# MINI", StringComparison.Ordinal),
            ["int_regra"] = systemText.Contains("INTEIROS", StringComparison.Ordinal),
            ["env"] = systemText.Contains("<env>", StringComparison.Ordinal),
            ["tail_scope"] = TailScope,
            ["harness_tails"] = TailsEnabled,
            ["behavior_tail"] = BehaviorTailEnabled,
            ["tools_tail_flag"] = ToolsTailEnabled,
            ["lane"] = BridgeLane.Value == true ? "bridge" : "opencode",
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            // Portao de fluxo (PENDINGS P1): prova do que ESTE request carregou.
            // main_harness vem de MainHarness.Apply (a prova, nao a flag de CLI);
            // tools_tail so existe quando o request declarou `tools` (sem tools a
            // cauda sai de proposito — sem ela o modelo emite JSON de tool como texto).
            ["main_harness"] = mainHarness,
            ["has_tools"] = hasTools,
            ["tools_tail"] = hasTools && HarnessTails.HasToolsTail(systemText, ToolsTailEffective),
            // Temperatura que vai no corpo do upstream + se o model id carrega role
            // (has_profile=true + valor == perfil do role = prova de "rodou a 0.1").
            ["temperature"] = temperature.HasValue ? JsonValue.Create(temperature.Value) : null,
            ["has_profile"] = hasProfile
        };
        if (Diagnostic) Log(LastSystemSent.DeepClone().AsObject());
    }
    // Sem amostra da sonda nesse intervalo = nenhum run ativo: paineis live
    // devem devolver vazio (Grafana NO DATA), nao o ultimo dado parado.
    const int LivePanelMaxAgeSec = 120;
    // Resultado dos steps concluidos (driver -> POST /api/test/step). O painel
    // lista os passados com veredito e tempo; o atual fica no enunciado.
    static readonly ConcurrentDictionary<string, JsonObject> TestSteps = new();
    // Enunciado do teste em execucao (publicado pelo runner; lido pelo painel).
    static string LastEnunciadoStep = "n/d";
    static string LastEnunciado = "";
    static long LastEnunciadoMs;
    // Historico do enunciado do step: inicial + mensagens adicionais
    // (nudges do runner e avisos do gate anti-repeat).
    static readonly object EnunciadoLock = new();
    static readonly ConcurrentQueue<JsonObject> TestStatementHistory = new();
    const int TestStatementHistoryCap = 64;
    // Persiste o ultimo enunciado para o painel nao sumir no restart da bridge.
    static readonly string EnunciadoStatePath =
        Path.Combine(Path.GetTempPath(), "opencode", "bridge-enunciado.json");
    static bool EnunciadoStateLoaded;
    // Arquivos distintos pedidos em write pelo modelo (conta no painel).
    static readonly ConcurrentDictionary<string, byte> GeneratedFiles = new();
    // Respostas do agente (painel a direita do enunciado): ultimas 6.
    static readonly object AgentActivityLock = new();
    static readonly ConcurrentQueue<JsonObject> AgentActivity = new();
    const int AgentActivityCap = 64;

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

    /// <summary>
    /// Contabiliza uma resposta chat/completions (passthrough) no /api/metrics.
    /// Mantem a metrica antiga: t/s por wall clock (output / tempo total do
    /// request). `timings` so serve de fallback para tokens quando `usage`
    /// nao vem no corpo.
    /// </summary>
    static void RecordChatUsage(JsonNode? usage, JsonNode? timings, double wallSecs)
    {
        int inTok = usage?["prompt_tokens"]?.GetValue<int>()
                    ?? usage?["input_tokens"]?.GetValue<int>() ?? 0;
        int outTok = usage?["completion_tokens"]?.GetValue<int>()
                     ?? usage?["output_tokens"]?.GetValue<int>() ?? 0;
        if (inTok == 0) inTok = timings?["prompt_n"]?.GetValue<int>() ?? 0;
        if (outTok == 0) outTok = timings?["predicted_n"]?.GetValue<int>() ?? 0;
        if (outTok > 0 && wallSecs > 0) AddMetrics(inTok, outTok, wallSecs);
    }

    /// <summary>Timestamp (epoch ms) de uma amostra da sonda.</summary>
    static long ProbeTs(JsonObject sample)
    {
        try { return sample["ts"]?.GetValue<long>() ?? 0; }
        catch { return 0; }
    }

    /// <summary>Ha amostra da sonda dentro da janela? Gate dos paineis live.</summary>
    static bool ProbeFresh(int maxAgeSec)
    {
        long cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - maxAgeSec * 1000L;
        foreach (var sample in ProbeSamples)
            if (ProbeTs(sample) >= cutoff) return true;
        return false;
    }

    /// <summary>Acrescenta uma mensagem ao historico do enunciado do step.</summary>
    static void AddEnunciadoMessage(string kind, string text)
    {
        text = (text ?? "").Replace("\r", " ").Trim();
        if (text.Length == 0) return;
        lock (EnunciadoLock)
        {
            var last = TestStatementHistory.LastOrDefault();
            if (last?["text"]?.GetValue<string>() == text) return;
            if (kind == "enunciado" && TestStatementHistory.Count > 0)
            {
                // O prompt inicial e publicado a cada request (PostObj); nao
                // repete a primeira linha quando o anti-repeat injeta um aviso.
                var first = TestStatementHistory.FirstOrDefault();
                if (first?["text"]?.GetValue<string>() == text) return;
                kind = "adicional";
            }
            var row = new JsonObject
            {
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["time"] = DateTime.Now.ToString("HH:mm:ss"),
                ["step"] = LastEnunciadoStep,
                ["kind"] = kind,
                ["text"] = text
            };
            TestStatementHistory.Enqueue(row);
            while (TestStatementHistory.Count > TestStatementHistoryCap) TestStatementHistory.TryDequeue(out _);
            LastEnunciado = text;
            LastEnunciadoMs = row["ts"]!.GetValue<long>();
            SaveEnunciadoStateLocked();
        }
    }

    /// <summary>Restaura o ultimo enunciado do disco (sobrevive ao restart da bridge).</summary>
    static void LoadEnunciadoState()
    {
        lock (EnunciadoLock)
        {
            if (EnunciadoStateLoaded) return;
            EnunciadoStateLoaded = true;
            try
            {
                if (!File.Exists(EnunciadoStatePath)) return;
                var root = JsonNode.Parse(File.ReadAllText(EnunciadoStatePath))?.AsObject();
                if (root is null) return;
                LastEnunciadoStep = root["step"]?.GetValue<string>() ?? "n/d";
                LastEnunciado = root["prompt"]?.GetValue<string>() ?? "";
                LastEnunciadoMs = root["ms"]?.GetValue<long>() ?? 0;
                while (TestStatementHistory.TryDequeue(out _)) { }
                if (root["messages"] is JsonArray arr)
                    foreach (var node in arr)
                        if (node is JsonObject row) TestStatementHistory.Enqueue(row.DeepClone().AsObject());
            }
            catch { /* estado corrompido nao derruba a bridge */ }
        }
    }

    /// <summary>Grava o estado do enunciado; chamado sob EnunciadoLock.</summary>
    static void SaveEnunciadoStateLocked()
    {
        try
        {
            string? dir = Path.GetDirectoryName(EnunciadoStatePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var root = new JsonObject
            {
                ["step"] = LastEnunciadoStep,
                ["prompt"] = LastEnunciado,
                ["ms"] = LastEnunciadoMs
            };
            var arr = new JsonArray();
            foreach (var row in TestStatementHistory) arr.Add(row.DeepClone());
            root["messages"] = arr;
            File.WriteAllText(EnunciadoStatePath, root.ToJsonString());
        }
        catch { /* painel nunca derruba o pedido */ }
    }

    /// <summary>Runner publicou o enunciado; troca de step limpa o historico.</summary>
    static void SetEnunciado(string step, string prompt)
    {
        LoadEnunciadoState();
        lock (EnunciadoLock)
        {
            if (step != LastEnunciadoStep)
            {
                while (TestStatementHistory.TryDequeue(out _)) { }
                LastEnunciadoStep = step;
                // O painel "Respostas do agente" tambem reinicia no novo step.
                lock (AgentActivityLock)
                {
                    while (AgentActivity.TryDequeue(out _)) { }
                }
            }
            LastEnunciado = prompt;
            LastEnunciadoMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            SaveEnunciadoStateLocked();
        }
        if (prompt.Length > 0) AddEnunciadoMessage("enunciado", prompt);
    }

    /// <summary>
    /// Zera o estado do painel (enunciado, historico, respostas do agente e
    /// lista de steps). Chamado no reset de bateria: o painel comeca limpo em
    /// cada run, sem arrastar o enunciado anterior pelo arquivo de estado.
    /// </summary>
    static void ClearPanelState()
    {
        lock (EnunciadoLock)
        {
            while (TestStatementHistory.TryDequeue(out _)) { }
            LastEnunciadoStep = "n/d";
            LastEnunciado = "";
            LastEnunciadoMs = 0;
            // O estado em disco ja' foi zerado: nao recarrega o run anterior.
            EnunciadoStateLoaded = true;
            SaveEnunciadoStateLocked();
        }
        lock (AgentActivityLock)
            while (AgentActivity.TryDequeue(out _)) { }
        TestSteps.Clear();
    }

    /// <summary>Snapshot do enunciado: step, ultimo prompt e lista de mensagens.</summary>
    static (string Step, string Prompt, long Ms, JsonArray Messages) SnapshotEnunciado()
    {
        LoadEnunciadoState();
        var messages = new JsonArray();
        string step, prompt;
        long ms;
        lock (EnunciadoLock)
        {
            // Mantem o ultimo enunciado publicado mesmo entre steps/sem prompt
            // (units) e apos restart: o painel nao pode "sumir" enquanto o run
            // ainda esta em andamento. A troca real limpa em SetEnunciado.
            step = LastEnunciadoStep;
            prompt = LastEnunciado;
            ms = LastEnunciadoMs;
            foreach (var row in TestStatementHistory)
                messages.Add(row.DeepClone());
        }
        return (step, prompt, ms, messages);
    }

    /// <summary>Publica uma resposta/tool do agente no painel ao lado do enunciado.</summary>
    static void RecordAgentActivity(string kind, string text)
    {
        text = (text ?? "").Replace("\r", " ").Trim();
        if (text.Length == 0) return;
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length > 1200) text = text[..1200] + "…";
        lock (AgentActivityLock)
        {
            var last = AgentActivity.LastOrDefault();
            if (last?["kind"]?.GetValue<string>() == kind
                && last?["text"]?.GetValue<string>() == text) return;
            var row = new JsonObject
            {
                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["time"] = DateTime.Now.ToString("HH:mm:ss"),
                ["step"] = LastEnunciadoStep,
                ["kind"] = kind,
                ["text"] = text
            };
            AgentActivity.Enqueue(row);
            while (AgentActivity.Count > AgentActivityCap) AgentActivity.TryDequeue(out _);
        }
    }

    /// <summary>Resume uma tool call para o painel (path/comando/pattern).</summary>
    static string FormatToolCall(string name, string args)
    {
        string path = "", cmd = "", pattern = "";
        try
        {
            var a = JsonNode.Parse(args)?.AsObject();
            path = a?["path"]?.GetValue<string>() ?? "";
            cmd = a?["command"]?.GetValue<string>() ?? "";
            pattern = a?["pattern"]?.GetValue<string>() ?? "";
        }
        catch { }
        if (name.Equals("write", StringComparison.OrdinalIgnoreCase) && path.Length > 0) return "write " + path;
        if (name.Equals("edit", StringComparison.OrdinalIgnoreCase) && path.Length > 0) return "edit " + path;
        if (cmd.Length > 0) return name + ": " + cmd;
        if (pattern.Length > 0) return name + ": " + pattern;
        return name;
    }

    /// <summary>Registra mensagem/tools de uma resposta chat (OpenAI).</summary>
    static void RecordChatActivity(string json)
    {
        try
        {
            var root = JsonNode.Parse(json)?.AsObject();
            if (root?["choices"]?[0]?["message"] is not JsonObject msg) return;
            string content = "";
            if (msg["content"] is JsonValue cv) { try { content = cv.GetValue<string>(); } catch { } }
            if (content.Length > 0) RecordAgentActivity("assistant", content);
            if (msg["tool_calls"] is JsonArray calls)
                foreach (var c in calls)
                {
                    string name = c?["function"]?["name"]?.GetValue<string>() ?? "";
                    string args = c?["function"]?["arguments"]?.GetValue<string>() ?? "{}";
                    RecordToolCall(name, args);
                    RecordAgentActivity("tool", FormatToolCall(name, args));
                }
        }
        catch { /* metrica nunca derruba o pedido */ }
    }

    /// <summary>Registra mensagem/tools de uma resposta Responses.</summary>
    static void RecordResponsesActivity(JsonObject responseObj)
    {
        try
        {
            if (responseObj["output"] is not JsonArray output) return;
            foreach (var node in output)
            {
                if (node is not JsonObject item) continue;
                string type = item["type"]?.GetValue<string>() ?? "";
                if (type == "message" && item["content"] is JsonArray parts)
                {
                    string text = string.Join("\n", parts.OfType<JsonObject>()
                        .Select(p => p["text"]?.GetValue<string>() ?? "")
                        .Where(s => s.Length > 0));
                    if (text.Length > 0) RecordAgentActivity("assistant", text);
                }
                else if (type == "function_call")
                {
                    string name = item["name"]?.GetValue<string>() ?? "";
                    string args = item["arguments"]?.GetValue<string>() ?? "{}";
                    RecordToolCall(name, args);
                    RecordAgentActivity("tool", FormatToolCall(name, args));
                }
            }
        }
        catch { /* metrica nunca derruba o pedido */ }
    }

    /// <summary>Conta arquivos distintos pedidos em write pelo modelo.</summary>
    static void RecordToolCall(string name, string args)
    {
        if (!name.Equals("write", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var a = JsonNode.Parse(args)?.AsObject();
            string path = a?["path"]?.GetValue<string>() ?? "";
            if (path.Length > 0)
                GeneratedFiles.TryAdd(LastEnunciadoStep + "|" + path, 0);
        }
        catch { }
    }

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    /// <summary>Auth no upstream: repassa o Authorization do cliente ou usa a ApiKey configurada.</summary>
    /// <param name="req">Request upstream.</param>
    /// <param name="ctx">Contexto com os headers originais.</param>
    static void WithAuth(HttpRequestMessage req, HttpListenerContext ctx)
    {
        // A ApiKey da bridge e a credencial do UPSTREAM (llama --api-key).
        // O Bearer do cliente (JWT da Web) vale so na porta da bridge: repassa-lo
        // para o llama resultava em 401 "Invalid API Key" e a Web ficava sem resposta.
        if (!string.IsNullOrEmpty(ApiKey))
        {
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);
            return;
        }
        string? incoming = ctx.Request.Headers["Authorization"];
        if (!string.IsNullOrWhiteSpace(incoming)) req.Headers.TryAddWithoutValidation("Authorization", incoming.Trim());
    }

    // JSON sem escape unicode -> acentos do pt-BR chegam inteiros no cliente
    static readonly JsonSerializerOptions SerOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // Marcadores de template que as vezes escapam para o texto final.
    static readonly Regex ReTemplate = new(@"<\|im_(start|end)\|>", RegexOptions.Compiled);



    // Catálogo real de tools (+ apelidos shell -> shell): só estes nomes
    // viram tool_call na coação; resto (echo alucinado) fica como prosa.
    static readonly HashSet<string> RealTools = new(StringComparer.OrdinalIgnoreCase)
        { "shell", "read", "grep", "glob", "fetch", "webfetch", "websearch", "git", "subagent", "write", "edit", "question", "skill" };












    /// <summary>Repovoa o ring /api/test/probe a partir do log (substitui o
    /// helper de shell). So roda quando DIAGNOSTIC esta ligado.</summary>
    static void SeedProbeFromLog()
    {
        if (!Diagnostic) return;
        try
        {
            if (!File.Exists(LogPath)) return;
            var lines = File.ReadAllLines(LogPath);
            int start = Math.Max(0, lines.Length - 5000);
            for (int i = start; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var obj = JsonNode.Parse(line)?.AsObject();
                    if (obj is null) continue;
                    if ((obj["event"]?.GetValue<string>() ?? "") != "response") continue;
                    if (obj["tps_output"] is null || obj["tps_total"] is null) continue;
                    var sample = obj.DeepClone().AsObject();
                    if (sample["ts"] is null)
                        sample["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (sample["step"] is null)
                        sample["step"] = "bridge-replay";
                    ProbeSamples.Enqueue(sample);
                    while (ProbeSamples.Count > 500) ProbeSamples.TryDequeue(out _);
                }
                catch { /* linha malformada nunca derruba o seed */ }
            }
        }
        catch { /* seed nunca derruba a bridge */ }
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
            ApplyMode();                                           // 1.2b modo manda no harness/tails
        }
        catch (ArgumentException ex)
        {
            // REGRA PRIMORDIAL: modo bridge + harness off e combinacao invalida.
            // Nao sobe o listener (exit 2) e devolve a excecao de argumentos.
            Console.Error.WriteLine($"[bridge] ERRO DE ARGUMENTOS: {ex.Message}");
            return 2;
        }
        if (!ValidateConfig()) return 2;                           // 1.3 coerencia de flags
        // Sonda Serilog: probe do AntiRepeat, SO com DIAGNOSTIC ligado
        // (--diagnostic / appsettings diagnostic / env DIAGNOSTIC=1).
        // Desligado = Serilog mudo; o JSONL do LogPath segue sempre ativo.
        if (Diagnostic)
            Serilog.Log.Logger = new Serilog.LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console(restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information,
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}")
                .WriteTo.File($"/tmp/bridge-sonda-{Port}.log", rollingInterval: Serilog.RollingInterval.Day,
                    retainedFileCountLimit: 7, restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Debug)
                .CreateLogger();
        Serilog.Log.Information("sonda diagnostic={Diagnostic} antiRepeat={AntiRepeat} window={Window}",
            Diagnostic, AntiRepeat, AntiRepeatWindow);
        try
        {
            var logDir = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrEmpty(logDir)) Directory.CreateDirectory(logDir);
            File.AppendAllText(LogPath, $"{{\"event\":\"start\",\"port\":{Port},\"upstream\":\"{Upstream}\",\"model\":\"{Model}\",\"thinking\":\"{Thinking}\"}}\n");
        }
        catch { /* log de startup nunca derruba a bridge (ex.: /tmp owned by root) */ }

        // Seeder em C#: repovoa o ring /api/test/probe a partir do log, sem
        // helper de shell. So roda quando DIAGNOSTIC esta ligado.
        SeedProbeFromLog();

        // HttpListener e o servidor HTTP embutido da BCL (sem Kestrel/NuGet)
        using var listener = new HttpListener();
        // "*" = todas as interfaces (modo remoto). IP especifico = so local.
        string prefixHost = Listen is "0.0.0.0" or "*" or "+" ? "*" : Listen;
        listener.Prefixes.Add($"http://{prefixHost}:{Port}/");
        listener.Start();
        StartedAt = DateTime.UtcNow;
        Console.WriteLine($"[opencodebridge] {Listen}:{Port} -> {Upstream} (model={Model}, thinking={Thinking})");

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
    //
    // GRUPOS (a ordem do switch segue o grupo e toda flag vive em UM grupo):
    //   A) rede/processo .... --listen (alias --host) --port --upstream --model --log --workspace
    //   B) auth/admin ....... --api-key --jwt-secret --jwt-expiry --users-db --bridge-managed
    //   C) transporte ....... --passthrough-responses --stream-per-delta --qwen-standard
    //                         --thinking-format (alias --thinking) --thinking-off-roles
    //   D) harness/prompt ... --main-harness --harness-tails --harness-behavior-tail --harness-tools-tail
    //   E) coerção de tools . --upstream-parser --coerce-text-tools --coerce-narrated --tool-strategy
    //   F) anti-repeat ...... --anti-repeat --anti-repeat-abort --anti-repeat-retry
    //                         --anti-repeat-window --anti-repeat-notice
    //   G) orçamento ........ --max-tokens --coerced-max-tokens (alias --cpu-max-tokens)
    //                         --context-guard --context-tokens --context-slots --context-threshold
    //   H) geração/artefatos  --temperature --tool-output-limit
    //   I) segurança ........ --risk-ask --risk-ask-minutes --risk-tools-alerts --tool-blacklist
    //   J) observabilidade .. --diagnostic --diagnostic-level
    //   L) legado ........... --legacy-harness (nunca em producao)
    static void ParseArgs(string[] cliArgs)
    {
        for (int i = 0; i < cliArgs.Length - 1; i++)
        {
            switch (cliArgs[i])
            {
                // ---------- A) rede/processo ----------
                case "--port": Port = int.Parse(cliArgs[i + 1]); break;
                // --listen 0.0.0.0 expõe a bridge na rede (opencode remoto).
                // --host e ALIAS historico do mesmo destino (nao uma 2a opcao).
                case "--listen": case "--host": Listen = cliArgs[i + 1]; break;
                case "--upstream": Upstream = cliArgs[i + 1].TrimEnd('/'); break;
                case "--model": Model = cliArgs[i + 1]; break;
                case "--log": LogPath = cliArgs[i + 1]; break;
                case "--workspace": WorkspaceRoot = cliArgs[i + 1]; break;

                // ---------- B) auth/admin ----------
                case "--api-key": ApiKey = cliArgs[i + 1]; break;
                case "--jwt-secret": if (cliArgs[i + 1].Length >= 16) Auth.JwtSecret = cliArgs[i + 1]; break;
                case "--jwt-expiry": if (int.TryParse(cliArgs[i + 1], out int jexp) && jexp > 0) Auth.JwtExpiryMinutes = jexp; break;
                case "--users-db": Auth.UsersDbPath = cliArgs[i + 1]; break;
                case "--bridge-managed": BridgeManagedAll = cliArgs[i + 1] is not "false" and not "0"; break;

                // ---------- C) transporte/compat ----------
                case "--passthrough-responses": PassthroughResponses = cliArgs[i + 1] is not "false" and not "0"; break;
                case "--stream-per-delta": StreamPerDelta = cliArgs[i + 1] is not "false" and not "0"; break;
                case "--qwen-standard": QwenStd = cliArgs[i + 1] is not "false" and not "0"; break;
                // --thinking-format e o nome canonico: o flag decide o FORMATO do
                // repasse do reasoning (events/off), nao o liga/desliga do modelo
                // (isso vem do perfil/sufixo do model id). --thinking = alias.
                case "--thinking-format": case "--thinking": Thinking = cliArgs[i + 1]; break;
                case "--thinking-off-roles":
                    foreach (var r in (cliArgs[i + 1] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        ThinkingOffRoles.Add(r);
                    break;

                // ---------- D) harness/prompt ----------
                case "--harness-tails":
                    // Alias legado (compat): liga/desliga AS DUAS caudas. As flags
                    // reais passaram a ser --harness-behavior e --harness-tools.
                    TailsEnabled = cliArgs[i + 1] is not "false" and not "0";
                    BehaviorTailEnabled = TailsEnabled;
                    ToolsTailEnabled = TailsEnabled;
                    break;
                case "--harness-behavior-tail": HarnessBehaviorTail = cliArgs[i + 1]; BehaviorTailCustom = true; break;
                case "--harness-tools-tail": HarnessToolsTail = cliArgs[i + 1]; ToolsTailCustom = true; break;
                case "--harness-behavior": BehaviorTailEnabled = cliArgs[i + 1] is not "false" and not "0"; break;
                case "--harness-tools": ToolsTailEnabled = cliArgs[i + 1] is not "false" and not "0"; break;
                case "--tail-scope":
                    if (cliArgs[i + 1] is "managed" or "all" or "off") TailScope = cliArgs[i + 1];
                    break;
                case "--main-harness": MainHarnessOn = cliArgs[i + 1] is not "false" and not "0"; break;
                case "--mode":
                    if (BridgeMode.TryParse(cliArgs[i + 1], out BridgeMode mmode)) Mode = mmode;
                    else { Console.Error.WriteLine($"--mode '{cliArgs[i + 1]}' invalido (bridge|opencode)"); Environment.Exit(2); }
                    break;
                case "--breaker-soft": if (int.TryParse(cliArgs[i + 1], out int bs) && bs >= 0) BreakerSoft = bs; break;
                case "--breaker-hard": if (int.TryParse(cliArgs[i + 1], out int bh) && bh >= 0) BreakerHard = bh; break;

                // ---------- E) coerção de tools ----------
                // Capacidade do upstream: native (parser proprio) x none (texto puro).
                case "--upstream-parser": UpstreamParser = cliArgs[i + 1].Trim().ToLowerInvariant(); break;
                case "--coerce-text-tools": CoerceTextTools = cliArgs[i + 1] is not "false" and not "0"; break;
                case "--coerce-narrated": CoerceNarrated = cliArgs[i + 1] is not "false" and not "0"; break;
                case "--tool-strategy": ToolStrategy = cliArgs[i + 1] is not "false" and not "0"; break;
                // Shim generico de tool calling: upstream llama recusa grammar
                // customizada junto com tools; o shim remove tools, injeta a
                // declaracao no system e forca um envelope JSON via grammar.
                case "--tool-shim": ToolShimMode = cliArgs[i + 1].Trim().ToLowerInvariant(); break;
                case "--tool-shim-max-string": ToolShimMaxString = int.Parse(cliArgs[i + 1]); break;
                case "--tool-shim-min-calls": ToolShimMinCalls = int.Parse(cliArgs[i + 1]); break;

                // ---------- F) anti-repeat ----------
                case "--anti-repeat": AntiRepeat = cliArgs[i + 1] is not "false" and not "0"; break;
                case "--anti-repeat-abort": AntiRepeatAbort = cliArgs[i + 1] is not "false" and not "0"; break;
                case "--anti-repeat-retry": AntiRepeatRetry = cliArgs[i + 1] is not "false" and not "0"; break;
                case "--anti-repeat-window": AntiRepeatWindow = int.Parse(cliArgs[i + 1]); break;
                case "--anti-repeat-args-window": AntiRepeatArgsWindow = int.Parse(cliArgs[i + 1]); break;
                case "--anti-repeat-notice": AntiRepeatNotice = cliArgs[i + 1]; break;

                // ---------- G) orçamento/contexto ----------
                // 0 = sem teto: a infra (llama -c/--n-predict) e o cliente mandam.
                case "--max-tokens": MaxTokens = int.Parse(cliArgs[i + 1]); break;
                // Teto da linha COAGIDA (upstream sem parser). --cpu-max-tokens e
                // alias historico: "cpu" era a inferencia por nome de modelo.
                case "--coerced-max-tokens": case "--cpu-max-tokens": CpuMaxTokens = int.Parse(cliArgs[i + 1]); break;
                case "--context-guard": ContextGuard = cliArgs[i + 1] is not "false" and not "0"; break;
                case "--context-threshold": { double v = double.Parse(cliArgs[i + 1], CultureInfo.InvariantCulture); if (v >= 0.5 && v <= 0.99) ContextThreshold = v; break; }
                case "--context-tokens": ContextTokens = int.Parse(cliArgs[i + 1]); break;
                case "--context-slots": ContextSlots = int.Parse(cliArgs[i + 1]); break;

                // ---------- H) geração/artefatos ----------
                // Invariante: em locale pt-BR, Parse("0.7") quebraria sem isso
                case "--temperature": Temperature = double.Parse(cliArgs[i + 1], CultureInfo.InvariantCulture); break;
                // Temperatura AUXILIAR (retry/reconsideracao): vence o role de
                // desvio. Existe p/ quando o model id nao carrega role e o
                // desvio cairia no default sem o operador conseguir ajustar.
                case "--aux-temp": AuxTemp = double.Parse(cliArgs[i + 1], CultureInfo.InvariantCulture); break;
                // 0 = sem corte do resultado de tool (conteudo integral).
                case "--tool-output-limit": ToolOutputLimit = int.Parse(cliArgs[i + 1]); break;
                // Repasse dos parametros de amostragem do cliente ao llama
                // (repeat_penalty/dry_*/top_p/min_p/seed). Opt-in.
                case "--sampling-passthrough": SamplingPassthrough = cliArgs[i + 1] is not "false" and not "0"; break;

                // ---------- I) segurança/permissões ----------
                case "--risk-ask": RiskAsk = cliArgs[i + 1] is not "false" and not "0"; break;
                case "--risk-ask-minutes": if (int.TryParse(cliArgs[i + 1], out int ramc) && ramc > 0) RiskAskMinutes = ramc; break;
                case "--risk-tools-alerts":   // "Plan=git\\s+push,\\brm\\b;Orch=\\bdd\\b"
                    foreach (var grp in (cliArgs[i + 1] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        int eq = grp.IndexOf('=');
                        if (eq <= 0) continue;
                        var rx = new List<Regex>();
                        foreach (var p in grp[(eq + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            if (TryRegex(p, out var compiled)) rx.Add(compiled);
                        if (rx.Count > 0) RiskToolsAlerts[grp[..eq].Trim()] = rx;
                    }
                    break;
                case "--tool-blacklist":   // "Plan=write,sed,rm;MoE=write"
                    foreach (var grp in (cliArgs[i + 1] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        int eq = grp.IndexOf('=');
                        if (eq <= 0) continue;
                        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var t in grp[(eq + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            set.Add(t);
                        ToolBlacklist[grp[..eq].Trim()] = set;
                    }
                    break;

                // ---------- J) observabilidade ----------
                case "--diagnostic": Diagnostic = cliArgs[i + 1] is not "false" and not "0"; break;
                case "--diagnostic-level": DiagnosticLevel = cliArgs[i + 1].Trim().ToLowerInvariant(); break;

                // ---------- L) legado (nunca em producao) ----------
                case "--legacy-harness": LegacyHarness = cliArgs[i + 1] is not "false" and not "0"; break;
            }
        }
    }

    /// <summary>
    /// Aplica a matriz do modo (Core/BridgeMode): opencode desliga só o main
    /// harness e mantém fixes + anti-repeat + tails (quando as flags deixam);
    /// bridge mantém base, tails e anti-repeat.
    /// </summary>
    static void ApplyMode()
    {
        if (Mode is not { } m) return;
        // REGRA PRIMORDIAL: modo bridge exige MainHarness ON. Combinacao
        // bridge + harness off e invalida e NAO deve subir (excecao de args).
        // A raia opencode (m.MainHarness==false) e a unica que roda sem o nosso
        // harness, pois ja carrega o dela no stream com o llama.
        if (m.MainHarness && !MainHarnessOn)
            throw new ArgumentException(
                "modo bridge exige main harness ON: --main-harness 0 e invalido com --mode bridge. " +
                "Use --mode opencode para a raia que ja carrega o harness proprio no stream.");
        if (!m.MainHarness) MainHarnessOn = false;
        if (!m.Tails) TailsEnabled = false;
        if (m.AntiRepeat) AntiRepeat = true;
    }

    /// <summary>Coerencia entre flags — roda ANTES de subir o listener.</summary>
    /// <remarks>Fail-fast (exit 2) para combinacao impossivel; aviso em stderr
    /// para flag aceita que ficaria INERTE — o caso que ja custou rodada: flag
    /// ligada na unit sem nenhum efeito (ex.: retry do anti-repeat preso no
    /// --legacy-harness).</remarks>
    /// <returns>False = nao subir.</returns>
    static bool ValidateConfig()
    {
        var errors = new List<string>();
        var warns = new List<string>();

        // --diagnostic 1 (legado) sem nivel declarado = dump integral.
        if (Diagnostic && DiagnosticLevel == "off") DiagnosticLevel = "dump";
        if (DiagnosticLevel is not ("off" or "metrics" or "dump"))
            errors.Add($"--diagnostic-level '{DiagnosticLevel}' invalido (off|metrics|dump)");
        Diagnostic = DiagnosticLevel is "metrics" or "dump";

        if (UpstreamParser is not ("native" or "none"))
            errors.Add($"--upstream-parser '{UpstreamParser}' invalido (native|none)");
        if (AntiRepeatWindow < ContentAntiRepeat.WindowMin || AntiRepeatWindow > ContentAntiRepeat.WindowMax)
            errors.Add($"--anti-repeat-window {AntiRepeatWindow} fora da faixa {ContentAntiRepeat.WindowMin}..{ContentAntiRepeat.WindowMax}");
        if (AntiRepeatArgsWindow < ToolArgsAntiRepeatReply.ArgsWindowMin || AntiRepeatArgsWindow > ToolArgsAntiRepeatReply.ArgsWindowMax)
            errors.Add($"--anti-repeat-args-window {AntiRepeatArgsWindow} fora da faixa {ToolArgsAntiRepeatReply.ArgsWindowMin}..{ToolArgsAntiRepeatReply.ArgsWindowMax}");
        if (AntiRepeatNotice.Length < 20)
            errors.Add("--anti-repeat-notice precisa de >= 20 caracteres");
        if (ContextGuard && ContextTokens <= 0)
            errors.Add("--context-guard 1 exige --context-tokens > 0 (sem o tamanho do ctx o guard nunca dispara)");
        if (ContextSlots < 1)
            errors.Add($"--context-slots {ContextSlots} invalido: use >= 1 (slots paralelos do llama, -np)");

        if (CpuMaxTokens > 0 && !CoerceFor())
            warns.Add($"--coerced-max-tokens {CpuMaxTokens} inerte: use --upstream-parser none ou --coerce-text-tools 1");
        if (!TailsEnabled && (BehaviorTailCustom || ToolsTailCustom) && TailScope == "managed")
            warns.Add("cauda de harness declarada com --harness-tails 0: inerte (use --harness-tails 1 para ativar)");
        if (TailsEnabled && TailScope == "managed" && Mode?.Name == "opencode")
            warns.Add("--harness-tails 1 com --tail-scope managed: o caminho opencode (modelo com sufixo de role) fica SEM cauda — use --tail-scope all");
        if (TailScope == "all" && !TailsAtivas())
            warns.Add("--tail-scope all com caudas vazias: nada a injetar");
        if (TailsEnabled && !BehaviorTailEnabled)
            warns.Add("--harness-behavior 0: cauda de comportamento desligada (tools segue pela flag propria)");
        if (TailsEnabled && !ToolsTailEnabled)
            warns.Add("--harness-tools 0: cauda de tools desligada (comportamento segue pela flag propria)");
        if (BreakerHard > 0 && BreakerSoft > BreakerHard)
            errors.Add($"--breaker-soft {BreakerSoft} maior que --breaker-hard {BreakerHard}: o aviso nunca apareceria");
        if (!ContextGuard && ContextTokens > 0)
            warns.Add($"--context-tokens {ContextTokens} inerte: --context-guard 0");
        if (!ContextGuard && Math.Abs(ContextThreshold - 0.95) > 0.0001)
            warns.Add("--context-threshold sem --context-guard 1: inerte");
        if (LegacyHarness)
            warns.Add("--legacy-harness 1 reanima guardrail/strip/retry historicos: nao usar em producao");

        foreach (var w in warns) Console.Error.WriteLine("[bridge][aviso] " + w);
        foreach (var e in errors) Console.Error.WriteLine("[bridge][erro] " + e);
        return errors.Count == 0;
    }

    /// <summary>Grupos de flags em vigor — quem opera a bridge de fora ve o mapa
    /// inteiro (a qual rotina cada flag pertence) sem ler a linha de comando.</summary>
    /// <returns>Objeto { grupo: [flags ativas] }.</returns>
    static JsonObject ConfigGroups()
    {
        var g = new JsonObject();
        void Add(string group, params string[] flags)
        {
            var arr = new JsonArray();
            foreach (var f in flags) if (f.Length > 0) arr.Add(f);
            if (arr.Count > 0) g[group] = arr;
        }
        Add("rede-processo", "listen=" + Listen, "port=" + Port, "upstream=" + Upstream, "model=" + Model);
        Add("transporte", "thinking-format=" + Thinking,
            PassthroughResponses ? "passthrough-responses" : "", StreamPerDelta ? "stream-per-delta" : "",
            QwenStd ? "qwen-standard" : "");
        Add("harness-prompt", MainHarnessOn ? "main-harness" : "", TailsEnabled ? "harness-tails" : "",
            BehaviorTailCustom ? "harness-behavior-tail" : "", ToolsTailCustom ? "harness-tools-tail" : "");
        Add("coercao-tools", CoerceFor() ? "coercao=on" : "coercao=off", "upstream-parser=" + UpstreamParser,
            CoerceTextTools ? "coerce-text-tools" : "", CoerceNarrated ? "coerce-narrated" : "",
            ToolStrategy ? "tool-strategy" : "");
        Add("anti-repeat", AntiRepeat ? "anti-repeat" : "", AntiRepeatAbort ? "anti-repeat-abort" : "",
            AntiRepeatRetry ? "anti-repeat-retry" : "", "window=" + AntiRepeatWindow,
            "args_window=" + AntiRepeatArgsWindow);
        Add("orcamento", MaxTokens > 0 ? "max-tokens=" + MaxTokens : "",
            CpuMaxTokens > 0 ? "coerced-max-tokens=" + CpuMaxTokens : "",
            ContextGuard ? "context-guard=" + ContextTokens : "");
        Add("observabilidade", DiagnosticLevel == "off" ? "" : "diagnostic-level=" + DiagnosticLevel);
        Add("legado", LegacyHarness ? "legacy-harness" : "");
        return g;
    }

    // =====================================================================
    // 2) ROTEAMENTO — GET /health, POST /v1/responses, demais = passa direto
    // =====================================================================
    /// <summary>Roteia cada HTTP: docs, saude, /v1/responses, passthrough.</summary>
    /// <param name="ctx">Contexto HTTP.</param>
    static void HandleRequest(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        // Quem e o cliente DESTE pedido (uma vez por requisicao): o tail-scope
        // "managed" decide por cliente e nao pelo sufixo do modelo.
        bool opencodeClient = IsOpencodeClient(ctx);
        // O harness de teste declara o modo por header; o CLI do opencode nao
        // manda header e continua sendo reconhecido pelo User-Agent. Assim a
        // raia bridge e a raia opencode nao se cruzam nem por sufixo de modelo.
        string? modeHdr = ctx.Request.Headers["X-Bridge-Mode"];
        if (!string.IsNullOrWhiteSpace(modeHdr))
        {
            if (modeHdr.Equals("opencode", StringComparison.OrdinalIgnoreCase)) opencodeClient = true;
            else if (modeHdr.Equals("bridge", StringComparison.OrdinalIgnoreCase)) opencodeClient = false;
        }
        OpencodeClient.Value = opencodeClient;
        // MainHarness + tails so no caminho bridge. Em opencode o cliente se
        // auto-gerencia; mesmo um request direto com header opencode nao recebe
        // o harness da bridge. Mode=opencode no processo tambem desliga a raia.
        BridgeLane.Value = !opencodeClient && (Mode is null || Mode.MainHarness);
        bool modelRequest = ctx.Request.HttpMethod == "POST" && path.StartsWith("/v1/", StringComparison.Ordinal);
        bool modelFailed = false;
        try
        {
            Interlocked.Increment(ref TotalRequests);

            // Conta o modo do cliente apenas em request de modelo; health,
            // metrics e dashboard nao poluem a leitura do painel.
            if (modelRequest)
            {
                Interlocked.Increment(ref ModelRequests);
                bool opencode = OpencodeClient.Value;
                if (opencode) Interlocked.Increment(ref RequestsOpencode);
                else Interlocked.Increment(ref RequestsBridge);
                LastClient = opencode ? "opencode" : "bridge";
                LastRequestMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                LastModelActivityMs = LastRequestMs;
            }

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

            // ---- Autenticacao JWT (login contra a base de usuarios do Web)
            if (ctx.Request.HttpMethod == "POST" && path == "/api/auth/login")
            {
                var loginBody = JsonNode.Parse(ReadBody(ctx))?.AsObject() ?? new JsonObject();
                string email = loginBody["email"]?.GetValue<string>() ?? "";
                string password = loginBody["password"]?.GetValue<string>() ?? "";
                var found = Auth.Login(email, password);
                if (found is null)
                {
                    Log(new JsonObject { ["event"] = "auth", ["result"] = "denied", ["email"] = email });
                    WriteJson(ctx, 401, new JsonObject { ["error"] = "credenciais invalidas" });
                    return;
                }
                string? token = Auth.CreateToken(email.Trim().ToLowerInvariant(), found.Value.IsAdmin);
                if (token is null)
                {
                    WriteJson(ctx, 500, new JsonObject { ["error"] = "jwt nao configurado na bridge (jwt.secret)" });
                    return;
                }
                Log(new JsonObject { ["event"] = "auth", ["result"] = "ok", ["email"] = email, ["admin"] = found.Value.IsAdmin });
                WriteJson(ctx, 200, new JsonObject
                {
                    ["token"] = token, ["token_type"] = "Bearer",
                    ["expires_in"] = Auth.JwtExpiryMinutes * 60, ["admin"] = found.Value.IsAdmin,
                });
                return;
            }

            // Metricas de vazao (tokens/s) p/ o e2e exibir no console/arquivo.
            if (ctx.Request.HttpMethod == "GET" && path == "/api/metrics")
            {
                if (!ClientAuthorized(ctx)) { WriteJson(ctx, 401, new JsonObject { ["error"] = "nao autorizado (Bearer JWT ou ApiKey)" }); return; }
                // fresh=1 (painel): sem request de modelo em voo e sem atividade
                // recente, devolve vazio para o Grafana exibir NO DATA em vez do
                // acumulado parado. Sem o parametro, mantem o contrato antigo.
                bool freshOnly = ctx.Request.QueryString["fresh"] is "1" or "true" or "yes";
                int freshAge = int.TryParse(ctx.Request.QueryString["max_age_s"], out int fa)
                    ? Math.Clamp(fa, 1, 3600) : 60;
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long outstanding = Math.Max(0, ModelRequests - ModelResponses - ModelFailedRequests);
                long lastActivity = LastModelActivityMs;
                if (freshOnly && outstanding == 0
                    && (lastActivity == 0 || nowMs - lastActivity > freshAge * 1000L))
                {
                    WriteJson(ctx, 200, new JsonArray());
                    return;
                }
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
                    ["model_requests"] = ModelRequests,
                    ["model_responses"] = ModelResponses,
                    ["model_failed_requests"] = ModelFailedRequests,
                    ["http_requests"] = TotalRequests, ["http_failed_requests"] = FailedRequests,
                    ["requests_opencode"] = RequestsOpencode, ["requests_bridge"] = RequestsBridge,
                    ["last_client"] = LastClient, ["last_request_ms"] = LastRequestMs,
                    ["input_tokens"] = inTok, ["output_tokens"] = outTok,
                    ["elapsed_seconds"] = Math.Round(elapsed, 2),
                    ["tps_output"] = Math.Round(tpsOut, 2),
                    ["tps_total"] = Math.Round(tpsTotal, 2),
                    ["last_model_activity_ms"] = lastActivity,
                    ["model_outstanding"] = outstanding,
                    ["model_idle_seconds"] = lastActivity == 0 ? -1
                        : Math.Round((nowMs - lastActivity) / 1000.0, 1),
                    // Recusas por anel (sempre contadas, independente do diagnostic).
                    ["ring2_deny"] = Metrics.Ring2Deny,
                    ["ring3_deny"] = Metrics.Ring3Deny,
                    ["nudge_args"] = Metrics.NudgeArgs,
                    ["anti_repeat_content"] = Metrics.AntiRepeatContent,
                    ["anti_repeat_tools"] = Metrics.AntiRepeatTools,
                    // Abortos do upstream (conexao encerrada antes da resposta):
                    // retry com nudge e falha final (502 upstream_aborted).
                    ["upstream_abort"] = Metrics.UpstreamAbortCount,
                    ["upstream_abort_retry"] = Metrics.UpstreamAbortRetryCount,
                    ["tools_calls"] = ToolsCalls,
                    ["files_generated"] = GeneratedFiles.Count
                });
                return;
            }

            // Reset dos diagnosticos cumulativos (POST /api/diag/reset): zera os
            // contadores de /api/metrics e (opcional) as amostras da sonda, sem
            // reiniciar a bridge. Os numeros sao por vida do processo; sem este
            // reset o painel mistura runs e so zera com restart. Com o reset, o
            // painel mede o step/run atual sem downtime e sem derrubar keep-alive.
            if (ctx.Request.HttpMethod == "POST" && path == "/api/diag/reset")
            {
                if (!ClientAuthorized(ctx)) { WriteJson(ctx, 401, new JsonObject { ["error"] = "nao autorizado (Bearer JWT ou ApiKey)" }); return; }
                bool clearProbe = ctx.Request.QueryString["probe"] is "1" or "true" or "yes";
                bool clearFiles = ctx.Request.QueryString["files"] is "1" or "true" or "yes";
                // ?panel=1 zera o painel (enunciado/historico/activity/steps). O
                // reset de bateria usa; o reset por step NAO (apagaria o enunciado
                // no meio do run).
                bool clearPanel = ctx.Request.QueryString["panel"] is "1" or "true" or "yes";
                Metrics.Clear();
                System.Threading.Interlocked.Exchange(ref TotalRequests, 0);
                System.Threading.Interlocked.Exchange(ref FailedRequests, 0);
                System.Threading.Interlocked.Exchange(ref RequestsOpencode, 0);
                System.Threading.Interlocked.Exchange(ref RequestsBridge, 0);
                System.Threading.Interlocked.Exchange(ref ModelRequests, 0);
                System.Threading.Interlocked.Exchange(ref ModelResponses, 0);
                System.Threading.Interlocked.Exchange(ref ModelFailedRequests, 0);
                System.Threading.Interlocked.Exchange(ref LastRequestMs, 0);
                System.Threading.Interlocked.Exchange(ref LastModelActivityMs, 0);
                System.Threading.Interlocked.Exchange(ref ToolsCalls, 0);
                LastClient = "n/d";
                lock (MetricsLock)
                {
                    TotalInputTokens = 0;
                    TotalOutputTokens = 0;
                    TotalElapsedSecs = 0;
                }
                if (clearFiles) GeneratedFiles.Clear();
                if (clearProbe) while (ProbeSamples.TryDequeue(out _)) { }
                if (clearPanel) ClearPanelState();
                var resetAck = new JsonObject
                {
                    ["ok"] = true,
                    ["event"] = "diag-reset",
                    ["probe"] = clearProbe,
                    ["files"] = clearFiles,
                    ["panel"] = clearPanel,
                    ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                Log(resetAck.DeepClone().AsObject());
                WriteJson(ctx, 200, resetAck);
                return;
            }

            // Enunciado do teste em execucao: o runner publica antes de rodar o
            // cenario e o painel Infinity le. GET sem corpo devolve o ultimo.
            if (ctx.Request.HttpMethod == "GET" && path == "/api/test/enunciado/messages")
            {
                if (!ClientAuthorized(ctx)) { WriteJson(ctx, 401, new JsonObject { ["error"] = "nao autorizado (Bearer JWT ou ApiKey)" }); return; }
                if (ctx.Request.QueryString["fresh"] is "1" or "true" or "yes"
                    && !ProbeFresh(LivePanelMaxAgeSec))
                {
                    WriteJson(ctx, 200, new JsonArray());
                    return;
                }
                WriteJson(ctx, 200, SnapshotEnunciado().Messages);
                return;
            }

            // Respostas do agente (painel a direita): ultimas N do step atual.
            if (ctx.Request.HttpMethod == "GET" && path == "/api/test/activity")
            {
                if (!ClientAuthorized(ctx)) { WriteJson(ctx, 401, new JsonObject { ["error"] = "nao autorizado (Bearer JWT ou ApiKey)" }); return; }
                if (ctx.Request.QueryString["fresh"] is "1" or "true" or "yes"
                    && !ProbeFresh(LivePanelMaxAgeSec))
                {
                    WriteJson(ctx, 200, new JsonArray());
                    return;
                }
                int limit = int.TryParse(ctx.Request.QueryString["limit"], out int lim) ? Math.Clamp(lim, 1, 50) : 6;
                string step = SnapshotEnunciado().Step;
                var rows = AgentActivity
                    .Where(x => (x["step"]?.GetValue<string>() ?? "") == step)
                    .Reverse()
                    .Take(limit)
                    .ToArray();
                var arr = new JsonArray();
                foreach (var x in rows) arr.Add(x.DeepClone());
                WriteJson(ctx, 200, arr);
                return;
            }

            if (path == "/api/test/enunciado")
            {
                if (!ClientAuthorized(ctx)) { WriteJson(ctx, 401, new JsonObject { ["error"] = "nao autorizado (Bearer JWT ou ApiKey)" }); return; }
                if (ctx.Request.HttpMethod == "POST")
                {
                    var body = JsonNode.Parse(ReadBody(ctx))?.AsObject() ?? new JsonObject();
                    SetEnunciado(
                        body["step"]?.GetValue<string>() ?? "n/d",
                        body["prompt"]?.GetValue<string>() ?? "");
                    WriteJson(ctx, 200, new JsonObject { ["ok"] = true });
                    return;
                }
                var snap = SnapshotEnunciado();
                WriteJson(ctx, 200, new JsonObject
                {
                    ["step"] = snap.Step,
                    ["prompt"] = snap.Prompt,
                    ["updated_ms"] = snap.Ms,
                    ["messages"] = snap.Messages
                });
                return;
            }

            // Sonda por step: o StepProbe publica amostras (tps, VRAM, GPU,
            // tools) a cada 2s; o painel Infinity le a serie e o ultimo valor.
            if (path == "/api/test/probe" || path == "/api/test/probe/latest")
            {
                if (!ClientAuthorized(ctx)) { WriteJson(ctx, 401, new JsonObject { ["error"] = "nao autorizado (Bearer JWT ou ApiKey)" }); return; }
                if (ctx.Request.HttpMethod == "POST")
                {
                    var body = JsonNode.Parse(ReadBody(ctx))?.AsObject() ?? new JsonObject();
                    var sample = body.DeepClone().AsObject();
                    if (sample["ts"] is null)
                        sample["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    ProbeSamples.Enqueue(sample);
                    while (ProbeSamples.Count > ProbeCap) ProbeSamples.TryDequeue(out _);
                    WriteJson(ctx, 200, new JsonObject { ["ok"] = true, ["samples"] = ProbeSamples.Count });
                    return;
                }
                // max_age_s (painel): so amostras dentro da janela. Sem amostra
                // recente devolve vazio => Grafana NO DATA em vez de serie parada.
                int probeAge = int.TryParse(ctx.Request.QueryString["max_age_s"], out int pa)
                    ? Math.Clamp(pa, 1, 86400) : 0;
                long probeCutoff = probeAge > 0
                    ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - probeAge * 1000L : 0;
                bool Fresh(JsonObject x) => probeAge == 0 || ProbeTs(x) >= probeCutoff;
                if (path.EndsWith("/latest", StringComparison.Ordinal))
                {
                    var latest = ProbeSamples
                        .Where(Fresh)
                        .GroupBy(x => x["step"]?.GetValue<string>() ?? "n/d")
                        .Select(g => g.OrderByDescending(x => x["ts"]?.GetValue<long>() ?? 0).First())
                        .ToArray();
                    var arr = new JsonArray();
                    foreach (var x in latest) arr.Add(x.DeepClone());
                    WriteJson(ctx, 200, arr);
                    return;
                }
                string stepFilter = ctx.Request.QueryString["step"] ?? "";
                int limit = int.TryParse(ctx.Request.QueryString["limit"], out int lim) ? Math.Clamp(lim, 1, ProbeCap) : 1000;
                var rows = ProbeSamples
                    .Where(Fresh)
                    .Where(x => stepFilter.Length == 0 || (x["step"]?.GetValue<string>() ?? "") == stepFilter)
                    .ToArray();
                var slice = rows.Skip(Math.Max(0, rows.Length - limit)).ToArray();
                var series = new JsonArray();
                foreach (var x in slice) series.Add(x.DeepClone());
                WriteJson(ctx, 200, series);
                return;
            }

            // Atestacao honesta: ultimo system enviado ao upstream (FsMini chegou?).
            if (path == "/api/test/last-system")
            {
                if (!ClientAuthorized(ctx)) { WriteJson(ctx, 401, new JsonObject { ["error"] = "nao autorizado (Bearer JWT ou ApiKey)" }); return; }
                WriteJson(ctx, 200, LastSystemSent?.DeepClone().AsObject() ?? new JsonObject());
                return;
            }

            // Steps concluidos: o driver publica step_end; o painel lista os
            // passados (veredito + tempo) e mantem o atual no enunciado.
            if (path == "/api/test/step" || path == "/api/test/steps")
            {
                if (!ClientAuthorized(ctx)) { WriteJson(ctx, 401, new JsonObject { ["error"] = "nao autorizado (Bearer JWT ou ApiKey)" }); return; }
                if (ctx.Request.HttpMethod == "POST" && path == "/api/test/step")
                {
                    var body = JsonNode.Parse(ReadBody(ctx))?.AsObject() ?? new JsonObject();
                    string stepName = body["step"]?.GetValue<string>() ?? "";
                    if (stepName.Length == 0) { WriteJson(ctx, 400, new JsonObject { ["error"] = "step obrigatorio" }); return; }
                    var row = body.DeepClone().AsObject();
                    row["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    TestSteps[stepName] = row;
                    WriteJson(ctx, 200, new JsonObject { ["ok"] = true, ["steps"] = TestSteps.Count });
                    return;
                }
                if (ctx.Request.QueryString["fresh"] is "1" or "true" or "yes"
                    && !ProbeFresh(LivePanelMaxAgeSec))
                {
                    WriteJson(ctx, 200, new JsonArray());
                    return;
                }
                bool warOnly = ctx.Request.QueryString["war"] is "1" or "true" or "yes";
                var list = TestSteps.Values
                    .Where(x => !warOnly || !string.IsNullOrWhiteSpace(x["mode"]?.GetValue<string>()))
                    .OrderBy(x => x["ts"]?.GetValue<long>() ?? 0)
                    .ToArray();
                var arr = new JsonArray();
                foreach (var x in list) arr.Add(x.DeepClone());
                WriteJson(ctx, 200, arr);
                return;
            }

            // ---- API de gerenciamento (permite operar a bridge remotamente)
            if (ctx.Request.HttpMethod == "GET" && path == "/api/status")
            {
                if (!ClientAuthorized(ctx)) { WriteJson(ctx, 401, new JsonObject { ["error"] = "nao autorizado (Bearer JWT ou ApiKey)" }); return; }
                WriteJson(ctx, 200, BuildStatus());
                return;
            }

            // Flag mestra EFETIVA do harness (fonte unica: a web consulta aqui).
            // Com as tails desligadas (--harness-tails 0), o efetivo responde false
            // mesmo com as caudas declaradas; o main harness segue independente.
            if (ctx.Request.HttpMethod == "GET" && path == "/api/harness")
            {
                // Efetivo por caminho: opencode (model com sufixo) x direto/Web.
                WriteJson(ctx, 200, new JsonObject
                {
                    ["tail_scope"] = TailScope,
                    ["main_harness"] = MainHarnessOn,
                    ["main_harness_scope"] = "managed-only",
                    ["main_harness_opencode"] = false,
                    ["main_harness_chars"] = MainHarness.Text.Length,
                    ["harness_tails"] = TailsEnabled,
                    ["tails_managed"] = TailsAtivas() && TailScope is "all" or "managed",
                    ["tails_opencode"] = false,
                    ["behavior_tail"] = BehaviorTailEnabled,
                    ["tools_tail"] = ToolsTailEnabled,
                    ["behavior_chars"] = BehaviorTailEffective.Length,
                    ["tools_chars"] = ToolsTailEffective.Length
                });
                return;
            }

            // Politica de tools: a Web consulta AQUI (fonte unica), em vez de
            // manter regra propria. Body: { model, tool, arguments }.
            if (ctx.Request.HttpMethod == "POST" && path == "/api/permission/check")
            {
                var pr = JsonNode.Parse(ReadBody(ctx))?.AsObject() ?? new JsonObject();
                string pModel = pr["model"]?.GetValue<string>() ?? "";
                string pTool = pr["tool"]?.GetValue<string>() ?? "";
                string pArgs = pr["arguments"] is JsonValue pv && pv.TryGetValue<string>(out string? ps)
                    ? ps : JsonToString(pr["arguments"]);
                var (action, reason) = PermissionOf(pModel, pTool, pArgs);
                Log(new JsonObject { ["event"] = "permission-check", ["tool"] = pTool,
                                     ["role"] = RoleOfModel(pModel), ["action"] = action, ["reason"] = reason });
                WriteJson(ctx, 200, new JsonObject
                {
                    ["action"] = action, ["reason"] = reason,
                    ["role"] = RoleOfModel(pModel), ["tool"] = pTool,
                    ["notice"] = reason.Length == 0 ? "" : string.Format(ToolBlockNotice, pTool, RoleOfModel(pModel)),
                });
                return;
            }

            if (ctx.Request.HttpMethod == "POST" && path == "/api/translate")
            {
                // Dry-run: mostra o /v1/chat/completions que SERIA enviado ao
                // vLLM, sem chamar o modelo. Util p/ depurar a traducao.
                var req = JsonNode.Parse(ReadBody(ctx))?.AsObject() ?? new JsonObject();
                var chat = BuildChatRequest(req, out string requestedModel, out double temperature, out string thinking, out int toolCount, out string tempSource, out string? breakerTrip, out string breakerState);
                WriteJson(ctx, 200, new JsonObject
                {
                    ["requested_model"] = requestedModel,
                    ["temperature"] = temperature,
                    ["temperature_source"] = tempSource,
                    ["request_temperature"] = req["temperature"]?.GetValue<double>(),
                    ["tail_scope"] = TailScope,
                    ["tails_on"] = TailsOn(requestedModel),
                    ["breaker"] = breakerState,
                    ["thinking"] = thinking,
                    ["message_count"] = chat["messages"]!.AsArray().Count,
                    ["tool_count"] = toolCount,
                    ["chat_request"] = chat
                });
                return;
            }

if (ctx.Request.HttpMethod == "POST" && path == "/api/sanitize")
            {
                // Dry-run do bypass: mostra o input SEM os itens reasoning,
                // sem chamar o modelo. Corpo: {input:[...]}.
                var req = JsonNode.Parse(ReadBody(ctx))?.AsObject() ?? new JsonObject();
int dropped = 0;
                bool moved = false;
                try { dropped = StripReasoningItems(req); moved = NormalizeSystemFirst(req); } catch { }
WriteJson(ctx, 200, new JsonObject
                {
                    ["dropped_reasoning"] = dropped,
                    ["system_normalized"] = moved,
                    ["has_instructions"] = req.ContainsKey("instructions"),
                    ["input"] = req["input"]?.DeepClone() ?? new JsonArray()
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
                    reqThink, req["tools"] is JsonArray cta && cta.Count > 0);
                WriteJson(ctx, 200, responseObj);
                return;
            }

            if (ctx.Request.HttpMethod == "POST" && path == "/api/config")
            {
                if (!ClientAuthorized(ctx)) { WriteJson(ctx, 401, new JsonObject { ["error"] = "nao autorizado (Bearer JWT ou ApiKey)" }); return; }
                // Ajuste remoto de parametros (parcial: so o que vier no corpo).
                var req = JsonNode.Parse(ReadBody(ctx))?.AsObject() ?? new JsonObject();
                if (req["thinking"]?.GetValue<string>() is string thinking && thinking is "events" or "off")
                    Thinking = thinking;
                if (req["temperature"]?.GetValue<double>() is double tempValue)
                    Temperature = tempValue;
                if (req["max_tokens"]?.GetValue<int>() is int maxTokens && maxTokens >= 0)
                    MaxTokens = maxTokens;
                if (req["cpu_max_tokens"]?.GetValue<int>() is int cpuMax && cpuMax >= 0)
                    CpuMaxTokens = cpuMax;
                Log(new JsonObject { ["event"] = "config", ["thinking"] = Thinking, ["temperature"] = Temperature, ["max_tokens"] = MaxTokens,
["cpu_max_tokens"] = CpuMaxTokens, ["sampling_passthrough"] = SamplingPassthrough });
                WriteJson(ctx, 200, BuildStatus());
                return;
            }

            if (ctx.Request.HttpMethod == "POST" && path == "/api/tools/execute")
            {
                // Execução de tools na bridge (dono atual: Web delega p/ cá).
                // Corpo: {name, arguments, timeout?}. Raiz = --workspace ou cwd.
                Interlocked.Increment(ref ToolsCalls);
                var req = JsonNode.Parse(ReadBody(ctx))?.AsObject() ?? new JsonObject();
                string name = req["name"]?.GetValue<string>() ?? "";
                string args = req["arguments"]?.ToJsonString() ?? "{}";
                int timeout = req["timeout"]?.GetValue<int>() ?? 300;
                string root = WorkspaceRoot.Length > 0 ? WorkspaceRoot : Environment.CurrentDirectory;
                // Workspace por chamada (o harness do teste aponta o diretorio da
                // tarefa; o enunciado fica natural, sem paths de coordenacao).
                string reqWs = req["workspace"]?.GetValue<string>() ?? "";
                if (!string.IsNullOrWhiteSpace(reqWs))
                {
                    var extra = new[] { "/mnt", "/media", "/tmp" };
                    if (!Core.Tools.WorkspaceGuard.TryResolve(root, reqWs, out string wsRoot, out string whyWs, extra))
                    {
                        WriteJson(ctx, 200, new JsonObject { ["ok"] = false, ["error"] = whyWs });
                        return;
                    }
                    root = wsRoot;
                }
                // Role do pedido: 'role' explícito ou sufixo do model id (ex.:
                // Gemma4-E4B-Q4-build => build). Sem role = política conservadora
                // (git somente leitura), como o Plan.
                string role = req["role"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(role))
                    RoleProfiles.TryResolve(req["model"]?.GetValue<string>(), Profiles, out role, out _);
                var executor = new Core.Tools.BridgeToolExecutor(root, Upstream, ApiKey, Log, role);
                var result = executor.Execute(name, args, Math.Clamp(timeout, 1, 600));
                if (result.Success)
                {
                    RecordToolCall(name, args);
                    RecordAgentActivity("tool", FormatToolCall(name, args));
                }
                string output = result.Success ? result.Output : "";
                if (ToolOutputLimit > 0 && output.Length > ToolOutputLimit) output = output[..ToolOutputLimit];
                string error = result.Success ? "" : result.Error;
                if (ToolOutputLimit > 0 && error.Length > ToolOutputLimit) error = error[..ToolOutputLimit];
                Log(new JsonObject { ["event"] = "tool_exec", ["tool"] = name, ["ok"] = result.Success });
                WriteJson(ctx, 200, new JsonObject
                {
                    ["ok"] = result.Success, ["output"] = output, ["error"] = error
                });
                return;
            }

            if (ctx.Request.HttpMethod == "GET" && (path == "/health" || path == "/" || path == "/v1/health"))
            {
                WriteJson(ctx, 200, new JsonObject
                {
                    ["status"] = "ok", ["bridge"] = "opencodebridge",
                    ["upstream"] = Upstream, ["model"] = Model, ["thinking"] = Thinking
                });
                return;
            }

            if (ctx.Request.HttpMethod == "POST" && (path == "/v1/responses" || path == "/responses"))
            {
                if (!ClientAuthorized(ctx)) { WriteJson(ctx, 401, new JsonObject { ["error"] = "nao autorizado (Bearer JWT ou ApiKey)" }); return; }
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
                if (!ClientAuthorized(ctx)) { WriteJson(ctx, 401, new JsonObject { ["error"] = "nao autorizado (Bearer JWT ou ApiKey)" }); return; }
                Passthrough(ctx, ReadBody(ctx));
                return;
            }

            // GET de compatibilidade (ex.: /v1/models p/ clientes listarem o modelo)
            if (ctx.Request.HttpMethod == "GET" && (path == "/v1/models" || path == "/models"))
            {
                if (!ClientAuthorized(ctx)) { WriteJson(ctx, 401, new JsonObject { ["error"] = "nao autorizado (Bearer JWT ou ApiKey)" }); return; }
                Passthrough(ctx, "");
                return;
            }

            // Qualquer outra rota (models, embeddings, ...) tambem passa direto
            if (ctx.Request.HttpMethod == "POST")
            {
                if (!ClientAuthorized(ctx)) { WriteJson(ctx, 401, new JsonObject { ["error"] = "nao autorizado (Bearer JWT ou ApiKey)" }); return; }
                Passthrough(ctx, ReadBody(ctx));
                return;
            }

            WriteJson(ctx, 404, new JsonObject { ["error"] = "rota nao suportada: " + path });
        }
        catch (Exception ex)
        {
            modelFailed = true;
            Interlocked.Increment(ref FailedRequests);
            if (path.StartsWith("/v1/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref ModelFailedRequests);
                LastModelActivityMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
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
            // Aborto do upstream que persistiu ao retry: 502 estruturado, nao o
            // 500 opaco (o cliente distingue transporte de erro de modelo e pode
            // orientar o turno seguinte).
            if (ex is RetryNudge.UpstreamAbortException abort)
            {
                Log(new JsonObject
                {
                    ["event"] = "upstream-aborted", ["path"] = path,
                    ["error"] = Truncate(abort.Message, 400)
                });
                try
                {
                    WriteJson(ctx, 502, new JsonObject
                    {
                        ["error"] = new JsonObject
                        {
                            ["type"] = "upstream_aborted",
                            ["message"] = abort.Message
                        }
                    });
                }
                catch { }
                return;
            }
            Log(new JsonObject { ["event"] = "error", ["path"] = path, ["error"] = Truncate(msg, 800) });
            try { WriteJson(ctx, 500, new JsonObject { ["error"] = ex.Message }); } catch { }
        }
        finally
        {
            // Uma resposta de modelo concluida = request /v1/* que nao estourou
            // nem terminou em erro HTTP. Cobre chat, stream, responses e
            // passthrough sem depender de AddMetrics (que so roda em alguns
            // caminhos e deixava o painel travado).
            if (modelRequest && !modelFailed && ctx.Response.StatusCode < 400)
            {
                Interlocked.Increment(ref ModelResponses);
                LastModelActivityMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
        }
    }

    // Foto da configuracao + telemetria p/ quem opera a bridge remotamente.
    /// <summary>Foto de config + telemetria p/ operacao remota.</summary>
    /// <returns>Status JSON.</returns>
    static JsonObject BuildStatus() => new()
    {
        ["status"] = "ok", ["bridge"] = "opencodebridge", ["version"] = Version,
        ["listen"] = Listen, ["port"] = Port,
        ["upstream"] = Upstream, ["model"] = Model, ["thinking"] = Thinking,
["temperature"] = Temperature, ["max_tokens"] = MaxTokens,
        // Temperatura auxiliar do retry: null quando o desvio vem do role (plan).
        ["aux_temp"] = double.IsNaN(AuxTemp) ? null : JsonValue.Create(AuxTemp),
["context_tokens"] = ContextTokens, ["context_guard"] = ContextGuard, ["context_slots"] = ContextSlots,
["mode"] = Mode?.Name ?? "auto", ["tail_scope"] = TailScope,
["main_harness"] = MainHarnessOn, ["main_harness_scope"] = "managed-only", ["main_harness_opencode"] = false, ["harness_tails"] = TailsEnabled, ["tails_managed"] = TailsEnabled, ["tails_opencode"] = false, ["breaker_soft"] = BreakerSoft, ["breaker_hard"] = BreakerHard,
        ["last_client"] = LastClient, ["last_request_ms"] = LastRequestMs,
        ["last_enunciado_step"] = LastEnunciadoStep, ["last_enunciado_ms"] = LastEnunciadoMs,
["api_key"] = string.IsNullOrEmpty(ApiKey) ? "" : "***",
        ["passthrough_responses"] = PassthroughResponses,
        ["tool_strategy"] = ToolStrategy,
        ["tool_shim"] = ToolShimMode, ["tool_shim_max_string"] = ToolShimMaxString,
        ["tool_shim_min_calls"] = ToolShimMinCalls,
        ["cpu_max_tokens"] = CpuMaxTokens, ["coerced_max_tokens"] = CpuMaxTokens,
        ["upstream_parser"] = UpstreamParser,
        ["anti_repeat"] = AntiRepeat, ["anti_repeat_abort"] = AntiRepeatAbort,
        ["anti_repeat_window"] = AntiRepeatWindow,
        ["anti_repeat_args_window"] = AntiRepeatArgsWindow,
        ["sampling_passthrough"] = SamplingPassthrough,
        ["diagnostic_level"] = DiagnosticLevel,
        ["config_groups"] = ConfigGroups(),
        ["legacy_harness"] = LegacyHarness, ["stream_per_delta"] = StreamPerDelta,
        ["bridge_managed"] = BridgeManagedAll ? "all" : "direct-only",
        ["uptime_seconds"] = (long)(DateTime.UtcNow - StartedAt).TotalSeconds,
        ["total_requests"] = TotalRequests, ["failed_requests"] = FailedRequests,
        ["model_requests"] = ModelRequests, ["model_responses"] = ModelResponses,
        ["model_failed_requests"] = ModelFailedRequests
    };

    /// <summary>Le o corpo da requisicao como texto UTF-8.</summary>
    /// <param name="ctx">Contexto HTTP.</param>
    /// <returns>Corpo bruto.</returns>
    static string ReadBody(HttpListenerContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding ?? Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // Auth do cliente na bridge: Bearer JWT valido OU ApiKey legada.
    // ApiKey vazia = somente JWT. Sem credencial = 401.
    static bool ClientAuthorized(HttpListenerContext ctx)
    {
        string? h = ctx.Request.Headers["Authorization"];
        if (!string.IsNullOrWhiteSpace(h) && h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            string tok = h["Bearer ".Length..].Trim();
            if (Auth.TryValidate(tok, out _, out _)) return true;
            if (!string.IsNullOrEmpty(ApiKey) && tok == ApiKey) return true;
        }
        return false;
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
        var startedAt = DateTime.UtcNow;
        // opencode 1.x fala chat/completions direto (sem /v1/responses): o id
        // com sufixo de perfil (-plan/-build/-orch) precisa virar o Model
        // servido, senao o vLLM responde 404. Temperatura do perfil entra
        // quando o cliente nao informa nenhuma. llama.cpp aceita qualquer
        // id, mas normalizar aqui mantem o log/telemetria coerentes.
        string outBody = body;
        // Model id PEDIDO (com sufixo de role), lido fora do try: o desvio de
        // temperatura do retry e o aborto do upstream precisam dele depois.
        string reqModelOut = "";
        try
        {
            var node = JsonNode.Parse(body)?.AsObject();
            // Modelo/role originais ANTES do rewrite; o main harness usa o role
            // para preservar o system do subagente e sugerir segmentacao.
            string reqModel = node?["model"]?.GetValue<string>() ?? "";
            reqModelOut = reqModel;
            string reqRole = RoleOfModel(reqModel);
            // Role resolvido ANTES do rewrite do model (o upstream usa o id servido,
            // sem sufixo): decide a precedencia do desvio de temperatura no retry.
            bool reqHasRole = reqRole.Length > 0;
            // Marca opencode ANTES do rewrite (model com sufixo = tubo puro).
            bool opencodeIn = node is not null && IsOpencodeModel(reqModel);
            string profThinkMid = "";
            if (node is not null && node["model"]?.GetValue<string>() is string mid && mid != Model)
            {
                bool hasProfile = TryProfile(mid, out double profTemp, out profThinkMid);
                if (hasProfile || CoerceFor())
                {
                    node["model"] = Model;
                    if (node["messages"] is JsonArray chatMessages)
                    {
                        foreach (var m in chatMessages)
                        {
                            if (m is JsonObject mo && mo["role"]?.GetValue<string>() is string mrole && (mrole == "system" || mrole == "developer"))
                            {
                                if (mo["content"] is JsonValue mcv && mcv.TryGetValue<string>(out string? mtext) && !string.IsNullOrWhiteSpace(mtext))
                                    mo["content"] = LegacyHarness ? WithSystemGuardrail(mtext, QwenStd) : mtext;
                                else if (mo["content"] is JsonArray mparts)
                                {
                                    foreach (var p in mparts)
                                    {
                                        if (p is JsonObject po && po["text"] is JsonValue ptv && ptv.TryGetValue<string>(out string? ptext) && !string.IsNullOrWhiteSpace(ptext))
                                        { po["text"] = LegacyHarness ? WithSystemGuardrail(ptext, QwenStd) : ptext; break; }
                                    }
                                }
                            }
                        }
                    }
                    if (node["temperature"] is null && !double.IsNaN(profTemp))
                        node["temperature"] = profTemp;
                    // Linha CPU: o 3B entra em loop de filler ([END_OF_TEXT])
                    // quando divaga — stop dedicado (só aqui; Responses/chat
                    // nativo do vLLM jamais recebe stop automático).
                    if (node["stop"] is null && CoerceFor())
                        node["stop"] = new JsonArray("[END_OF_TEXT]");
                    outBody = node.ToJsonString(SerOpts);
                }
            }
            // Main harness no caminho chat (o WAR usa /v1/chat/completions
            // direto). So na raia bridge; opencode se auto-gerencia. O array
            // `tools` NAO e tocado: o schema continua sendo a fonte do request.
            // mhChat: prova para o portao de fluxo (PENDINGS P1) de que ESTE
            // request passou pelo harness — registrado em GET /api/test/last-system.
            bool mhChat = false;
            if (node is not null && node["messages"] is JsonArray chatHarness
                && BridgeLane.Value == true)
            {
                // Tools barradas ao role vao no system (o array `tools` continua
                // completo: blacklist bloqueia a chamada, nao esconde a tool).
                bool applied = MainHarness.Apply(chatHarness, true, reqRole, BannedFor(reqModel));
                mhChat = applied;
                if (applied)
                {
                    outBody = node.ToJsonString(SerOpts);
                    if (Diagnostic) Log(new JsonObject
                    {
                        ["event"] = "main-harness",
                        ["path"] = "chat",
                        ["model"] = reqModel,
                        ["role"] = reqRole,
                        ["chars"] = MainHarness.Text.Length,
                        ["tools"] = node["tools"] is JsonArray ta ? ta.Count : 0
                    });
                }
            }
            // Temperatura no repasse: quem esta conectado manda. Se o corpo ja
            // traz temperature, passa reto (o numero do cliente, do role que ele
            // configura, chega intacto); so injetamos quando falta — ai vale o
            // perfil do role e, sem perfil, o global 0.3. --profile-temp-wins
            // forca o perfil por cima do cliente (opt-in).
            if (node is not null)
            {
                double? reqT2 = node["temperature"]?.GetValue<double>();
                double profT2 = double.NaN;
                bool hasProf2 = node["model"]?.GetValue<string>() is string tmid2 && TryProfile(tmid2, out profT2, out _);
                if (reqT2 is null)
                {
                    (double effTemp, string src) = Sampling.Temperature(reqT2, hasProf2, profT2, Temperature);
                    node["temperature"] = effTemp;
                    outBody = node.ToJsonString(SerOpts);
                    if (Diagnostic) Log(new JsonObject
                    {
                        ["event"] = "temp-inject",
                        ["temp"] = effTemp,
                        ["source"] = src,
                        ["model"] = node["model"]?.GetValue<string>() ?? Model
                    });
                }
            }
            // Turno de RETRY (nudge de reparo/breaker no historico): sobe a
            // temperatura para a do role de desvio (plan). Reenviar o mesmo
            // corpo na mesma temperatura tende a repetir a mesma saida.
            if (node is not null && node["messages"] is JsonArray retryMsgs
                && RetryNudge.HasRetryHint(retryMsgs))
            {
                double curRetry = node["temperature"]?.GetValue<double>() ?? Temperature;
                double detour = Sampling.ForRetry(curRetry,
                    Sampling.DetourReference(reqHasRole, Profiles, AuxTemp));
                if (Math.Abs(detour - curRetry) > 0.0001)
                {
                    node["temperature"] = detour;
                    outBody = node.ToJsonString(SerOpts);
                    if (Diagnostic) Log(new JsonObject
                    {
                        ["event"] = "temp-retry",
                        ["from"] = curRetry,
                        ["to"] = detour,
                        ["model"] = node["model"]?.GetValue<string>() ?? Model
                    });
                }
            }
            // Formula por caminho (--tail-scope): "all" injeta tambem no opencode.
            // Sem switch mestre: cada cauda se aplica por estar configurada
            // (appsettings ou --harness-behavior-tail / --harness-tools-tail).
            if (node is not null
                && node["messages"] is JsonArray fm)
            {
                string fmodel = node["model"]?.GetValue<string>() ?? "";
                bool fHasTools = node["tools"] is JsonArray ftools && ftools.Count > 0;
                foreach (var m in fm)
                {
                    if (m is JsonObject mo && mo["role"]?.GetValue<string>() is "system" or "developer"
                        && mo["content"] is JsonValue mcv && mcv.TryGetValue<string>(out string? mtext))
                    {
                        mo["content"] = WithFormula(mtext, fmodel, fHasTools,
                            ToolNamesOf(node["tools"] as JsonArray));
                        break;
                    }
                }
                outBody = node.ToJsonString(SerOpts);
                string sysSentChat = fm.OfType<JsonObject>()
                    .FirstOrDefault(x => x["role"]?.GetValue<string>() is "system" or "developer")
                    ?["content"]?.GetValue<string>() ?? "";
                if (sysSentChat.Length > 0) RecordSystemSent("chat", reqRole, fmodel, sysSentChat,
                    node["temperature"]?.GetValue<double>(), TryProfile(fmodel, out _, out _),
                    mhChat, fHasTools);
            }
            // QwenStandard: tools OpenAI -> Alibaba + re-serializa p/ upstream.
            // Sem tool_choice explícito: força "required" (VL só emite calls assim).
            if (QwenStd && node is not null && node["tools"] is JsonArray qtools && qtools.Count > 0)
            {
                int nq = QwenStandard.TranslateToolsForUpstream(qtools);
                if (node["tool_choice"] is null) node["tool_choice"] = "required";
                outBody = node.ToJsonString(SerOpts);
                if (Diagnostic) Log(new JsonObject { ["event"] = "qwen-tools", ["count"] = nq });
            }
            // Thinking default da linha (--thinking): sem o kwargs o template
            // O template pode ligar thinking sozinho; vale p/ todo cliente sem flag propria.
            if (node is not null && node["chat_template_kwargs"] is null)
            {
                bool wantThinking = profThinkMid.Length > 0 ? profThinkMid == "events" : Thinking == "events";
                node["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = wantThinking };
                outBody = node.ToJsonString(SerOpts);
            }
            // Convencao do operador: cap de saida TAMBEM no repasse
            // /v1/chat/completions. Sem isso, cliente sem max_tokens deixava o
            // llama gerar ate o EOS (divagacao de 40k+ tokens, ~26 min/turno).
            // O valor do cliente (ex.: belts, 4096) prevalece; so injeta quando
            // ausente. cpuLine escolhe o teto (GPU 16k x CPU 8k).
            if (node is not null && node["max_tokens"] is null && node["max_completion_tokens"] is null)
            {
                bool cpuLinePt = CoerceFor();
                int capPt = cpuLinePt ? CpuMaxTokens : MaxTokens;
                if (capPt > 0)
                {
                    node["max_tokens"] = capPt;
                    outBody = node.ToJsonString(SerOpts);
                }
            }
            // tools quebram o parser do template 14B (400); vale p/ qualquer
            // model id, com ou sem sufixo de perfil. Exceção permanente.
            if (node is not null && node["tools"] is JsonArray toolsArr)
            {
                string beforeStrip = toolsArr.ToJsonString(SerOpts);
                ApplyPatternException(toolsArr, "chat");
                // Code Mode do opencode: sem sandbox JS na bridge, `execute` sai
                // do catalogo (o modelo so pode escolher tools diretas).
                ToolSanitizer.StripExecuteTools(toolsArr, ToolStrategy, "chat", Log);
                ToolSanitizer.NeutralizeCodeMode(node, ToolStrategy, Log);
                ToolHelp.ApplyTo(toolsArr);
                if (toolsArr.ToJsonString(SerOpts) != beforeStrip)
                    outBody = node.ToJsonString(SerOpts);
            }
        }
        catch { outBody = body; }
        // Preserva o verbo original (GET p/ models, POST p/ completions).
        // Linha CPU (llama sem parser server-side): forca upstream sem stream
        // p/ coagir o texto em tool_calls e devolver SSE sintetizado — senao o
        // opencode 1.x recebe texto puro, nenhuma tool executa e o agente entra
        // em loop re-planejando. Linha GPU segue em streaming real.
        bool isChat = ctx.Request.Url!.AbsolutePath.Contains("chat/completions");
        // ToolShim generico: o llama recusa "grammar + tools" ("Cannot use
        // custom grammar constraints with tools"). O shim remove tools, injeta
        // a declaracao no system e forca um envelope JSON via grammar; a
        // resposta volta convertida para tool_calls OpenAI. Tudo derivado do
        // array tools do request (nenhum cenario hardcoded).
        bool shimActive = false;
        bool shimWantStream = false;
        if (ToolShimMode == "json" && isChat)
        {
            try
            {
                var sn = JsonNode.Parse(outBody)?.AsObject();
                shimWantStream = sn?["stream"]?.GetValue<bool>() ?? false;
                string? shimReason = null;
                if (sn is not null && ToolShim.Apply(sn, ToolShimMaxString, ToolShimMinCalls, MaxTokens, out shimReason))
                {
                    outBody = sn.ToJsonString(SerOpts);
                    shimActive = true;
                    if (Diagnostic) Log(new JsonObject { ["event"] = "tool-shim", ["mode"] = "json", ["max_string"] = ToolShimMaxString });
                }
                else if (Diagnostic && shimReason is not null)
                    Log(new JsonObject { ["event"] = "tool-shim-skip", ["reason"] = shimReason });
            }
            catch { }
        }
        // Coerce quando o pedido traz tools e o upstream nao parseia de volta
        // (coder/deepseek via llama): força non-stream + sintetiza tool_calls.
        bool toolsAsked = false;
        try { toolsAsked = JsonNode.Parse(outBody)?.AsObject() is JsonObject ob && ob["tools"] is JsonArray ta && ta.Count > 0; }
        catch { }
        // Coerção de LINHA: so quando o upstream nao tem parser proprio
        // (--upstream-parser none) ou quando o operador pediu (--coerce-text-tools).
        // Antes isto vinha embrulhado em LegacyHarness + um "toolsAsked => assume
        // que o upstream nao parseia", heuristica que ligava coerção por acidente.
        // Agora e CAPACIDADE explicita, sem flag-deus e sem nome de modelo.
        bool coerceLine = CoerceFor() && isChat
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
        WithAuth(forward, ctx);
        if (ctx.Request.HttpMethod is "POST" or "PUT" or "PATCH")
            forward.Content = new StringContent(outBody, Encoding.UTF8, "application/json");
        // Anti-repeat com abort em stream: o cliente pediu JSON unico, mas
        // consumimos o upstream em SSE para o detector cortar o loop DURANTE a
        // geracao. O retry pos-hoc pagava os 16.384 tokens (~375 s) inteiros
        // antes de trocar o conteudo.
        if (isChat && AntiRepeat && AntiRepeatAbort && !coerceLine && !shimActive
            && ctx.Request.HttpMethod is "POST" or "PUT" or "PATCH"
            && !BodyWantsStream(outBody) && TryStreamAbortChat(ctx, outBody, startedAt))
            return;
        // Linha CPU + stream: o opencode TUI aborta em 300s sem headers e o
        // llama leva minutos no prefill — descarrega headers + heartbeat AGORA
        // (comentário SSE, ignorado pelo parser) e só então chama o upstream.
        bool headersSent = false;
        if ((coerceLine && wantStream) || (shimActive && shimWantStream))
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.OutputStream.Write(new UTF8Encoding(false).GetBytes(": bridge working\n\n"));
            ctx.Response.OutputStream.Flush();
            headersSent = true;
        }
        using var upstreamResponse = SendWithAbortRetry(ctx, forward, outBody,
            HttpCompletionOption.ResponseHeadersRead, reqModelOut);
        if (coerceLine && upstreamResponse.IsSuccessStatusCode)
        {
            CoercedChatResponse(ctx, upstreamResponse, wantStream, headersSent, AnnouncedTools(outBody), startedAt);
            return;
        }
        if (shimActive)
        {
            ShimChatResponse(ctx, upstreamResponse, outBody, shimWantStream, headersSent, startedAt);
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
            var sseText = new StringBuilder();
            var rawChatSse = new StringBuilder();
            JsonNode? sseUsage = null, sseTimings = null;
            int sseChecked = 0;
            bool sseTrip = false;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Contains("\"reasoning\":"))
                    line = line.Replace("\"reasoning\":", "\"reasoning_content\":");
                if (QwenStd) line = QwenStandard.TranslateChunkNames(line);
                writer.WriteLine(line);
                if (Diagnostic) rawChatSse.AppendLine(line);
                // Sonda stream: cobre chat E completions legado (ambos passam por
                // aqui). So observa e registra — o fluxo NAO para; a contencao
                // silenciosa mora no loop da Web.
                if (!sseTrip && line.StartsWith("data:"))
                {
                    try
                    {
                        string data = line["data:".Length..].Trim();
                        if (data is not ("[DONE]" or ""))
                        {
                            var node = JsonNode.Parse(data);
                            WalkText(node, sseText);
                            // Metrica de t/s: o chunk final do llama traz usage/
                            // timings. Guardar o ultimo visto e somar no fim do SSE.
                            if (node?["usage"] is JsonNode u) sseUsage = u.DeepClone();
                            if (node?["timings"] is JsonNode t) sseTimings = t.DeepClone();
                        }
                    }
                    catch { }
                    if (sseText.Length - sseChecked >= 512)
                    {
                        sseChecked = sseText.Length;
                        string whereSse = isChat ? "bypass-chat-stream" : "bypass-completions-stream";
                        if (CheckRepeat(sseText.ToString(), whereSse))
                        {
                            sseTrip = true;
                            Log(new JsonObject { ["event"] = "anti-repeat", ["where"] = whereSse, ["window"] = AntiRepeatWindow, ["chars"] = sseText.Length });
                        }
                    }
                }
            }
            // Avaliacao de fecho: stream curto nunca atinge o passo de 512.
            // Sem break/DONE: o fluxo segue ate o fim; a Web conteve em silencio.
            if (!sseTrip && sseText.Length > 0 && CheckRepeat(sseText.ToString(), isChat ? "bypass-chat-stream" : "bypass-completions-stream"))
            {
                sseTrip = true;
                Log(new JsonObject { ["event"] = "anti-repeat", ["where"] = isChat ? "bypass-chat-stream" : "bypass-completions-stream", ["window"] = AntiRepeatWindow, ["chars"] = sseText.Length, ["late"] = true });
            }
            if (sseText.Length > 0) RecordAgentActivity("assistant", sseText.ToString());
            // t/s do stream: usa usage/timings do ultimo chunk (geracao real).
            RecordChatUsage(sseUsage, sseTimings, (DateTime.UtcNow - startedAt).TotalSeconds);
            // Sonda traffic FULL (só com DIAGNOSTICS; chat stream: integros).
            if (Diagnostic)
            {
                string dump = rawChatSse.ToString();
                var traffic = new JsonObject { ["event"] = "traffic", ["mode"] = "chat-stream", ["model"] = Model, ["req_len"] = outBody.Length, ["req"] = outBody, ["res_len"] = dump.Length, ["res"] = dump };
                AddPerf(traffic, startedAt, dump, outBody);
                Log(traffic);
                // Sonda compacta: paridade com o caminho /v1/responses (t/s sem
                // precisar varrer o dump de trafego). So com DIAGNOSTICS.
                var perf = new JsonObject { ["event"] = "response", ["mode"] = "chat-stream", ["model"] = Model, ["chars"] = sseText.Length };
                AddPerf(perf, startedAt, sseText.ToString(), outBody);
                Log(perf);
            }
        }
        else
        {
            // JSON unico: mesma renomeacao no corpo completo.
            using var reader = new StreamReader(upstreamStream, new UTF8Encoding(false));
            string json = reader.ReadToEnd();
            if (json.Contains("\"reasoning\":"))
                json = json.Replace("\"reasoning\":", "\"reasoning_content\":");
            // Guarda de ctx: prompt atingiu o limiar -> 413 pedindo compactacao.
            if (isChat && ContextGuardTripped(json, out int cgPrompt, out double cgFrac))
            {
                Log(new JsonObject { ["event"] = "context-guard", ["where"] = "bypass-chat", ["prompt_tokens"] = cgPrompt, ["fraction"] = Math.Round(cgFrac, 4) });
                WriteJson(ctx, 413, new JsonObject
                {
                    ["error"] = new JsonObject
                    {
                        ["message"] = $"Contexto em {cgFrac:P0} ({cgPrompt} tokens de {ContextTokens}). Compacte a sessao e tente de novo.",
                        ["type"] = "invalid_request_error",
                        ["code"] = "context_compact_required",
                    }
                });
                return;
            }
            // Sonda bypass-chat: repeticao no corpo = 1 retry de contencao.
            // Cobre tambem /v1/completions legado (mesmo corpo, chave "text").
            if (CheckRepeat(ExtractChatText(json), isChat ? "bypass-chat" : "bypass-completions"))
            {
                Log(new JsonObject { ["event"] = "anti-repeat", ["where"] = isChat ? "bypass-chat" : "bypass-completions", ["window"] = AntiRepeatWindow });
                // Retry governado pelo ANTI-REPEAT (--anti-repeat-retry), nao pelo
                // harness legado: com --legacy-harness 0 a deteccao virava so sonda
                // e o loop ia inteiro ate o max_tokens (medido: 16.384 tok / 378 s).
                if (AntiRepeatRetry)
                {
                    string? second = RetryBypassChat(outBody, ctx);
                    if (second is not null)
                    {
                        json = second;
                        if (json.Contains("\"reasoning\":"))
                            json = json.Replace("\"reasoning\":", "\"reasoning_content\":");
                        Log(new JsonObject { ["event"] = "anti-repeat-retry", ["where"] = "bypass-chat" });
                    }
                }
            }
            // ToolsCoercion no bypass: pedido com tools sem tool_calls = sintetiza.
            // Inibivel (--harness-tails 0): caminho limpo p/ harness opencode.
            if (isChat && toolsAsked && CoerceFor())
            {
                try
                {
                    var root = JsonNode.Parse(json)?.AsObject();
                    if (root is not null)
                    {
                        int n = CoerceChatCalls(root, AnnouncedTools(outBody));
                        if (n > 0)
                        {
                            json = root.ToJsonString(SerOpts);
                            Log(new JsonObject { ["event"] = "coerced-chat", ["where"] = "bypass-chat", ["count"] = n });
                        }
                    }
                }
                catch { }
            }
            // QwenStandard: tool_calls Alibaba -> OpenAI p/ o cliente.
            if (QwenStd)
            {
                try
                {
                    var qroot = JsonNode.Parse(json)?.AsObject();
                    if (qroot is not null)
                    {
                        int nq = QwenStandard.TranslateCompletionForClient(qroot);
                        if (nq > 0)
                        {
                            json = qroot.ToJsonString(SerOpts);
                            if (Diagnostic) Log(new JsonObject { ["event"] = "qwen-tools-back", ["count"] = nq });
                        }
                    }
                }
                catch { }
            }
            RecordChatActivity(json);
            // t/s do corpo unico: usage + timings do llama (sem stream).
            try
            {
                var root = JsonNode.Parse(json);
                RecordChatUsage(root?["usage"], root?["timings"], (DateTime.UtcNow - startedAt).TotalSeconds);
            }
            catch { }
            var bytes = new UTF8Encoding(false).GetBytes(json);
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            // Sonda traffic FULL (só com DIAGNOSTICS; chat sem stream: integros).
            if (Diagnostic)
            {
                var traffic = new JsonObject { ["event"] = "traffic", ["mode"] = "chat", ["model"] = Model, ["req_len"] = outBody.Length, ["req"] = outBody, ["res_len"] = json.Length, ["res"] = json };
                AddPerf(traffic, startedAt, json, outBody);
                Log(traffic);
                // Sonda compacta: paridade com o caminho /v1/responses.
                var perf = new JsonObject { ["event"] = "response", ["mode"] = "chat", ["model"] = Model, ["chars"] = json.Length };
                AddPerf(perf, startedAt, json, outBody);
                Log(perf);
            }
        }
        ctx.Response.Close();
    }


    /// <summary>Resposta chat sem parser server-side: coage texto em tool_calls e devolve JSON ou SSE sintetizado.</summary>
    static void CoercedChatResponse(HttpListenerContext ctx, HttpResponseMessage upstreamResponse, bool wantStream, bool headersSent = false, List<string>? known = null, DateTime startedAt = default)
    {
        string json;
        using (var reader = new StreamReader(upstreamResponse.Content.ReadAsStream(), new UTF8Encoding(false)))
            json = reader.ReadToEnd();
        int coerced = 0;
        try
        {
            var root = JsonNode.Parse(json)?.AsObject();
            if (root is not null)
            {
                coerced = CoerceChatCalls(root, known);
                if (coerced > 0) json = root.ToJsonString(SerOpts);
            }
        }
        catch { /* corpo inesperado: devolve cru */ }
        Log(new JsonObject { ["event"] = "coerced-chat", ["model"] = Model, ["count"] = coerced });
        // Sonda traffic FULL (só com DIAGNOSTICS): resposta integra.
        if (Diagnostic)
        {
            var traffic = new JsonObject { ["event"] = "traffic", ["mode"] = "coerced", ["model"] = Model, ["res_len"] = json.Length, ["res"] = json };
            AddPerf(traffic, startedAt == default ? DateTime.UtcNow : startedAt, json);
            Log(traffic);
        }
        if (!wantStream)
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            var bytes = new UTF8Encoding(false).GetBytes(json);
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
            return;
        }
        WriteChatSse(ctx, JsonNode.Parse(json)?.AsObject() ?? new JsonObject(), headersSent);
    }

    /// <summary>Sintetiza um chunk SSE chat-completions (role/content/tool_calls/usage) e fecha com [DONE].</summary>
    static void WriteChatSse(HttpListenerContext ctx, JsonObject root, bool headersSent)
    {
        if (!headersSent)
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
        }
        using var writer = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false)) { AutoFlush = true };
        try
        {
            var msg = root["choices"]?.AsArray().FirstOrDefault();
            string? finish = msg?["finish_reason"]?.GetValue<string>();
            var m = msg?["message"]?.AsObject();
            var delta = new JsonObject { ["role"] = "assistant" };
            if (m?["content"]?.GetValue<string>() is string c && c.Length > 0) delta["content"] = c;
            if (m?["tool_calls"] is JsonArray tc) delta["tool_calls"] = (JsonNode?)JsonNode.Parse(tc.ToJsonString(SerOpts));
            var chunk = new JsonObject
            {
                ["id"] = root["id"]?.GetValue<string>() ?? ("chatcmpl-" + Guid.NewGuid().ToString("N")[..8]),
                ["object"] = "chat.completion.chunk",
                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["model"] = Model,
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish
                })
            };
            if (root["usage"] is JsonObject usage) chunk["usage"] = usage.DeepClone();
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
    /// <param name="thinking">Modo thinking efetivo.</param>
    /// <param name="toolCount">No. de tools convertidas.</param>
    /// <returns>Body p/ /v1/chat/completions.</returns>
    static JsonObject BuildChatRequest(JsonObject req, out string requestedModel, out double temperature, out string thinking, out int toolCount)
        => BuildChatRequest(req, out requestedModel, out temperature, out thinking, out toolCount, out _, out _, out _);

    /// <summary>Tradução completa com procedência da temperatura e veredito do breaker.</summary>
    /// <param name="req">Request Responses.</param>
    /// <param name="requestedModel">Model id do cliente (eco).</param>
    /// <param name="temperature">Temperatura efetiva.</param>
    /// <param name="thinking">Modo thinking efetivo.</param>
    /// <param name="toolCount">No. de tools convertidas.</param>
    /// <param name="tempSource">"profile" | "request" | "global".</param>
    /// <param name="breakerTrip">Aviso quando o breaker encerrou o turno (null = segue).</param>
    /// <param name="breakerState">Estado do breaker: "none" | "nudge" | "trip".</param>
    /// <returns>Body p/ /v1/chat/completions.</returns>
    static JsonObject BuildChatRequest(JsonObject req, out string requestedModel, out double temperature,
        out string thinking, out int toolCount, out string tempSource, out string? breakerTrip, out string breakerState)
    {
        breakerTrip = null;
        breakerState = "none";
        // O opencode manda "openai/Gemma4-E4B-Q4" mas o Llama so conhece o id
        // servido ("Gemma4-E4B-Q4") e responde 404 p/ outro id. Por isso o
        // upstream SEMPRE usa o Model configurado; o id original so volta
        // como eco no campo "model" da resposta.
        requestedModel = req["model"]?.GetValue<string>() ?? Model;
        // Perfil (-plan/-build): temperatura+thinking do perfil.
        bool hasProfile = TryProfile(requestedModel, out double profTemp, out string profThink);
        thinking = hasProfile && !string.IsNullOrEmpty(profThink) ? profThink : Thinking;
        // ThinkingOffRoles: o operador manda desligar por NOME de role, acima do perfil.
        if (ThinkingOffRoles.Contains(RoleOfModel(requestedModel))) thinking = "off";
        // Temperatura: o perfil do role e a fonte de verdade quando o model id
        // tem sufixo (-build = 0.1). O cliente as vezes reenvia a temperatura do
        // modelo BASE (0.3/0.5) e o role rodava quente — foi o que produziu os
        // identificadores corrompidos da sessao de 2026-10-23. --request-temp-wins
        // restaura a prioridade do cliente.
        double? reqTemp = req["temperature"]?.GetValue<double>();
        // Mesma regra do /v1/chat, em if: o cliente manda; sem ele, o perfil do
        // role; sem perfil, o global 0.3. --profile-temp-wins inverte o 1o par.
        (temperature, tempSource) = Sampling.Temperature(reqTemp, hasProfile, profTemp, Temperature);

        // Converte a conversa Responses -> messages do formato chat
        var messages = InputToMessages(req["input"], req["instructions"], hasProfile,
            new TranslationOptions(LegacyHarness, QwenStd, ToolOutputLimit, RiskScanAnswers));
        // Main harness: BASE do caminho gerido (cliente != CLI do opencode).
        // O discriminador e o CLIENTE (igual as tails), nunca o sufixo do modelo:
        // o WAR usa <modelo>-build e ficaria sem base.
        bool mainHarness = MainHarness.Apply(messages, BridgeLane.Value == true, RoleOfModel(requestedModel),
            BannedFor(requestedModel));
        // Turno de RETRY (nudge de reparo/breaker no historico): temperatura de
        // desvio lida do role plan, nao um literal.
        if (RetryNudge.HasRetryHint(messages))
        {
            double detour = Sampling.ForRetry(temperature,
                Sampling.DetourReference(hasProfile, Profiles, AuxTemp));
            if (Math.Abs(detour - temperature) > 0.0001)
            {
                temperature = detour;
                tempSource = "retry";
            }
        }
        if (Diagnostic && mainHarness)
            Log(new JsonObject
            {
                ["event"] = "main-harness",
                ["path"] = "responses",
                ["model"] = requestedModel,
                ["role"] = RoleOfModel(requestedModel),
                ["chars"] = MainHarness.Text.Length,
                ["tails"] = TailsOn(requestedModel)
            });
        // Caudas por peca, penduradas no system que InputToMessages ja consolida
        // (instructions + os system do meio). O gate decide por caminho
        // (--tail-scope): "all" injeta tambem no opencode, "managed" so no
        // cliente direto/Web. Sem system nao ha onde pendurar: nao inventamos.
        // Tails: UNICO incremento sobre a base (podem coexistir com ela).
        bool tails = TailsOn(requestedModel);
        bool reqHasTools = req["tools"] is JsonArray rta && rta.Count > 0;
        if (tails)
        {
            if (messages.Count > 0 && messages[0] is JsonObject sys0
                && sys0["role"]?.GetValue<string>() == "system"
                && sys0["content"] is JsonValue sysv && sysv.TryGetValue<string>(out string? syst))
                sys0["content"] = WithFormula(syst, requestedModel, reqHasTools,
                    ToolNamesOf(req["tools"] as JsonArray));
            else
                // Cliente sem system/instructions (WAR/SDK): a formula do harness
                // precisa existir como system; antes a tail era descartada e o
                // modelo rodava sem DATA/GIT/EDIT/GLOB.
                messages.Insert(0, new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = WithFormula("", requestedModel, reqHasTools,
                        ToolNamesOf(req["tools"] as JsonArray))
                });
        }
        string sysSentR = messages.OfType<JsonObject>()
            .FirstOrDefault(x => x["role"]?.GetValue<string>() is "system" or "developer")
            ?["content"]?.GetValue<string>() ?? "";
        if (sysSentR.Length > 0) RecordSystemSent("responses", RoleOfModel(requestedModel), requestedModel, sysSentR,
            temperature, hasProfile, mainHarness, reqHasTools);
        if (Diagnostic)
            Log(new JsonObject
            {
                ["event"] = "tails",
                ["scope"] = TailScope,
                ["on"] = tails,
                ["model"] = requestedModel,
                ["temperature"] = temperature,
                ["temperature_source"] = tempSource,
                ["request_temperature"] = reqTemp,
                ["has_system"] = messages.Count > 0 && messages[0]?["role"]?.GetValue<string>() == "system",
                ["attached"] = messages.Count > 0 && messages[0] is JsonObject s1
                               && s1["content"] is JsonValue s1v
                               && (s1v.TryGetValue<string>(out string? t1)
                                   ? HarnessTails.HasToolsTail(t1, ToolsTailEffective)
                                   : false),
                ["behavior_tail"] = BehaviorTailEnabled,
                ["tools_tail"] = ToolsTailEnabled,
                ["behavior_chars"] = BehaviorTailEffective.Length,
                ["tools_chars"] = ToolsTailEffective.Length
            });
        // Breaker de loop agentico: le o historico (function_call + resultado) e
        // decide nudge/trip. Trip = nao chama o modelo (o turno esta perdido).
        if (BreakerSoft > 0)
        {
            var loopGuard = new ToolsBreaker(BreakerSoft, BreakerHard);
            var verdict = loopGuard.Evaluate(messages);
            if (verdict.Verdict != BreakerVerdict.None)
            {
                Log(new JsonObject
                {
                    ["event"] = "tool-error-breaker", ["model"] = requestedModel,
                    ["verdict"] = verdict.Verdict.ToString().ToLowerInvariant(),
                    ["streak"] = verdict.Streak, ["distinct_targets"] = verdict.DistinctTargets,
                    ["soft"] = BreakerSoft, ["hard"] = BreakerHard
                });
                Metrics.BreakerTrip(verdict.Verdict.ToString().ToLowerInvariant());
                breakerState = verdict.Verdict.ToString().ToLowerInvariant();
                if (verdict.Verdict == BreakerVerdict.Nudge)
                    loopGuard.ApplyNudge(messages, verdict.Notice);
                else
                    breakerTrip = verdict.Notice;
            }
        }
        // Converte as tools Responses (formato "chato") -> formato chat
        var tools = ToolsToChat(req["tools"]);
        // Sonda: tool anunciada que nao chegou ao chat (perda silenciosa).
        if (Diagnostic && req["tools"] is JsonArray askedArr)
        {
            var asked = new List<string>();
            foreach (var e in askedArr)
                if (e is JsonObject eo && eo["type"]?.GetValue<string>() != "namespace")
                    if (eo["function"]?["name"]?.GetValue<string>() is string fn) asked.Add(fn);
                    else if (eo["name"]?.GetValue<string>() is string en) asked.Add(en);
            var got = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in ToolNames(tools))
                if (g?.GetValue<string>() is string gn) got.Add(gn);
            var lostNames = new JsonArray();
            foreach (var a in asked) if (!got.Contains(a)) lostNames.Add(a);
            if (lostNames.Count > 0)
            {
                var askedNames = new JsonArray();
                foreach (var a in asked) askedNames.Add(a);
                Log(new JsonObject
                {
                    ["event"] = "tools-lost",
                    ["asked"] = askedNames,
                    ["got"] = ToolNames(tools),
                    ["lost"] = lostNames
                });
            }
        }
        // Allowlist das tools pedidas: a coercao de tool call narrada depende dela
        // (sem lista, so passaria o catalogo RealTools, que nao cobre write/edit/glob).
        ChatToResponsesAnnounced = AnnouncedTools(req["tools"] as JsonArray);
        // Exceção permanente do pattern (sempre ligada, só este caso).
        ApplyPatternException(tools, "responses");
        ToolSanitizer.StripExecuteTools(tools, ToolStrategy, "responses", Log);
        toolCount = tools?.Count ?? 0;

        // Monta o body do /v1/chat/completions
        // Sem teto por padrao (MaxTokens/CpuMaxTokens = 0): a infra (llama
        // -c/--n-predict) e o cliente (max_tokens) controlam o tamanho da saida.
        // So ha clamp quando o operador define >0 via appsettings/--max-tokens.
        // A linha e COAGIDA quando o upstream nao tem parser proprio
        // (--upstream-parser none) ou o operador pediu --coerce-text-tools:
        // nesses casos vale o teto da linha coagida e o stop proprio. Nao ha
        // mais inferencia por id de modelo ("cpu"): a capacidade define a linha.
        bool coercedLine = CoerceFor();
        int cap = coercedLine ? CpuMaxTokens : MaxTokens;   // 0 = sem teto
        // O pedido do cliente NAO limita a saida: o bridge ignora
        // max_tokens/max_output_tokens do corpo e usa apenas o teto do operador.
        // Motivo: Gemma 4 com thinking ON gasta centenas de tokens em raciocinio;
        // um teto baixo do cliente (ex.: 100) zerava o content. Truncamento
        // ficou proibido por design — quem decide e o EOS do modelo.
        int? outTokens = cap > 0 ? cap : null;
        // Thinking do cliente (Web manda o do cadastro; opencode não manda):
        // presente vence o default da linha; ausente segue o flag --thinking.
        bool wantThinking = thinking == "events";
        try
        {
            var kw = req["chat_template_kwargs"]?.AsObject();
            if (kw?["enable_thinking"] is JsonValue ev && ev.TryGetValue<bool>(out bool b)) wantThinking = b;
        }
        catch { }
        var chat = new JsonObject
        {
            ["model"] = Model,
            ["messages"] = messages,
            ["temperature"] = temperature,
            ["stream"] = false,          // upstream sem stream: resposta unica, sintetizamos o SSE
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = wantThinking }
        };
        // Sem teto e sem pedido do cliente: omite max_tokens (llama decide).
        if (outTokens.HasValue) chat["max_tokens"] = outTokens.Value;
        // Amostragem: quando ligado, o cliente decide (o llama aceita
        // repeat_penalty/dry_* por request). Opt-in para nao mudar o body padrao.
        if (SamplingPassthrough) CopySamplingParams(req, chat);
        if (tools is { Count: > 0 })
        {
            chat["tools"] = tools;
            // Honra tool_choice explicito do cliente (ex.: testes pedem
            // "required"); so define padrao quando ausente: Coder precisa de
            // "required" p/ emitir tool_calls nativo, Gemma decide sozinho
            // ("auto", bake-off 3/3).
            if (chat["tool_choice"] is null)
                chat["tool_choice"] = IsCoder ? "required" : "auto";
            // parallel_tool_calls nao e forcado: o cliente/servidor decide.
            // (removida a ordem fixa de "1 call por resposta".)
        }
        ToolSanitizer.NeutralizeCodeMode(chat, ToolStrategy, Log);
        // Stop sequences: repasse opcional (string ou array). ATENCAO: com
        // tools ativas, stop pode truncar o JSON da tool call no meio.
        if (req["stop"] is JsonNode stop)
        {
            bool hasStop = stop is JsonArray stopArr ? stopArr.Count > 0
                : stop.GetValueKind() == System.Text.Json.JsonValueKind.String
                  && !string.IsNullOrEmpty(stop.GetValue<string>());
            if (hasStop) chat["stop"] = stop.DeepClone();
        }
        // Linha CPU sem parser: o 3B entra em loop de filler ([END_OF_TEXT])
        // quando divaga — stop dedicado corta o loop sem afetar tool calls
        // (elas nunca vem nativas aqui, só via coercion do texto).
        else if (coercedLine) chat["stop"] = new JsonArray("[END_OF_TEXT]");
        return chat;
    }

    /// <summary>Campos de amostragem que o llama.cpp aceita por request.</summary>
    static readonly string[] SamplingKeys =
    {
        "repeat_penalty", "repeat_last_n", "dry_multiplier", "dry_base",
        "dry_allowed_length", "dry_penalty_last_n", "top_p", "top_k",
        "min_p", "typical_p", "frequency_penalty", "presence_penalty",
        "seed", "xtc_probability", "xtc_threshold"
    };

    /// <summary>Copia ao upstream os campos de amostragem numericos do cliente.</summary>
    /// <param name="from">Request do cliente (Responses).</param>
    /// <param name="to">Body montado para o /v1/chat/completions.</param>
    internal static void CopySamplingParams(JsonObject from, JsonObject to)
    {
        foreach (string key in SamplingKeys)
        {
            if (from[key] is not JsonNode node) continue;
            if (node.GetValueKind() != JsonValueKind.Number) continue;
            to[key] = node.DeepClone();
        }
    }

    /// <summary>Nucleo: Responses -> chat -> vLLM -> Responses (JSON ou SSE).</summary>
    /// <param name="ctx">Contexto HTTP.</param>
    /// <param name="body">Request Responses.</param>
    static void HandleResponses(HttpListenerContext ctx, string body)
    {
        var startedAt = DateTime.UtcNow;

        // 4.1-4.4) Le o request Responses e monta o chat request
        var req = JsonNode.Parse(body)?.AsObject() ?? new JsonObject();
        // Fail fast honesto: sem input nao ha o que traduzir — 400 aqui em
        // vez de depender do upstream (vLLM nega, llama.cpp aceita e divaga).
        if (req["input"] is not JsonArray arr || arr.Count == 0)
        {
            WriteJson(ctx, 400, new JsonObject { ["error"] = "input ausente ou vazio: Responses exige input com ao menos 1 mensagem" });
            return;
        }
// Bypass llama: upstream fala Responses nativo; so removemos os itens
        // reasoning (llama.cpp exige content array neles) e encaminhamos o
        // resto intacto — tools nativas, sem traducao p/ chat.
        // EXCETO com tools: llama /v1/responses nao devolve function_call
        // (so texto) — cai na traducao p/ chat + ToolsCoercion.
        // Regra primordial: em bridge lane o harness é ON, então o bypass
        // (que pula o harness) só vale fora da raia bridge. Tools-less em
        // bridge lane cai na tradução (BuildChatRequest), que aplica o harness.
        if (PassthroughResponses && !ReqHasTools(req) && BridgeLane.Value != true)
        {
            ForwardResponses(ctx, body, req);
            return;
        }
        bool wantStream = req["stream"]?.GetValue<bool>() ?? false;
        var chat = BuildChatRequest(req, out string requestedModel, out double temperature, out string thinking,
            out int toolCount, out string tempSource, out string? breakerTrip, out string breakerState);

        // Breaker trip: o turno ja esta perdido (N falhas no mesmo alvo). Nao
        // chama o modelo; devolve o aviso como mensagem final (o cliente mostra
        // ao usuario e a proxima mensagem recomeca com diagnostico).
        if (breakerTrip is not null)
        {
            var synthetic = new JsonObject
            {
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = breakerTrip },
                    ["finish_reason"] = "stop"
                }),
                ["usage"] = new JsonObject { ["prompt_tokens"] = 0, ["completion_tokens"] = (breakerTrip.Length / 4) + 1 }
            };
            var tripObj = ChatToResponses(synthetic, requestedModel, temperature, thinking, toolCount > 0, chat["messages"]);
            WriteJson(ctx, 200, tripObj);
            return;
        }

        // Shim generico tambem no caminho Responses -> chat: o upstream recusa
        // grammar + tools, entao o envelope JSON e aplicado aqui, depois da
        // traducao, e a resposta volta convertida para function_call.
        bool shimmed = false;
        string? shimReason = null;
        if (ToolShimMode == "json" && toolCount > 0
            && ToolShim.Apply(chat, ToolShimMaxString, ToolShimMinCalls, MaxTokens, out shimReason))
        {
            shimmed = true;
            if (Diagnostic) Log(new JsonObject { ["event"] = "tool-shim", ["mode"] = "responses", ["max_string"] = ToolShimMaxString });
        }
        else if (Diagnostic && shimReason is not null)
            Log(new JsonObject { ["event"] = "tool-shim-skip", ["where"] = "responses", ["reason"] = shimReason });

        // Stream real sai por caminho proprio (sem a chamada unica abaixo).
        if (wantStream)
        {
            // Sonda: dump do request CHAT completo (system + tools) do caminho stream.
            if (Diagnostic) Log(new JsonObject { ["event"]="traffic", ["mode"]="translate-stream-req", ["model"]=requestedModel, ["req_len"]=chat.ToJsonString().Length, ["req"]=chat.ToJsonString() });
            if (shimmed)
                HandleResponsesStreamShim(ctx, chat, requestedModel, temperature, thinking, toolCount, startedAt, tempSource);
            else
                HandleResponsesStream(ctx, chat, requestedModel, temperature, thinking, startedAt, tempSource);
            return;
        }

        // 4.5) CHAMADA AO vLLM
var chatRequest = new HttpRequestMessage(HttpMethod.Post, Upstream + "/v1/chat/completions")
        {
            Content = new StringContent(chat.ToJsonString(SerOpts), Encoding.UTF8, "application/json")
        };
        WithAuth(chatRequest, ctx);
        using var upstreamResponse = SendWithAbortRetry(ctx, chatRequest, chat.ToJsonString(SerOpts),
            HttpCompletionOption.ResponseContentRead, requestedModel);
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
        if (shimmed) ToolShim.TransformResponse(chatJson);
        var responseObj = ChatToResponses(chatJson, requestedModel, temperature, thinking, toolCount > 0, chat["messages"]);
        RecordResponsesActivity(responseObj);

        double elapsed = (DateTime.UtcNow - startedAt).TotalSeconds;
        int inTok = responseObj["usage"]?["input_tokens"]?.GetValue<int>() ?? 0;
        int outTok = responseObj["usage"]?["output_tokens"]?.GetValue<int>() ?? 0;
        Log(new JsonObject
        {
            ["event"] = "response", ["model"] = requestedModel, ["stream"] = wantStream,
            ["temperature"] = temperature, ["temperature_source"] = tempSource,
            ["message_count"] = chat["messages"]!.AsArray().Count, ["tool_count"] = toolCount,
            ["has_tool_call"] = responseObj["output"]!.AsArray().Any(i => i?["type"]?.GetValue<string>() == "function_call"),
            ["has_reasoning"] = responseObj["output"]!.AsArray().Any(i => i?["type"]?.GetValue<string>() == "reasoning"),
            ["elapsed"] = Math.Round(elapsed, 2),
            ["input_tokens"] = inTok, ["output_tokens"] = outTok,
            ["tps_output"] = Math.Round(elapsed > 0 ? outTok / elapsed : 0, 1)
        });
        // Sonda traffic FULL (só com DIAGNOSTICS): traducao req/res integros.
        if (Diagnostic)
        {
            string dumpRes = responseObj.ToJsonString();
            var traffic = new JsonObject { ["event"] = "traffic", ["mode"] = "translate", ["model"] = requestedModel, ["req_len"] = chat.ToJsonString().Length, ["req"] = chat.ToJsonString(), ["res_len"] = dumpRes.Length, ["res"] = dumpRes };
            AddPerf(traffic, startedAt, dumpRes, chat.ToJsonString());
            Log(traffic);
        }
        AddMetrics(inTok, outTok, elapsed);

        // 4.7) Sem stream: devolve JSON direto.
        WriteJson(ctx, 200, responseObj);
    }

/// <summary>Bypass: encaminha /v1/responses ao upstream apos remover itens reasoning.</summary>
    /// <param name="ctx">Contexto HTTP.</param>
    /// <param name="body">Corpo original.</param>
    /// <param name="req">Request ja parseado.</param>
    static void ForwardResponses(HttpListenerContext ctx, string body, JsonObject req)
    {
var startedAt = DateTime.UtcNow;
string outBody = body;
        int dropped = 0;
        try
        {
            dropped = StripReasoningItems(req);
            bool moved = NormalizeSystemFirst(req);
            // Temperatura efetiva no bypass: perfil do role vence (mesma regra do
            // caminho traduzido); sem perfil vale o global. Sem isto o llama usava
            // o --temp da unit e o numero do perfil nunca valia no /v1/responses.
            if (req["temperature"] is null)
            {
                double profT = double.NaN;
                bool hasProf = req["model"]?.GetValue<string>() is string tmid && TryProfile(tmid, out profT, out _);
                (double effTemp, string src) = Sampling.Temperature(null, hasProf, profT, Temperature);
                req["temperature"] = effTemp;
                if (Diagnostic) Log(new JsonObject
                {
                    ["event"] = "temp-inject",
                    ["temp"] = effTemp,
                    ["source"] = src,
                    ["model"] = req["model"]?.GetValue<string>() ?? Model
                });
            }
            if (dropped > 0 || moved || req["temperature"] is not null) outBody = req.ToJsonString(SerOpts);
        }
        catch { outBody = body; }
        // Formula: instructions + agents + tail, por caminho (--tail-scope).
        // "all" injeta tambem no opencode; "managed" so no cliente direto/Web.
        try
        {
            bool pthHasTools = req["tools"] is JsonArray pta && pta.Count > 0;
            if (req.TryGetPropertyValue("instructions", out JsonNode? fins) && fins is JsonValue fiv
                && fiv.TryGetValue<string>(out string? finst))
            {
                req["instructions"] = WithFormula(finst, req["model"]?.GetValue<string>() ?? "", pthHasTools);
                outBody = req.ToJsonString(SerOpts);
            }
            else
            {
                // Cliente que nao manda `instructions` (WAR/SDK) ainda recebe a
                // formula do harness; sem isso as tails nao chegavam ao modelo.
                // O system que o NormalizeSystemFirst fundiu no `input[0]` vira
                // `instructions` (UNICO system do upstream): a cauda pega nele e
                // nao sobra um segundo system dentro do input.
                string baseSys = TakeLeadingSystem(req);
                string formula = WithFormula(baseSys, req["model"]?.GetValue<string>() ?? "", pthHasTools);
                if (!string.IsNullOrWhiteSpace(formula))
                {
                    req["instructions"] = formula;
                    outBody = req.ToJsonString(SerOpts);
                }
            }
        }
        catch { }
        var forward = new HttpRequestMessage(HttpMethod.Post, Upstream + "/v1/responses")
        {
            Content = new StringContent(outBody, Encoding.UTF8, "application/json")
        };
        WithAuth(forward, ctx);
using var upstreamResponse = SendWithAbortRetry(ctx, forward, outBody,
            HttpCompletionOption.ResponseHeadersRead, req["model"]?.GetValue<string>());
        double elapsed = (DateTime.UtcNow - startedAt).TotalSeconds;
        string? errBody = null;
        if (!upstreamResponse.IsSuccessStatusCode)
        {
            Interlocked.Increment(ref FailedRequests);
            Interlocked.Increment(ref ModelFailedRequests);
            LastModelActivityMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            try
            {
                using var er = new StreamReader(upstreamResponse.Content.ReadAsStream(), new UTF8Encoding(false));
                errBody = er.ReadToEnd();
            }
            catch { errBody = ""; }
        }
var shape = new StringBuilder();
        try
        {
            if (req.TryGetPropertyValue("instructions", out JsonNode? ins) && ins is not null) shape.Append("[instructions]+");
            if (req["input"] is JsonArray li)
                foreach (var x in li)
                    shape.Append(x is JsonObject o ? (o["type"]?.GetValue<string>() ?? "?") + ":" + (o["role"]?.GetValue<string>() ?? "-") + " " : "str ");
        }
        catch { }
        Log(new JsonObject { ["event"] = "passthrough-responses", ["model"] = Model, ["dropped_reasoning"] = dropped, ["status"] = (int)upstreamResponse.StatusCode, ["elapsed"] = Math.Round(elapsed, 2), ["shape"] = shape.ToString().Trim(), ["upstream_error"] = (errBody ?? "").Length > 500 ? errBody![..500] : (errBody ?? "") });
        // Sonda FULL (só com DIAGNOSTICS): pedido integro que seguiu ao upstream.
        if (Diagnostic) Log(new JsonObject { ["event"] = "traffic", ["mode"] = "responses-req", ["model"] = Model, ["stream"] = (upstreamResponse.Content.Headers.ContentType?.ToString() ?? "").Contains("text/event-stream"), ["req_len"] = outBody.Length, ["req"] = outBody });
        ctx.Response.StatusCode = (int)upstreamResponse.StatusCode;
        ctx.Response.ContentType = upstreamResponse.Content.Headers.ContentType?.ToString() ?? "application/json";
        if (errBody is not null)
        {
            var eb = new UTF8Encoding(false).GetBytes(errBody);
            ctx.Response.OutputStream.Write(eb, 0, eb.Length);
            ctx.Response.Close();
            return;
        }
        using var stream = upstreamResponse.Content.ReadAsStream();
        if ((ctx.Response.ContentType ?? "").Contains("text/event-stream"))
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            using var writer = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false)) { AutoFlush = true };
            string? line;
            // Sonda bypass-responses-stream: so observa e registra (o fluxo NAO
            // para; a contencao silenciosa mora no loop da Web).
            var sseText = new StringBuilder();
            var rawSse = new StringBuilder();
            int sseChecked = 0;
            bool sseTrip = false;
            while ((line = reader.ReadLine()) != null)
            {
                writer.WriteLine(line);
                if (Diagnostic) rawSse.AppendLine(line);
                if (!sseTrip && line.StartsWith("data:"))
                {
                    try
                    {
                        string data = line["data:".Length..].Trim();
                        if (data is not ("[DONE]" or "")) WalkText(JsonNode.Parse(data), sseText);
                    }
                    catch { }
                    if (sseText.Length - sseChecked >= 512)
                    {
                        sseChecked = sseText.Length;
                        if (CheckRepeat(sseText.ToString(), "bypass-responses-stream"))
                        {
                            sseTrip = true;
                            Log(new JsonObject { ["event"] = "anti-repeat", ["where"] = "bypass-responses-stream", ["window"] = AntiRepeatWindow, ["chars"] = sseText.Length });
                        }
                    }
                }
            }
            // Avaliacao de fecho: stream curto nunca atinge o passo de 512.
            // Sem break: o fluxo segue ate o fim; a Web conteve em silencio.
            if (!sseTrip && sseText.Length > 0 && CheckRepeat(sseText.ToString(), "bypass-responses-stream"))
            {
                sseTrip = true;
                Log(new JsonObject { ["event"] = "anti-repeat", ["where"] = "bypass-responses-stream", ["window"] = AntiRepeatWindow, ["chars"] = sseText.Length, ["late"] = true });
            }
            if (sseText.Length > 0) RecordAgentActivity("assistant", sseText.ToString());
            // Sonda traffic FULL (só com DIAGNOSTICS): pedido e SSE integros.
            if (Diagnostic)
            {
                string dump = rawSse.ToString();
                var traffic = new JsonObject { ["event"] = "traffic", ["mode"] = "responses-stream", ["model"] = Model, ["req_len"] = outBody.Length, ["req"] = outBody, ["res_len"] = dump.Length, ["res"] = dump };
                AddPerf(traffic, startedAt, dump, outBody);
                Log(traffic);
            }
        }
        else
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            string json = reader.ReadToEnd();
            // Sonda bypass-responses: repeticao no objeto = 1 retry de contencao.
            // Retry inibivel (--harness-tails 0): deteccao vira so sonda.
            if (CheckRepeat(ExtractResponsesText(json), "bypass-responses"))
            {
                Log(new JsonObject { ["event"] = "anti-repeat", ["where"] = "bypass-responses", ["window"] = AntiRepeatWindow });
                // Mesmo desacoplamento do bypass-chat: quem governa e o anti-repeat.
                if (AntiRepeatRetry)
                {
                    string? second = RetryBypassResponses(req, ctx);
                    if (second is not null)
                    {
                        json = second;
                        Log(new JsonObject { ["event"] = "anti-repeat-retry", ["where"] = "bypass-responses" });
                    }
                }
            }
            try
            {
                if (JsonNode.Parse(json) is JsonObject ro) RecordResponsesActivity(ro);
            }
            catch { }
            var bytes = new UTF8Encoding(false).GetBytes(json);
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            if (Diagnostic)
            {
                var traffic = new JsonObject { ["event"] = "traffic", ["mode"] = "responses", ["model"] = Model, ["req_len"] = outBody.Length, ["req"] = outBody, ["res_len"] = json.Length, ["res"] = json };
                AddPerf(traffic, startedAt, json, outBody);
                Log(traffic);
            }
        }
        ctx.Response.Close();
    }





    /// <remarks>As duas caudas entram quando <see cref="TailsOn"/> permite o
    /// caminho: "all" injeta tambem no opencode, "managed" so no cliente
    /// direto/Web (--harness-tails 1). Cada enunciado de inducAO mora na sua propria
    /// peca: ToolsTail (como usar a tool) e BehaviorPrompt (disciplina de
    /// comportamento), porque sao coisas diferentes e mexer numa nao deve tocar
    /// na outra.</remarks>
    /// <param name="sys">System prompt original.</param>
    /// <returns>System prompt composto.</returns>
    internal static string WithFormula(string sys) => WithFormula(sys, "");

    /// <summary>Composição das caudas decidida pelo model id (perfil = opencode).</summary>
    /// <param name="sys">System prompt original.</param>
    /// <param name="modelId">Model id do pedido (define o caminho).</param>
    /// <param name="hasTools">Request traz array `tools`? (sem tools: nada de cauda de tools)</param>
    /// <param name="toolNames">Nomes das tools do request (null = todas as do catalogo)</param>
    /// <returns>System prompt composto.</returns>
    internal static string WithFormula(string sys, string? modelId, bool hasTools = true,
        IReadOnlyCollection<string>? toolNames = null)
    {
        // Fonte unica das caudas no Core; o host so decide ligar/desligar e
        // passa as sobrescritas de CLI/appsettings.
        bool behaviorOn = BehaviorTailOn();
        bool toolsOn = ToolsTailOn();
        if (!behaviorOn && !toolsOn) return sys ?? "";
        return HarnessTails.Compose(sys, true,
            behaviorOn ? HarnessBehaviorTail : "",
            toolsOn ? HarnessToolsTail : "",
            hasTools, toolNames);
    }

    /// <summary>
    /// Nomes das tools do array `tools` do request (null quando o request nao
    /// traz o array). A cauda de tools e' composta por esta lista.
    /// </summary>
    static IReadOnlyCollection<string>? ToolNamesOf(JsonArray? tools)
    {
        if (tools is null) return null;
        var names = new List<string>();
        foreach (var n in ToolNames(tools))
            if (n?.GetValue<string>() is string s && s.Length > 0) names.Add(s);
        return names;
    }

    /// <summary>Pedido opencode (perfil com sufixo)? Se sim, tubo puro, sem formula.</summary>
    static bool IsOpencodeModel(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return false;
        foreach (var k in Profiles.Keys)
            if (modelId.EndsWith("-" + k, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Remove chaves "pattern" nao-ancoradas dos schemas das tools (recursivo).</summary>
    // ---------------------------------------------------------------------
    // 3.9) Estrategia de tools (ToolStrategy)
    //   O opencode expoe a tool "execute" (Code Mode) cujo sandbox so contem
    //   os namespaces do catalogo. Modelos pequenos (Gemma 4 12B) tentam
    //   `return tools.shell({...})` dentro de execute, recebem
    //   "Unknown tool 'shell'" e repetem a mesma chamada em loop.
    //   Resgate deterministico: execute com UMA chamada tools.X({...}) vira a
    //   tool direta X (nome/args parseados); qualquer outra forma passa
    //   intacta. A nota de estrategia no system reduz a reincidencia.
    // ---------------------------------------------------------------------
    static readonly Regex ReExecuteCall = new(
        @"^(?:return\s+|await\s+)?tools\s*(?:\.\s*([A-Za-z_$][\w$]*)|\[\s*['""]([A-Za-z_$][\w$]*)['""]\s*\])\s*\(\s*(\{[\s\S]*\})\s*\)\s*;?$",
        RegexOptions.Compiled);

    static readonly HashSet<string> DirectTools = new(StringComparer.Ordinal)
        { "shell", "bash", "sh", "read", "write", "edit", "grep", "glob", "fetch", "webfetch", "git" };

    /// <summary>Maximo de chamadas extraidas de um unico execute (anti-explosao).</summary>
    const int MaxUnwrapCalls = 8;

    /// <summary>Chamadas tools.X(...) embutidas em um script maior.</summary>
    static readonly Regex ReEmbeddedToolCall = new(
        @"tools\s*(?:\.\s*([A-Za-z_$][\w$]*)|\[\s*['""]([A-Za-z_$][\w$]*)['""]\s*\])\s*\(",
        RegexOptions.Compiled);

    /// <summary>API de Node (require/fs) que nao existe no sandbox de execute.</summary>
    static readonly Regex ReRequireFs = new(@"\brequire\s*\(|\bfs\s*\.", RegexOptions.Compiled);

    /// <summary>Mensagem guiada devolvida como resultado quando o script usa require/fs.</summary>
    const string GuideCommand =
        "printf '%s\\n' 'ERRO: execute e um sandbox de namespaces do catalogo - NAO existe require/fs. " +
        "Nao repita o mesmo codigo: reemita a MESMA acao com as tools diretas (shell p/ comandos, write p/ arquivos, edit, read, grep, glob).'";

    /// <summary>Tokens de controle que o template vaza para dentro dos argumentos.</summary>
    static readonly Regex ReLeakedTemplate = new(@"<\|?[A-Za-z_]{2,20}\|?>", RegexOptions.Compiled);



    /// <summary>Marcadores de template que o 12B vaza para dentro do comando shell.</summary>
    static readonly string[] LeakMarkers = { "<tool_call|>", "<|tool_call>", "<|channel>", "<channel|>" };

    /// <summary>Tools que podem aparecer como chamada inline (template vazado no texto).</summary>
    static readonly HashSet<string> InlineTools = new(StringComparer.Ordinal)
    { "edit", "write", "read", "grep", "glob", "shell", "webfetch", "websearch", "subagent", "skill", "question" };

    /// <summary>Par chave=valor de uma chamada inline (nome{chave: valor, ...}).</summary>
    static readonly Regex ReInlineArg = new(@"(\w+)\s*[:=]\s*(""(?:[^""\\]|\\.)*""|[^,}]+)", RegexOptions.Compiled);


    /// <summary>Chamada invalida vira shell com a correcao (o erro volta como resultado da tool).</summary>
    /// <param name="name">Tool pedida pelo modelo.</param>
    /// <param name="why">Motivo objetivo (o que falta/esta invalido).</param>
    /// <param name="original">Argumentos originais (so para log).</param>
    /// <returns>Chamada substituta (shell) com o texto corretivo.</returns>
    static (string Name, string Arguments) NudgeArgs(string name, string why, string original)
    {
        Metrics.Deny("nudge", name, why);
        Log(new JsonObject { ["event"] = "tool-args-guide", ["tool"] = name, ["reason"] = why, ["len"] = original.Length });
        string msg = "printf '%s\\n' 'ERRO na tool " + name + ": " + why +
            ". Nao repita igual: reemita a chamada com JSON completo - " +
            "edit=path+oldString+newString; write=path+content; read=path; grep/glob=pattern; shell=command; " +
            "subagent=agent+description+prompt. Nao aninhe JSON dentro de um valor.'";
        return ("shell", new JsonObject { ["command"] = msg }.ToJsonString(SerOpts));
    }

    /// <summary>Converte o valor cru de uma chamada inline em no JSON (string, bool ou numero).</summary>
    /// <param name="raw">Valor como veio no texto.</param>
    /// <returns>No JSON correspondente.</returns>
    static JsonNode? ToJsonValue(string raw)
    {
        string v = raw.Trim();
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
            v = v[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        if (bool.TryParse(v, out bool b)) return JsonValue.Create(b);
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return JsonValue.Create(d);
        return JsonValue.Create(v);
    }

    /// <summary>Le uma chamada inline no formato nome{chave: valor, ...} (template vazado).</summary>
    /// <param name="text">Texto do comando.</param>
    /// <returns>Nome + argumentos quando for uma tool conhecida; null caso contrario.</returns>
    static (string Name, JsonObject Args)? ParseInlineCall(string text)
    {
        string t = text.Trim();
        int open = t.IndexOf('{');
        if (open <= 0) return null;
        string name = t[..open].Trim().TrimEnd(':').Trim();
        int call = name.LastIndexOf("call:", StringComparison.Ordinal);
        if (call >= 0) name = name[(call + 5)..].Trim();
        if (!InlineTools.Contains(name)) return null;
        int end = FindObjectEnd(t, open);
        if (end <= open) return null;
        var args = new JsonObject();
        foreach (Match m in ReInlineArg.Matches(t[(open + 1)..end]))
            args[m.Groups[1].Value] = ToJsonValue(m.Groups[2].Value);
        return (name, args);
    }

    /// <summary>Promove a chamada inline a chamada nativa (com schema conferido).</summary>
    /// <param name="call">Nome + argumentos lidos do texto.</param>
    /// <param name="original">Argumentos originais (para o guia quando faltar chave).</param>
    /// <returns>Chamada efetiva (nativa ou guia).</returns>
    static (string Name, string Arguments) PromoteInline((string Name, JsonObject Args) call, string original)
    {
        string args = call.Args.ToJsonString(SerOpts);
        var missing = MissingArgs(call.Name, args);
        if (missing.Count > 0) return NudgeArgs(call.Name, "faltam as chaves " + string.Join(", ", missing), original);
        Log(new JsonObject { ["event"] = "tool-shell-leak", ["tool"] = call.Name, ["mode"] = "promote" });
        return (call.Name, args);
    }


    /// <summary>Converte escape duplo ("\\n" literal de 2 chars) em quebra real no comando shell.</summary>
    /// <remarks>Caso real (2026-10-23): o modelo emitiu
    /// <c>python3 - &lt;&lt;'PY'\n import sqlite3</c> — o bash recebeu a sequencia
    /// literal e respondeu "here-document delimitado pelo fim do arquivo" +
    /// "erro de sintaxe". So atua quando o comando NAO tem nenhuma quebra real,
    /// para nao estragar comando que cita "\n" de proposito (printf/regex).
    /// Independe de ToolStrategy: e higiene de argumento, nao resgate de padrao.</remarks>
    /// <param name="name">Nome da tool.</param>
    /// <param name="arguments">Argumentos JSON.</param>
    /// <returns>Nome/argumentos com escapes normalizados.</returns>
    internal static (string Name, string Arguments) FixLiteralEscapes(string name, string arguments)
    {
        if (!string.Equals(name, "shell", StringComparison.Ordinal)
            && !string.Equals(name, "bash", StringComparison.Ordinal)) return (name, arguments);
        JsonObject? obj;
        try { obj = JsonNode.Parse(arguments) as JsonObject; } catch { return (name, arguments); }
        if (obj?["command"] is not JsonValue cv || !cv.TryGetValue<string>(out string? cmd)) return (name, arguments);
        if (!cmd.Contains("\\n", StringComparison.Ordinal) || cmd.Contains('\n')) return (name, arguments);
        string fixedCmd = cmd.Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\\t", "\t");
        obj["command"] = fixedCmd;
        Log(new JsonObject
        {
            ["event"] = "tool-args-escape", ["tool"] = name,
            ["mode"] = "literal-to-newline", ["newlines"] = fixedCmd.Count(c => c == '\n')
        });
        return (name, obj.ToJsonString(SerOpts));
    }

    /// <summary>Corta lixo de template vazado no comando shell e promove chamada inline.</summary>
    /// <remarks>O 12B mistura o texto do comando com o proximo tool call
    /// (`date +"..."}&lt;tool_call|&gt;...`) e o bash quebra por sintaxe; aqui o comando
    /// e cortado no lixo e, quando so sobra lixo, a chamada aninhada e promovida.</remarks>
    /// <param name="name">Nome da tool.</param>
    /// <param name="arguments">Argumentos JSON.</param>
    /// <returns>Nome/argumentos efetivos (guia quando o comando era so lixo).</returns>
    internal static (string Name, string Arguments) SanitizeShellText(string name, string arguments)
    {
        if (!ToolStrategy || !string.Equals(name, "shell", StringComparison.Ordinal)) return (name, arguments);
        JsonObject? obj = null;
        try { obj = JsonNode.Parse(arguments) as JsonObject; } catch { return (name, arguments); }
        if (obj?["command"] is not JsonValue cv || !cv.TryGetValue<string>(out string? cmd)) return (name, arguments);
        int cut = -1;
        foreach (var m in LeakMarkers)
        {
            int i = cmd.IndexOf(m, StringComparison.Ordinal);
            if (i >= 0 && (cut < 0 || i < cut)) cut = i;
        }
        if (cut < 0) return (name, arguments);
        string head = cmd[..cut];
        string tail = cmd[cut..];
        // Chamada inline no inicio do comando (ex.: websearch{query: "..."}<tool_call|>).
        var inline = ParseInlineCall(head.Trim());
        if (inline is not null) return PromoteInline(inline.Value, arguments);
        head = TrimUnbalanced(head.TrimEnd());
        if (head.Length == 0)
        {
            var inTail = ParseInlineCall(tail);
            if (inTail is not null) return PromoteInline(inTail.Value, arguments);
            return NudgeArgs(name, "comando so tinha lixo de template (<tool_call|>)", arguments);
        }
        obj["command"] = head;
        Log(new JsonObject { ["event"] = "tool-shell-leak", ["tool"] = name, ["mode"] = "trim", ["removed"] = cmd.Length - head.Length });
        return (name, obj.ToJsonString(SerOpts));
    }

    /// <summary>Limpa/normaliza os argumentos de uma tool call emitida pelo modelo.</summary>
    /// <remarks>O 12B as vezes vaza tokens de template para dentro do JSON e o cliente
    /// rejeita a chamada; repetir o mesmo codigo 256x (tools-dev-pipe) nao resolve.
    /// Aqui a chamada invalida vira um shell com a correcao, que volta como resultado.</remarks>
    /// <param name="name">Nome da tool pedida pelo modelo.</param>
    /// <param name="arguments">Argumentos crus.</param>
    /// <returns>Nome/argumentos efetivos (shell guiado quando irrecuperavel).</returns>
    internal static (string Name, string Arguments) SanitizeToolCall(string name, string arguments)
    {
        // Higiene de escape (vale mesmo com ToolStrategy desligada).
        var escaped = FixLiteralEscapes(name, arguments);
        name = escaped.Name; arguments = escaped.Arguments;
        if (!ToolStrategy || string.IsNullOrWhiteSpace(arguments)) return (name, arguments);
        try
        {
            string candidate = arguments;
            if (!IsJsonObject(candidate))
            {
                string cleaned = ReLeakedTemplate.Replace(candidate, "").Trim();
                if (IsJsonObject(cleaned))
                {
                    Log(new JsonObject { ["event"] = "tool-args-clean", ["tool"] = name });
                    candidate = cleaned;
                }
                else
                {
                    // Sobrou prosa depois do objeto: recorta so o objeto balanceado.
                    int open = cleaned.IndexOf('{');
                    int end = open >= 0 ? FindObjectEnd(cleaned, open) : -1;
                    string slice = end > open ? cleaned[open..(end + 1)] : "";
                    if (slice.Length > 0 && IsJsonObject(slice))
                    {
                        Log(new JsonObject { ["event"] = "tool-args-clean", ["tool"] = name, ["mode"] = "slice" });
                        candidate = slice;
                    }
                    else
                    {
                        return NudgeArgs(name, "JSON invalido ou com token de template", arguments);
                    }
                }
            }
            // Lixo de template dentro do comando shell: corta no marcador ou promove a chamada.
            var shell = SanitizeShellText(name, candidate);
            if (!shell.Name.Equals(name, StringComparison.Ordinal) || !shell.Arguments.Equals(candidate, StringComparison.Ordinal))
                return shell;
            // Schema: o 12B manda JSON valido mas sem as chaves obrigatorias
            // (ex.: edit sem path/oldString, com o JSON aninhado em newString).
            var missing = MissingArgs(name, candidate);
            if (missing.Count > 0)
                return NudgeArgs(name, "faltam as chaves " + string.Join(", ", missing), arguments);
            return (name, candidate);
        }
        catch { return (name, arguments); }
    }

    /// <summary>Resgata execute com uma unica chamada tools.X({...}) p/ a tool direta X.</summary>
    /// <param name="name">Nome original da tool.</param>
    /// <param name="arguments">Argumentos JSON originais.</param>
    /// <returns>Nome/argumentos efetivos (inalterados quando nao se aplica).</returns>
    internal static (string Name, string Arguments) UnwrapExecuteCall(string name, string arguments)
    {
        var calls = UnwrapExecuteCalls(name, arguments);
        return calls.Count > 0 ? calls[0] : (name, arguments);
    }

    /// <summary>Resgata execute p/ uma ou mais tools diretas (script com N chamadas).</summary>
    /// <param name="name">Nome original da tool.</param>
    /// <param name="arguments">Argumentos JSON originais.</param>
    /// <returns>Chamadas efetivas, na ordem do script (vazio quando nao se aplica).</returns>
    internal static List<(string Name, string Arguments)> UnwrapExecuteCalls(string name, string arguments)
    {
        var result = new List<(string Name, string Arguments)>();
        if (!ToolStrategy || !string.Equals(name, "execute", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(arguments))
            return result;
        try
        {
            string code = JsonNode.Parse(arguments)?["code"]?.GetValue<string>() ?? "";
            string trimmed = StripLeadingJsComments(code).Trim();

            var match = ReExecuteCall.Match(trimmed);
            if (match.Success)
            {
                string tool = match.Groups[1].Success && match.Groups[1].Value.Length > 0
                    ? match.Groups[1].Value
                    : match.Groups[2].Value;
                if (tool is "bash" or "sh") tool = "shell";
                if (DirectTools.Contains(tool)
                    && TryParseJsValue(match.Groups[3].Value, out JsonNode? parsed)
                    && parsed is JsonObject single)
                {
                    Log(new JsonObject { ["event"] = "tool-unwrap", ["from"] = "execute", ["to"] = tool, ["calls"] = 1 });
                    result.Add((tool, single.ToJsonString(SerOpts)));
                    return result;
                }
            }

            foreach (var (tool, argsJson) in ExtractEmbeddedToolCalls(trimmed))
            {
                if (result.Count >= MaxUnwrapCalls) break;
                string direct = tool is "bash" or "sh" ? "shell" : tool;
                if (!DirectTools.Contains(direct)) continue;
                if (!TryParseJsValue(argsJson, out JsonNode? node) || node is not JsonObject obj) continue;
                result.Add((direct, obj.ToJsonString(SerOpts)));
            }
            if (result.Count > 0)
            {
                Log(new JsonObject { ["event"] = "tool-unwrap", ["from"] = "execute", ["to"] = result[0].Name, ["calls"] = result.Count });
                return result;
            }

            // Script puro de Node (require/fs): devolve um resultado guiado via shell
            // para o modelo corrigir na proxima tentativa em vez de repetir o codigo.
            if (ReRequireFs.IsMatch(trimmed))
            {
                Log(new JsonObject { ["event"] = "tool-guide", ["from"] = "execute", ["reason"] = "require/fs" });
                result.Add(("shell", new JsonObject { ["command"] = GuideCommand }.ToJsonString(SerOpts)));
            }
            return result;
        }
        catch { return result; }
    }

    /// <summary>Extrai as chamadas tools.X({...}) de um script JS, na ordem do texto.</summary>
    /// <param name="code">Codigo JS.</param>
    /// <returns>Pares (tool, literal do objeto de argumentos).</returns>
    static List<(string Tool, string ArgsJson)> ExtractEmbeddedToolCalls(string code)
    {
        var found = new List<(string, string)>();
        foreach (Match m in ReEmbeddedToolCall.Matches(code))
        {
            if (found.Count >= MaxUnwrapCalls) break;
            string tool = m.Groups[1].Success && m.Groups[1].Value.Length > 0
                ? m.Groups[1].Value
                : m.Groups[2].Value;
            int brace = code.IndexOf('{', m.Index + m.Length);
            if (brace < 0) continue;
            int end = FindObjectEnd(code, brace);
            if (end < 0) continue;
            found.Add((tool, code[brace..(end + 1)]));
        }
        return found;
    }











    /// <summary>Exceção permanente do caso pattern: sempre ligada, só este caso.</summary>
    /// <remarks>llama.cpp aborta 400 sem ^...$; sem este strip nenhuma tool com
    /// regex chega ao modelo. Independe de --harness-tails/--legacy-harness.</remarks>
    /// <param name="tools">Array de tools (mutado no lugar).</param>
    /// <param name="mode">Rótulo p/ o log (responses/chat).</param>
    static void ApplyPatternException(JsonNode? tools, string mode)
    {
        var hits = new List<string>();
        int dropped = ToolSanitizer.StripUnanchoredPatterns(tools, "$", hits);
        if (dropped > 0)
        {
            var where = new JsonArray();
            foreach (var h in hits) where.Add(h);
            Log(new JsonObject { ["event"] = "pattern-strip", ["mode"] = mode, ["dropped"] = dropped, ["hits"] = where, ["tools"] = ToolNames(tools as JsonArray) });
        }
    }

    internal static bool ContextGuardEnabled() => ContextGuard;
    internal static (bool Enabled, double Threshold, int Tokens, int Slots) ContextGuardConfig() => (ContextGuard, ContextThreshold, ContextTokens, ContextSlots);
    internal static void ContextGuardSetup(bool enabled, double threshold, int tokens, int slots)
    { ContextGuard = enabled; ContextThreshold = threshold; ContextTokens = tokens; ContextSlots = slots > 0 ? slots : 1; }

    // Guarda de ctx: a regra mora no Core (ContextGuard); aqui so os parametros.
    internal static bool ContextGuardTripped(string json, out int promptTokens, out double fraction) =>
        Rochas.OpenCodeBridge.Core.ContextGuard.Tripped(json,
            new ContextGuardSettings(ContextGuardEnabled(), ContextThreshold, ContextTokens, ContextSlots),
            out promptTokens, out fraction);

    /// <summary>Remove tags de controle e registra a sonda (host: o log fica aqui).</summary>
    /// <param name="s">Texto.</param>
    /// <param name="where">Ponto de observacao.</param>
    /// <returns>Texto limpo.</returns>
    static string StripControlTags(string s, string where)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var removed = new List<string>();
        string clean = ToolCoercion.StripControlTags(s, removed);
        if (Diagnostic)
            foreach (string tag in removed)
                Log(new JsonObject { ["event"] = "reasoning-tag", ["where"] = where, ["tag"] = tag });
        return clean;
    }

    /// <summary>Detector com sonda: avalia e registra o probe Serilog (trip=Warning, resto=Debug).</summary>
    /// <param name="accumulated">Texto acumulado ate aqui.</param>
    /// <param name="where">Ponto de observacao.</param>
    /// <returns>True se repeticao detectada.</returns>
    static bool CheckRepeat(string accumulated, string where)
    {
        bool trip = AntiRepeat && new StreamAntiRepeatReply(AntiRepeatWindow).IsRepeat(accumulated);
        // Sonda: loga no maximo 1x a cada 2k chars p/ nao afogar o sink em stream.
        int chars = accumulated?.Length ?? 0;
        if (trip)
        {
            int dupAt = -1; string dupCtx = "";
            var norm = ContentAntiRepeat.Normalize(accumulated ?? "");
            int w = Math.Min(AntiRepeatWindow, norm.Length);
            if (w > 0 && norm.Length >= 2 * w)
            {
                var win = norm[^w..];
                dupAt = norm[..^w].IndexOf(win, StringComparison.Ordinal);
                if (dupAt >= 0)
                    dupCtx = norm.Substring(dupAt, Math.Min(SondaMaxChars, norm.Length - dupAt));
            }
            Sonda(where, chars, trip, TailText(norm, SondaMaxChars), dupAt, dupCtx);
        }
        else if (chars < 512 || (chars % 2048) < 256)
            Sonda(where, chars, trip, TailText(ContentAntiRepeat.Normalize(accumulated ?? ""), SondaMaxChars));
        return trip;
    }





    /// <summary>Consome o upstream em SSE, acumula a resposta e ABORTA a geracao
    /// quando o anti-repeat dispara: corta o loop em ~512 chars de texto novo em
    /// vez de deixar o modelo gastar max_tokens (medido: 16.384 tok / 375 s por
    /// turno degenerado, com ~15,5k tokens descartados pelo parser do llama).
    /// Preserva os tool calls ja fechados e tenta 1 retry com o aviso invisivel.
    /// Devolve true quando ja escreveu a resposta no ctx.</summary>
    static bool TryStreamAbortChat(HttpListenerContext ctx, string outBody, DateTime startedAt)
    {
        string sbody;
        try
        {
            var n = JsonNode.Parse(outBody)?.AsObject();
            if (n is null) return false;
            n["stream"] = true;
            n["stream_options"] = new JsonObject { ["include_usage"] = true };
            sbody = n.ToJsonString(SerOpts);
        }
        catch { return false; }

        using var cts = new CancellationTokenSource();
        HttpResponseMessage? resp = null;
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post, Upstream + "/v1/chat/completions")
            { Content = new StringContent(sbody, Encoding.UTF8, "application/json") };
            WithAuth(req, ctx);
            resp = Http.Send(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode) { resp.Dispose(); return false; }
            string rct = resp.Content.Headers.ContentType?.ToString() ?? "";
            if (!rct.Contains("text/event-stream")) { resp.Dispose(); return false; }

            var content = new StringBuilder();
            var scan = new StringBuilder();
            var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Args)>();
            int inTok = 0, outTok = 0, checkedAt = 0;
            bool tripped = false, gotAnything = false;

            using (var rd = new StreamReader(resp.Content.ReadAsStream(cts.Token), new UTF8Encoding(false)))
            {
                string? line;
                while ((line = rd.ReadLine()) != null)
                {
                    if (!line.StartsWith("data:")) continue;
                    string data = line[5..].Trim();
                    if (data.Length == 0 || data == "[DONE]") continue;
                    JsonObject? chunk = null;
                    try { chunk = JsonNode.Parse(data)?.AsObject(); } catch { }
                    if (chunk is null) continue;
                    if (chunk["usage"] is JsonObject us)
                    {
                        inTok = us["prompt_tokens"]?.GetValue<int>() ?? inTok;
                        outTok = us["completion_tokens"]?.GetValue<int>() ?? outTok;
                    }
                    if (chunk["choices"] is not JsonArray chs || chs.Count == 0) continue;
                    if (chs[0]?["delta"] is not JsonObject dl) continue;
                    if (dl["content"] is JsonValue cv && cv.TryGetValue<string>(out string? ctxt) && ctxt.Length > 0)
                    { content.Append(ctxt); scan.Append(ctxt); gotAnything = true; }
                    if (dl["reasoning_content"] is JsonValue rv && rv.TryGetValue<string>(out string? rtxt) && rtxt.Length > 0)
                    { scan.Append(rtxt); gotAnything = true; }
                    // O loop mora nos ARGUMENTS do tool call (o parser do llama
                    // descarta o resto). Sem isto o detector olhava so o content.
                    if (dl["tool_calls"] is JsonArray tcs)
                        foreach (var tcn in tcs)
                        {
                            if (tcn is not JsonObject tco) continue;
                            int idx = tco["index"]?.GetValue<int>() ?? 0;
                            if (!calls.TryGetValue(idx, out var cur))
                                cur = (tco["id"]?.GetValue<string>() ?? "", "", new StringBuilder());
                            string? nm = tco["function"]?["name"]?.GetValue<string>();
                            string? ar = tco["function"]?["arguments"]?.GetValue<string>();
                            if (!string.IsNullOrEmpty(nm)) { cur.Name = nm; gotAnything = true; }
                            if (!string.IsNullOrEmpty(ar)) { cur.Args.Append(ar); gotAnything = true; }
                            calls[idx] = cur;
                            // Args tem janela PROPRIA (AntiRepeatArgsWindow): arquivo
                            // grande repete trechos legitimos e o corte no meio da call
                            // derrubava o turno (o salvage recusa args truncado). O texto
                            // (content/reasoning) segue na janela do conteudo.
                            // `edit`/`write` NAO abortam por args: o args e' o arquivo
                            // inteiro e a repeticao pode ser trecho legitimo; cortar a
                            // call gera JSON invalido, o salvage recusa e o modelo repete
                            // o aviso em prosa (medido 2026-10-30 no algo-cs-sort, 6x).
                            // O loop dessas tools e' contido pelo ToolsBreaker + guardas
                            // (conteudo elidido/reescrita total) e pelo compilador.
                            if (AntiRepeat && cur.Args.Length >= ToolArgsAntiRepeatReply.ProbeMinChars
                                && cur.Name is not ("edit" or "write"))
                            {
                                string argsSoFar = cur.Args.ToString();
                                if (new ToolArgsAntiRepeatReply(AntiRepeatArgsWindow).IsRepeat(argsSoFar))
                                {
                                    Log(new JsonObject { ["event"] = "anti-repeat", ["where"] = "stream-abort-tool-args",
                                        ["tool"] = cur.Name, ["window"] = AntiRepeatArgsWindow, ["chars"] = argsSoFar.Length });
                                    tripped = true;
                                    break;
                                }
                            }
                        }
                    if (tripped) break;
                    if (scan.Length - checkedAt >= 512)
                    {
                        checkedAt = scan.Length;
                        if (CheckRepeat(scan.ToString(), "stream-abort"))
                        {
                            tripped = true;
                            Log(new JsonObject { ["event"] = "anti-repeat", ["where"] = "stream-abort", ["window"] = AntiRepeatWindow, ["chars"] = scan.Length });
                            break;   // para de ler; o dispose/cancel abaixo corta o upstream
                        }
                    }
                }
            }
            if (tripped)
            {
                try { resp.Dispose(); } catch { }
                try { cts.Cancel(); } catch { }
            }

            // Salvage: entra toda call que tenha nome E args JSON parseavel. Args
            // truncado (o abort corta no meio) NAO pode virar tool call: o cliente
            // reenvia essa assistant e o llama responde 500 no parse, repetindo para
            // sempre. Nesse caso o texto avisa e pede o reenvio correto.
            var keep = new JsonArray();
            var brokenArgs = new List<string>();
            foreach (var kv in calls)
            {
                if (string.IsNullOrEmpty(kv.Value.Name)) continue;
                string a = kv.Value.Args.ToString();
                if (!ToolCallRepair.ArgsOk(a)) { brokenArgs.Add(kv.Value.Name); continue; }
                keep.Add(new JsonObject
                {
                    ["id"] = string.IsNullOrEmpty(kv.Value.Id) ? "call_" + kv.Key : kv.Value.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = kv.Value.Name, ["arguments"] = a }
                });
            }
            string cbody = content.ToString();
            if (brokenArgs.Count > 0)
            {
                cbody = (cbody.Length > 0 ? cbody + "\n\n" : "")
                        + ToolCallRepair.Notice(brokenArgs);
                Log(new JsonObject { ["event"] = "salvage-args-rejected", ["tools"] = string.Join(",", brokenArgs) });
            }
            // Sem nada recebido (stream vazio) e sem trip: cai no caminho normal.
            // Qualquer delta vira resposta — nunca re-gerar (dobraria o custo).
            if (!gotAnything) { resp.Dispose(); return false; }

            var msg = new JsonObject { ["role"] = "assistant", ["content"] = cbody };
            if (keep.Count > 0) msg["tool_calls"] = keep;
            var outObj = new JsonObject
            {
                ["id"] = "chatcmpl-bridge-" + Guid.NewGuid().ToString("N")[..12],
                ["object"] = "chat.completion",
                ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["model"] = Model,
                ["choices"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0,
                    ["finish_reason"] = keep.Count > 0 ? "tool_calls" : "stop",
                    ["message"] = msg
                }),
                ["usage"] = new JsonObject
                {
                    ["prompt_tokens"] = inTok,
                    ["completion_tokens"] = outTok,
                    ["total_tokens"] = inTok + outTok
                }
            };
            string json = outObj.ToJsonString(SerOpts);
            RecordChatActivity(json);
            // Este caminho (anti-repeat com abort em stream) escreve o JSON
            // direto no ctx, sem passar pelo bloco non-stream abaixo que
            // contabilizava a metrica. Mantem a t/s antiga: wall clock do
            // request inteiro + tokens de usage do chunk final.
            RecordChatUsage(outObj["usage"], null, (DateTime.UtcNow - startedAt).TotalSeconds);

            // No caminho de abort em stream NAO fazemos retry: o retry pos-hoc
            // re-gerava o turno inteiro e degenerava de novo (medido). Ficamos
            // com o SALVAGE dos tool calls ja fechados + content acumulado.
            if (tripped)
                Log(new JsonObject { ["event"] = "anti-repeat-salvage", ["where"] = "stream-abort", ["calls"] = keep.Count });

            var payload = JsonNode.Parse(json)?.AsObject();
            if (payload is null) { resp.Dispose(); return false; }
            WriteJson(ctx, 200, payload);
            if (Diagnostic)
            {
                Log(new JsonObject
                {
                    ["event"] = "traffic", ["mode"] = "chat", ["model"] = Model,
                    ["req_len"] = outBody.Length, ["req"] = outBody,
                    ["res_len"] = json.Length, ["res"] = json,
                    ["aborted"] = tripped
                });
                var perf = new JsonObject { ["event"] = "response", ["mode"] = "chat", ["model"] = Model, ["chars"] = json.Length };
                AddPerf(perf, startedAt, json, outBody);
                Log(perf);
            }
            return true;
        }
        catch
        {
            try { resp?.Dispose(); } catch { }
            return false;
        }
    }


    /// <summary>Le a resposta non-stream do shim, converte o envelope JSON em
    /// tool_calls (ou content final) e devolve JSON ou Chat SSE conforme o cliente.</summary>
    static void ShimChatResponse(HttpListenerContext ctx, HttpResponseMessage upstreamResponse, string requestBody,
        bool wantStream, bool headersSent, DateTime startedAt)
    {
        string raw = upstreamResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!upstreamResponse.IsSuccessStatusCode)
        {
            if (!headersSent)
            {
                WriteJson(ctx, (int)upstreamResponse.StatusCode, new JsonObject
                {
                    ["error"] = new JsonObject
                    {
                        ["message"] = "tool-shim: upstream " + (int)upstreamResponse.StatusCode + " — " + Truncate(raw, 400),
                        ["type"] = "bridge_error"
                    }
                });
            }
            else
            {
                var errRoot = new JsonObject
                {
                    ["id"] = "chatcmpl-" + Guid.NewGuid().ToString("N")[..8],
                    ["object"] = "chat.completion",
                    ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["model"] = Model,
                    ["choices"] = new JsonArray(new JsonObject
                    {
                        ["index"] = 0,
                        ["message"] = new JsonObject
                        {
                            ["role"] = "assistant",
                            ["content"] = "tool-shim: upstream " + (int)upstreamResponse.StatusCode + " — " + Truncate(raw, 400)
                        },
                        ["finish_reason"] = "stop"
                    })
                };
                WriteChatSse(ctx, errRoot, headersSent);
            }
            return;
        }
        JsonObject? obj = null;
        try { obj = JsonNode.Parse(raw)?.AsObject(); } catch { }
        if (obj is null)
        {
            if (!headersSent)
            {
                WriteJson(ctx, 502, new JsonObject
                {
                    ["error"] = new JsonObject
                    {
                        ["message"] = "tool-shim: resposta upstream invalida",
                        ["type"] = "bridge_error"
                    }
                });
            }
            else
            {
                var errRoot = new JsonObject
                {
                    ["id"] = "chatcmpl-" + Guid.NewGuid().ToString("N")[..8],
                    ["object"] = "chat.completion",
                    ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["model"] = Model,
                    ["choices"] = new JsonArray(new JsonObject
                    {
                        ["index"] = 0,
                        ["message"] = new JsonObject
                        {
                            ["role"] = "assistant",
                            ["content"] = "tool-shim: resposta upstream invalida"
                        },
                        ["finish_reason"] = "stop"
                    })
                };
                WriteChatSse(ctx, errRoot, headersSent);
            }
            return;
        }
        var transformed = ToolShim.TransformResponse(obj);
        RecordChatActivity(transformed.ToJsonString(SerOpts));
        string json = transformed.ToJsonString(SerOpts);
        if (!wantStream)
            WriteJson(ctx, 200, transformed);
        else
            WriteChatSse(ctx, transformed, headersSent);
        if (Diagnostic)
        {
            Log(new JsonObject
            {
                ["event"] = "traffic", ["mode"] = "chat-shim", ["model"] = Model,
                ["req_len"] = requestBody.Length, ["req"] = requestBody,
                ["res_len"] = json.Length, ["res"] = json
            });
            var perf = new JsonObject { ["event"] = "response", ["mode"] = "chat-shim", ["model"] = Model, ["chars"] = json.Length };
            AddPerf(perf, startedAt, json, requestBody);
            Log(perf);
        }
    }

    static string? RetryBypassChat(string outBody, HttpListenerContext ctx)
    {
        try
        {
            var node = JsonNode.Parse(outBody)?.AsObject();
            if (node?["messages"] is not JsonArray msgs) return null;
            msgs.Add(new JsonObject { ["role"] = "user", ["content"] = AntiRepeatNotice });
            var retry = new HttpRequestMessage(HttpMethod.Post, Upstream + "/v1/chat/completions")
            { Content = new StringContent(node.ToJsonString(SerOpts), Encoding.UTF8, "application/json") };
            WithAuth(retry, ctx);
            using var r = Http.Send(retry);
            if (!r.IsSuccessStatusCode) return null;
            using var rd = new StreamReader(r.Content.ReadAsStream(), new UTF8Encoding(false));
            return rd.ReadToEnd();
        }
        catch { return null; }
    }

    /// <summary>Bypass responses: 1 retry de contencao com o aviso anexado ao input.</summary>
    static string? RetryBypassResponses(JsonObject req, HttpListenerContext ctx)
    {
        try
        {
            if (req["input"] is not JsonArray input) return null;
            input.Add(new JsonObject
            {
                ["type"] = "message", ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = AntiRepeatNotice })
            });
            var retry = new HttpRequestMessage(HttpMethod.Post, Upstream + "/v1/responses")
            { Content = new StringContent(req.ToJsonString(SerOpts), Encoding.UTF8, "application/json") };
            WithAuth(retry, ctx);
            using var r = Http.Send(retry);
            if (!r.IsSuccessStatusCode) return null;
            using var rd = new StreamReader(r.Content.ReadAsStream(), new UTF8Encoding(false));
            return rd.ReadToEnd();
        }
        catch { return null; }
    }

    /// <summary>
    /// Envia ao upstream e, em ABORTO de conexão (o llama cancela a task quando
    /// o parser rejeita a saída), repete UMA vez com nudge de outra abordagem e
    /// temperatura de desvio. Reenviar o mesmo corpo na mesma temperatura tende
    /// a repetir a mesma saída; por isso o retry muda o input e o sampling.
    /// Segunda falha = <see cref="RetryNudge.UpstreamAbortException"/> (o catch
    /// global devolve 502 estruturado em vez do 500 opaco).
    /// </summary>
    /// <param name="ctx">Contexto HTTP (auth e path para o log).</param>
    /// <param name="forward">Request já montado para o upstream.</param>
    /// <param name="body">Corpo JSON enviado (base do retry com nudge).</param>
    /// <param name="option">Modo de leitura da resposta.</param>
    /// <param name="requestedModel">Model id PEDIDO (com sufixo de role, antes do
    /// rewrite para o id servido): decide a precedência do desvio de temperatura.</param>
    /// <returns>Resposta do upstream (primeira ou do retry).</returns>
    static HttpResponseMessage SendWithAbortRetry(HttpListenerContext ctx, HttpRequestMessage forward,
        string body, HttpCompletionOption option = HttpCompletionOption.ResponseContentRead,
        string? requestedModel = null)
    {
        try
        {
            return RecoverOverflow(ctx, Http.Send(forward, option), body, forward, option);
        }
        catch (Exception ex) when (RetryNudge.IsUpstreamAbort(ex))
        {
            double current = Temperature;
            try { current = JsonNode.Parse(body)?["temperature"]?.GetValue<double>() ?? Temperature; } catch { }
            // Mesma precedencia da temperatura do turno: role resolvido manda;
            // sem role, o valor global do host; sem ele, o perfil de desvio.
            bool hasRole = !string.IsNullOrEmpty(requestedModel)
                           && TryProfile(requestedModel, out _, out _);
            double detour = Sampling.DetourReference(hasRole, Profiles, AuxTemp);
            string? retryBody = RetryNudge.WithAbortNudge(body, current, detour);
            Metrics.UpstreamAbort(true);
            Log(new JsonObject
            {
                ["event"] = "upstream-abort-retry",
                ["path"] = ctx.Request.Url?.AbsolutePath ?? "",
                ["error"] = Truncate(ex.Message, 300),
                ["temperature"] = Sampling.ForRetry(current, detour),
                ["nudged"] = retryBody is not null
            });
            if (retryBody is null) throw new RetryNudge.UpstreamAbortException(ex.Message, ex);
            var retry = new HttpRequestMessage(forward.Method, forward.RequestUri)
            {
                Content = new StringContent(retryBody, Encoding.UTF8, "application/json")
            };
            WithAuth(retry, ctx);
            try
            {
                return RecoverOverflow(ctx, Http.Send(retry, option), retryBody, retry, option);
            }
            catch (Exception ex2) when (RetryNudge.IsUpstreamAbort(ex2))
            {
                Metrics.UpstreamAbort(false);
                throw new RetryNudge.UpstreamAbortException(ex2.Message + " (apos 1 retry com nudge)", ex2);
            }
        }
    }

    /// <summary>
    /// Recuperacao de estouro de contexto no upstream. O 400
    /// <c>exceed_context_size</c> do llama nao e' "ponte fora": o INPUT nao cabe no
    /// slot. Escalada (regra no Core, loop aqui): 1) nudge — lembrete de economia
    /// ao modelo, sem cortar conteudo; 2) compactacao — <see cref="ContextGuard.Compact"/>,
    /// unica rotina que encolhe o input; 3) 413 <c>context_compact_required</c>.
    /// </summary>
    /// <remarks>
    /// So' vale para corpo no formato chat (com <c>messages</c>): e' o unico com
    /// historico compactavel. Nos demais formatos a resposta volta intacta. O
    /// corpo do erro e' lido para a deteccao; quando NAO e' estouro ele e'
    /// recolocado na resposta para o chamador repassar como antes.
    /// </remarks>
    /// <param name="ctx">Contexto HTTP (auth da retentativa).</param>
    /// <param name="response">Resposta recebida do upstream.</param>
    /// <param name="body">Corpo enviado (chat: com <c>messages</c>).</param>
    /// <param name="forward">Request original (metodo/URI da retentativa).</param>
    /// <param name="option">Modo de leitura da resposta.</param>
    /// <returns>Resposta boa, resposta de erro reencolada ou 413.</returns>
    static HttpResponseMessage RecoverOverflow(HttpListenerContext ctx, HttpResponseMessage response,
        string body, HttpRequestMessage forward, HttpCompletionOption option)
    {
        if (response.IsSuccessStatusCode || !ContextGuardEnabled() || ContextTokens <= 0)
            return response;
        try { if (JsonNode.Parse(body)?["messages"] is not JsonArray) return response; }
        catch { return response; }
        string errBody;
        try { errBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult(); }
        catch { return response; }
        if (!Rochas.OpenCodeBridge.Core.ContextGuard.LooksLikeOverflow(errBody, out int promptTokens))
        {
            // Nao e' estouro: o corpo lido volta no lugar do stream consumido.
            response.Content.Dispose();
            response.Content = new StringContent(errBody, Encoding.UTF8, "application/json");
            return response;
        }
        int status = (int)response.StatusCode;
        response.Dispose();
        int perSlot = ContextTokens / Math.Max(1, ContextSlots);
        Log(new JsonObject
        {
            ["event"] = "context-guard",
            ["where"] = "upstream-400",
            ["status"] = status,
            ["prompt_tokens"] = promptTokens,
            ["ctx_tokens"] = ContextTokens,
            ["slots"] = ContextSlots,
            ["fraction"] = perSlot > 0 ? Math.Round((double)promptTokens / perSlot, 4) : 0
        });
        string current = body;
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            // 1) nudge (lembrete, sem trim); 2) compactacao (encolhe o input).
            string? next = attempt == 1
                ? NudgeOverflowBody(current)
                : CompactOverflowBody(current, ContextTokens, ContextSlots);
            if (next is null) break;
            current = next;
            var retry = new HttpRequestMessage(forward.Method, forward.RequestUri)
            {
                Content = new StringContent(current, Encoding.UTF8, "application/json")
            };
            WithAuth(retry, ctx);
            HttpResponseMessage resp;
            try { resp = Http.Send(retry, option); }
            catch (Exception ex)
            {
                Log(new JsonObject { ["event"] = "context-recovery-fail", ["attempt"] = attempt, ["error"] = Truncate(ex.Message, 300) });
                break;
            }
            Log(new JsonObject
            {
                ["event"] = attempt == 1 ? "context-nudge" : "context-compact",
                ["where"] = "upstream-400",
                ["attempt"] = attempt,
                ["req_chars"] = current.Length,
                ["ok"] = resp.IsSuccessStatusCode
            });
            if (resp.IsSuccessStatusCode) return resp;
            string err2;
            try { err2 = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult(); }
            catch { err2 = ""; }
            resp.Dispose();
            if (!Rochas.OpenCodeBridge.Core.ContextGuard.LooksLikeOverflow(err2, out _))
            {
                // Outro erro (nao e' mais estouro): devolve como veio.
                return new HttpResponseMessage((HttpStatusCode)(int)status)
                {
                    Content = new StringContent(err2, Encoding.UTF8, "application/json")
                };
            }
        }
        // 3) sinal honesto: nem a compactacao cabe -> o cliente compacta a sessao.
        return new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge)
        {
            Content = new StringContent(new JsonObject
            {
                ["error"] = new JsonObject
                {
                    ["message"] = $"Contexto estourou o slot ({promptTokens} tokens de {perSlot}). Compacte a sessao e tente de novo.",
                    ["type"] = "invalid_request_error",
                    ["code"] = "context_compact_required"
                }
            }.ToJsonString(SerOpts), Encoding.UTF8, "application/json")
        };
    }

    /// <summary>Corpo com o lembrete de economia (1a tentativa); null quando nao ha o que fazer.</summary>
    /// <param name="body">Corpo chat atual.</param>
    /// <returns>Corpo nudgado ou null.</returns>
    static string? NudgeOverflowBody(string body)
    {
        var node = JsonNode.Parse(body)?.AsObject();
        if (node?["messages"] is not JsonArray msgs) return null;
        // Nao duplica: o cliente pode reenviar um corpo que ja' carrega o lembrete.
        foreach (var m in msgs)
            if (m is JsonObject o && o["content"] is JsonValue v && v.TryGetValue<string>(out string? s)
                && s.Contains(Rochas.OpenCodeBridge.Core.ContextGuard.NudgeText, StringComparison.Ordinal))
                return null;
        msgs.Add(new JsonObject { ["role"] = "user", ["content"] = Rochas.OpenCodeBridge.Core.ContextGuard.NudgeText });
        return node!.ToJsonString(SerOpts);
    }

    /// <summary>Corpo compactado (2a tentativa) ate' 70% do slot; null quando nada a cortar.</summary>
    /// <param name="body">Corpo chat atual.</param>
    /// <param name="tokens">Ctx TOTAL servido.</param>
    /// <param name="slots">Slots paralelos do llama.</param>
    /// <returns>Corpo compactado ou null.</returns>
    static string? CompactOverflowBody(string body, int tokens, int slots)
    {
        var node = JsonNode.Parse(body)?.AsObject();
        if (node?["messages"] is not JsonArray msgs) return null;
        // Teto de 70%: o 400 provou que 100% do slot nao cabe; cortar ate' 95%
        // deixaria o proximo turno estourando de novo (o cliente reenvia o historico).
        if (!Rochas.OpenCodeBridge.Core.ContextGuard.Compact(msgs, tokens, slots, 0.70)) return null;
        return node!.ToJsonString(SerOpts);
    }

    /// <summary>
    /// Extrai o system fundido no inicio do <c>input</c> (gramatica do protocolo
    /// no Core: <see cref="ResponsesTranslation.TakeLeadingSystem"/>).
    /// </summary>
    /// <param name="req">Request Responses (mutado: o system inicial sai do input).</param>
    /// <returns>Texto do system inicial (vazio quando nao havia).</returns>
    static string TakeLeadingSystem(JsonObject req)
        => ResponsesTranslation.TakeLeadingSystem(req);

    /// <summary>Funde system/developer do input num unico system inicial (o template exige system primeiro).</summary>
    /// <param name="req">Request Responses (mutado no lugar).</param>
    /// <returns>True se reordenou.</returns>
static bool NormalizeSystemFirst(JsonObject req)
    {
        if (req["input"] is not JsonArray list) return false;
        var sys = new StringBuilder();
        // instructions vira system no llama: fundir junto senao sao 2 systems.
        if (req.TryGetPropertyValue("instructions", out JsonNode? ins)
            && ins is JsonValue iv && iv.TryGetValue<string>(out string? st) && !string.IsNullOrWhiteSpace(st))
            sys.Append(st.Trim());
        req.Remove("instructions");
        var rest = new JsonArray();
        foreach (var it in list)
        {
            if (it is JsonObject o && o["type"]?.GetValue<string>() == "message"
                && o["role"]?.GetValue<string>() is string role && (role == "system" || role == "developer"))
            {
                string t = ExtractResponseText(o["content"]);
                if (!string.IsNullOrWhiteSpace(t)) { if (sys.Length > 0) sys.Append("\n\n"); sys.Append(t.Trim()); }
                continue;
            }
            rest.Add(it?.DeepClone());
        }
        if (sys.Length == 0) return false;
        var final = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "message", ["role"] = "system",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = LegacyHarness ? WithSystemGuardrail(sys.ToString(), QwenStd) : sys.ToString() })
            }
        };
        foreach (var it in rest) final.Add(it?.DeepClone());
        req["input"] = final;
        return true;
    }


    /// <summary>Shim no caminho Responses stream: upstream non-stream, envelope
    /// JSON -> tool_calls, chat -> Responses e SSE tipado sintetizado.</summary>
    /// <param name="ctx">Contexto HTTP.</param>
    /// <param name="chat">Chat request ja montado.</param>
    /// <param name="requestedModel">Model id p/ eco.</param>
    /// <param name="temperature">Efetiva.</param>
    /// <param name="thinking">Modo thinking efetivo.</param>
    /// <param name="toolCount">Tools originais (para o mapeamento de function_call).</param>
    /// <param name="startedAt">Inicio p/ elapsed.</param>
    /// <param name="tempSource">Procedencia da temperatura (vai para o log).</param>
    static void HandleResponsesStreamShim(HttpListenerContext ctx, JsonObject chat,
        string requestedModel, double temperature, string thinking, int toolCount, DateTime startedAt,
        string tempSource = "")
    {
        chat["stream"] = false;
        chat.Remove("stream_options");
        var chatRequest = new HttpRequestMessage(HttpMethod.Post, Upstream + "/v1/chat/completions")
        {
            Content = new StringContent(chat.ToJsonString(SerOpts), Encoding.UTF8, "application/json")
        };
        WithAuth(chatRequest, ctx);

        using var upstreamResponse = SendWithAbortRetry(ctx, chatRequest, chat.ToJsonString(SerOpts),
            HttpCompletionOption.ResponseContentRead, requestedModel);
        string responseBody = upstreamResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!upstreamResponse.IsSuccessStatusCode)
        {
            Log(new JsonObject { ["event"] = "error", ["mode"] = "shim-responses", ["status"] = (int)upstreamResponse.StatusCode, ["upstream_error"] = Truncate(responseBody, 500) });
            ctx.Response.StatusCode = (int)upstreamResponse.StatusCode;
            byte[] errorBytes = Encoding.UTF8.GetBytes(responseBody);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = errorBytes.Length;
            ctx.Response.OutputStream.Write(errorBytes);
            ctx.Response.Close();
            return;
        }

        var chatJson = JsonNode.Parse(responseBody)?.AsObject() ?? new JsonObject();
        ToolShim.TransformResponse(chatJson);
        var responseObj = ChatToResponses(chatJson, requestedModel, temperature, thinking, toolCount > 0, chat["messages"]);
        RecordResponsesActivity(responseObj);

        double elapsed = (DateTime.UtcNow - startedAt).TotalSeconds;
        int inTok = responseObj["usage"]?["input_tokens"]?.GetValue<int>() ?? 0;
        int outTok = responseObj["usage"]?["output_tokens"]?.GetValue<int>() ?? 0;
        Log(new JsonObject
        {
            ["event"] = "response", ["model"] = requestedModel, ["stream"] = true,
            ["temperature"] = temperature, ["temperature_source"] = tempSource,
            ["shim"] = true, ["message_count"] = chat["messages"]!.AsArray().Count,
            ["tool_count"] = toolCount,
            ["has_tool_call"] = responseObj["output"]!.AsArray().Any(i => i?["type"]?.GetValue<string>() == "function_call"),
            ["elapsed"] = Math.Round(elapsed, 2),
            ["input_tokens"] = inTok, ["output_tokens"] = outTok,
            ["tps_output"] = Math.Round(elapsed > 0 ? outTok / elapsed : 0, 1)
        });
        if (Diagnostic)
        {
            string dumpRes = responseObj.ToJsonString();
            var traffic = new JsonObject { ["event"] = "traffic", ["mode"] = "shim-translate", ["model"] = requestedModel, ["req_len"] = chat.ToJsonString().Length, ["req"] = chat.ToJsonString(), ["res_len"] = dumpRes.Length, ["res"] = dumpRes };
            AddPerf(traffic, startedAt, dumpRes, chat.ToJsonString());
            Log(traffic);
        }
        AddMetrics(inTok, outTok, elapsed);

        byte[] sse = BuildSse(responseObj, thinking);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/event-stream; charset=utf-8";
        ctx.Response.Headers["Cache-Control"] = "no-cache";
        ctx.Response.SendChunked = true;
        ctx.Response.OutputStream.Write(sse);
        ctx.Response.OutputStream.Flush();
        ctx.Response.Close();
    }

    /// <summary>Streaming real: repassa deltas do vLLM na hora, fecha com completed.</summary>
    /// <param name="ctx">Contexto HTTP.</param>
    /// <param name="chat">Chat request ja montado.</param>
    /// <param name="requestedModel">Model id p/ eco.</param>
    /// <param name="temperature">Efetiva.</param>
    /// <param name="thinking">Modo thinking efetivo.</param>
    /// <param name="startedAt">Inicio p/ elapsed.</param>
    /// <param name="tempSource">Procedencia da temperatura (vai para o log).</param>
    static void HandleResponsesStream(HttpListenerContext ctx, JsonObject chat,
        string requestedModel, double temperature, string thinking, DateTime startedAt,
        string tempSource = "")
    {
        // Pede stream ao upstream (com usage no fim p/ fechar os tokens).
        chat["stream"] = true;
        chat["stream_options"] = new JsonObject { ["include_usage"] = true };

var chatRequest = new HttpRequestMessage(HttpMethod.Post, Upstream + "/v1/chat/completions")
        {
            Content = new StringContent(chat.ToJsonString(SerOpts), Encoding.UTF8, "application/json")
        };
        WithAuth(chatRequest, ctx);

        // Headers do upstream ANTES de abrir o SSE: se der erro aqui,
        // devolvemos JSON de erro limpo (nada foi enviado ao cliente ainda).
        using var upstreamResponse = SendWithAbortRetry(ctx, chatRequest, chat.ToJsonString(SerOpts),
            HttpCompletionOption.ResponseHeadersRead, requestedModel);
        if (!upstreamResponse.IsSuccessStatusCode)
        {
            string errorBody = upstreamResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            Log(new JsonObject { ["event"] = "error", ["mode"] = "live", ["status"] = (int)upstreamResponse.StatusCode, ["upstream_error"] = Truncate(errorBody, 500) });
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
            ["output"] = new JsonArray(), ["parallel_tool_calls"] = false,
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
        // Cliente com harness proprio (opencode): passa reto, sem hold-back de
        // marcador e sem coercao — o cliente gerencia as tools.
        bool managed = ManagedFor(requestedModel);
        // Efeito "digitando" e gestao sao do cliente direto (Web UI). No
        // opencode (perfil) o texto agrega como antes: caminho livre.
        bool perDelta = StreamPerDelta && managed;
        // Coercao de tool call narrada: rotina PROPRIA (--coerce-narrated),
        // independente do LegacyHarness, limitada ao cliente gerido (cliente com
        // harness proprio gerencia as tools sozinho).
        bool coerceOn = CoerceNarrated && managed;
        bool repeatTrip = false;   // AntiRepeat: thinking repetido detectado, aborta e conteve (1 retry)
        string carry = "";   // texto ainda nao classificado (tag pode partir no meio do chunk)
        string reasonBuf = "";   // hold-back do reasoning: tag de controle pode partir entre chunks
        var toolAccum = new SortedDictionary<int, (string Id, string Name, StringBuilder Args)>();
        // Sonda anti-repeat nos ARGUMENTS de uma mesma call (marcador curto repetido
        // como "<tool_call|>" nao cabe na janela de 512 do thinking/content e o args
        // cresce ate o max_tokens truncar o JSON). So loga: nao altera a saida.
        var argsProbeLogged = new HashSet<int>();
        int inputTokens = 0, outputTokens = 0;

        // Emite texto de reasoning ja limpo (abre o item no primeiro delta).
        void EmitThinking(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (!thinkOpen && thinking == "events")
            {
                reasoningId = "rs_" + NewId();
                // Formato NATIVO do llama.cpp (reasoning_text + content). O item
                // PRECISA nascer com content[0] do tipo reasoning_text: o parser
                // do opencode faz Y.output[idx].content[content_index] e lanca
                // "missing content at index" (delta descartado, pensamento so
                // aparecia no completed = quadro de snippet).
                Emit("response.output_item.added", new JsonObject
                {
                    ["type"] = "response.output_item.added", ["output_index"] = 0,
                    ["item"] = new JsonObject
                    {
                        ["id"] = reasoningId, ["summary"] = new JsonArray(), ["type"] = "reasoning",
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "reasoning_text", ["text"] = "" }),
                        ["encrypted_content"] = "", ["status"] = "in_progress"
                    }
                });
                thinkOpen = true;
            }
            // Modo "off": sem item reasoning; o raciocinio vira texto comum.
            if (!thinkOpen && thinking != "events") { FlushMessage(text); return; }
            if (!thinkOpen) return;
            thinkingText.Append(text);
            // AntiRepeat: cauda do thinking ja vista antes = loop invalido;
            // arma o flag (o loop de leitura aborta e o modelo e contido).
            if (!repeatTrip && CheckRepeat(thinkingText.ToString(), "thinking"))
            {
                repeatTrip = true;
                Log(new JsonObject { ["event"] = "anti-repeat", ["where"] = "thinking", ["window"] = AntiRepeatWindow, ["thinking_chars"] = thinkingText.Length });
            }
            // output_index/content_index sao OBRIGATORIOS no schema do opencode
            // para reasoning_text.delta; sem eles o evento e descartado e o
            // pensamento so aparece no fecho (quadro de snippet).
            Emit("response.reasoning_text.delta", new JsonObject
            {
                ["type"] = "response.reasoning_text.delta", ["delta"] = text, ["item_id"] = reasoningId,
                ["output_index"] = 0, ["content_index"] = 0
            });
        }

        // Recebe reasoning_content. Segura 12 chars (maior tag de controle) para
        // nao vazar tag partida entre chunks: o texto retido sai no chunk
        // seguinte, entao NAO ha truncamento — so atraso de uma tag de tamanho.
        void FlushThinking(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (thinking != "events") { EmitThinking(StripControlTags(text, "stream")); return; }
            reasonBuf = StripControlTags(reasonBuf + text, "stream");   // tags completas saem aqui
            int hold = PartialTagLen(reasonBuf);                        // so o prefixo ambiguo fica
            string emit = hold > 0 ? reasonBuf[..^hold] : reasonBuf;
            reasonBuf = hold > 0 ? reasonBuf[^hold..] : "";
            EmitThinking(emit);
        }

        // Descarrega o hold-back no fim do stream (sanitizado).
        void FlushReasonRest()
        {
            if (reasonBuf.Length == 0) return;
            string rest = StripControlTags(reasonBuf, "stream-tail");
            reasonBuf = "";
            EmitThinking(rest);
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
            // AntiRepeat tambem no content (agente sem thinking repete aqui).
            if (!repeatTrip && CheckRepeat(contentText.ToString(), "content"))
            {
                repeatTrip = true;
                Log(new JsonObject { ["event"] = "anti-repeat", ["where"] = "content", ["window"] = AntiRepeatWindow, ["content_chars"] = contentText.Length });
            }
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
                    if (perDelta)
                    {
                        // "Digitando": emite ja, mas segura (a) prefixo ambiguo de
                        // tag partida entre chunks e (b) a partir de um marcador de
                        // tool call NARRADA — o fecho decide entre descartar como
                        // chamada ou devolver como prosa. Sem (b) o efeito vaza
                        // "[Assistant tool call]: nome(...)" no content.
                        int hold = coerceOn ? ContentHoldBack(carry) : PartialTagPrefixLen(carry, " thinking");
                        int upto = carry.Length - hold;
                        if (upto > 0) { FlushMessage(carry[..upto]); carry = carry[upto..]; }
                    }
                    else if (carry.Length > 256) { FlushMessage(carry[..^16]); carry = carry[^16..]; }
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
                    if (perDelta)
                    {
                        int hold = PartialTagPrefixLen(carry, "</think>");
                        int upto = carry.Length - hold;
                        if (upto > 0) { FlushThinking(carry[..upto]); carry = carry[upto..]; }
                    }
                    else if (carry.Length > 256) { FlushThinking(carry[..^16]); carry = carry[^16..]; }
                    break;
                }
            }
        }

        // ---- Le o SSE do upstream e traduz delta a delta
        // AntiRepeat: ate 2 passagens; a 1a usa o fetch acima, na 2a o chat
        // ja leva o aviso e o upstream e buscado de novo.
        HttpResponseMessage RefetchUpstream()
        {
            var retryReq = new HttpRequestMessage(HttpMethod.Post, Upstream + "/v1/chat/completions")
            {
                Content = new StringContent(chat.ToJsonString(SerOpts), Encoding.UTF8, "application/json")
            };
            WithAuth(retryReq, ctx);
            return Http.Send(retryReq, HttpCompletionOption.ResponseHeadersRead);
        }
        HttpResponseMessage? retryResp = null;
        for (int attempt = 0; ; attempt++)
        {
            HttpResponseMessage srcResp;
            if (attempt == 0) srcResp = upstreamResponse;
            else
            {
                retryResp = RefetchUpstream();
                if (!retryResp.IsSuccessStatusCode) break;
                srcResp = retryResp;
            }
            using var attemptScope = attempt == 0 ? null as IDisposable : retryResp;
            using var stream = srcResp.Content.ReadAsStream();
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
                    {
                        acc.Args.Append(argsChunk);
                        if (AntiRepeat && acc.Args.Length >= ToolArgsAntiRepeatReply.ProbeMinChars && !argsProbeLogged.Contains(index))
                        {
                            string argsText = acc.Args.ToString();
                            if (new ToolArgsAntiRepeatReply(AntiRepeatArgsWindow).IsRepeat(argsText))
                            {
                                argsProbeLogged.Add(index);
                                Log(new JsonObject { ["event"] = "anti-repeat-args", ["tool"] = acc.Name,
                                    ["chars"] = argsText.Length, ["window"] = AntiRepeatArgsWindow });
                            }
                        }
                    }
                }
            }
            if (repeatTrip) break;   // AntiRepeat: para de consumir o loop invalido
        }

            if (!repeatTrip) break;
            // O retry de contencao e do ANTI-REPEAT, nao do harness legado: governa-lo
            // por --anti-repeat-retry deixa "cortar o loop" e "tentar de novo" como
            // coisas independentes, em vez de amarradas ao tubo puro.
            if (!AntiRepeatRetry) break;
            if (attempt >= 1) break;   // 1 retry de contencao no maximo
            // Contencao: descarta a janela repetida e devolve o aviso ao modelo.
            repeatTrip = false;
            thinkingText.Clear();
            contentText.Clear();
            carry = "";
            reasonBuf = "";
            inThink = false;
            chat["messages"]!.AsArray().Add(new JsonObject { ["role"] = "user", ["content"] = AntiRepeatNotice });
            Log(new JsonObject { ["event"] = "anti-repeat-retry" });
        }

        // ---- Fecho: a cauda segurada pode ser uma tool call NARRADA
        // (formato "[Assistant tool call]: nome({...})" que o opencode serializa
        // no historico). A coerção roda ANTES de descarregar a cauda: com o
        // efeito "digitando" o texto ja saiu chunk a chunk, entao a cauda nunca
        // pode ir para a prosa antes de sabermos se e chamada ou texto.
        string heldTail = carry;
        carry = "";
        if (inThink) { FlushThinking(heldTail); heldTail = ""; }

        if (coerceOn && toolAccum.Count == 0 && ReqHasTools(chat))
        {
            var knownTail = AnnouncedTools(chat["tools"]?.AsArray());
            var found = ExtractTextToolCalls(contentText.ToString() + heldTail, knownTail);
            foreach (var (name, args) in found)
                toolAccum[toolAccum.Count] = ("call_" + NewId(), name, new StringBuilder(args));
            if (found.Count > 0)
            {
                // Nada a retirar do que ja foi emitido? Retira: o texto final
                // (done/completed) para no inicio do marcador, coerente com os
                // deltas (a cauda segurada nunca foi emitida).
                int markerAt = TextToolMarkerIndex(contentText.ToString());
                if (markerAt >= 0) contentText.Length = markerAt;
                heldTail = "";
                Log(new JsonObject { ["event"] = "coerced", ["where"] = "stream",
                                     ["model"] = requestedModel, ["count"] = found.Count });
            }
        }
        // Ja havia call nativa e a cauda narrada e eco do historico: descarta.
        if (coerceOn && toolAccum.Count > 0 && heldTail.Length > 0 && TextToolMarkerIndex(heldTail) == 0)
            heldTail = "";
        if (heldTail.Length > 0) FlushMessage(heldTail);
        FlushReasonRest();

        int msgIndex = thinkOpen ? 1 : 0;
        if (thinkOpen)
        {
            string thinkingFinal = StripControlTags(thinkingText.ToString(), "done");
            // Nativo (llama.cpp): o texto vai em content[].text do tipo
            // reasoning_text; o reasoning_text.done consolida os deltas.
            Emit("response.reasoning_text.done", new JsonObject
            {
                ["type"] = "response.reasoning_text.done", ["item_id"] = reasoningId,
                ["output_index"] = 0, ["content_index"] = 0, ["text"] = thinkingFinal
            });
            Emit("response.output_item.done", new JsonObject
            {
                ["type"] = "response.output_item.done", ["output_index"] = 0,
                ["item"] = new JsonObject
                {
                    ["id"] = reasoningId, ["summary"] = new JsonArray(), ["type"] = "reasoning",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "reasoning_text", ["text"] = thinkingFinal }),
                    ["encrypted_content"] = "", ["status"] = "completed"
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

        // A coerção roda no fecho (bloco acima), sobre texto acumulado + cauda.

        int callIndex = (thinkOpen ? 1 : 0) + (msgOpen ? 1 : 0);
        var toolList = new List<(string Id, string Name, string Arguments)>();
        // AntiRepeat cross-turn: a chamada ja repetiu no historico, entao e loop. Nao
        // emitimos (o modelo so reenfileiraria o mesmo comando e o turno travaria aqui);
        // no lugar, devolve o aviso de contencao como texto para mudar de abordagem.
        var loopedSigs = AntiRepeat ? new RepeatedCall(2).Find(chat["messages"])
                                     : new HashSet<string>(StringComparer.Ordinal);
        var bannedHere = ToolBlacklist.TryGetValue(RoleOfModel(requestedModel), out var bh) ? bh
                         : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        RiskRefused.Clear();   // recusa vale no request corrente, nao para sempre
        foreach (var (_, acc) in toolAccum)
        {
            string arguments = acc.Args.ToString();
            // Bloqueio por role: a tool foi anunciada, mas a chamada nao sai.
            // Risco autorizado no turno anterior: a chamada sai, com o aval do
            // usuario registrado. Uma concessao = uma execucao.
            bool riskOk = RiskConsumeApproval(requestedModel, acc.Name, arguments);
            // Ordem de precedencia: blacklist de role > risco. A recusa do role
            // e limite de papel e nao se resolve com autorizacao do usuario.
            // Bloqueio de role virou PERMISSAO: o usuario decide, em vez de a
            // bridge recusar por conta propria. Mesmo primitive do risco.
            if (!riskOk && bannedHere.Contains(acc.Name) && RiskAsk)
            {
                string roleB = RoleOfModel(requestedModel);
                string bid = "risk_" + NewId();
                PendingRisks[bid] = new PendingRisk(acc.Name, arguments, roleB);
                Metrics.Deny("ring2", acc.Name, "papel");
                Log(new JsonObject { ["event"] = "tool-ask", ["tool"] = acc.Name, ["role"] = roleB,
                                     ["reason"] = "papel", ["call_id"] = bid });
                if (!msgOpen)
                {
                    messageId = "msg_" + NewId();
                    Emit("response.output_item.added", new JsonObject
                    {
                        ["type"] = "response.output_item.added", ["output_index"] = callIndex,
                        ["item"] = new JsonObject { ["id"] = messageId, ["type"] = "message",
                            ["role"] = "assistant", ["content"] = new JsonArray(), ["status"] = "in_progress" }
                    });
                    msgOpen = true;
                    callIndex++;
                }
                FlushMessage("\n\nEsta tool fica fora do meu papel. Pedi a confirmacao ao usuario na interface; aguardo a resposta.\n\n");
                var bitem = new JsonObject
                {
                    ["type"] = "function_call", ["id"] = bid, ["call_id"] = bid,
                    ["name"] = "question", ["arguments"] = RiskQuestionArgs(acc.Name, arguments, roleB, "papel")
                };
                Emit("response.output_item.added", new JsonObject
                {
                    ["type"] = "response.output_item.added", ["output_index"] = callIndex,
                    ["item"] = (JsonNode)bitem.DeepClone()
                });
                Emit("response.function_call_arguments.delta", new JsonObject
                {
                    ["type"] = "response.function_call_arguments.delta", ["item_id"] = bid,
                    ["output_index"] = callIndex, ["delta"] = bitem["arguments"]!.GetValue<string>()
                });
                Emit("response.function_call_arguments.done", new JsonObject
                {
                    ["type"] = "response.function_call_arguments.done", ["item_id"] = bid,
                    ["output_index"] = callIndex, ["arguments"] = bitem["arguments"]!.GetValue<string>()
                });
                Emit("response.output_item.done", new JsonObject
                {
                    ["type"] = "response.output_item.done", ["output_index"] = callIndex, ["item"] = bitem
                });
                callIndex++;
                continue;
            }
            if (!riskOk && bannedHere.Contains(acc.Name))
            {
                Metrics.Deny("ring2", acc.Name, "role");
                Log(new JsonObject { ["event"] = "tool-blocked", ["tool"] = acc.Name, ["role"] = RoleOfModel(requestedModel) });
                if (!msgOpen)
                {
                    messageId = "msg_" + NewId();
                    Emit("response.output_item.added", new JsonObject
                    {
                        ["type"] = "response.output_item.added", ["output_index"] = callIndex,
                        ["item"] = new JsonObject { ["id"] = messageId, ["type"] = "message",
                            ["role"] = "assistant", ["content"] = new JsonArray(), ["status"] = "in_progress" }
                    });
                    msgOpen = true;
                    callIndex++;
                }
                FlushMessage("\n\n" + string.Format(ToolBlockNotice, acc.Name, RoleOfModel(requestedModel)) + "\n\n");
                continue;
            }
            // Comando de risco sem aval: a chamada vira pergunta na interface.
            if (!riskOk && IsRiskCall(requestedModel, acc.Name, arguments))
            {
                string role = RoleOfModel(requestedModel);
                string qid = "risk_" + NewId();
                PendingRisks[qid] = new PendingRisk(acc.Name, arguments, role);
                Log(new JsonObject { ["event"] = "risk-ask", ["tool"] = acc.Name, ["role"] = role, ["call_id"] = qid });
                if (!msgOpen)
                {
                    messageId = "msg_" + NewId();
                    Emit("response.output_item.added", new JsonObject
                    {
                        ["type"] = "response.output_item.added", ["output_index"] = callIndex,
                        ["item"] = new JsonObject { ["id"] = messageId, ["type"] = "message",
                            ["role"] = "assistant", ["content"] = new JsonArray(), ["status"] = "in_progress" }
                    });
                    msgOpen = true;
                    callIndex++;
                }
                FlushMessage("\n\nEsta acao e de risco. Pedi a confirmacao ao usuario na interface;aguardo a resposta antes de rodar.\n\n");
                var qitem = new JsonObject
                {
                    ["type"] = "function_call", ["id"] = qid, ["call_id"] = qid,
                    ["name"] = "question", ["arguments"] = RiskQuestionArgs(acc.Name, arguments, role, "risco")
                };
                Emit("response.output_item.added", new JsonObject
                {
                    ["type"] = "response.output_item.added", ["output_index"] = callIndex,
                    ["item"] = (JsonNode)qitem.DeepClone()
                });
                Emit("response.function_call_arguments.delta", new JsonObject
                {
                    ["type"] = "response.function_call_arguments.delta", ["item_id"] = qid,
                    ["output_index"] = callIndex, ["delta"] = qitem["arguments"]!.GetValue<string>()
                });
                Emit("response.function_call_arguments.done", new JsonObject
                {
                    ["type"] = "response.function_call_arguments.done", ["item_id"] = qid,
                    ["output_index"] = callIndex, ["arguments"] = qitem["arguments"]!.GetValue<string>()
                });
                Emit("response.output_item.done", new JsonObject
                {
                    ["type"] = "response.output_item.done", ["output_index"] = callIndex, ["item"] = qitem
                });
                callIndex++;
                continue;
            }
            if (riskOk)
                FlushMessage("\n\nO usuario autorizou esta acao; executando agora.\n\n");
            if (loopedSigs.Contains(ToolSig.Of(acc.Name, arguments)))
            {
                repeatTrip = true;
                Log(new JsonObject { ["event"] = "anti-repeat-loop", ["tool"] = acc.Name });
                if (!msgOpen)
                {
                    messageId = "msg_" + NewId();
                    Emit("response.output_item.added", new JsonObject
                    {
                        ["type"] = "response.output_item.added", ["output_index"] = callIndex,
                        ["item"] = new JsonObject { ["id"] = messageId, ["type"] = "message",
                            ["role"] = "assistant", ["content"] = new JsonArray(), ["status"] = "in_progress" }
                    });
                    msgOpen = true;
                    callIndex++;
                }
                FlushMessage("\n\n" + AntiRepeatNotice + "\n\n");
                continue;
            }
            var effective = UnwrapExecuteCalls(acc.Name, arguments);
            if (effective.Count == 0) effective.Add((acc.Name, arguments));
            for (int ci = 0; ci < effective.Count; ci++)
            {
                string callId = ci == 0 ? acc.Id : acc.Id + "_" + ci;
                var (effectiveName, effectiveArgs) = SanitizeToolCall(effective[ci].Name, effective[ci].Arguments);
                toolList.Add((callId, effectiveName, effectiveArgs));
                var item = new JsonObject
                {
                    ["type"] = "function_call", ["id"] = callId, ["call_id"] = callId,
                    ["name"] = effectiveName, ["arguments"] = effectiveArgs
                };
                Emit("response.output_item.added", new JsonObject
                {
                    ["type"] = "response.output_item.added", ["output_index"] = callIndex,
                    ["item"] = (JsonNode)item.DeepClone()
                });
                Emit("response.function_call_arguments.delta", new JsonObject
                {
                    ["type"] = "response.function_call_arguments.delta", ["item_id"] = callId,
                    ["output_index"] = callIndex, ["delta"] = effectiveArgs
                });
                Emit("response.function_call_arguments.done", new JsonObject
                {
                    ["type"] = "response.function_call_arguments.done", ["item_id"] = callId,
                    ["output_index"] = callIndex, ["arguments"] = effectiveArgs
                });
                Emit("response.output_item.done", new JsonObject
                {
                    ["type"] = "response.output_item.done", ["output_index"] = callIndex, ["item"] = item
                });
                callIndex++;
            }
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
        var finalObj = ChatToResponses(synthetic, requestedModel, temperature, thinking, ReqHasTools(chat));
        finalObj["id"] = respId;
        RecordResponsesActivity(finalObj);
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
            ["temperature"] = temperature, ["temperature_source"] = tempSource,
            ["message_count"] = chat["messages"]!.AsArray().Count, ["tool_count"] = toolList.Count,
            ["has_tool_call"] = toolList.Count > 0, ["has_reasoning"] = thinkOpen,
            ["elapsed"] = Math.Round(liveElapsed, 2),
            ["input_tokens"] = inputTokens, ["output_tokens"] = outputTokens,
            ["tps_output"] = Math.Round(liveElapsed > 0 ? outputTokens / liveElapsed : 0, 1)
        });
        AddMetrics(inputTokens, outputTokens, liveElapsed);
        ctx.Response.Close();
    }




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
        // Sonda (--diagnostic 1): conta entrada/saida e nomeia o que caiu. A
        // conversao tem 3 ramos com `continue` e nenhum log; sem isto uma tool
        // malformada desaparece em silencio e o modelo so responde prosa.
        var dropped = new JsonArray();
        var defaulted = new JsonArray();
        foreach (var toolNode in list)
        {
            if (toolNode is not JsonObject entry) { dropped.Add("nao-objeto"); continue; }
            string type = entry["type"]?.GetValue<string>() ?? "";

            if (type == "function" && entry["function"] is JsonObject)
            { converted.Add(entry.DeepClone()); continue; }

            if (type == "function" && entry["name"] is not null)
            {
                var schemaSrc = entry["parameters"] ?? entry["inputSchema"];
                if (schemaSrc is null) defaulted.Add(entry["name"]?.GetValue<string>() ?? "?");
                converted.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = entry["name"]?.GetValue<string>(),
                        ["description"] = entry["description"]?.GetValue<string>() ?? "",
                        ["parameters"] = CloneOrDefault(schemaSrc)
                    }
                });
                continue;
            }

            if (type == "namespace" && entry["tools"] is JsonArray nested)
            {
                int before = converted.Count;
                foreach (var nestedNode in nested)
                {
                    if (nestedNode is not JsonObject nestedTool || nestedTool["name"] is null)
                    { dropped.Add("namespace-sem-nome"); continue; }
                    var schemaSrc = nestedTool["parameters"] ?? nestedTool["inputSchema"];
                    if (schemaSrc is null) defaulted.Add(nestedTool["name"]?.GetValue<string>() ?? "?");
                    converted.Add(new JsonObject
                    {
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = nestedTool["name"]?.GetValue<string>(),
                            ["description"] = nestedTool["description"]?.GetValue<string>() ?? "",
                            ["parameters"] = CloneOrDefault(schemaSrc)
                        }
                    });
                }
                if (converted.Count - before != nested.Count) dropped.Add("namespace-parcial");
                continue;
            }

            // Nao casa nenhum ramo: some do payload. Motivo+nome vao p/ a sonda.
            dropped.Add($"{(type.Length == 0 ? "sem-type" : type)}:{entry["name"]?.GetValue<string>() ?? "?"}");
        }
        if (Diagnostic)
        {
            var names = new JsonArray();
            foreach (var t in converted)
                if (t?["function"]?["name"]?.GetValue<string>() is string name) names.Add(name);
            Log(new JsonObject
            {
                ["event"] = "tools-to-chat",
                ["in"] = list.Count,
                ["out"] = converted.Count,
                ["names"] = names,
                ["dropped"] = dropped,
                ["default_schema"] = defaulted,
                ["result_null"] = converted.Count == 0
            });
        }
        if (converted.Count == 0) return null;
        return ToolHelp.Apply(converted);
    }

    // ---------------------------------------------------------------------
    // 4.6) resposta chat -> objeto Responses
    //   reasoning (thinking do modelo) -> item "reasoning"   [modo events]
    //   content                          -> item "message"
    //   tool_calls                       -> item "function_call" (SEM status:
    //                                        o opencode conduz in_progress->completed)
    // ---------------------------------------------------------------------
    /// <summary>Allowlist de tools vista pela ultima traducao: a coercao de tool call
    /// narrada depende dela (sem lista, so passaria o catalogo RealTools).</summary>
    static List<string> ChatToResponsesAnnounced = new();

    /// <summary>Converte resposta chat em objeto Responses (reasoning+message+calls).</summary>
    /// <param name="chat">Chat completion.</param>
    /// <param name="model">Model id p/ eco.</param>
    /// <param name="temperature">Efetiva.</param>
    /// <param name="thinkMode">Modo thinking efetivo.</param>
    /// <param name="hadTools">A resposta tinha tools disponiveis.</param>
    /// <param name="historyMessages">Mensagens do REQUEST (nao da resposta): o anti-repeat
    /// cross-turn compara as tool calls do historico com a chamada nova.</param>
    /// <returns>Objeto Responses.</returns>
    static JsonObject ChatToResponses(JsonObject chat, string model, double temperature, string? thinkMode = null, bool hadTools = false, JsonNode? historyMessages = null)
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
        string blockedNotice = "";
        // AntiRepeat cross-turn: a chamada ja repetiu no historico, entao e loop.
        var loopedSigs = AntiRepeat ? new RepeatedCall(2).Find(historyMessages)
                                     : new HashSet<string>(StringComparer.Ordinal);
        // Loop de REESCRITA: mesmo path de `write` repetido no historico. O modelo
        // varia o conteudo, entao a assinatura exata nao casa; o path e' estavel.
        var writtenPaths = AntiRepeat ? new RepeatedWritePath(1).Find(historyMessages)
                                      : new HashSet<string>(StringComparer.Ordinal);
        if (message["tool_calls"] is JsonArray callArray)
        {
            foreach (var callNode in callArray)
            {
                if (callNode is not JsonObject callObj) continue;
                string id = callObj["id"]?.GetValue<string>() ?? ("call_" + NewId());
                var funcObj = callObj["function"]?.AsObject() ?? new JsonObject();
                string nativeName = funcObj["name"]?.GetValue<string>() ?? "";
                if (nativeName.StartsWith("default.", StringComparison.OrdinalIgnoreCase))
                    nativeName = nativeName["default.".Length..];
                // Risco autorizado no turno anterior: uma concessao = uma execucao.
                bool riskOk = RiskConsumeApproval(model, nativeName, JsonToString(funcObj["arguments"]));
                // Bloqueio por role: a tool foi anunciada ao modelo, mas a chamada nao
                // sai. O aviso entra como texto, para o modelo explicar a recusa.
                if (!riskOk && BannedFor(model).Contains(nativeName) && RiskAsk)
                {
                    string roleB = RoleOfModel(model);
                    string bid = "risk_" + NewId();
                    PendingRisks[bid] = new PendingRisk(nativeName, JsonToString(funcObj["arguments"]), roleB);
                    Metrics.Deny("ring2", nativeName, "papel");
                    Log(new JsonObject { ["event"] = "tool-ask", ["tool"] = nativeName, ["role"] = roleB,
                                         ["reason"] = "papel", ["call_id"] = bid });
                    toolCalls.Add((bid, "question", RiskQuestionArgs(nativeName, JsonToString(funcObj["arguments"]), roleB, "papel")));
                    if (blockedNotice.Length == 0)
                        blockedNotice = "\n\nEsta tool fica fora do meu papel. Pedi a confirmacao ao usuario na interface; aguardo a resposta.\n\n";
                    continue;
                }
                if (!riskOk && BannedFor(model).Contains(nativeName))
                {
                    Metrics.Deny("ring2", nativeName, "role");
                    Log(new JsonObject { ["event"] = "tool-blocked", ["tool"] = nativeName, ["role"] = RoleOfModel(model) });
                    if (blockedNotice.Length == 0)
                        blockedNotice = "\n\n" + string.Format(ToolBlockNotice, nativeName, RoleOfModel(model)) + "\n\n";
                    continue;
                }
                // Reescrita do MESMO arquivo: conteudo varia, mas o alvo e' o mesmo.
                // Cutuca (nao bloqueia) para o modelo trocar de abordagem.
                if (AntiRepeat && nativeName.Equals("write", StringComparison.OrdinalIgnoreCase)
                    && blockedNotice.Length == 0)
                {
                    string wpath = "";
                    try { wpath = JsonNode.Parse(JsonToString(funcObj["arguments"]))?["path"]?.GetValue<string>() ?? ""; }
                    catch { }
                    if (wpath.Length > 0 && writtenPaths.Contains(wpath))
                    {
                        Log(new JsonObject { ["event"] = "anti-repeat-write", ["tool"] = nativeName, ["path"] = wpath });
                        blockedNotice = "\n\n" + AntiRepeatNotice + "\n\n";
                    }
                }
                // AntiRepeat cross-turn: nao emitimos a chamada que ja repetiu; o modelo
                // so reenfileiraria o mesmo comando e o turno travaria aqui.
                if (loopedSigs.Contains(ToolSig.Of(nativeName, JsonToString(funcObj["arguments"]))))
                {
                    Log(new JsonObject { ["event"] = "anti-repeat-loop", ["tool"] = nativeName });
                    if (blockedNotice.Length == 0)
                        blockedNotice = "\n\n" + AntiRepeatNotice + "\n\n";
                    continue;
                }
                // Comando de risco sem aval: a chamada vira pergunta na interface.
                if (!riskOk && IsRiskCall(model, nativeName, JsonToString(funcObj["arguments"])))
                {
                    string role = RoleOfModel(model);
                    string qid = "risk_" + NewId();
                    PendingRisks[qid] = new PendingRisk(nativeName, JsonToString(funcObj["arguments"]), role);
                    Log(new JsonObject { ["event"] = "risk-ask", ["tool"] = nativeName, ["role"] = role, ["call_id"] = qid });
                    toolCalls.Add((qid, "question", RiskQuestionArgs(nativeName, JsonToString(funcObj["arguments"]), role, "risco")));
                    if (blockedNotice.Length == 0)
                        blockedNotice = "\n\nEsta acao e de risco. Pedi a confirmacao ao usuario na interface; aguardo a resposta antes de rodar.\n\n";
                    continue;
                }
                if (riskOk && blockedNotice.Length == 0)
                    blockedNotice = "\n\nO usuario autorizou esta acao; executando agora.\n\n";
                string rawArgs = JsonToString(funcObj["arguments"]);
                var effective = UnwrapExecuteCalls(nativeName, rawArgs);
                if (effective.Count == 0) effective.Add((nativeName, rawArgs));
                for (int ci = 0; ci < effective.Count; ci++)
                {
                    var (callName, callArgs) = SanitizeToolCall(effective[ci].Name, effective[ci].Arguments);
                    toolCalls.Add((ci == 0 ? id : id + "_" + ci, callName, callArgs));
                }
            }
        }
        // Coerção: sem tool_calls nativos e com tools pedidas, extrai do texto
        // (ToolsCoercion: hermes/plural/fence — coder/deepseek via llama).
        // So atua se o texto parsear; prosa normal passa intacta.
        if (CoerceNarrated && ManagedFor(model) && toolCalls.Count == 0 && hadTools)
        {
            foreach (var (name, args) in ExtractTextToolCalls(content, ChatToResponsesAnnounced))
                toolCalls.Add(("call_" + NewId(), name, args));
            if (toolCalls.Count > 0)
            {
                // Remove os blocos consumidos p/ nao duplicar texto + call.
                // A narrada sai INTEIRA (marcador + args + parentese), senao o
                // JSON dos argumentos ficaria colado na prosa.
                content = ReToolTag.Replace(content, "");
                content = ReToolsTag.Replace(content, "");
                content = ReFunctionTag.Replace(content, "");
                content = ReFencedCall.Replace(content, "");
                content = StripNarratedCalls(content);
                Log(new JsonObject { ["event"] = "coerced", ["model"] = model, ["count"] = toolCalls.Count });
            }
            else if (CoerceNarrated && ReAssistantToolCall.IsMatch(content))
                Log(new JsonObject { ["event"] = "coerce-narrated-miss", ["model"] = model, ["had_tools"] = hadTools });
        }

        // Sonda (so log) de repeticao nos argumentos de uma tool call no caminho
        // nao-stream: o texto do args pode crescer em loop sem tocar thinking/content.
        if (AntiRepeat)
            foreach (var (_, probeName, probeArgs) in toolCalls)
                if (probeArgs.Length >= ToolArgsAntiRepeatReply.ProbeMinChars && new ToolArgsAntiRepeatReply(AntiRepeatArgsWindow).IsRepeat(probeArgs))
                    Log(new JsonObject { ["event"] = "anti-repeat-args", ["tool"] = probeName,
                        ["chars"] = probeArgs.Length, ["window"] = AntiRepeatArgsWindow, ["mode"] = "probe" });

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
                ["id"] = "rs_" + NewId(), ["summary"] = new JsonArray(), ["type"] = "reasoning",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "reasoning_text", ["text"] = thinking }),
                ["encrypted_content"] = "", ["status"] = "completed"
            });
        }

        // blockedNotice conta mesmo com content vazio: o modelo costuma emitir SO a tool
        // call, e sem isso o turno ficaria sem texto nenhum para o usuario ler.
        if (!string.IsNullOrWhiteSpace(content) || blockedNotice.Length > 0)
        {
            output.Add(new JsonObject
            {
                ["id"] = "msg_" + NewId(), ["type"] = "message", ["role"] = "assistant",
                ["status"] = "completed",
                ["content"] = new JsonArray(new JsonObject
                {
                    ["type"] = "output_text", ["text"] = content + blockedNotice,
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
            ["parallel_tool_calls"] = false,
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

    // Separa o bloco de thinking (<think>...</think>, modo hibrido do template)
    // do texto final. Sem tag de fechamento (corte por max_tokens), tudo apos
    // a abertura e considerado thinking.
    // Tags de controle de canal/thinking que alguns templates deixam vazar no
    // texto (Gemma 4: <|channel>thought ... <channel|>; Qwen: </thought>).
    // Nao sao conteudo: se chegam ao reasoning, a UI mostra a tag crua no bloco
    // de pensamento. Removidas com sonda (evento reasoning-tag).


    // Marcadores TEXTUAIS de tool call: o opencode serializa a tool no historico
    // como "[Assistant tool call]: nome({...})" e o modelo copia esse formato.
    // No stream o texto a partir deles fica retido; quem decide (descartar como
    // chamada ou devolver como prosa) e o fecho.





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
        string thought = StripControlTags(text[(openIdx + 7)..closeIdx], "split");
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
    /// <param name="thinkMode">Modo thinking efetivo.</param>
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
                // Raciocinio no formato NATIVO do llama.cpp: content[].text +
                // reasoning_text.delta. O par summary_text/part fazia o opencode
                // renderizar o pensamento como snippet de resumo.
                string thinkingText = item["summary"]?[0]?["text"]?.GetValue<string>()
                                      ?? item["content"]?[0]?["text"]?.GetValue<string>() ?? "";
                string reasoningId = item["id"]?.GetValue<string>() ?? ("rs_" + NewId());
                var reasoningOpen = new JsonObject
                {
                    ["id"] = reasoningId, ["summary"] = new JsonArray(), ["type"] = "reasoning",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "reasoning_text", ["text"] = "" }),
                    ["encrypted_content"] = "", ["status"] = "in_progress"
                };
                WriteEvent(sse, "response.output_item.added", new JsonObject
                {
                    ["type"] = "response.output_item.added", ["output_index"] = outputIndex,
                    ["sequence_number"] = sequence++, ["item"] = reasoningOpen
                });
                if (thinkingText.Length > 0)
                {
                    WriteEvent(sse, "response.reasoning_text.delta", new JsonObject
                    {
                        ["type"] = "response.reasoning_text.delta", ["delta"] = thinkingText,
                        ["item_id"] = reasoningId, ["output_index"] = outputIndex, ["content_index"] = 0,
                        ["sequence_number"] = sequence++
                    });
                    WriteEvent(sse, "response.reasoning_text.done", new JsonObject
                    {
                        ["type"] = "response.reasoning_text.done", ["item_id"] = reasoningId,
                        ["output_index"] = outputIndex, ["content_index"] = 0, ["text"] = thinkingText,
                        ["sequence_number"] = sequence++
                    });
                }
                var reasoningDone = new JsonObject
                {
                    ["id"] = reasoningId, ["summary"] = new JsonArray(), ["type"] = "reasoning",
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "reasoning_text", ["text"] = thinkingText }),
                    ["encrypted_content"] = "", ["status"] = "completed"
                };
                WriteEvent(sse, "response.output_item.done", new JsonObject
                {
                    ["type"] = "response.output_item.done", ["output_index"] = outputIndex,
                    ["sequence_number"] = sequence++, ["item"] = reasoningDone
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
    internal static void Log(JsonObject obj)
    {
        try
        {
            obj["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            // O gate anti-repeat vira mensagem adicional no historico do
            // enunciado (o mesmo aviso que a bridge injeta no modelo) e conta
            // no painel de gates: conteudo vs tools.
            string eventName = obj["event"]?.GetValue<string>() ?? "";
            if (eventName == "anti-repeat")
            {
                AddEnunciadoMessage("anti-repeat", AntiRepeatNotice);
                Metrics.AntiRepeatGate(false);
            }
            else if (eventName == "anti-repeat-args" || eventName == "anti-repeat-loop" || eventName == "anti-repeat-salvage")
            {
                Metrics.AntiRepeatGate(true);
            }
            // --diagnostic-level metrics: mede sem guardar os corpos (o dump
            // integral passava de 280 MB numa rodada de belt, so de req/res).
            if (DiagnosticLevel == "metrics") { obj.Remove("req"); obj.Remove("res"); }
            File.AppendAllText(LogPath, obj.ToJsonString(SerOpts) + "\n");
        }
        catch { /* log nunca derruba o pedido */ }
    }

    /// <summary>Acrescenta elapsed/tps ao dump de trafego (sonda DIAGNOSTIC).</summary>
    /// <param name="ev">Evento de log (mutado).</param>
    /// <param name="startedAt">Inicio do atendimento.</param>
    /// <param name="responseText">Corpo/SSE da resposta (usage/timings ou estimativa).</param>
    /// <param name="requestText">Corpo do request (estimativa de input quando nao ha usage).</param>
    static void AddPerf(JsonObject ev, DateTime startedAt, string? responseText = null, string? requestText = null)
    {
        double elapsed = Math.Max(0, (DateTime.UtcNow - startedAt).TotalSeconds);
        var (inTok, outTok, tpsOut, tpsIn) = UsageTokens(responseText);
        if (inTok <= 0 && !string.IsNullOrEmpty(requestText))
            inTok = Math.Max(0, requestText.Length / 4); // estimativa quando o upstream omite usage
        if (outTok <= 0 && !string.IsNullOrEmpty(responseText))
            outTok = Math.Max(0, responseText.Length / 4); // idem: sem usage/timings no SSE
        ev["elapsed"] = Math.Round(elapsed, 2);
        ev["input_tokens"] = inTok;
        ev["output_tokens"] = outTok;
        ev["tps_output"] = Math.Round(tpsOut > 0 ? tpsOut : (elapsed > 0 ? outTok / elapsed : 0), 1);
        ev["tps_total"] = Math.Round(elapsed > 0 ? (inTok + outTok) / elapsed : 0, 1);
        if (tpsIn > 0) ev["tps_prompt"] = Math.Round(tpsIn, 1);
    }

    /// <summary>Extrai tokens/tps de usage ou timings do JSON de resposta e das linhas SSE.</summary>
    static (int In, int Out, double TpsOut, double TpsIn) UsageTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (0, 0, 0, 0);
        static int Pick(JsonObject u, string a, string b)
            => u[a]?.GetValue<int>() ?? u[b]?.GetValue<int>() ?? 0;
        static double PickD(JsonObject u, string a)
            => u[a]?.GetValue<double>() ?? 0;
        static (int, int, double, double) Read(JsonObject o)
        {
            int i = 0, ou = 0;
            double to = 0, ti = 0;
            if (o["usage"] is JsonObject u)
            {
                i = Pick(u, "input_tokens", "prompt_tokens");
                ou = Pick(u, "output_tokens", "completion_tokens");
            }
            if (o["response"]?["usage"] is JsonObject ru)
            {
                if (i == 0) i = Pick(ru, "input_tokens", "prompt_tokens");
                if (ou == 0) ou = Pick(ru, "output_tokens", "completion_tokens");
            }
            // llama.cpp manda timings no ultimo chunk SSE (e no JSON nao-stream):
            // predicted_n/prompt_n sao exatos; predicted_per_second evita
            // diluir o tk/s com o overhead de rede do elapsed.
            if (o["timings"] is JsonObject t)
            {
                if (i == 0) i = t["prompt_n"]?.GetValue<int>() ?? 0;
                if (ou == 0) ou = t["predicted_n"]?.GetValue<int>() ?? 0;
                to = PickD(t, "predicted_per_second");
                ti = PickD(t, "prompt_per_second");
            }
            return (i, ou, to, ti);
        }
        try
        {
            if (JsonNode.Parse(text) is JsonObject obj) return Read(obj);
        }
        catch { /* pode ser SSE: varre as linhas data: */ }
        foreach (var raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            string data = line["data:".Length..].Trim();
            if (data is "" or "[DONE]") continue;
            try
            {
                if (JsonNode.Parse(data) is not JsonObject o) continue;
                var r = Read(o);
                if (r.Item2 > 0 || r.Item3 > 0) return r;
            }
            catch { }
        }
        return (0, 0, 0, 0);
    }

    /// <summary>Sonda Serilog do AntiRepeat; nunca derruba o pedido.</summary>
    /// <param name="where">Ponto de observacao (thinking/content/bypass-chat/bypass-responses).</param>
    /// <param name="chars">Acumulado ate aqui.</param>
    /// <param name="trip">Detector disparou?</param>
    /// <param name="tail">Amostra da cauda (ultimos 2048 chars normalizados).</param>
    /// <param name="dupAt">Indice da duplicata na cauda (-1 = nao localizada).</param>
    /// <param name="dupCtx">Trecho da duplicata (até 2048 chars).</param>
    static void Sonda(string where, int chars, bool trip, string tail, int dupAt = -1, string dupCtx = "")
    {
        if (!Diagnostic) return;
        try
        {
            // dup_at = posicao (no texto normalizado) da ocorrencia anterior da
            // mesma janela; dup_ctx = 2048 chars a partir dela. Sem isso o log so
            // mostra a cauda e nao prova ONDE o modelo se repetiu.
            if (trip)
                Serilog.Log.Warning("anti-repeat-sonda where={Where} chars={Chars} window={Window} trip={Trip} dup_at={DupAt} dup_ctx={DupCtx} tail={Tail}",
                    where, chars, AntiRepeatWindow, trip, dupAt, dupCtx, tail);
            else
                Serilog.Log.Debug("anti-repeat-sonda where={Where} chars={Chars} window={Window} trip={Trip} dup_at={DupAt} tail={Tail}",
                    where, chars, AntiRepeatWindow, trip, dupAt, tail);
        }
        catch { /* sonda nunca derruba o pedido */ }
    }


    /// <summary>Compila um padrao de risco; padrao invalido nao derruba a bridge.</summary>
    static bool TryRegex(string pattern, out Regex rx)
    {
        try { rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                            TimeSpan.FromMilliseconds(100)); return true; }
        catch { rx = null!; return false; }
    }
    /// <summary>Corta string no tamanho maximo.</summary>
    /// <param name="s">Texto.</param>
    /// <param name="n">Maximo.</param>
    /// <returns>Texto cortado.</returns>
    static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
    static string TailText(string s, int n) => s.Length <= n ? s : s[^n..];
}
