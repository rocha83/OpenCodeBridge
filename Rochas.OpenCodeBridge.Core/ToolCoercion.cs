using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Coerção: transforma em tool_call de verdade o que o modelo escreveu como
/// TEXTO (tags &lt;tool_call&gt;, &lt;tools&gt;, &lt;function&gt;, fence json, objeto cru
/// e "[Assistant tool call]: nome({...})"). Regra pura do núcleo: não loga, não
/// conhece HTTP — o host decide quando coagir e registra os eventos.
/// </summary>
public static class ToolCoercion
{
    /// <summary>Tags de controle de template que vazam para o texto.</summary>
    public static readonly string[] ControlTags =
        { "<|channel>", "<channel|>", "<|channel|>", "<thought>", "</thought>", "<thinking>", "</thinking>" };

    /// <summary>Marcadores que anunciam tool call no texto.</summary>
    public static readonly string[] TextToolMarkers =
        { "[Assistant tool call]", "<tool_call>", "<tools>", "<function>" };

    /// <summary>Fenced ```json{"name":..}``` (formato pedido por alguns templates).</summary>
    public static readonly Regex ReFencedCall = new(@"```(?:json)?\s*(\{\s*""name""\s*:[\s\S]*?\})\s*```", RegexOptions.Compiled);

    /// <summary>&lt;tool_call&gt;...&lt;/tool_call&gt; (aceita a variante &lt;/tool_call|&gt;).</summary>
    public static readonly Regex ReToolTag = new(@"<tool_call>([\s\S]*?)</tool_call(?:\|>)?", RegexOptions.Compiled);

    /// <summary>Coder/Hermes via llama ecoa o wrapper plural &lt;tools&gt;.</summary>
    public static readonly Regex ReToolsTag = new(@"<tools>([\s\S]*?)</tools>", RegexOptions.Compiled);

    /// <summary>Coder com tool_choice required: &lt;function&gt;{...}&lt;/function&gt;.</summary>
    public static readonly Regex ReFunctionTag = new(@"<function>([\s\S]*?)</function>", RegexOptions.Compiled);

    /// <summary>Nomes das tools anunciadas no request (array de tools).</summary>
    /// <param name="tools">Array do request (chat ou responses).</param>
    /// <returns>Nomes válidos.</returns>
    public static List<string> AnnouncedTools(JsonArray? tools)
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

    /// <summary>Nomes das tools anunciadas a partir do corpo JSON cru.</summary>
    /// <param name="bodyJson">Corpo do request.</param>
    /// <returns>Nomes válidos (lista vazia quando o corpo não parseia).</returns>
    public static List<string> AnnouncedTools(string bodyJson)
    {
        try { return AnnouncedTools(JsonNode.Parse(bodyJson)?.AsObject()?["tools"]?.AsArray()); }
        catch { return new List<string>(); }
    }

    /// <summary>Registra uma call de texto na lista, aplicando apelido e allowlist.</summary>
    /// <param name="json">Envelope {"name","arguments"} em texto.</param>
    /// <param name="list">Lista de calls do turno.</param>
    /// <param name="known">Tools anunciadas (null = sem allowlist).</param>
    public static void TryAddTextCall(string json, List<(string Name, string Arguments)> list, List<string>? known = null)
    {
        try
        {
            var o = TextGrammar.TryParseJson(json.Trim())?.AsObject();
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
            string canon = ResponsesTranslation.CanonToolAlias(name);
            string emit = known is { Count: > 0 }
                ? known.FirstOrDefault(k => ResponsesTranslation.CanonToolAlias(k) == canon) ?? canon
                : canon;
            if (emit.Length == 0) return;
            list.Add((emit, TextGrammar.JsonToString(o["arguments"])));
        }
        catch { /* texto que parece call mas nao parseia: ignora */ }
    }

    /// <summary>Extrai tool calls do texto (tags, fence, ângulo, call narrada).</summary>
    /// <param name="content">Texto do assistente.</param>
    /// <param name="known">Tools anunciadas (null = sem allowlist).</param>
    /// <returns>Calls encontradas, na ordem dos formatos testados.</returns>
    public static List<(string Name, string Arguments)> ExtractTextToolCalls(string content, List<string>? known = null)
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

