using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rochas.AI.Manager.Application.Helpers;

public static class ToolCallCoercer
{
    private static readonly Regex ReAngle = new(@"<\{""name""[^>]*\}>", RegexOptions.Compiled);
    private static readonly Regex ReAssistant = new(@"\[Assistant tool call\]:\s*(\w+)\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static int CoerceChatCalls(JsonObject root, List<string>? known = null)
    {
        try
        {
            var choices = root["choices"]?.AsArray();
            if (choices is null || choices.Count == 0) return 0;
            var msg = choices[0]?["message"]?.AsObject();
            if (msg is null || msg["tool_calls"] is not null) return 0;
            var content = msg["content"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(content)) return 0;

            var calls = ExtractTextToolCalls(content, known);
            if (calls.Count == 0) return 0;

            var arr = new JsonArray();
            foreach (var (name, args) in calls)
            {
                arr.Add(new JsonObject
                {
                    ["index"] = arr.Count,
                    ["id"] = "call_" + Guid.NewGuid().ToString("N")[..8],
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args }
                });
            }
            msg["tool_calls"] = arr;
            if (choices[0] is JsonObject first) first["finish_reason"] = "tool_calls";
            return calls.Count;
        }
        catch { return 0; }
    }

    public static List<(string Name, string Arguments)> ExtractTextToolCalls(string content, List<string>? known = null)
    {
        var list = new List<(string, string)>();
        if (string.IsNullOrEmpty(content)) return list;

        // Formato <{"name":...}> (chaves balanceadas)
        int i = 0;
        while (i < content.Length)
        {
            int lt = content.IndexOf('<', i);
            if (lt < 0 || lt + 1 >= content.Length) break;
            int j = lt + 1;
            if (content[j] != '{') { i = j; continue; }
            if (TryBalanced(content, j, out var one) && one.Contains("\"name\""))
            { TryAdd(list, one, known); i = j + one.Length; }
            else i = j + 1;
        }

        // Formato [Assistant tool call]: nome({...})
        foreach (Match m in ReAssistant.Matches(content))
        {
            int idx = m.Index + m.Length;
            if (idx >= content.Length || content[idx] != '{') continue;
            if (!TryBalanced(content, idx, out var obj)) continue;
            try
            {
                var argObj = JsonNode.Parse(obj)?.AsObject();
                if (argObj is null) continue;
                var env = new JsonObject { ["name"] = m.Groups[1].Value, ["arguments"] = argObj };
                TryAdd(list, env.ToJsonString(), known);
            }
            catch { }
        }

        // Ultimo recurso: objeto cru no inicio
        if (list.Count == 0)
        {
            string t = content.TrimStart();
            if (t.StartsWith("{\"name\"", StringComparison.Ordinal) && t.Contains("\"arguments\""))
            {
                if (TryBalanced(t, 0, out var one)) TryAdd(list, one, known);
            }
        }

        return list;
    }

    private static void TryAdd(List<(string, string)> list, string json, List<string>? known)
    {
        try
        {
            var node = JsonNode.Parse(json)?.AsObject();
            if (node is null) return;
            var name = node["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) return;
            if (known is not null && known.Count > 0 && !known.Contains(name)) return;
            var args = node["arguments"];
            var argsStr = args is null ? "{}" : args.ToJsonString();
            list.Add((name, argsStr));
        }
        catch { }
    }

    private static bool TryBalanced(string s, int start, out string json)
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
}