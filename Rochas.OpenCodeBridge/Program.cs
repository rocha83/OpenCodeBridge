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
//  O QUE NAO ESTA AQUI (de proposito — heranca problematica legada):
//  coercion de paths, grounding, extracao de pseudo-tool-call em texto,
//  retry com nudge, footer de tokens, strip agressivo de template.
//  Se surgir algo especifico do Qwen3-8B, amadurecemos nesta versao.
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

namespace Rochas.OpenCodeBridge;

internal static partial class Program
{
    // ---------------------------------------------------------------------
    // 1) CONFIGURACAO (linhas de comando) — tudo sobrescrevivel sem recompilar
    // ---------------------------------------------------------------------
    static int Port = 4124;                                        // porta da bridge
    static string Listen = "0.0.0.0";                              // interface de escuta padrao: rede toda (acesso externo; units systemd repetem via --listen)
    static string Upstream = "http://127.0.0.1:4100";              // vLLM
    static string Model = "qwen3-8b-awq";                          // id servido pelo vLLM
    static string Thinking = "events";                             // "events" = mostra | "off" = descarta
    static double Temperature = 0.3;                                 // medio (escala 0.1 baixo / 0.3 medio / 0.5 alto); tool calling continua deterministico via tool_choice
    static int MaxTokens;                                          // teto de saida da linha GPU: 0 = SEM TETO (a infra/llama controla via -c/--n-predict e o cliente decide via max_tokens); >0 = clamp
    static int CpuMaxTokens;                                       // teto da linha CPU: 0 = SEM TETO (deixa o llama controlar); >0 = clamp p/ conter divagacao em decode lento (~5 tok/s)
    static int ToolOutputLimit;                                      // corte por resultado de tool: 0 = SEM CORTE (padrao); config via appsettings "toolOutputLimit" / --tool-output-limit
    static string WorkspaceRoot = "";                               // raiz p/ POST /api/tools/execute; vazio = cwd do processo
    static string LogPath = "/tmp/qwen3-bridge.log";
    static string ApiKey = "";
    static bool PassthroughResponses = false;                    // Bypass p/ upstream com Responses nativo (llama.cpp): so remove itens reasoning, resto passa direto                                     // Bearer p/ upstream com auth (llama --api-key); vazio = so repasse
    static readonly string Version = "2.1";
    // Trava anti-loop no nivel da bridge: complementa QUALQUER system
    // prompt no final (Responses e chat), sem depender do agente.
    static readonly string SystemGuardrail = "ao listar arquivos locais preferencialmente use as tools glob ou read, atentando-se ao modo de uso correto destas, e ao navegar na web use somente enderecos externos, informando user agent Firefox no Windows 11 ao navegar via fetch ou curl; ao executar tools que resultem em erro, leia a mensagem de erro e adapte a proxima chamada, tentando no maximo mais 2 vezes, e sempre responda na lingua do usuario, seja verboso apenas ao declarar acoes com tools dentro do loop agentico e em conversa simples sem tools responda em no maximo 2 frases; porem envie apenas uma vez a cada mensagem do usuario JAMAIS repita a mesma pergunta varias vezes; se o usuario nao respondeu siga com a melhor suposicao e declare a decisao tomada; FORMA DE USO DAS TOOLS (obrigatoria): catalogo — leitura: glob (lista por padrao), read (le arquivo), grep (busca regex), fetch (GET http somente-leitura), display (mostra na tela); edicao: write (grava/cria), edit (substitui trecho exato); execucao: shell (comando unico; pipes/redirecionamento via bash -c); git somente-leitura (status/log/diff; escrita via shell com cd + git); delegacao: subagent (uma fase por vez); use apenas as anunciadas no bloco # Tools, com os nomes exatos, sem nenhum prefixo (nunca default., nunca outro namespace); cada chamada vai sozinha em <tool_call>{\"name\": \"<nome-exato>\", \"arguments\": {<args>}}</tool_call>, sem prosa antes, dentro ou depois do bloco; uma tool por vez, depois aguarde o resultado para a proxima acao; utilize a tool echo apenas em composicao de scripts, NUNCA para enviar mensagens ao usuario limite de writes por faixa: apos criar os artefatos planejados (max 8), responda BUILD-OK e pare. Nao repita a mesma chamada write.";

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
    // Inibidor mestre do harness da bridge (--harness 0): com ele desligado a
    // bridge vira tubo puro (sem sufixo guardrail, sem stop, sem pattern-strip,
    // sem coercao, sem retry/aviso) — todo o conteudo vem do harness opencode.
    static bool Harness = true;
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
    static string HarnessToolsTail = "MODO DE USO DAS TOOLS: uma tool call por resposta; aguarde o resultado antes da proxima. Arquivos: prefira write/edit; no shell, redirecionamento (>) e pipes (|) vao via bash -c \"...\"; nunca use echo para conversar. Rede: use a tool fetch, nunca shell+curl. Git: configure user.name/user.email locais do repo antes do commit; versione com init/add/commit (commit sempre com mensagem informada). Git e zip sao comandos independentes (a ordem so importa se o enunciado pedir); se o enunciado pedir versionar E empacotar, versione primeiro e empacote depois - assim o pacote nao entra no commit. Zip: empacote com output (o arquivo .zip) e inputs (caminhos relativos ao workspace, separados por espaco), excluindo bin/obj. Tarefa de N passos: so responda texto depois de concluir TODOS - se voce anunciou um passo e ainda falta, emita a tool call em vez de texto. Se uma tool falhar, leia o erro e varie a abordagem (max 2 tentativas); nao repita a mesma chamada. Se um build/teste falhar, leia a mensagem de erro e ajuste o trecho exato antes de rodar de novo. Projetos: crie com `dotnet new` UMA vez e escreva o fonte na pasta do projeto com a tool write (nunca recrie o projeto nem use echo/heredoc). Arquivos grandes (>32kb): leia em blocos (offset+limit, ou head/tail/`sed -n 'N,Mp'`), nunca o arquivo inteiro de uma vez. Matematica sempre qualificada: C# use Math.Sin/Math.Cos/Math.Sqrt/Math.PI; F# use System.Math.Sin (ou `open System` e depois Math.Sin) — nunca `sin`/`cos`/`sqrt`/`pi` minusculos nem `open System.Math` (invalido em F#). F# NAO tem operadores OCaml (`+.`/`-.`/`*.`/`/.`) - isso quebra o build: use `+ - * /` normais (valem para float) e `%` so com int. F#: nao indexe lista com indice float (`x.[i]`); use `[0..n]` int ou `List.map (fun xi -> Math.Sin xi) xs` direto. F#: formate com `printfn \"%f\"`/`\"%.6f\"` (sintaxe `%`), NUNCA `{0:F6}` (isso e C#, quebra o F#). F#: rode com `dotnet run --project <dir>` (nunca o dll direto); use let/printfn/List.map, pipe |>, match com fallback. ANTI-ALUCINACAO: NUNCA invente comando ou ferramenta - antes de usar, confirme que existe (ex.: command -v <cmd>); servicos sao gerenciados por systemctl (nao existe service_manager nesta maquina). Se uma tool esta bloqueada para o seu role (ex.: write para Plan/MoE/Orch), nao tente contornar por shell: explique que a acao cabe a outro role. Prefira caminhos absolutos em write/read; nome com espaco quebra o glob. shell: confirme o caminho com ls antes de mv/rm. FORMATO DE EMISSAO (obrigatorio): para agir, emita a CHAMADA DE TOOL ESTRUTURADA (canal nativo de tool, com nome e argumentos JSON) - esse e o unico formato que executa; a chamada escrita no corpo como [Assistant tool call]: nome({...}), ou apenas anunciada como 'Vou usar a tool X', vira prosa, nada executa e o turno trava. FORMATACAO NUMERICA: em C# use ToString com o formato F6 e System.Globalization.CultureInfo.InvariantCulture - nunca o printf %.6f; em F# use printfn com %.6f passando o valor como argumento, pois %.6f sem argumento imprime o literal. F# NUMERICO: some com `[0..n] |> Seq.sumBy (fun i -> ...)` - a sequencia vai na MESMA expressao como argumento; nunca deixe a sequencia sozinha depois de Seq.map/Seq.choose (gera FS0193 'function value is missing arguments'). Use List.average xs e potencia com `**`; desvio = sqrt (List.average (List.map (fun v -> (v-media)**2.0) xs)). CAMINHOS RELATIVOS (sem barra inicial) em write/read; ARGUMENTOS JSON DAS TOOLS DEVEM SER VALIDOS E COMPLETOS (escape \\n em strings). DICA NUMERICA (Simpson): em [0,pi] com n subintervalos, alterne pesos 4 (indices impares) e 2 (pares) e multiplique por h/3; resultado esperado ~2.0 para n=100. Use Math.Sin/System.Math.Sin qualificado. QUALIDADE E EVIDENCIA: contexto primeiro - leia docs/AGENTS.md e o git log antes de planejar; grave todos os artefatos da tarefa de uma vez, sem stub/TODO/placeholder/esqueleto (entrega parcial honesta so se o enunciado mandar parar); correcao sempre por edit no trecho exato (sed -i e negado pelo inspetor), nunca reescrevendo o arquivo inteiro; so declare pronto depois de executar o ACEITE e anexar a evidencia (trecho de log/teste); build/teste vermelho significa tarefa NAO concluida; fail-fast honesto: tool, modelo ou comando que nao existe nao se inventa - diga, liste alternativas e pare; responda no idioma do usuario.";
    // Retry de contencao do anti-repeat: 1 segunda chamada ao modelo com o aviso,
    // descartando a janela repetida. Governado por --anti-repeat-retry, nao pelo
    // LegacyHarness (ver o bloco do retry).
    static bool AntiRepeatRetry = true;
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
    static string HarnessBehaviorTail =
        "COMPORTAMENTO (workspace): quando o usuario informar pastas de atuacao fora do workspace, procure pastas ou arquivos citados tambem um nivel abaixo (pastas vizinhas) e na arvore de subpastas, tanto do workspace quanto dos paths citados. " +
        "COMPORTAMENTO (formato): em conversa escreva com Unicode (setas, menor-igual, pi, somatorio) em vez de LaTeX; use $...$ somente dentro de bloco de codigo ou de formula matematica, onde o cliente renderiza.";
    // O operador DECLAROU uma cauda (CLI/appsettings) ou o valor e o default
    // embutido? Usado so pela validacao, p/ avisar "cauda inerte com --harness 0"
    // sem acusar o default.
    static bool BehaviorTailCustom, ToolsTailCustom;
    // Ha alguma cauda de inducAO configurada? E o que o /api/harness reporta:
    // nao existe mais switch mestre, o que vale e ter cauda ou nao.
    static bool TailsAtivas() => HarnessBehaviorTail.Length > 0 || HarnessToolsTail.Length > 0;
    static int AntiRepeatWindow = 256;                           // janela (chars) da cauda comparada com o historico (64..2048)
    // Guarda de ctx (contextGuard): ao atingir o limiar do ctx, a bridge
    // devolve 413 pedindo compactacao em vez da resposta. Parametrizavel
    // via appsettings (contextGuard) ou CLI; ContextTokens=0 = inativo.
    static bool ContextGuard = false;
    static double ContextThreshold = 0.95;                       // fracao do ctx (0.5..0.99)
    static int ContextTokens = 0;                                // tamanho do ctx do modelo servido
    static bool Diagnostic = false;                              // sonda Serilog: --diagnostic / appsettings diagnostic / env DIAGNOSTIC=1
    // Granularidade da sonda (grupo observabilidade):
    //   off     = nenhuma sonda
    //   metrics = evento de trafego com metricas (usage/tps/elapsed), SEM os
    //             corpos req/res (o dump integral passou de 280 MB numa rodada)
    //   dump    = metrics + corpos completos (comportamento historico)
    // --diagnostic 1 (legado) sem nivel declarado implica "dump".
    static string DiagnosticLevel = "off";
    static string AntiRepeatNotice = "Aqui e a ponte de contencao OpenCodeBridge: pare de enviar o mesmo raciocinio, provavelmente voce entrou em repeticao invalida; quando informada a repeticao, nao responda mais nada, apenas encerre em silencio, obrigado.";

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
        string sig = ToolSig(tool, args);
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
                RiskApprovedOnce[ToolSig(risk.Tool, risk.Args)] = DateTime.UtcNow.AddMinutes(RiskAskMinutes);
                notices.Add($"Ponte OpenCodeBridge: o usuario AUTORIZOU a chamada de risco pendente. "
                          + $"Reemita agora, sem mudar nada, a tool '{risk.Tool}' com os argumentos: {risk.Args}");
            }
            else
            {
                RiskRefused.Add(ToolSig(risk.Tool, risk.Args));
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
        string a = NormRepeat(answer).ToLowerInvariant();
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
        string sig = ToolSig(tool, args);
        if (RiskRefused.Contains(sig)) return false;   // ja recusado: nao insiste
        string hay = NormRepeat(tool + " " + args);
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
    static string RoleOfModel(string? modelId)
    {
        if (string.IsNullOrEmpty(modelId)) return "";
        foreach (var k in Profiles.Keys)
            if (modelId.EndsWith("-" + k, StringComparison.OrdinalIgnoreCase))
                return char.ToUpperInvariant(k[0]) + k[1..];
        return "";
    }
    /// <summary>A linha servida precisa de coerção texto->tool_calls?</summary>
    /// <remarks>Decidido pela CAPACIDADE do upstream (--upstream-parser none) ou
    /// pelo pedido explicito do operador (--coerce-text-tools 1). Nao olha nome
    /// de modelo (id e livre, e a suite E2E roda ids "8b" na linha sem parser) e
    /// nao depende do LegacyHarness: coerção de tool call e reanimacao de modo
    /// legado sao rotinas distintas, com flags distintas.</remarks>
    /// <returns>True = coagir texto em tool_calls.</returns>
    static bool CoerceFor() => CoerceTextTools || UpstreamParser == "none";

    // Perfis por sufixo no model (ex.: openai/qwen3.5-9b-plan): plan =
    // thinking ligado + temp media p/ planejar; build = thinking fundido +
    // temp media p/ executar; orch = coordena subagentes; moe = uso geral
    // hibrido; scripter = temp baixa (determinismo) e thinking off.
    // Escala: 0.3 (min: build/scripter/exec) / 0.5 (plan/orch) / 0.8 (moe).
    // Request sem sufixo usa os
    // globais. O caminho Qwen3 padrao (sem sufixo) fica byte-identico.
    static readonly Dictionary<string, (double Temp, string Think)> Profiles =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["plan"] = (0.5, "events"),
            ["build"] = (0.3, "off"),
            ["orch"] = (0.5, "events"),
            ["moe"] = (0.8, "events"),
            ["scripter"] = (0.3, "off"),
            ["exec"] = (0.3, "off"),
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
            if (cfg["harness"]?.GetValue<bool>() is bool hn) Harness = hn;
            if (cfg["harnessToolsTail"]?.GetValue<string>() is string ham && ham.Length > 0) { HarnessToolsTail = ham; ToolsTailCustom = true; }
            if (cfg["harnessBehaviorTail"]?.GetValue<string>() is string bt) { HarnessBehaviorTail = bt; BehaviorTailCustom = true; }
            if (cfg["antiRepeatRetry"]?.GetValue<bool>() is bool arr) AntiRepeatRetry = arr;
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
            if (cfg["antiRepeatWindow"]?.GetValue<int>() is int arw && arw >= 64 && arw <= 2048) AntiRepeatWindow = arw;
            if (cfg["antiRepeatNotice"]?.GetValue<string>() is string arn && arn.Length >= 20) AntiRepeatNotice = arn;
            if (cfg["diagnostic"]?.GetValue<bool>() is bool dg) Diagnostic = dg;
            if (cfg["toolStrategy"]?.GetValue<bool>() is bool tstrat) ToolStrategy = tstrat;
            if (cfg["contextGuard"]?.AsObject() is JsonObject cg)
            {
                if (cg["enabled"]?.GetValue<bool>() is bool cge) ContextGuard = cge;
                if (cg["threshold"]?.GetValue<double>() is double cgt && cgt >= 0.5 && cgt <= 0.99) ContextThreshold = cgt;
                if (cg["contextTokens"]?.GetValue<int>() is int cct && cct > 0) ContextTokens = cct;
            }
            if (cfg["qwenStandard"]?.GetValue<bool>() is bool qs) QwenStd = qs;
            if (Environment.GetEnvironmentVariable("DIAGNOSTIC") is "1" or "true" or "True") Diagnostic = true;
            if (Environment.GetEnvironmentVariable("HARNESS") is "0" or "false" or "False") Harness = false;
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

    // Coerção: fenced ```json{"name":..}, tags <tool_call>..</tool_call> e
    // o formato cru do 3B na CPU: <{"name": ..}}> (sem fences/tags).
    static readonly Regex ReFencedCall = new(@"```(?:json)?\s*(\{\s*""name""\s*:[\s\S]*?\})\s*```", RegexOptions.Compiled);
    static readonly Regex ReToolTag = new(@"<tool_call>([\s\S]*?)</tool_call(?:\|>)?", RegexOptions.Compiled);
    // Coder/Hermes via llama: embrulha no plural <tools> (template poe as
    // assinaturas em <tools> e o modelo ecoa o wrapper no lugar de <tool_call>).
    static readonly Regex ReToolsTag = new(@"<tools>([\s\S]*?)</tools>", RegexOptions.Compiled);
    // Coder com tool_choice required: <function>{...}</function> + flood de \n.
    static readonly Regex ReFunctionTag = new(@"<function>([\s\S]*?)</function>", RegexOptions.Compiled);

    // opencode serializa a tool call no historico como "[Assistant tool call]: nome({...})".
    // Em contexto longo o modelo copia esse formato em vez de emitir a chamada estruturada, e
    // o parser nativo do llama nao ve nada: o turno vira prosa e trava. Aqui devolvemos a
    // chamada de verdade. Nome segue a allowlist de tools anunciadas, entao prosa
    // que so menciona uma tool nao vira chamada.
    static readonly Regex ReAssistantToolCall = new(
        @"\[\s*Assistant\s+tool\s+call\s*\]\s*:?\s*([A-Za-z_][A-Za-z0-9_.]*)\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Catálogo real de tools (+ apelidos shell -> shell): só estes nomes
    // viram tool_call na coação; resto (echo alucinado) fica como prosa.
    static readonly HashSet<string> RealTools = new(StringComparer.OrdinalIgnoreCase)
        { "shell", "read", "grep", "glob", "fetch", "webfetch", "websearch", "git", "subagent", "write", "edit", "question", "skill" };
    static string CanonToolAlias(string n) => n.Trim().ToLowerInvariant() switch
    {
        "bash" or "sh" or "exec" or "proc" => "shell",
        _ => n.Trim(),
    };

    /// <summary>Nomes de tools anunciadas no pedido (chat ou responses).</summary>
    static List<string> AnnouncedTools(JsonArray? tools)
    {
        var names = new List<string>();
        if (tools is null) return names;
        foreach (var t in tools)
        {
            var o = t?.AsObject();
            string? n = o?["function"]?.AsObject()?["name"]?.GetValue<string>()
                ?? o?["name"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(n)) names.Add(n!);
        }
        return names;
    }

    static List<string> AnnouncedTools(string bodyJson)
    {
        try { return AnnouncedTools(JsonNode.Parse(bodyJson)?.AsObject()?["tools"]?.AsArray()); }
        catch { return new List<string>(); }
    }

    /// <summary>Extrai tool calls de texto (fenced JSON ou tags) p/ coercão.</summary>
    static List<(string Name, string Arguments)> ExtractTextToolCalls(string content, List<string>? known = null)
    {
        var list = new List<(string, string)>();
        if (string.IsNullOrEmpty(content)) return list;
        foreach (Match m in ReToolTag.Matches(content)) TryAddTextCall(m.Groups[1].Value, list, known);
        if (list.Count == 0)
            foreach (Match m in ReToolsTag.Matches(content)) TryAddTextCall(m.Groups[1].Value, list, known);
        if (list.Count == 0)
            foreach (Match m in ReFunctionTag.Matches(content)) TryAddTextCall(m.Groups[1].Value, list, known);
        if (list.Count == 0)
            foreach (Match m in ReFencedCall.Matches(content)) TryAddTextCall(m.Groups[1].Value, list, known);
        if (list.Count == 0)
            foreach (string json in ExtractAngleCalls(content, false)) TryAddTextCall(json, list, known);
        if (list.Count == 0)
            ExtractAssistantToolCalls(content, list, known);
        if (list.Count == 0)
        {
            // Ultimo recurso: resposta INTEIRA e um objeto {"name":..} cru
            // (3B alterna formatos entre runs). Exige "arguments" p/ nao
            // confundir com codigo que mencione "name".
            string t = content.TrimStart();
            if (t.StartsWith("{\"name\"", StringComparison.Ordinal) && t.Contains("\"arguments\""))
                foreach (string json in ExtractAngleCalls(t, true)) TryAddTextCall(json, list, known);
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

    /// <summary>Formato "[Assistant tool call]: nome({...})" -> tool call de verdade.</summary>
    /// <remarks>Reconstroi o envelope {"name","arguments"} a partir do nome do regex e dos <see cref="TryAddTextCall"/>,
    /// que aplica a allowlist. Le ate o fechamento balanceado do objeto de argumentos, entao
    /// nao depende de chaves balanceadas no texto todo.</remarks>
    static void ExtractAssistantToolCalls(string content, List<(string Name, string Arguments)> list, List<string>? known)
    {
        foreach (Match m in ReAssistantToolCall.Matches(content))
        {
            int i = m.Index + m.Length;              // logo apos o '(' do nome
            if (i >= content.Length || content[i] != '{') continue;
            if (!TryBalanced(content, i, out string obj)) continue;
            try
            {
                // TryBalanced devolve o que esta entre as chaves do nome: sao os ARGUMENTOS,
                // nao um envelope {"name","arguments"}. O nome vem do grupo do regex.
                if (!TryParseArgs(obj, out JsonObject argObj)) continue;
                var envelope = new JsonObject { ["name"] = m.Groups[1].Value, ["arguments"] = argObj };
                TryAddTextCall(envelope.ToJsonString(SerOpts), list, known);
            }
            catch { /* prosa que nao e JSON: segue como texto */ }
        }
    }

    /// <summary>Parse JSON tolerante: estrito e, se falhar, com escapes reparados.</summary>
    static JsonNode? TryParseJson(string json)
    {
        try { return JsonNode.Parse(json); } catch { }
        string repaired = RepairJsonEscapes(json);
        if (repaired == json) return null;
        try { return JsonNode.Parse(repaired); } catch { return null; }
    }

    /// <summary>Repara escapes que o modelo inventa (barra + char fora do JSON:
    /// <c>\;</c>, <c>\-</c>, <c>\s</c>) duplicando a barra. Sem isso
    /// <c>find ... -exec cat {} \;</c> derruba o parse e a chamada narrada morre.</summary>
    static string RepairJsonEscapes(string json)
    {
        var sb = new StringBuilder(json.Length + 8);
        for (int i = 0; i < json.Length; i++)
        {
            char c = json[i];
            if (c != '\\' || i + 1 >= json.Length) { sb.Append(c); continue; }
            char n = json[i + 1];
            if (n is '"' or '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't' or 'u')
            { sb.Append(c).Append(n); i++; continue; }
            sb.Append("\\\\").Append(n);   // \; -> \\; (barra literal)
            i++;
        }
        return sb.ToString();
    }

    /// <summary>Argumentos de tool a partir do texto: JSON valido, JSON reparado
    /// ou o caso <c>{"command": "..."}</c> com aspas internas soltas (shell narrado:
    /// <c>find . -name "*evolution*.md"</c> sem escape).</summary>
    static bool TryParseArgs(string raw, out JsonObject args)
    {
        args = new JsonObject();
        if (TryParseJson(raw) is JsonObject parsed) { args = parsed; return true; }
        var match = Regex.Match(raw, "^\\{\\s*\"command\"\\s*:\\s*\"([\\s\\S]*)\"\\s*\\}$");
        if (!match.Success) return false;
        string command = match.Groups[1].Value
            .Replace("\\\"", "\"")     // \" do JSON -> aspas reais
            .Replace("\\\\", "\\");    // \\ do JSON -> barra real
        args = new JsonObject { ["command"] = command };
        return true;
    }

    /// <summary>Remove do texto do historico as chamadas narradas
    /// ("[Assistant tool call]: nome({...})"), mantendo a prosa em volta.</summary>
    /// <remarks>A chamada de verdade vem como item function_call no mesmo input;
    /// deixar o texto ensina o modelo a narrar em vez de emitir a chamada.</remarks>
    static string StripNarratedCalls(string text)
    {
        if (string.IsNullOrEmpty(text) || !ReAssistantToolCall.IsMatch(text)) return text;
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            var m = ReAssistantToolCall.Match(text, i);
            if (!m.Success) { sb.Append(text, i, text.Length - i); break; }
            sb.Append(text, i, m.Index - i);
            int start = m.Index + m.Length;
            if (start < text.Length && text[start] == '{' && TryBalanced(text, start, out string obj))
            {
                int end = start + obj.Length;
                if (end < text.Length && text[end] == ')') end++;
                i = end;
            }
            else i = m.Index + m.Length;
        }
        return Regex.Replace(sb.ToString(), @"[ \t]+\r?\n", "\n").TrimEnd();
    }

    static void TryAddTextCall(string json, List<(string Name, string Arguments)> list, List<string>? known = null)
    {
        try
        {
            var o = TryParseJson(json.Trim())?.AsObject();
            if (o is null) return;
            string name = o["name"]?.GetValue<string>() ?? "";
            if (name.Length == 0) return;
            // Prefixo alucinado do harness (default.shell): o registro usa
            // nomes simples; "default." nunca e tool valida — remove sem risco.
            if (name.StartsWith("default.", StringComparison.OrdinalIgnoreCase))
                name = name["default.".Length..];
            // Sem allowlist: uso geral de tools. Com lista anunciada, casa o apelido
            // (shell -> shell); sem lista, usa o nome como veio. O limite de escrita/
            // execucao por role e feito na emissao (ToolBlacklist), nao aqui.
            string canon = CanonToolAlias(name);
            string emit = known is { Count: > 0 }
                ? known.FirstOrDefault(k => CanonToolAlias(k) == canon) ?? canon
                : canon;
            if (emit.Length == 0) return;
            list.Add((emit, JsonToString(o["arguments"])));
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
    //
    // GRUPOS (a ordem do switch segue o grupo e toda flag vive em UM grupo):
    //   A) rede/processo .... --listen (alias --host) --port --upstream --model --log --workspace
    //   B) auth/admin ....... --api-key --jwt-secret --jwt-expiry --users-db --bridge-managed
    //   C) transporte ....... --passthrough-responses --stream-per-delta --qwen-standard
    //                         --thinking-format (alias --thinking) --thinking-off-roles
    //   D) harness/prompt ... --harness --harness-behavior-tail --harness-tools-tail
    //   E) coerção de tools . --upstream-parser --coerce-text-tools --coerce-narrated --tool-strategy
    //   F) anti-repeat ...... --anti-repeat --anti-repeat-abort --anti-repeat-retry
    //                         --anti-repeat-window --anti-repeat-notice
    //   G) orçamento ........ --max-tokens --coerced-max-tokens (alias --cpu-max-tokens)
    //                         --context-guard --context-tokens --context-threshold
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
                case "--harness": Harness = cliArgs[i + 1] is not "false" and not "0"; break;
                case "--harness-behavior-tail": HarnessBehaviorTail = cliArgs[i + 1]; BehaviorTailCustom = true; break;
                case "--harness-tools-tail": HarnessToolsTail = cliArgs[i + 1]; ToolsTailCustom = true; break;

                // ---------- E) coerção de tools ----------
                // Capacidade do upstream: native (parser proprio) x none (texto puro).
                case "--upstream-parser": UpstreamParser = cliArgs[i + 1].Trim().ToLowerInvariant(); break;
                case "--coerce-text-tools": CoerceTextTools = cliArgs[i + 1] is not "false" and not "0"; break;
                case "--coerce-narrated": CoerceNarrated = cliArgs[i + 1] is not "false" and not "0"; break;
                case "--tool-strategy": ToolStrategy = cliArgs[i + 1] is not "false" and not "0"; break;

                // ---------- F) anti-repeat ----------
                case "--anti-repeat": AntiRepeat = cliArgs[i + 1] is not "false" and not "0"; break;
                case "--anti-repeat-abort": AntiRepeatAbort = cliArgs[i + 1] is not "false" and not "0"; break;
                case "--anti-repeat-retry": AntiRepeatRetry = cliArgs[i + 1] is not "false" and not "0"; break;
                case "--anti-repeat-window": AntiRepeatWindow = int.Parse(cliArgs[i + 1]); break;
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

                // ---------- H) geração/artefatos ----------
                // Invariante: em locale pt-BR, Parse("0.7") quebraria sem isso
                case "--temperature": Temperature = double.Parse(cliArgs[i + 1], CultureInfo.InvariantCulture); break;
                // 0 = sem corte do resultado de tool (conteudo integral).
                case "--tool-output-limit": ToolOutputLimit = int.Parse(cliArgs[i + 1]); break;

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
        if (AntiRepeatWindow < 64 || AntiRepeatWindow > 2048)
            errors.Add($"--anti-repeat-window {AntiRepeatWindow} fora da faixa 64..2048");
        if (AntiRepeatNotice.Length < 20)
            errors.Add("--anti-repeat-notice precisa de >= 20 caracteres");
        if (ContextGuard && ContextTokens <= 0)
            errors.Add("--context-guard 1 exige --context-tokens > 0 (sem o tamanho do ctx o guard nunca dispara)");

        if (CpuMaxTokens > 0 && !CoerceFor())
            warns.Add($"--coerced-max-tokens {CpuMaxTokens} inerte: use --upstream-parser none ou --coerce-text-tools 1");
        if (!Harness && (BehaviorTailCustom || ToolsTailCustom))
            warns.Add("cauda de harness declarada com --harness 0: inerte (a bridge nao injeta prompt)");
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
        Add("harness-prompt", Harness ? "harness" : "",
            BehaviorTailCustom ? "harness-behavior-tail" : "", ToolsTailCustom ? "harness-tools-tail" : "");
        Add("coercao-tools", CoerceFor() ? "coercao=on" : "coercao=off", "upstream-parser=" + UpstreamParser,
            CoerceTextTools ? "coerce-text-tools" : "", CoerceNarrated ? "coerce-narrated" : "",
            ToolStrategy ? "tool-strategy" : "");
        Add("anti-repeat", AntiRepeat ? "anti-repeat" : "", AntiRepeatAbort ? "anti-repeat-abort" : "",
            AntiRepeatRetry ? "anti-repeat-retry" : "", "window=" + AntiRepeatWindow);
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
                    ["tps_total"] = Math.Round(tpsTotal, 2),
                    // Recusas por anel (sempre contadas, independente do diagnostic).
                    ["ring2_deny"] = Metrics.Ring2Deny,
                    ["ring3_deny"] = Metrics.Ring3Deny,
                    ["nudge_args"] = Metrics.NudgeArgs
                });
                return;
            }

            // ---- API de gerenciamento (permite operar a bridge remotamente)
            if (ctx.Request.HttpMethod == "GET" && path == "/api/status")
            {
                if (!ClientAuthorized(ctx)) { WriteJson(ctx, 401, new JsonObject { ["error"] = "nao autorizado (Bearer JWT ou ApiKey)" }); return; }
                WriteJson(ctx, 200, BuildStatus());
                return;
            }

            // Flag mestra do harness (fonte unica: a web consulta aqui).
            if (ctx.Request.HttpMethod == "GET" && path == "/api/harness")
            {
                WriteJson(ctx, 200, new JsonObject { ["harness"] = TailsAtivas() });
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
                Log(new JsonObject { ["event"] = "config", ["thinking"] = Thinking, ["temperature"] = Temperature, ["max_tokens"] = MaxTokens, ["cpu_max_tokens"] = CpuMaxTokens });
                WriteJson(ctx, 200, BuildStatus());
                return;
            }

            if (ctx.Request.HttpMethod == "POST" && path == "/api/tools/execute")
            {
                // Execução de tools na bridge (dono atual: Web delega p/ cá).
                // Corpo: {name, arguments, timeout?}. Raiz = --workspace ou cwd.
                var req = JsonNode.Parse(ReadBody(ctx))?.AsObject() ?? new JsonObject();
                string name = req["name"]?.GetValue<string>() ?? "";
                string args = req["arguments"]?.ToJsonString() ?? "{}";
                int timeout = req["timeout"]?.GetValue<int>() ?? 300;
                string root = WorkspaceRoot.Length > 0 ? WorkspaceRoot : Environment.CurrentDirectory;
                var executor = new Tools.BridgeToolExecutor(root, Upstream, ApiKey);
                var result = executor.Execute(name, args, Math.Clamp(timeout, 1, 600));
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
                    ["status"] = "ok", ["bridge"] = "qwen3-csharp",
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
["api_key"] = string.IsNullOrEmpty(ApiKey) ? "" : "***",
        ["passthrough_responses"] = PassthroughResponses,
        ["tool_strategy"] = ToolStrategy,
        ["cpu_max_tokens"] = CpuMaxTokens, ["coerced_max_tokens"] = CpuMaxTokens,
        ["upstream_parser"] = UpstreamParser,
        ["anti_repeat"] = AntiRepeat, ["anti_repeat_abort"] = AntiRepeatAbort,
        ["diagnostic_level"] = DiagnosticLevel,
        ["config_groups"] = ConfigGroups(),
        ["harness"] = TailsAtivas(), ["legacy_harness"] = LegacyHarness, ["stream_per_delta"] = StreamPerDelta,
        ["bridge_managed"] = BridgeManagedAll ? "all" : "direct-only",
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
        try
        {
            var node = JsonNode.Parse(body)?.AsObject();
            // Marca opencode ANTES do rewrite (model com sufixo = tubo puro).
            bool opencodeIn = node is not null && IsOpencodeModel(node["model"]?.GetValue<string>());
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
                                    mo["content"] = LegacyHarness ? WithSystemGuardrail(mtext) : mtext;
                                else if (mo["content"] is JsonArray mparts)
                                {
                                    foreach (var p in mparts)
                                    {
                                        if (p is JsonObject po && po["text"] is JsonValue ptv && ptv.TryGetValue<string>(out string? ptext) && !string.IsNullOrWhiteSpace(ptext))
                                        { po["text"] = LegacyHarness ? WithSystemGuardrail(ptext) : ptext; break; }
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
            // Temperatura efetiva no repasse: o cliente manda, senao perfil/global
            // (escala 0.1 baixo / 0.3 medio / 0.5 alto). Sem isto o llama usava o
            // --temp 1.0 da unit e o numero do perfil nunca valia no /v1/chat.
            if (node is not null && node["temperature"] is null)
            {
                double effTemp = Temperature;
                if (node["model"]?.GetValue<string>() is string tmid2
                    && TryProfile(tmid2, out double pt2, out _) && !double.IsNaN(pt2))
                    effTemp = pt2;
                node["temperature"] = effTemp;
                outBody = node.ToJsonString(SerOpts);
                if (Diagnostic) Log(new JsonObject
                {
                    ["event"] = "temp-inject",
                    ["temp"] = effTemp,
                    ["model"] = node["model"]?.GetValue<string>() ?? Model
                });
            }
            // Formula (Harness, cliente web = model sem sufixo de perfil):
            // system + agents + tail. Opencode (sufixo) passa limpo.
            // Sem switch mestre: cada cauda se aplica por estar configurada
            // (appsettings ou --harness-behavior-tail / --harness-tools-tail).
            if (node is not null
                && node["messages"] is JsonArray fm)
            {
                foreach (var m in fm)
                {
                    if (m is JsonObject mo && mo["role"]?.GetValue<string>() is "system" or "developer"
                        && mo["content"] is JsonValue mcv && mcv.TryGetValue<string>(out string? mtext))
                    { mo["content"] = WithFormula(mtext); break; }
                }
                outBody = node.ToJsonString(SerOpts);
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
            // Qwen liga thinking sozinho; vale p/ todo cliente sem flag propria.
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
                StripExecuteTools(toolsArr, "chat");
                NeutralizeCodeMode(node);
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
        if (isChat && AntiRepeat && AntiRepeatAbort && !coerceLine
            && ctx.Request.HttpMethod is "POST" or "PUT" or "PATCH"
            && !BodyWantsStream(outBody) && TryStreamAbortChat(ctx, outBody, startedAt))
            return;
        // Linha CPU + stream: o opencode TUI aborta em 300s sem headers e o
        // llama leva minutos no prefill — descarrega headers + heartbeat AGORA
        // (comentário SSE, ignorado pelo parser) e só então chama o upstream.
        bool headersSent = false;
        if (coerceLine && wantStream)
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.OutputStream.Write(new UTF8Encoding(false).GetBytes(": bridge working\n\n"));
            ctx.Response.OutputStream.Flush();
            headersSent = true;
        }
        using var upstreamResponse = Http.Send(forward, HttpCompletionOption.ResponseHeadersRead);
        if (coerceLine && upstreamResponse.IsSuccessStatusCode)
        {
            CoercedChatResponse(ctx, upstreamResponse, wantStream, headersSent, AnnouncedTools(outBody), startedAt);
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
                        if (data is not ("[DONE]" or "")) WalkText(JsonNode.Parse(data), sseText);
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
            // Inibivel (--harness 0): caminho limpo p/ harness opencode.
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

    /// <summary>Coage pseudo-calls do texto em tool_calls nativas (ToolsCoercion); 0 = nada a fazer.</summary>
    /// <param name="root">Completion chat (mutado no lugar).</param>
    /// <param name="known">Tools anunciadas no pedido (allowlist; vazio = catálogo).</param>
    /// <returns>No. de calls sintetizadas.</returns>
    static int CoerceChatCalls(JsonObject root, List<string>? known = null)
    {
        try
        {
            var msg = root?["choices"]?.AsArray().FirstOrDefault()?["message"]?.AsObject();
            if (msg is null || msg["tool_calls"] is not null) return 0;
            string content = msg["content"]?.GetValue<string>() ?? "";
            var calls = ExtractTextToolCalls(content, known);
            if (calls.Count == 0) return 0;
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
            if (root!["choices"]!.AsArray()[0]?.AsObject() is JsonObject first)
                first["finish_reason"] = "tool_calls";
            return calls.Count;
        }
        catch { return 0; }
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
        if (!headersSent)
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream";
        }
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
    /// <param name="thinking">Modo thinking efetivo.</param>
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
        // ThinkingOffRoles: o operador manda desligar por NOME de role, acima do perfil.
        if (ThinkingOffRoles.Contains(RoleOfModel(requestedModel))) thinking = "off";
        // Temperatura: a do opencode tem prioridade; a da linha de comando
        // e so o padrao quando o request nao informa nenhuma.
        temperature = req["temperature"]?.GetValue<double>()
            ?? (hasProfile && !double.IsNaN(profTemp) ? profTemp : Temperature);

        // Converte a conversa Responses -> messages do formato chat
        var messages = InputToMessages(req["input"], req["instructions"], hasProfile);
        // Caudas por peca, penduradas no system que InputToMessages ja consolida
        // (instructions + os system do meio). Em producao esse system sempre existe:
        // no opencode vem do agent.md, na Web UI do system_prompt do agente. O gate
        // fica como estava — sem fallback, para nao duplicar system quando o
        // conteudo vier em lista de partes (visão/multimodal) em vez de string.
        if (messages.Count > 0 && messages[0] is JsonObject sys0
            && sys0["role"]?.GetValue<string>() == "system"
            && sys0["content"] is JsonValue sysv && sysv.TryGetValue<string>(out string? syst))
            sys0["content"] = WithFormula(syst);
        if (Diagnostic)
            Log(new JsonObject
            {
                ["event"] = "tails",
                ["has_system"] = messages.Count > 0 && messages[0]?["role"]?.GetValue<string>() == "system",
                ["attached"] = messages.Count > 0 && messages[0] is JsonObject s1
                               && s1["content"] is JsonValue s1v
                               && (s1v.TryGetValue<string>(out string? t1) ? t1.Contains(HarnessToolsTail[..20]) : false),
                ["behavior_chars"] = HarnessBehaviorTail.Length,
                ["tools_chars"] = HarnessToolsTail.Length
            });
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
        StripExecuteTools(tools, "responses");
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
        if (tools is { Count: > 0 })
        {
            chat["tools"] = tools;
            // Qwen3 decide sozinho ("auto", bake-off 3/3); Coder precisa de
            // "required" p/ emitir tool_calls nativo em vez de texto.
            chat["tool_choice"] = IsCoder ? "required" : "auto";
        }
        NeutralizeCodeMode(chat);
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
        if (PassthroughResponses && !ReqHasTools(req))
        {
            ForwardResponses(ctx, body, req);
            return;
        }
        bool wantStream = req["stream"]?.GetValue<bool>() ?? false;
        var chat = BuildChatRequest(req, out string requestedModel, out double temperature, out string thinking, out int toolCount);

        // Stream real sai por caminho proprio (sem a chamada unica abaixo).
        if (wantStream)
        {
            // Sonda: dump do request CHAT completo (system + tools) do caminho stream.
            if (Diagnostic) Log(new JsonObject { ["event"]="traffic", ["mode"]="translate-stream-req", ["model"]=requestedModel, ["req_len"]=chat.ToJsonString().Length, ["req"]=chat.ToJsonString() });
            HandleResponsesStream(ctx, chat, requestedModel, temperature, thinking, startedAt);
            return;
        }

        // 4.5) CHAMADA AO vLLM
var chatRequest = new HttpRequestMessage(HttpMethod.Post, Upstream + "/v1/chat/completions")
        {
            Content = new StringContent(chat.ToJsonString(SerOpts), Encoding.UTF8, "application/json")
        };
        WithAuth(chatRequest, ctx);
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
        var responseObj = ChatToResponses(chatJson, requestedModel, temperature, thinking, toolCount > 0, chat["messages"]);

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
            // Temperatura efetiva: cliente manda, senao perfil/global (0.1/0.3/0.5).
            // Sem isto o bypass Responses caia no --temp 1.0 do llama.
            if (req["temperature"] is null)
            {
                double effTemp = Temperature;
                if (req["model"]?.GetValue<string>() is string tmid
                    && TryProfile(tmid, out double pt, out _) && !double.IsNaN(pt))
                    effTemp = pt;
                req["temperature"] = effTemp;
                if (Diagnostic) Log(new JsonObject
                {
                    ["event"] = "temp-inject",
                    ["temp"] = effTemp,
                    ["model"] = req["model"]?.GetValue<string>() ?? Model
                });
            }
            if (dropped > 0 || moved || req["temperature"] is not null) outBody = req.ToJsonString(SerOpts);
        }
        catch { outBody = body; }
        // Formula (Harness, cliente web = model sem sufixo): instructions +
        // agents + tail. Opencode (sufixo) passa limpo.
        try
        {
            if (req.TryGetPropertyValue("instructions", out JsonNode? fins) && fins is JsonValue fiv
                && fiv.TryGetValue<string>(out string? finst))
            {
                req["instructions"] = WithFormula(finst);
                outBody = req.ToJsonString(SerOpts);
            }
        }
        catch { }
        var forward = new HttpRequestMessage(HttpMethod.Post, Upstream + "/v1/responses")
        {
            Content = new StringContent(outBody, Encoding.UTF8, "application/json")
        };
        WithAuth(forward, ctx);
using var upstreamResponse = Http.Send(forward, HttpCompletionOption.ResponseHeadersRead);
        double elapsed = (DateTime.UtcNow - startedAt).TotalSeconds;
        string? errBody = null;
        if (!upstreamResponse.IsSuccessStatusCode)
        {
            Interlocked.Increment(ref FailedRequests);
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
            // Retry inibivel (--harness 0): deteccao vira so sonda.
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
            var bytes = new UTF8Encoding(false).GetBytes(json);
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            // Sonda traffic FULL (só com DIAGNOSTICS; sem stream: integros).
            if (Diagnostic)
            {
                var traffic = new JsonObject { ["event"] = "traffic", ["mode"] = "responses", ["model"] = Model, ["req_len"] = outBody.Length, ["req"] = outBody, ["res_len"] = json.Length, ["res"] = json };
                AddPerf(traffic, startedAt, json, outBody);
                Log(traffic);
            }
        }
        ctx.Response.Close();
    }

/// <summary>Texto de content Responses (string ou lista de partes input/output_text).</summary>
    static string ExtractResponseText(JsonNode? node)
    {
        if (node is null) return "";
        if (node is JsonValue v && v.TryGetValue<string>(out string? s)) return s ?? "";
        if (node is JsonArray arr)
        {
            var b = new StringBuilder();
            foreach (var part in arr)
                if (part is JsonObject o && o["text"] is JsonValue tv && tv.TryGetValue<string>(out string? t) && !string.IsNullOrWhiteSpace(t))
                { if (b.Length > 0) b.Append('\n'); b.Append(t); }
            return b.ToString();
        }
        return "";
    }

    /// <summary>Nomes das tools anunciadas (diagnostico: o que o opencode mandou).</summary>
    static JsonArray ToolNames(JsonArray? tools)
    {
        var arr = new JsonArray();
        if (tools is null) return arr;
        foreach (var t in tools)
        {
            string n = (t as JsonObject)?["function"]?["name"]?.GetValue<string>()
                ?? (t as JsonObject)?["name"]?.GetValue<string>() ?? "?";
            if (arr.Count < 24) arr.Add(n);
        }
        return arr;
    }

    /// <summary>Request com tools anunciadas? (llama /v1/responses nao suporta tools).</summary>
    static bool ReqHasTools(JsonObject req) => req["tools"] is JsonArray a && a.Count > 0;

    /// <summary>Complementa qualquer system prompt com o sufixo padrao anti-loop (sem duplicar).</summary>
    /// <param name="text">System prompt original.</param>
    /// <returns>System prompt com o sufixo no final.</returns>
    static string WithSystemGuardrail(string text)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0) return t;
        // QwenStandard: ModelPrompt sintetizado PREFIXADO + system do agente;
        // sem o guardrail legado (o anti-repeat textual vive só no ModelPrompt).
        if (QwenStd) return QwenStandard.ComposeSystem(t);
        if (t.Contains("mais 2 vezes", StringComparison.OrdinalIgnoreCase)) return t;
        return t + "\n\n" + SystemGuardrail;
    }

    /// <remarks>As duas caudas entram sempre que o Harness esta ligado: nao ha excecao
    /// por "ser opencode". Cada enunciado de inducAO mora na sua propria peca: ToolsTail
    /// (como usar a tool) e BehaviorPrompt (disciplina de comportamento), porque sao coisas
    /// diferentes e mexer numa nao deve tocar na outra.</remarks>
    /// <param name="sys">System prompt original.</param>
    internal static string WithFormula(string sys)
    {
        // --harness 0: sem caudas (cliente traz o proprio harness, ex.: opencode).
        if (!Harness) return sys ?? "";
        string t = (sys ?? "").Trim();
        // Ordem: comportamento primeiro (o enquadramento geral), depois uso de tools
        // (o especifico). Assim o "como agir" vem antes do "como chamar a ferramenta".
        if (HarnessBehaviorTail.Length > 0) t = (t.Length > 0 ? t + "\n\n" : "") + HarnessBehaviorTail;
        if (HarnessToolsTail.Length > 0) t = (t.Length > 0 ? t + "\n\n" : "") + HarnessToolsTail;
        return t;
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
    /// <remarks>Compat qwen3/qwen3.5: o template do 14B gera parser gramatical e
    /// rejeita pattern sem ^...$ (400); o do 9B ignorava. Remover equivale ao
    /// comportamento do 9B (geracao livre, description continua guiando).</remarks>
    /// <param name="node">Tools ou sub-schema (mutado no lugar).</param>
    /// <returns>Quantas chaves removidas.</returns>
    static int StripUnanchoredPatterns(JsonNode? node, string path = "$", List<string>? hits = null)
    {
        int dropped = 0;
        if (node is JsonObject obj)
        {
            if (obj["pattern"] is JsonValue pv && pv.TryGetValue<string>(out string? pat)
                && !string.IsNullOrEmpty(pat) && !(pat.StartsWith('^') && pat.EndsWith('$')))
            {
                obj.Remove("pattern");
                hits?.Add(path + ".pattern");
                dropped++;
            }
            foreach (var kv in obj)
                dropped += StripUnanchoredPatterns(kv.Value, path + "." + kv.Key, hits);
        }
        else if (node is JsonArray arr)
        {
            for (int i = 0; i < arr.Count; i++)
                dropped += StripUnanchoredPatterns(arr[i], $"{path}[{i}]", hits);
        }
        return dropped;
    }

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

    /// <summary>Nota que substitui o bloco Code Mode do opencode (sandbox JS inexistente aqui).</summary>
    const string CodeModeNote =
        "# Tools (harness da bridge)\n" +
        "As tools do catalogo (shell, write, read, edit, grep, glob, webfetch, websearch, ...) sao chamadas DIRETAS. " +
        "Esta bridge NAO oferece `execute` nem sandbox JavaScript: Code Mode nao existe aqui. " +
        "Nao empacote varias acoes em um script; emita uma tool call por acao (paralelas quando independentes).\n";

    /// <summary>Remove a tool `execute` (Code Mode do opencode) do catalogo enviado ao modelo.</summary>
    /// <param name="tools">Array de tools (formato chat ou responses).</param>
    /// <param name="mode">Rotulo do caminho, so para log.</param>
    /// <returns>Quantidade removida.</returns>
    internal static int StripExecuteTools(JsonNode? tools, string mode)
    {
        if (!ToolStrategy || tools is not JsonArray arr) return 0;
        int removed = 0;
        for (int i = arr.Count - 1; i >= 0; i--)
        {
            string? name = arr[i]?["function"]?["name"]?.GetValue<string>() ?? arr[i]?["name"]?.GetValue<string>();
            if (!string.Equals(name, "execute", StringComparison.Ordinal)) continue;
            arr.RemoveAt(i);
            removed++;
        }
        if (removed > 0)
            Log(new JsonObject { ["event"] = "tool-strip", ["mode"] = mode, ["tool"] = "execute", ["removed"] = removed, ["left"] = arr.Count });
        return removed;
    }

    /// <summary>Troca o bloco nativo Code Mode (opencode) pela nota de tools diretas.</summary>
    /// <param name="node">Request chat (usa "messages").</param>
    /// <returns>Quantidade de mensagens reescritas.</returns>
    internal static int NeutralizeCodeMode(JsonNode? node)
    {
        if (!ToolStrategy || node?["messages"] is not JsonArray msgs) return 0;
        int hits = 0;
        foreach (var m in msgs)
        {
            if (m is not JsonObject mo || mo["content"] is not JsonValue cv || !cv.TryGetValue<string>(out string? text)) continue;
            int start = text.IndexOf("# Code Mode", StringComparison.Ordinal);
            if (start < 0) continue;
            // Preserva o catalogo de skills (vem logo apos a secao nativa).
            int end = text.IndexOf("Skills provide specialized instructions", start + 1, StringComparison.Ordinal);
            if (end < 0) end = text.IndexOf("\n# ", start + 1, StringComparison.Ordinal);
            string tail = end >= 0 ? text[end..] : "";
            mo["content"] = text[..start] + CodeModeNote + tail;
            hits++;
        }
        if (hits > 0) Log(new JsonObject { ["event"] = "harness-strip", ["mode"] = "code-mode", ["hits"] = hits });
        return hits;
    }

    /// <summary>Tokens de controle que o template vaza para dentro dos argumentos.</summary>
    static readonly Regex ReLeakedTemplate = new(@"<\|?[A-Za-z_]{2,20}\|?>", RegexOptions.Compiled);

    /// <summary>JSON valido? (objeto, tolerante a espacos).</summary>
    /// <param name="text">Texto dos argumentos.</param>
    /// <returns>True quando JsonNode.Parse aceita.</returns>
    static bool IsJsonObject(string text)
    {
        try { return JsonNode.Parse(text) is JsonObject; } catch { return false; }
    }

    /// <summary>Chaves obrigatorias por tool (validadas antes de emitir a chamada).</summary>
    static readonly Dictionary<string, string[]> RequiredArgs = new(StringComparer.Ordinal)
    {
        ["edit"] = new[] { "path", "oldString", "newString" },
        ["write"] = new[] { "path", "content" },
        ["read"] = new[] { "path" },
        ["grep"] = new[] { "pattern" },
        ["glob"] = new[] { "pattern" },
        ["shell"] = new[] { "command" },
        ["webfetch"] = new[] { "url" },
        ["subagent"] = new[] { "agent", "description", "prompt" },
        ["skill"] = new[] { "id" },
        ["question"] = new[] { "questions" },
    };

    /// <summary>Marcadores de template que o 12B vaza para dentro do comando shell.</summary>
    static readonly string[] LeakMarkers = { "<tool_call|>", "<|tool_call>", "<|channel>", "<channel|>" };

    /// <summary>Tools que podem aparecer como chamada inline (template vazado no texto).</summary>
    static readonly HashSet<string> InlineTools = new(StringComparer.Ordinal)
    { "edit", "write", "read", "grep", "glob", "shell", "webfetch", "websearch", "subagent", "skill", "question" };

    /// <summary>Par chave=valor de uma chamada inline (nome{chave: valor, ...}).</summary>
    static readonly Regex ReInlineArg = new(@"(\w+)\s*[:=]\s*(""(?:[^""\\]|\\.)*""|[^,}]+)", RegexOptions.Compiled);

    /// <summary>Chaves obrigatorias ausentes nos argumentos (vazio p/ tool fora do mapa).</summary>
    /// <param name="name">Nome da tool.</param>
    /// <param name="arguments">Argumentos JSON.</param>
    /// <returns>Lista das chaves que faltam.</returns>
    internal static List<string> MissingArgs(string name, string arguments)
    {
        var missing = new List<string>();
        if (!RequiredArgs.TryGetValue(name, out var required)) return missing;
        JsonObject? obj = null;
        try { obj = JsonNode.Parse(arguments) as JsonObject; } catch { }
        if (obj is null) return missing;              // parse quebrado tem tratamento proprio
        foreach (var k in required)
            if (obj[k] is null) missing.Add(k);
        return missing;
    }

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

    /// <summary>Fecha chaves de fechamento sobrando no fim do comando (lixo do template).</summary>
    /// <param name="text">Texto do comando.</param>
    /// <returns>Comando com chaves balanceadas.</returns>
    static string TrimUnbalanced(string text)
    {
        int open = text.Count(c => c == '{');
        int close = text.Count(c => c == '}');
        string t = text;
        while (close > open && t.EndsWith('}'))
        {
            t = t[..^1].TrimEnd();
            close--;
        }
        return t;
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

    /// <summary>Indice do '}' que fecha o objeto iniciado em start (ignora strings).</summary>
    /// <param name="s">Texto JS.</param>
    /// <param name="start">Posicao do '{'.</param>
    /// <returns>Indice do fechamento ou -1.</returns>
    static int FindObjectEnd(string s, int start)
    {
        int depth = 0;
        char quote = '\0';
        for (int i = start; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0')
            {
                if (c == '\\') { i++; continue; }
                if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'' or '`') { quote = c; continue; }
            if (c == '{') depth++;
            else if (c == '}') { depth--; if (depth == 0) return i; }
        }
        return -1;
    }

    /// <summary>Descarta comentarios JS iniciais (// e /* */) antes da chamada.</summary>
    /// <param name="code">Codigo do sandbox.</param>
    /// <returns>Codigo sem comentarios iniciais.</returns>
    static string StripLeadingJsComments(string code)
    {
        int i = 0;
        while (i < code.Length)
        {
            while (i < code.Length && char.IsWhiteSpace(code[i])) i++;
            if (i + 1 < code.Length && code[i] == '/' && code[i + 1] == '/')
            {
                while (i < code.Length && code[i] != '\n') i++;
                continue;
            }
            if (i + 1 < code.Length && code[i] == '/' && code[i + 1] == '*')
            {
                int end = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) return code[i..];
                i = end + 2;
                continue;
            }
            break;
        }
        return code[i..];
    }

    /// <summary>Parseia um valor JS (subset JSON5) consumindo todo o texto.</summary>
    /// <param name="text">Literal JS.</param>
    /// <param name="node">Valor convertido (null quando falha).</param>
    /// <returns>True quando o literal inteiro e valido.</returns>
    static bool TryParseJsValue(string text, out JsonNode? node)
    {
        node = null;
        try
        {
            int i = 0;
            node = ParseJsValue(text, ref i);
            SkipJsSpace(text, ref i);
            return i == text.Length;
        }
        catch { node = null; return false; }
    }

    /// <summary>Valor JS: objeto, array, string (' " `), booleano, null ou numero.</summary>
    static JsonNode? ParseJsValue(string s, ref int i)
    {
        SkipJsSpace(s, ref i);
        if (i >= s.Length) throw new FormatException("valor JS ausente");
        char c = s[i];
        if (c == '{') return ParseJsObjectBody(s, ref i);
        if (c == '[') return ParseJsArrayBody(s, ref i);
        if (c is '"' or '\'' or '`') return JsonValue.Create(ParseJsString(s, ref i));
        if (s.AsSpan(i).StartsWith("true", StringComparison.Ordinal)) { i += 4; return JsonValue.Create(true); }
        if (s.AsSpan(i).StartsWith("false", StringComparison.Ordinal)) { i += 5; return JsonValue.Create(false); }
        if (s.AsSpan(i).StartsWith("null", StringComparison.Ordinal)) { i += 4; return null; }
        return JsonValue.Create(ParseJsNumber(s, ref i));
    }

    /// <summary>Objeto JS {chave: valor, ...} (chave identificador ou string).</summary>
    static JsonObject ParseJsObjectBody(string s, ref int i)
    {
        i++; // {
        var obj = new JsonObject();
        while (true)
        {
            SkipJsSpace(s, ref i);
            if (i >= s.Length) throw new FormatException("objeto JS sem fechamento");
            if (s[i] == '}') { i++; return obj; }
            string key = s[i] is '"' or '\'' or '`' ? ParseJsString(s, ref i) : ReadJsIdentifier(s, ref i);
            SkipJsSpace(s, ref i);
            if (i >= s.Length || s[i] != ':') throw new FormatException("objeto JS sem ':'");
            i++;
            obj[key] = ParseJsValue(s, ref i);
            SkipJsSpace(s, ref i);
            if (i < s.Length && s[i] == ',') { i++; continue; }
            if (i < s.Length && s[i] == '}') { i++; return obj; }
            throw new FormatException("objeto JS invalido");
        }
    }

    /// <summary>Array JS [valor, ...].</summary>
    static JsonArray ParseJsArrayBody(string s, ref int i)
    {
        i++; // [
        var arr = new JsonArray();
        while (true)
        {
            SkipJsSpace(s, ref i);
            if (i >= s.Length) throw new FormatException("array JS sem fechamento");
            if (s[i] == ']') { i++; return arr; }
            arr.Add(ParseJsValue(s, ref i));
            SkipJsSpace(s, ref i);
            if (i < s.Length && s[i] == ',') { i++; continue; }
            if (i < s.Length && s[i] == ']') { i++; return arr; }
            throw new FormatException("array JS invalido");
        }
    }

    /// <summary>String JS com aspas simples, duplas ou backtick (sem interpolacao).</summary>
    static string ParseJsString(string s, ref int i)
    {
        char quote = s[i++];
        var sb = new StringBuilder();
        while (i < s.Length)
        {
            char c = s[i++];
            if (c == quote) return sb.ToString();
            if (c == '\\' && i < s.Length)
            {
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("escape unicode curto");
                        sb.Append((char)int.Parse(s.AsSpan(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
                continue;
            }
            if (quote == '`' && c == '$' && i < s.Length && s[i] == '{')
                throw new FormatException("template com interpolacao nao suportado");
            sb.Append(c);
        }
        throw new FormatException("string JS sem fechamento");
    }

    /// <summary>Numero JS (inteiro ou decimal, notacao cientifica).</summary>
    static double ParseJsNumber(string s, ref int i)
    {
        int start = i;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] is '-' or '+' or '.' or 'e' or 'E')) i++;
        if (i == start) throw new FormatException("numero JS invalido");
        return double.Parse(s[start..i], NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>Identificador JS (chave sem aspas).</summary>
    static string ReadJsIdentifier(string s, ref int i)
    {
        int start = i;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '$')) i++;
        if (i == start) throw new FormatException("chave JS invalida");
        return s[start..i];
    }

    /// <summary>Pula espacos (e quebras) do literal JS.</summary>
    static void SkipJsSpace(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
    }

    /// <summary>Exceção permanente do caso pattern: sempre ligada, só este caso.</summary>
    /// <remarks>llama.cpp aborta 400 sem ^...$; sem este strip nenhuma tool com
    /// regex chega ao modelo. Independe de --harness/--legacy-harness.</remarks>
    /// <param name="tools">Array de tools (mutado no lugar).</param>
    /// <param name="mode">Rótulo p/ o log (responses/chat).</param>
    static void ApplyPatternException(JsonNode? tools, string mode)
    {
        var hits = new List<string>();
        int dropped = StripUnanchoredPatterns(tools, "$", hits);
        if (dropped > 0)
        {
            var where = new JsonArray();
            foreach (var h in hits) where.Add(h);
            Log(new JsonObject { ["event"] = "pattern-strip", ["mode"] = mode, ["dropped"] = dropped, ["hits"] = where, ["tools"] = ToolNames(tools as JsonArray) });
        }
    }

    /// <summary>AntiRepeat: a cauda (janela) do thinking ja apareceu antes no proprio stream?</summary>
    /// <param name="accumulated">Thinking acumulado ate aqui.</param>
    /// <param name="window">Tamanho da cauda comparada (chars).</param>
    /// <returns>True se a sequencia se repete (loop invalido provavel).</returns>
    static bool ThinkingRepeats(string accumulated, int window)
    {
        if (string.IsNullOrEmpty(accumulated) || window < 32) return false;
        // Normaliza (caixa/espaco) p/ pegar repeticao quase-verbatim do decoder.
        string norm = NormRepeat(accumulated);
        // Cauda verbatim (loops longos exatos).
        if (norm.Length >= 2 * window)
        {
            string tail = norm[^window..];
            if (norm.AsSpan(0, norm.Length - window).IndexOf(tail.AsSpan(), StringComparison.Ordinal) >= 0)
                return true;
        }
        // Loop curto com variacao ("Olá!" no meio): 3 blocos finais
        // consecutivos similares (>=0.85) = repeticao invalida provavel.
        foreach (int u in new[] { 24, 32, 40, 48, 64, 80, 96, 128 })
        {
            if (norm.Length < 3 * u) continue;
            var a = norm.AsSpan(norm.Length - u);
            var b = norm.AsSpan(norm.Length - 2 * u, u);
            var c = norm.AsSpan(norm.Length - 3 * u, u);
            if (BlockSim(a, b) >= 0.85 && BlockSim(b, c) >= 0.85) return true;
        }
        return false;
    }

    /// <summary>AntiRepeat cross-turn: assinatura canonica de uma tool call ("nome(args)").</summary>
    /// <remarks>Repeticao entre turnos nao aparece no thinking de um unico stream, entao
    /// <see cref="ThinkingRepeats"/> nunca a ve. Esta assinatura compara a chamada nova
    /// com as que ja constam no historico do proprio request.</remarks>
    static string ToolSig(string name, string args)
        => NormRepeat((name ?? "").Trim().ToLowerInvariant() + "(" + NormRepeat(args ?? "") + ")");

    /// <summary>Assinaturas de tool call que ja se repetiram no historico (>= minCount).</summary>
    static HashSet<string> LoopedToolSigs(JsonNode? messages, int minCount)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        if (messages is JsonArray arr)
            foreach (var m in arr)
            {
                if (m is not JsonObject mo || mo["tool_calls"] is not JsonArray calls) continue;
                foreach (var c in calls)
                {
                    if (c is not JsonObject co || co["function"] is not JsonObject fn) continue;
                    string sig = ToolSig(fn["name"]?.GetValue<string>() ?? "",
                                         fn["arguments"]?.GetValue<string>() ?? "");
                    if (sig == "()") continue;
                    counts[sig] = counts.TryGetValue(sig, out int n) ? n + 1 : 1;
                }
            }
        return new HashSet<string>(counts.Where(kv => kv.Value >= minCount).Select(kv => kv.Key),
                                   StringComparer.Ordinal);
    }

    /// <summary>Similaridade 0..1 entre blocos (1 - levenshtein/tamanho).</summary>
    static double BlockSim(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        int n = a.Length, m = b.Length;
        if (n == 0 || m == 0) return 0;
        Span<int> prev = stackalloc int[m + 1];
        Span<int> cur = stackalloc int[m + 1];
        for (int j = 0; j <= m; j++) prev[j] = j;
        for (int i = 1; i <= n; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= m; j++)
                cur[j] = Math.Min(prev[j] + 1, Math.Min(cur[j - 1] + 1, prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1)));
            var t = prev; prev = cur; cur = t;
        }
        return 1.0 - (double)prev[m] / Math.Max(n, m);
    }

    internal static bool ContextGuardEnabled() => ContextGuard;
    internal static (bool Enabled, double Threshold, int Tokens) ContextGuardConfig() => (ContextGuard, ContextThreshold, ContextTokens);
    internal static void ContextGuardSetup(bool enabled, double threshold, int tokens)
    { ContextGuard = enabled; ContextThreshold = threshold; ContextTokens = tokens; }

    // Guarda de ctx: prompt atingiu o limiar configurado.
    internal static bool ContextGuardTripped(string json, out int promptTokens, out double fraction)
    {
        promptTokens = 0; fraction = 0;
        if (!ContextGuard || ContextTokens <= 0) return false;
        try { promptTokens = JsonNode.Parse(json)?.AsObject()?["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0; }
        catch { return false; }
        fraction = (double)promptTokens / ContextTokens;
        return fraction >= ContextThreshold;
    }

    /// <summary>Detector com sonda: avalia e registra o probe Serilog (trip=Warning, resto=Debug).</summary>
    /// <param name="accumulated">Texto acumulado ate aqui.</param>
    /// <param name="where">Ponto de observacao.</param>
    /// <returns>True se repeticao detectada.</returns>
    static bool CheckRepeat(string accumulated, string where)
    {
        bool trip = AntiRepeat && ThinkingRepeats(accumulated, AntiRepeatWindow);
        // Sonda: loga no maximo 1x a cada 2k chars p/ nao afogar o sink em stream.
        int chars = accumulated?.Length ?? 0;
        if (trip)
        {
            int dupAt = -1; string dupCtx = "";
            var norm = NormRepeat(accumulated ?? "");
            int w = Math.Min(AntiRepeatWindow, norm.Length);
            if (w > 0 && norm.Length >= 2 * w)
            {
                var win = norm[^w..];
                dupAt = norm[..^w].IndexOf(win, StringComparison.Ordinal);
                if (dupAt >= 0)
                    dupCtx = Truncate(norm.Substring(dupAt, Math.Min(80, norm.Length - dupAt)), 80);
            }
            Sonda(where, chars, trip, Truncate(norm, 80), dupAt, dupCtx);
        }
        else if (chars < 512 || (chars % 2048) < 256)
            Sonda(where, chars, trip, Truncate(NormRepeat(accumulated ?? ""), 80));
        return trip;
    }

    /// <summary>Extrai texto util de um chat/completions (content + reasoning) p/ a sonda do bypass.</summary>
    static string ExtractChatText(string json)
    {
        try
        {
            var root = JsonNode.Parse(json)?.AsObject();
            var b = new StringBuilder();
            void Add(string? t) { if (!string.IsNullOrEmpty(t)) { if (b.Length > 0) b.Append('\n'); b.Append(t); } }
            foreach (var ch in root?["choices"]?.AsArray() ?? new JsonArray())
            {
                var m = ch?.AsObject()?["message"]?.AsObject() ?? ch?.AsObject()?["delta"]?.AsObject();
                if (m is null) continue;
                if (m["content"]?.GetValue<string>() is string c) Add(c);
                if (m["reasoning_content"]?.GetValue<string>() is string r) Add(r);
                else if (m["reasoning"]?.GetValue<string>() is string r2) Add(r2);
            }
            return b.ToString();
        }
        catch { return ""; }
    }

    /// <summary>Coleta valores de chaves de texto ("text"/"delta"/"content") em qualquer JSON (SSE do bypass).</summary>
    static void WalkText(JsonNode? node, StringBuilder b)
    {
        if (node is JsonObject o)
            foreach (var kv in o)
            {
                if ((kv.Key is "text" or "delta" or "content") && kv.Value is JsonValue v
                    && v.TryGetValue<string>(out string? t) && !string.IsNullOrEmpty(t))
                { if (b.Length > 0) b.Append('\n'); b.Append(t); }
                else WalkText(kv.Value, b);
            }
        else if (node is JsonArray a)
            foreach (var it in a) WalkText(it, b);
    }

    /// <summary>Extrai todo texto de um objeto Responses (output) p/ a sonda do bypass.</summary>
    static string ExtractResponsesText(string json)
    {
        try
        {
            var root = JsonNode.Parse(json)?.AsObject();
            var b = new StringBuilder();
            if (root?["output"] is JsonArray out_) foreach (var it in out_) WalkText(it, b);
            else WalkText(root, b);
            return b.ToString();
        }
        catch { return ""; }
    }

    /// <summary>Bypass chat: 1 retry de contencao com o aviso anexado as messages.</summary>
    /// <summary>O corpo pediu stream? Decidido no corpo, nao no flag de coerce.</summary>
    static bool BodyWantsStream(string body)
    {
        try { return JsonNode.Parse(body)?["stream"]?.GetValue<bool>() ?? false; }
        catch { return false; }
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
                            if (!string.IsNullOrEmpty(ar)) { cur.Args.Append(ar); scan.Append(ar); gotAnything = true; }
                            calls[idx] = cur;
                        }
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

            // Salvage: entra toda call que tenha nome. Arguments truncado e
            // mantido como veio (o llama nativo tambem devolve truncado quando
            // bate max_tokens; o runner salva o que fecha por regex).
            var keep = new JsonArray();
            foreach (var kv in calls)
            {
                if (string.IsNullOrEmpty(kv.Value.Name)) continue;
                string a = kv.Value.Args.ToString();
                keep.Add(new JsonObject
                {
                    ["id"] = string.IsNullOrEmpty(kv.Value.Id) ? "call_" + kv.Key : kv.Value.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = kv.Value.Name, ["arguments"] = a }
                });
            }
            string cbody = content.ToString();
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

    /// <summary>Funde system/developer do input num unico system inicial (jinja Qwen exige system primeiro).</summary>
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
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = LegacyHarness ? WithSystemGuardrail(sys.ToString()) : sys.ToString() })
            }
        };
        foreach (var it in rest) final.Add(it?.DeepClone());
        req["input"] = final;
        return true;
    }

    static int StripReasoningItems(JsonObject req)
    {
        if (req["input"] is not JsonArray list) return 0;
        var clean = new JsonArray();
        int dropped = 0;
        foreach (var it in list)
        {
            if (it is JsonObject o && o["type"]?.GetValue<string>() == "reasoning") { dropped++; continue; }
            clean.Add(it?.DeepClone());
        }
        if (dropped > 0) req["input"] = clean;
        return dropped;
    }

    /// <summary>Streaming real: repassa deltas do vLLM na hora, fecha com completed.</summary>
    /// <param name="ctx">Contexto HTTP.</param>
    /// <param name="chat">Chat request ja montado.</param>
    /// <param name="requestedModel">Model id p/ eco.</param>
    /// <param name="temperature">Efetiva.</param>
    /// <param name="thinking">Modo thinking efetivo.</param>
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
        WithAuth(chatRequest, ctx);

        // Headers do upstream ANTES de abrir o SSE: se der erro aqui,
        // devolvemos JSON de erro limpo (nada foi enviado ao cliente ainda).
        using var upstreamResponse = Http.Send(chatRequest, HttpCompletionOption.ResponseHeadersRead);
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
                        if (AntiRepeat && acc.Args.Length >= 1024 && !argsProbeLogged.Contains(index))
                        {
                            string argsText = acc.Args.ToString();
                            if (ThinkingRepeats(argsText, AntiRepeatWindow))
                            {
                                argsProbeLogged.Add(index);
                                Log(new JsonObject { ["event"] = "anti-repeat-args", ["tool"] = acc.Name,
                                    ["chars"] = argsText.Length, ["window"] = AntiRepeatWindow });
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
        var loopedSigs = AntiRepeat ? LoopedToolSigs(chat["messages"], 2)
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
            if (loopedSigs.Contains(ToolSig(acc.Name, arguments)))
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
    static JsonArray InputToMessages(JsonNode? input, JsonNode? instructions, bool profileClient = false)
    {
        var messages = new JsonArray();

// "instructions" + todo system/developer do meio viram UM system inicial
        // (jinja Qwen exige system primeiro; resumo de compactacao chega no meio).
        var sysText = new StringBuilder();
        if (instructions is JsonValue instructionNode && instructionNode.TryGetValue<string>(out string? systemText) && !string.IsNullOrWhiteSpace(systemText))
            sysText.Append(systemText.Trim());

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
                if (role is ("system" or "developer"))
                {
                    if (sysText.Length > 0) sysText.Append("\n\n");
                    sysText.Append(content.Trim());
                    continue;
                }
                if (role is not ("user" or "assistant")) role = "user";
                // O opencode reenvia a tool call TAMBEM como texto
                // ("[Assistant tool call]: nome({...})"). Mantido no historico, o
                // modelo copia esse formato em vez de emitir a chamada nativa; a
                // chamada estruturada (function_call) continua logo abaixo.
                if (role == "assistant" && !profileClient && LegacyHarness) content = StripNarratedCalls(content);
                if (string.IsNullOrWhiteSpace(content)) continue;
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
                if (ToolOutputLimit > 0 && resultText.Length > ToolOutputLimit) resultText = resultText[..ToolOutputLimit];
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = callId, ["content"] = resultText });
            }