    /// <summary>Objetos JSON precedidos de '&lt;' (formato cru do 3B).</summary>
    /// <param name="content">Texto.</param>
    /// <param name="bareStartOnly">Só tenta o objeto na posição 0.</param>
    /// <returns>Envelopes encontrados.</returns>
    public static List<string> ExtractAngleCalls(string content, bool bareStartOnly)
    {
        var out_ = new List<string>();
        // Objeto cru: tenta uma vez na posicao 0 (conteudo ja vem trimado).
        if (bareStartOnly)
        {
            if (content.Length > 0 && content[0] == '{' && TextGrammar.TryBalanced(content, 0, out string one) && one.Contains("\"name\""))
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
            if (TextGrammar.TryBalanced(content, j, out string one) && one.Contains("\"name\""))
            { out_.Add(one); i = j + one.Length; }
            else i = j + 1;
        }
        return out_;
    }

    /// <summary>Formato "[Assistant tool call]: nome({...})" no histórico.</summary>
    /// <param name="content">Texto do assistente.</param>
    /// <param name="list">Lista de calls do turno.</param>
    /// <param name="known">Tools anunciadas (null = sem allowlist).</param>
    public static void ExtractAssistantToolCalls(string content, List<(string Name, string Arguments)> list, List<string>? known)
    {
        foreach (Match m in TextGrammar.ReAssistantToolCall.Matches(content))
        {
            int i = m.Index + m.Length;              // logo apos o '(' do nome
            if (i >= content.Length || content[i] != '{') continue;
            if (!TextGrammar.TryBalanced(content, i, out string obj)) continue;
            try
            {
                // TryBalanced devolve o que esta entre as chaves do nome: sao os ARGUMENTOS,
                // nao um envelope {"name","arguments"}. O nome vem do grupo do regex.
                if (!ToolArgsParser.TryParseArgs(obj, out JsonObject argObj)) continue;
                var envelope = new JsonObject { ["name"] = m.Groups[1].Value, ["arguments"] = argObj };
                TryAddTextCall(envelope.ToJsonString(TextGrammar.JsonOpts), list, known);
            }
            catch { /* prosa que nao e JSON: segue como texto */ }
        }
    }

    /// <summary>Converte o texto da resposta chat em tool_calls (se houver).</summary>
    /// <param name="root">Corpo chat/completions (mutado).</param>
    /// <param name="known">Tools anunciadas (null = sem allowlist).</param>
    /// <returns>Quantidade de calls criadas.</returns>
    public static int CoerceChatCalls(JsonObject root, List<string>? known = null)
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

    /// <summary>Remove tags de controle do texto e devolve as removidas (o host loga).</summary>
    /// <param name="s">Texto.</param>
    /// <param name="removed">Tags encontradas (para a sonda do host).</param>
    /// <returns>Texto limpo.</returns>
    public static string StripControlTags(string s, List<string>? removed = null)
    {
        if (string.IsNullOrEmpty(s)) return s;
        foreach (var tag in ControlTags)
        {
            if (s.IndexOf(tag, StringComparison.OrdinalIgnoreCase) < 0) continue;
            s = s.Replace(tag, "", StringComparison.OrdinalIgnoreCase);
            removed?.Add(tag);
        }
        return s;
    }

    /// <summary>Tamanho do maior sufixo de <paramref name="s"/> que é prefixo de <paramref name="tag"/>.</summary>
    /// <param name="s">Texto.</param>
    /// <param name="tag">Tag.</param>
    /// <returns>Tamanho do sufixo.</returns>
    public static int PartialTagPrefixLen(string s, string tag)
    {
        int best = 0;
        int max = Math.Min(tag.Length - 1, s.Length);
        for (int k = max; k > best; k--)
            if (s.EndsWith(tag[..k], StringComparison.OrdinalIgnoreCase)) { best = k; break; }
        return best;
    }

    /// <summary>Posição do primeiro marcador de tool call no texto (-1 = prosa).</summary>
    /// <param name="s">Texto.</param>
    /// <returns>Índice do marcador.</returns>
    public static int TextToolMarkerIndex(string s)
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

    /// <summary>Quantos chars do carry não podem ir para o cliente (tag/marcador parcial).</summary>
    /// <param name="carry">Texto retido no buffer.</param>
    /// <returns>Quantidade a reter.</returns>
    public static int ContentHoldBack(string carry)
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

    /// <summary>Maior sufixo que é prefixo de uma tag de controle (stream).</summary>
    /// <param name="s">Texto retido.</param>
    /// <returns>Tamanho do sufixo.</returns>
    public static int PartialTagLen(string s)
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
}
