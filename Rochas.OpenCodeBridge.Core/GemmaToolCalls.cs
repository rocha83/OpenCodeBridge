using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Formato canonico de tool call do Gemma 3/4 (template nativo, "fix july 2026"):
/// <c>&lt;|tool_call&gt;call:NOME{chave:&lt;|"|&gt;valor&lt;|"|&gt;,outra:3}&lt;tool_call|&gt;</c>.
/// O modelo emite isso como token de controle; quando o request nao forca a call
/// (<c>tool_choice</c> ausente) ou o upstream nao parseia, o bloco chega como
/// TEXTO no content. Sem este leitor a bridge devolve o markup cru ao cliente e a
/// ordem vira prosa — medido em 2026-11-07 com o GGUF Q4_K_M-Imatrix.
/// Nucleo puro: nao loga e nao conhece HTTP.
/// </summary>
public static class GemmaToolCalls
{
    /// <summary>Delimitador de string do template gemma (nao e' aspas JSON).</summary>
    public const string StrDelim = "<|\"|>";

    /// <summary>Abertura de uma call: <c>&lt;|tool_call&gt;call:nome{</c>.</summary>
    public static readonly Regex ReCall = new(
        @"<\|tool_call>\s*call:\s*([A-Za-z0-9_.\-]+)\s*\{",
        RegexOptions.Compiled);

    /// <summary>
    /// Extrai TODAS as calls do texto, na ordem em que aparecem.
    /// </summary>
    /// <param name="content">Content do assistente (pode ter prosa em volta).</param>
    /// <returns>Pares nome/argumentos; lista vazia quando nao ha call.</returns>
    public static List<(string Name, JsonObject Args)> Extract(string content)
    {
        var list = new List<(string, JsonObject)>();
        if (string.IsNullOrEmpty(content)) return list;
        foreach (Match m in ReCall.Matches(content))
        {
            int brace = m.Index + m.Length - 1;             // posicao do '{'
            if (!TryParseObject(content, brace, out JsonObject obj, out _)) continue;
            list.Add((m.Groups[1].Value, obj));
        }
        return list;
    }

    /// <summary>Ha' pelo menos uma call canonica no texto?</summary>
    /// <param name="content">Texto.</param>
    /// <returns>True quando <see cref="ReCall"/> casa.</returns>
    public static bool HasCall(string content)
        => !string.IsNullOrEmpty(content) && ReCall.IsMatch(content);

    /// <summary>Remove os blocos de call do texto (para nao vazar markup ao cliente).</summary>
    /// <param name="content">Texto do assistente.</param>
    /// <returns>Texto sem os blocos <c>&lt;|tool_call&gt;...&lt;tool_call|&gt;</c>.</returns>
    public static string Strip(string content)
    {
        if (string.IsNullOrEmpty(content) || content.IndexOf("<|tool_call>", StringComparison.Ordinal) < 0)
            return content;
        var sb = new StringBuilder();
        int i = 0;
        while (true)
        {
            int at = content.IndexOf("<|tool_call>", i, StringComparison.Ordinal);
            if (at < 0) { sb.Append(content, i, content.Length - i); break; }
            sb.Append(content, i, at - i);
            int close = content.IndexOf("<tool_call|>", at, StringComparison.Ordinal);
            i = close < 0 ? content.Length : close + "<tool_call|>".Length;
            if (i >= content.Length) break;
        }
        // Sobra o separador de turno que o template coloca depois da call.
        return sb.ToString().Trim();
    }

    /// <summary>Parseia <c>{...}</c> na sintaxe gemma a partir de <paramref name="start"/>.</summary>
    /// <param name="s">Texto.</param>
    /// <param name="start">Posicao do '{'.</param>
    /// <param name="obj">Objeto lido.</param>
    /// <param name="end">Posicao logo apos o '}'.</param>
    /// <returns>True quando o objeto fecha.</returns>
    public static bool TryParseObject(string s, int start, out JsonObject obj, out int end)
    {
        obj = new JsonObject();
        end = start;
        if (start < 0 || start >= s.Length || s[start] != '{') return false;
        int i = start + 1;
        while (true)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) return false;
            if (s[i] == '}') { end = i + 1; return true; }
            if (s[i] == ',') { i++; continue; }

