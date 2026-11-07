using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Web.Services;

/// <summary>
/// Formatos de tool call em TEXTO da família DeepSeek (Coder V2-Lite / V3):
/// <list type="number">
/// <item>fence <c>```json {"name":…,"arguments":…}```</c> (e a variante de chave única
///   <c>{"&lt;tool&gt;": {…}}</c>);</item>
/// <item>tokens V3 <c><｜tool▁calls▁begin｜>…function<｜tool▁sep｜>nome```json …```…<｜tool▁calls▁end｜></c>;</item>
/// <item>XML oficial <c>&lt;tool_name&gt;&lt;param&gt;valor&lt;/param&gt;&lt;/tool_name&gt;</c>
///   (só para nomes anunciados);</item>
/// <item><c>&lt;attempt_completion&gt;</c> — turno final, desembrulha e encerra sem call.</item>
/// </list>
/// A validação (allowlist/catálogo/nome canônico) fica no chamador.
/// </summary>
public static class DeepSeekToolCalls
{
    /// <summary>Turno final: <c>&lt;attempt_completion&gt;…&lt;/attempt_completion&gt;</c>.</summary>
    public static readonly Regex ReAttemptCompletion = new(
        @"<attempt_completion>\s*(?:<result>(.*?)</result>|(.*?))\s*</attempt_completion>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>Fence JSON (com ou sem a tag <c>json</c>).</summary>
    public static readonly Regex ReFence = new(
        "```(?:json)?[^\\n\\r]*[\\r\\n]+(.*?)```",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>Bloco externo dos tokens V3.</summary>
    public static readonly Regex ReV3Block = new(
        "<｜tool▁calls▁begin｜>(.*?)<｜tool▁calls▁end｜>",
        RegexOptions.Singleline);

    /// <summary>Call interna dos tokens V3: function + nome + fence de args.</summary>
    public static readonly Regex ReV3Call = new(
        "<｜tool▁call▁begin｜>function<｜tool▁sep｜>(?<name>[^\\n\\r]+)[\\r\\n]+```(?:json)?[\\r\\n]+(?<args>.*?)```[\\r\\n]*<｜tool▁call▁end｜>",
        RegexOptions.Singleline);

    /// <summary>XML oficial: <c>&lt;tool_name&gt;…&lt;/tool_name&gt;</c>.</summary>
    public static readonly Regex ReXml = new(
        @"<(?<tool>[A-Za-z_][A-Za-z0-9_\-\.]{0,63})>(?<body>.*?)</\k<tool>>",
        RegexOptions.Singleline);

    static readonly HashSet<string> XmlSkip = new(StringComparer.OrdinalIgnoreCase)
        { "tool_call", "think", "thinking", "attempt_completion", "tool_response", "subthink" };

    static readonly JsonSerializerOptions Relaxed =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Desembrulha <c>&lt;attempt_completion&gt;</c>: o conteúdo vira texto final e
    /// nenhuma call é produzida.
    /// </summary>
    /// <param name="content">Content do assistente.</param>
    /// <param name="cleaned">Texto final sem o wrapper.</param>
    /// <returns>True quando havia o wrapper.</returns>
    public static bool TryUnwrapAttemptCompletion(string content, out string cleaned)
    {
        cleaned = content;
        if (string.IsNullOrEmpty(content)) return false;
        var att = ReAttemptCompletion.Match(content);
        if (!att.Success) return false;
        string result = att.Groups[1].Success ? att.Groups[1].Value : att.Groups[2].Value;
        cleaned = Regex.Replace(content.Replace(att.Value, "\n" + result + "\n"), @"\n{3,}", "\n\n").Trim();
        return true;
    }

    /// <summary>
    /// Varre fence → tokens V3 → XML oficial e devolve o texto sem os blocos
    /// que o chamador aceitou.
    /// </summary>
    /// <param name="text">Content do assistente.</param>
    /// <param name="tryEnvelope">Recebe envelope JSON; true = virou call.</param>
    /// <param name="addParsed">Adiciona call já parseada (nome + args JSON).</param>
    /// <param name="hasRoom">False quando o teto de calls do turno estourou.</param>
    /// <param name="known">Tools anunciadas (o XML só aceita estas).</param>
    /// <returns>Texto sem os blocos consumidos.</returns>
    public static string Extract(string text, Func<string, bool> tryEnvelope,
        Action<string, string> addParsed, Func<bool> hasRoom, IReadOnlyCollection<string>? known)
    {
        text = Fences(text, tryEnvelope, addParsed, hasRoom, known);
        text = V3Tokens(text, tryEnvelope, hasRoom);
        text = Xml(text, addParsed, hasRoom, known);
        return text;
    }

    static string Fences(string text, Func<string, bool> tryEnvelope, Action<string, string> addParsed,
        Func<bool> hasRoom, IReadOnlyCollection<string>? known)
    {
        foreach (Match m in ReFence.Matches(text))
        {
            if (!hasRoom()) break;
            string body = m.Groups[1].Value.Trim();
            if (!body.StartsWith("{")) continue;
            if (tryEnvelope(body)) { text = text.Replace(m.Value, "\n"); continue; }
            // Variante sem "name": {"<tool>": {...args...}} (chave única = tool).
            if (known is null || known.Count == 0) continue;
            JsonObject? obj;
            try { obj = JsonNode.Parse(body)?.AsObject(); }
            catch { continue; }
            if (obj is null || obj.Count != 1) continue;
            string key = obj.First().Key;
            string canonical = known.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? "";
            if (canonical.Length == 0 || obj[key] is not JsonObject argsObj) continue;
            addParsed(canonical, argsObj.ToJsonString(Relaxed));
            text = text.Replace(m.Value, "\n");
        }
        return text;
    }

    static string V3Tokens(string text, Func<string, bool> tryEnvelope, Func<bool> hasRoom)
    {
        foreach (Match outer in ReV3Block.Matches(text))
        {
            if (!hasRoom()) break;
            bool any = false;
            foreach (Match inner in ReV3Call.Matches(outer.Groups[1].Value))
            {
                if (!hasRoom()) break;
                string name = inner.Groups["name"].Value.Trim();
                string args = inner.Groups["args"].Value.Trim();
                string envelope = $"{{\"name\": {JsonQuote(name)}, \"arguments\": {(StartsJson(args) ? args : JsonQuote(args))}}}";
                if (tryEnvelope(envelope)) any = true;
            }
            if (any) text = text.Replace(outer.Value, "\n");
        }
        return text;
    }

    static string Xml(string text, Action<string, string> addParsed, Func<bool> hasRoom, IReadOnlyCollection<string>? known)
    {
        if (known is null || known.Count == 0) return text;
        foreach (Match m in ReXml.Matches(text))
        {
            if (!hasRoom()) break;
            string tag = m.Groups["tool"].Value;
            if (XmlSkip.Contains(tag)) continue;
            string canonical = known.FirstOrDefault(k => k.Equals(tag, StringComparison.OrdinalIgnoreCase)) ?? "";
            if (canonical.Length == 0) continue;
            var args = new JsonObject();
            foreach (Match p in Regex.Matches(m.Groups["body"].Value,
                         @"<(?<p>[A-Za-z_][A-Za-z0-9_\-\.]{0,63})>(?<v>.*?)</\k<p>>", RegexOptions.Singleline))
                args[p.Groups["p"].Value] = XmlUnescape(p.Groups["v"].Value.Trim());
            addParsed(canonical, args.ToJsonString(Relaxed));
            text = text.Replace(m.Value, "\n");
        }
        return text;
    }

    static string XmlUnescape(string s) => s
        .Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
        .Replace("&apos;", "'").Replace("&amp;", "&");

    static bool StartsJson(string s)
    {
        s = s.TrimStart();
        return s.StartsWith("{") || s.StartsWith("[");
    }

    static string JsonQuote(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
