using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Leitura dos argumentos de tool call do jeito que o modelo escreve: JSON
/// valido, JSON com escape inventado ou pseudo-JS (chave sem aspas, virgula
/// sobrando, aspas internas soltas). Nucleo puro: nenhum cliente reimplementa
/// o parser — console, Web e testes chamam estes metodos.
/// </summary>
public static class ToolArgsParser
{
    /// <summary>Indice do '}' que fecha o objeto iniciado em start (ignora strings).</summary>
    /// <param name="s">Texto JS.</param>
    /// <param name="start">Posicao do '{'.</param>
    /// <returns>Indice do fechamento ou -1.</returns>
    public static int FindObjectEnd(string s, int start)
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
    public static string StripLeadingJsComments(string code)
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
    public static bool TryParseJsValue(string text, out JsonNode? node)
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

    /// <summary>Pula espacos (e quebras) do literal JS.</summary>
    public static void SkipJsSpace(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
    }

    /// <summary>Valor JS: objeto, array, string (' " `), booleano, null ou numero.</summary>
    public static JsonNode? ParseJsValue(string s, ref int i)
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
    public static JsonObject ParseJsObjectBody(string s, ref int i)
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
    public static JsonArray ParseJsArrayBody(string s, ref int i)
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
    public static string ParseJsString(string s, ref int i)
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
    public static double ParseJsNumber(string s, ref int i)
    {
        int start = i;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] is '-' or '+' or '.' or 'e' or 'E')) i++;
        if (i == start) throw new FormatException("numero JS invalido");
        return double.Parse(s[start..i], NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>Identificador JS (chave sem aspas).</summary>
    public static string ReadJsIdentifier(string s, ref int i)
    {
        int start = i;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '$')) i++;
        if (i == start) throw new FormatException("chave JS invalida");
        return s[start..i];
    }

    /// <summary>Fecha chaves de fechamento sobrando no fim do comando (lixo do template).</summary>
    /// <param name="text">Texto do comando.</param>
    /// <returns>Comando com chaves balanceadas.</returns>
    public static string TrimUnbalanced(string text)
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

    /// <summary>JSON valido? (objeto, tolerante a espacos).</summary>
    /// <param name="text">Texto dos argumentos.</param>
    /// <returns>True quando JsonNode.Parse aceita.</returns>
    public static bool IsJsonObject(string text)
    {
        try { return JsonNode.Parse(text) is JsonObject; } catch { return false; }
    }

    /// <summary>Chaves obrigatorias ausentes nos argumentos (vazio p/ tool fora do mapa).</summary>
    /// <param name="name">Nome da tool.</param>
    /// <param name="arguments">Argumentos JSON.</param>
    /// <returns>Lista das chaves que faltam.</returns>
    public static List<string> MissingArgs(string name, string arguments)
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

    /// <summary>Chaves obrigatorias por tool (validadas antes de emitir a chamada).</summary>
    public static readonly Dictionary<string, string[]> RequiredArgs = new(StringComparer.Ordinal)
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
}