            string key = ReadKey(s, ref i);
            if (key.Length == 0) return false;
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] != ':') return false;
            i++;                                        // ':'
            SkipWs(s, ref i);
            if (!TryReadValue(s, ref i, out JsonNode? value)) return false;
            obj[key] = value;

            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ',') { i++; continue; }
            if (i < s.Length && s[i] == '}') { end = i + 1; return true; }
            return false;
        }
    }

    /// <summary>Chave: identificador cru ou string delimitada por <c>&lt;|"|&gt;</c>.</summary>
    static string ReadKey(string s, ref int i)
    {
        if (i >= s.Length) return "";
        if (string.CompareOrdinal(s, i, StrDelim, 0, StrDelim.Length) == 0)
        {
            i += StrDelim.Length;
            int close = s.IndexOf(StrDelim, i, StringComparison.Ordinal);
            if (close < 0) { i = s.Length; return ""; }
            string quoted = s[i..close];
            i = close + StrDelim.Length;
            return quoted;
        }
        int start = i;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '-' or '.')) i++;
        return s[start..i];
    }

    /// <summary>Valor: string gemma, objeto, array, numero, booleano ou null.</summary>
    static bool TryReadValue(string s, ref int i, out JsonNode? node)
    {
        node = null;
        SkipWs(s, ref i);
        if (i >= s.Length) return false;

        if (string.CompareOrdinal(s, i, StrDelim, 0, StrDelim.Length) == 0)
        {
            i += StrDelim.Length;
            int close = s.IndexOf(StrDelim, i, StringComparison.Ordinal);
            if (close < 0) { i = s.Length; return false; }
            string text = s[i..close];
            i = close + StrDelim.Length;
            node = JsonValue.Create(text);
            return true;
        }

        if (s[i] == '{')
        {
            if (!TryParseObject(s, i, out JsonObject nested, out int end)) return false;
            i = end;
            node = nested;
            return true;
        }

        if (s[i] == '[')
        {
            i++;
            var arr = new JsonArray();
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length) return false;
                if (s[i] == ']') { i++; node = arr; return true; }
                if (s[i] == ',') { i++; continue; }
                if (!TryReadValue(s, ref i, out JsonNode? item)) return false;
                arr.Add(item);
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; node = arr; return true; }
                return false;
            }
        }

        if (s[i] == '"')
        {
            int start = i;
            i++;
            while (i < s.Length && s[i] != '"')
            {
                if (s[i] == '\\') i++;
                i++;
            }
            if (i >= s.Length) return false;
            i++;
            try { node = JsonNode.Parse(s[start..i]); }
            catch { node = JsonValue.Create(s[(start + 1)..(i - 1)]); }
            return true;
        }

        // Cru ate' a virgula/fechamento no nivel atual.
        int from = i;
        while (i < s.Length && s[i] is not (',' or '}' or ']')) i++;
        string token = s[from..i].Trim();
        if (token.Length == 0) return false;
        if (token is "true") { node = JsonValue.Create(true); return true; }
        if (token is "false") { node = JsonValue.Create(false); return true; }
        if (token is "null") { node = null; return true; }   // JSON null legitimo
        if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
        { node = JsonValue.Create(l); return true; }
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
        { node = JsonValue.Create(d); return true; }
        node = JsonValue.Create(token);
        return true;
    }

    /// <summary>Pula espacos e virgulas soltas entre campos.</summary>
    static void SkipWs(string s, ref int i)
    {
        while (i < s.Length && (char.IsWhiteSpace(s[i]) || s[i] == '\n' || s[i] == '\r')) i++;
    }
}
