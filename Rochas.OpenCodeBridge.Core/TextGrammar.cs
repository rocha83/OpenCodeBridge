using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Gramática de texto do protocolo: marcadores que o modelo inventa no meio do
/// tráfego, reparo de escapes, leitura de JSON com chaves balanceadas e o
/// guardrail final do system prompt. Núcleo puro — nenhum cliente reimplementa.
/// </summary>
public static class TextGrammar
{
    /// <summary>JSON sem escape unicode: acentos do pt-BR chegam inteiros ao cliente.</summary>
    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>opencode serializa a tool call no histórico como "[Assistant tool call]: nome({...})".</summary>
    public static readonly Regex ReAssistantToolCall = new(
        @"\[\s*Assistant\s+tool\s+call\s*\]\s*:?\s*([A-Za-z_][A-Za-z0-9_.]*)\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Sufixo padrão de system prompt (modo legado: sem QwenStandard).</summary>
    public const string SystemGuardrail = "ao listar arquivos locais preferencialmente use as tools glob ou read, atentando-se ao modo de uso correto destas, e ao navegar na web use somente enderecos externos, informando user agent Firefox no Windows 11 ao navegar via fetch ou curl; ao executar tools que resultem em erro, leia a mensagem de erro e adapte a proxima chamada, tentando no maximo mais 2 vezes, e sempre responda na lingua do usuario, seja verboso apenas ao declarar acoes com tools dentro do loop agentico e em conversa simples sem tools responda em no maximo 2 frases; porem envie apenas uma vez a cada mensagem do usuario JAMAIS repita a mesma pergunta varias vezes; se o usuario nao respondeu siga com a melhor suposicao e declare a decisao tomada; FORMA DE USO DAS TOOLS (obrigatoria): catalogo — leitura: glob (lista por padrao), read (le arquivo), grep (busca regex), fetch (GET http somente-leitura), display (mostra na tela); edicao: write (grava/cria), edit (substitui trecho exato); execucao: shell (comando unico; pipes/redirecionamento via bash -c); git somente-leitura (status/log/diff; escrita via shell com cd + git); delegacao: subagent (uma fase por vez); use apenas as anunciadas no bloco # Tools, com os nomes exatos, sem nenhum prefixo (nunca default., nunca outro namespace); cada chamada vai sozinha em <tool_call>{\"name\": \"<nome-exato>\", \"arguments\": {<args>}}</tool_call>, sem prosa antes, dentro ou depois do bloco; utilize a tool echo apenas em composicao de scripts, NUNCA para enviar mensagens ao usuario limite de writes por faixa: apos criar os artefatos planejados (max 8), responda BUILD-OK. Nao repita a mesma chamada write.";

    /// <summary>Id aleatório hex de 16 chars.</summary>
    /// <returns>Id.</returns>
    public static string NewId() => Guid.NewGuid().ToString("N")[..16];

    /// <summary>Serializa nó; string passa direto, nulo vira {}</summary>
    /// <param name="node">Nó qualquer.</param>
    /// <returns>JSON textual.</returns>
    public static string JsonToString(JsonNode? node) =>
        node is null ? "{}"
        : node is JsonValue value && value.TryGetValue<string>(out string? s) ? s
        : node.ToJsonString(JsonOpts);

    /// <summary>Parse JSON tolerante: estrito e, se falhar, com escapes reparados.</summary>
    /// <param name="json">Texto JSON.</param>
    /// <returns>Nó ou null.</returns>
    public static JsonNode? TryParseJson(string json)
    {
        try { return JsonNode.Parse(json); } catch { }
        string repaired = RepairJsonEscapes(json);
        if (repaired == json) return null;
        try { return JsonNode.Parse(repaired); } catch { return null; }
    }

    /// <summary>Repara escapes que o modelo inventa (barra + char fora do JSON:
    /// <c>\;</c>, <c>\-</c>, <c>\s</c>) duplicando a barra. Sem isso
    /// <c>find ... -exec cat {} \;</c> derruba o parse e a chamada narrada morre.</summary>
    /// <param name="json">Texto JSON cru.</param>
    /// <returns>JSON com escapes válidos.</returns>
    public static string RepairJsonEscapes(string json)
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

    /// <summary>Extrai objeto JSON com chaves balanceadas a partir de um <c>{</c> (respeita strings).</summary>
    /// <param name="s">Texto.</param>
    /// <param name="start">Índice do <c>{</c>.</param>
    /// <param name="json">Objeto extraído (vazio quando falha).</param>
    /// <returns>True quando fecha o objeto.</returns>
    public static bool TryBalanced(string s, int start, out string json)
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

    /// <summary>Remove a tool call narrada ("[Assistant tool call]: nome({...})") do texto.</summary>
    /// <param name="text">Texto do assistente.</param>
    /// <returns>Texto sem os blocos narrados.</returns>
    public static string StripNarratedCalls(string text)
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

    /// <summary>Complementa o system prompt com o sufixo padrão anti-loop (sem duplicar).</summary>
    /// <param name="text">System prompt original.</param>
    /// <param name="qwenStandard">Modo QwenStandard ligado (ModelPrompt + system, sem guardrail legado).</param>
    /// <returns>System prompt final.</returns>
    public static string WithSystemGuardrail(string? text, bool qwenStandard)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0) return t;
        // QwenStandard: ModelPrompt sintetizado PREFIXADO + system do agente;
        // sem o guardrail legado (o anti-repeat textual vive só no ModelPrompt).
        if (qwenStandard) return QwenStandard.ComposeSystem(t);
        if (t.Contains("mais 2 vezes", StringComparison.OrdinalIgnoreCase)) return t;
        return t + "\n\n" + SystemGuardrail;
    }
}

/// <summary>Regras de montagem do tráfego de entrada (Responses -> chat).</summary>
/// <param name="LegacyHarness">Harness legado ativo (strip de call narrada + guardrail).</param>
/// <param name="QwenStandard">Modo QwenStandard (system sintetizado).</param>
/// <param name="ToolOutputLimit">Corte por resultado de tool (0 = sem corte).</param>
/// <param name="RiskAnswers">Respostas de risco do turno anterior (null = política desligada).</param>
public sealed record TranslationOptions(
    bool LegacyHarness = true,
    bool QwenStandard = false,
    int ToolOutputLimit = 0,
    Func<JsonNode?, IReadOnlyList<string>>? RiskAnswers = null)
{
    /// <summary>Opções neutras (sem harness, sem política de risco).</summary>
    public static TranslationOptions Plain { get; } = new(LegacyHarness: false);
}