// Outros tipos (reasoning, item_reference, calls ainda nao executados)
            // sao ignorados de proposito: nao fazem falta no fluxo chat.
        }
        // Respostas as perguntas de risco do turno anterior viram instrucao no
        // historico do MODELO (nao texto de prosa): e o que o reemite a chamada.
        foreach (string notice in RiskScanAnswers(input))
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = notice });

        if (sysText.Length > 0)
            messages.Insert(0, new JsonObject { ["role"] = "system", ["content"] = LegacyHarness ? WithSystemGuardrail(sysText.ToString()) : sysText.ToString() });
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
        return converted.Count > 0 ? converted : null;
    }

    // ---------------------------------------------------------------------
    // 4.6) resposta chat -> objeto Responses
    //   reasoning (thinking do Qwen3-8B) -> item "reasoning"   [modo events]
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
        var loopedSigs = AntiRepeat ? LoopedToolSigs(historyMessages, 2)
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
                // AntiRepeat cross-turn: nao emitimos a chamada que ja repetiu; o modelo
                // so reenfileiraria o mesmo comando e o turno travaria aqui.
                if (loopedSigs.Contains(ToolSig(nativeName, JsonToString(funcObj["arguments"]))))
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
                if (probeArgs.Length >= 1024 && ThinkingRepeats(probeArgs, AntiRepeatWindow))
                    Log(new JsonObject { ["event"] = "anti-repeat-args", ["tool"] = probeName,
                        ["chars"] = probeArgs.Length, ["window"] = AntiRepeatWindow, ["mode"] = "convert" });

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
    // Tags de controle de canal/thinking que alguns templates deixam vazar no
    // texto (Gemma 4: <|channel>thought ... <channel|>; Qwen: </thought>).
    // Nao sao conteudo: se chegam ao reasoning, a UI mostra a tag crua no bloco
    // de pensamento. Removidas com sonda (evento reasoning-tag).
    static readonly string[] ControlTags =
        { "<|channel>", "<channel|>", "<|channel|>", "<thought>", "</thought>", "<thinking>", "</thinking>" };

    /// <summary>Tamanho do maior sufixo de <paramref name="s"/> que e prefixo de <paramref name="tag"/>.</summary>
    static int PartialTagPrefixLen(string s, string tag)
    {
        int best = 0;
        int max = Math.Min(tag.Length - 1, s.Length);
        for (int k = max; k > best; k--)
            if (s.EndsWith(tag[..k], StringComparison.OrdinalIgnoreCase)) { best = k; break; }
        return best;
    }

    // Marcadores TEXTUAIS de tool call: o opencode serializa a tool no historico
    // como "[Assistant tool call]: nome({...})" e o modelo copia esse formato.
    // No stream o texto a partir deles fica retido; quem decide (descartar como
    // chamada ou devolver como prosa) e o fecho.
    static readonly string[] TextToolMarkers =
        { "[Assistant tool call]", "<tool_call>", "<tools>", "<function>" };

    /// <summary>Posicao do primeiro marcador de tool call no texto (-1 = prosa).</summary>
    static int TextToolMarkerIndex(string s)
    {
        if (string.IsNullOrEmpty(s)) return -1;
        int best = -1;
        foreach (string marker in TextToolMarkers)
        {
            int at = s.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && (best < 0 || at < best)) best = at;
        }
        return best;
    }

    /// <summary>Quanto segurar da cauda: tag partida ou inicio de marcador de call.</summary>
    static int ContentHoldBack(string carry)
    {
        int hold = PartialTagPrefixLen(carry, " thinking");
        foreach (string marker in TextToolMarkers)
        {
            int at = carry.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            int k = at >= 0 ? carry.Length - at : PartialTagPrefixLen(carry, marker);
            if (k > hold) hold = k;
        }
        return hold;
    }

    /// <summary>Tamanho do maior sufixo de <paramref name="s"/> que e prefixo de uma tag de controle.</summary>
    /// <remarks>Serve para segurar so o pedaco ambiguo entre chunks; sem isso o
    /// raciocinio inteiro atrasaria N chars e o hold-back sairia fora de ordem.</remarks>
    static int PartialTagLen(string s)
    {
        int best = 0;
        foreach (var tag in ControlTags)
        {
            int max = Math.Min(tag.Length - 1, s.Length);
            for (int k = max; k > best; k--)
                if (s.EndsWith(tag[..k], StringComparison.OrdinalIgnoreCase)) { best = k; break; }
        }
        return best;
    }

    /// <summary>Remove tags de controle do texto de reasoning, registrando o achado.</summary>
    static string StripControlTags(string s, string where)
    {
        if (string.IsNullOrEmpty(s)) return s;
        foreach (var tag in ControlTags)
        {
            if (s.IndexOf(tag, StringComparison.OrdinalIgnoreCase) < 0) continue;
            s = s.Replace(tag, "", StringComparison.OrdinalIgnoreCase);
            if (Diagnostic) Log(new JsonObject { ["event"] = "reasoning-tag", ["where"] = where, ["tag"] = tag });
        }
        return s;
    }

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
    /// <param name="tail">Amostra da cauda (80 chars).</param>
    static void Sonda(string where, int chars, bool trip, string tail, int dupAt = -1, string dupCtx = "")
    {
        if (!Diagnostic) return;
        try
        {
            // dup_at = posicao (no texto normalizado) da ocorrencia anterior da
            // mesma janela; dup_ctx = 80 chars a partir dela. Sem isso o log so
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

    /// <summary>Normaliza texto p/ o detector (minusculo, espacos colapsados).</summary>
    static string NormRepeat(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var b = new StringBuilder(s.Length);
        bool sp = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c)) { if (!sp) { b.Append(' '); sp = true; } }
            else { b.Append(char.ToLowerInvariant(c)); sp = false; }
        }
        return b.ToString();
    }

    /// <summary>Id aleatorio hex de 16 chars.</summary>
    /// <returns>Id.</returns>
    static string NewId() => Guid.NewGuid().ToString("N")[..16];

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
}
