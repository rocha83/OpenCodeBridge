using System.Text;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Leitura do corpo que vem do upstream: texto do chat/responses e se o
/// cliente pediu stream. Gramatica do protocolo, no nucleo.
/// </summary>
public static class ResponseBody
{
    /// <summary>Extrai texto util de um chat/completions (content + reasoning) p/ a sonda do bypass.</summary>
    public static string ExtractChatText(string json)
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

    /// <summary>Extrai todo texto de um objeto Responses (output) p/ a sonda do bypass.</summary>
    public static string ExtractResponsesText(string json)
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
    public static bool BodyWantsStream(string body)
    {
        try { return JsonNode.Parse(body)?["stream"]?.GetValue<bool>() ?? false; }
        catch { return false; }
    }

    /// <summary>Coleta valores de chaves de texto ("text"/"delta"/"content") em qualquer JSON (SSE do bypass).</summary>
    public static void WalkText(JsonNode? node, StringBuilder b)
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
}
