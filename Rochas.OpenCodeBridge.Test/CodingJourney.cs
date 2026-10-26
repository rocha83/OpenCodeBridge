using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>
/// Le uma jornada de codificacao (export completo de sessao opencode ou o
/// diario normalizado do caminho bridge) e devolve histogramas/metrias de
/// comportamento. A sonda usa o mesmo contrato nos dois modos para comparar
/// o modelo local com o opencode e medir adaptatividade sem olhar "achismo".
/// </summary>
internal static class CodingJourney
{
    /// <summary>Analisa o JSON de `opencode session export` (stream completo).</summary>
    public static JsonObject AnalyzeOpencode(string path)
    {
        var root = Parse(path);
        var info = root["info"] as JsonObject ?? new JsonObject();
        var messages = root["messages"] as JsonArray ?? new JsonArray();
        var acc = new Accumulator();
        string prompt = "";
        int systemMessages = 0;
        int compactions = 0;

        foreach (JsonNode? node in messages)
        {
            if (node is not JsonObject message) continue;
            string type = Str(message, "type");
            if (type == "user") prompt = Str(message, "text");
            else if (type == "system") systemMessages++;
            else if (type == "compaction") compactions++;
            if (type != "assistant") continue;

            int tools = 0;
            int textChars = 0;
            int reasonChars = 0;
            foreach (JsonNode? contentNode in message["content"] as JsonArray ?? new JsonArray())
            {
                if (contentNode is not JsonObject content) continue;
                string contentType = Str(content, "type");
                if (contentType == "reasoning")
                {
                    reasonChars += Str(content, "text").Length;
                }
                else if (contentType == "text")
                {
                    textChars += Str(content, "text").Length;
                }
                else if (contentType == "tool")
                {
                    tools++;
                    string name = Str(content, "name");
                    var state = content["state"] as JsonObject;
                    string status = state is null ? "" : Str(state, "status");
                    bool ok = status is "completed" or "success" or "ok";
                    var input = state?["input"] as JsonObject;
                    string file = input is null ? "" : FirstStr(input, "path", "filePath", "file_path");
                    string command = input is null ? "" : FirstStr(input, "command", "cmd");
                    acc.Tool(name, ok, status, file, command, input?.ToJsonString() ?? "{}");
                }
            }
            acc.Turn(tools, textChars, reasonChars);
        }

        return acc.Finish(
            mode: "opencode",
            model: info["model"]?.DeepClone(),
            agent: Str(info, "agent"),
            session: Str(info, "id"),
            prompt: prompt,
            promptChars: prompt.Length,
            tokens: info["tokens"]?.DeepClone(),
            systemMessages: systemMessages,
            compactions: compactions);
    }

    /// <summary>Analisa o diario normalizado gravado pelo ToolAgent no modo bridge.</summary>
    public static JsonObject AnalyzeBridge(string path)
    {
        var root = Parse(path);
        var acc = new Accumulator();
        var calls = root["calls"] as JsonArray ?? new JsonArray();
        var rounds = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = 0; i < calls.Count; i++)
        {
            if (calls[i] is not JsonObject call) continue;
            string name = Str(call, "name");
            var input = call["args"] as JsonObject;
            if (input is null && Str(call, "args") is { Length: > 0 } raw)
            {
                try { input = JsonNode.Parse(raw) as JsonObject; }
                catch { /* argumento nao-JSON: a jornada ainda conta a chamada */ }
            }
            string file = input is null ? "" : FirstStr(input, "path", "filePath", "file_path");
            string command = input is null ? "" : FirstStr(input, "command", "cmd");
            bool ok = call["ok"]?.GetValue<bool>() ?? false;
            string status = ok ? "completed" : "error";
            acc.Tool(name, ok, status, file, command, input?.ToJsonString() ?? "{}");

            string round = call["round"]?.ToJsonString().Trim('"') ?? "";
            if (round.Length == 0) round = i.ToString(CultureInfo.InvariantCulture);
            rounds[round] = rounds.TryGetValue(round, out int n) ? n + 1 : 1;
        }

        foreach (int tools in rounds.Values) acc.Turn(tools, 0, 0);

        string text = Str(root, "text");
        string reasoning = Str(root, "reasoning");
        acc.AddText(text.Length);
        acc.AddReasoning(reasoning.Length);

        long elapsed = callLong(root, "elapsed_ms");
        return acc.Finish(
            mode: "bridge",
            model: root["model"]?.DeepClone(),
            agent: Str(root, "agent"),
            session: Str(root, "session"),
            prompt: Str(root, "prompt"),
            promptChars: Str(root, "prompt").Length,
            tokens: root["tokens"]?.DeepClone(),
            systemMessages: 0,
            compactions: 0,
            elapsedMs: elapsed);
    }

    /// <summary>Extrai calls legiveis do export opencode para o log do teste.</summary>
    public static List<string> CallsFromOpencode(string path)
    {
        var list = new List<string>();
        var root = Parse(path);
        foreach (JsonNode? node in root["messages"] as JsonArray ?? new JsonArray())
        {
            if (node is not JsonObject message || Str(message, "type") != "assistant") continue;
            foreach (JsonNode? contentNode in message["content"] as JsonArray ?? new JsonArray())
            {
                if (contentNode is not JsonObject content || Str(content, "type") != "tool") continue;
                string name = Str(content, "name");
                if (name.Length == 0) continue;
                var state = content["state"] as JsonObject;
                string status = state is null ? "" : Str(state, "status");
                bool ok = status is "completed" or "success" or "ok";
                string args = (state?["input"] as JsonObject)?.ToJsonString() ?? "{}";
                if (args.Length > 240) args = args[..240] + "...";
                list.Add($"{name}({args}) -> {(ok ? "ok" : "fail")}");
            }
        }
        return list;
    }

    /// <summary>Compara os dois histogramas no mesmo step (quando --dual roda ambos).</summary>
    public static JsonObject Contrast(JsonObject opencode, JsonObject bridge)
    {
        return new JsonObject
        {
            ["tools_opencode"] = opencode["tools_total"]?.DeepClone(),
            ["tools_bridge"] = bridge["tools_total"]?.DeepClone(),
            ["tool_delta"] = Delta(
                opencode["tool_histogram"] as JsonObject,
                bridge["tool_histogram"] as JsonObject),
            ["metrics"] = new JsonObject
            {
                ["opencode"] = opencode["metrics"]?.DeepClone(),
                ["bridge"] = bridge["metrics"]?.DeepClone()
            }
        };
    }

    private static JsonObject Parse(string path)
    {
        try { return JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new JsonObject(); }
        catch { return new JsonObject(); }
    }

    private static JsonObject Delta(JsonObject? a, JsonObject? b)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kv in a ?? new JsonObject()) keys.Add(kv.Key);
        foreach (var kv in b ?? new JsonObject()) keys.Add(kv.Key);
        var delta = new JsonObject();
        foreach (string key in keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            int va = a?[key]?.GetValue<int>() ?? 0;
            int vb = b?[key]?.GetValue<int>() ?? 0;
            delta[key] = vb - va;
        }
        return delta;
    }

    private sealed class Accumulator
    {
        private readonly Dictionary<string, int> _tools = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _status = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _sequences = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _turnTools = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _textBuckets = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _reasonBuckets = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _files = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _validations = new(StringComparer.Ordinal);
        private readonly List<string> _sequenceList = new();
        private readonly JsonArray _errors = new();
        private int _turns;
        private int _withTools;
        private int _totalTools;
        private int _failures;
        private int _edits;
        private int _writes;
        private int _validationsCount;
        private int _textChars;
        private int _reasonChars;

        public void Tool(string name, bool ok, string status, string file, string command, string rawArgs)
        {
            if (name.Length == 0) return;
            Bump(_tools, name);
            Bump(_status, ok ? "ok" : "fail");
            _totalTools++;
            if (!ok) _failures++;
            if (name == "edit") _edits++;
            else if (name == "write") _writes++;
            if (file.Length > 0) Bump(_files, file);
            if (command.Length > 0)
            {
                string kind = CommandKind(command);
                if (kind.Length > 0)
                {
                    Bump(_validations, kind);
                    _validationsCount++;
                }
            }
            if (!ok && _errors.Count < 20)
            {
                _errors.Add(new JsonObject
                {
                    ["tool"] = name,
                    ["status"] = status,
                    ["file"] = file,
                    ["command"] = Trim(command, 240),
                    ["args"] = Trim(rawArgs, 240)
                });
            }
            _sequenceList.Add(name);
        }

        public void Turn(int tools, int textChars, int reasonChars)
        {
            _turns++;
            if (tools > 0) _withTools++;
            Bump(_turnTools, TurnBucket(tools));
            Bump(_textBuckets, CharBucket(textChars));
            Bump(_reasonBuckets, CharBucket(reasonChars));
            _textChars += textChars;
            _reasonChars += reasonChars;
        }

        public void AddText(int chars)
        {
            if (chars <= 0) return;
            _textChars += chars;
            Bump(_textBuckets, CharBucket(chars));
        }

        public void AddReasoning(int chars)
        {
            if (chars <= 0) return;
            _reasonChars += chars;
            Bump(_reasonBuckets, CharBucket(chars));
        }

        public JsonObject Finish(
            string mode, JsonNode? model, string agent, string session, string prompt,
            int promptChars, JsonNode? tokens, int systemMessages, int compactions,
            long elapsedMs = 0)
        {
            for (int i = 0; i + 1 < _sequenceList.Count; i++)
                Bump(_sequences, _sequenceList[i] + ">" + _sequenceList[i + 1]);

            var hist = new JsonObject
            {
                ["mode"] = mode,
                ["model"] = model,
                ["agent"] = agent,
                ["session"] = session,
                ["prompt"] = Trim(prompt, 400),
                ["prompt_chars"] = promptChars,
                ["tokens"] = tokens,
                ["elapsed_ms"] = elapsedMs,
                ["system_messages"] = systemMessages,
                ["compactions"] = compactions,
                ["turns"] = _turns,
                ["turns_with_tools"] = _withTools,
                ["tools_total"] = _totalTools,
                ["tool_histogram"] = ToJson(_tools),
                ["status_histogram"] = ToJson(_status),
                ["sequence_histogram"] = ToJson(_sequences),
                ["turn_tools_histogram"] = ToJson(_turnTools),
                ["text_chars_histogram"] = ToJson(_textBuckets),
                ["reasoning_chars_histogram"] = ToJson(_reasonBuckets),
                ["file_histogram"] = ToJson(_files),
                ["validation_histogram"] = ToJson(_validations),
                ["errors"] = _errors.DeepClone(),
                ["metrics"] = new JsonObject
                {
                    ["avg_tools_per_turn"] = _turns > 0 ? Math.Round((double)_totalTools / _turns, 2) : 0,
                    ["tool_diversity"] = _tools.Count,
                    ["edit_ratio"] = _edits + _writes > 0 ? Math.Round((double)_edits / (_edits + _writes), 3) : 0,
                    ["validation_per_edit"] = _edits > 0 ? Math.Round((double)_validationsCount / _edits, 2) : 0,
                    ["failure_rate"] = _totalTools > 0 ? Math.Round((double)_failures / _totalTools, 3) : 0,
                    ["reasoning_ratio"] = _textChars > 0 ? Math.Round((double)_reasonChars / _textChars, 2) : 0
                }
            };
            return hist;
        }
    }

    private static void Bump(Dictionary<string, int> map, string key)
        => map[key] = map.TryGetValue(key, out int n) ? n + 1 : 1;

    private static JsonObject ToJson(Dictionary<string, int> map)
    {
        var obj = new JsonObject();
        foreach (var kv in map.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
            obj[kv.Key] = kv.Value;
        return obj;
    }

    private static string CommandKind(string command)
    {
        if (Regex.IsMatch(command, @"\bbash\s+-n\b", RegexOptions.IgnoreCase)) return "bash-n";
        if (Regex.IsMatch(command, @"\bdotnet\s+build\b", RegexOptions.IgnoreCase)) return "dotnet-build";
        if (Regex.IsMatch(command, @"\bdotnet\s+test\b", RegexOptions.IgnoreCase)) return "dotnet-test";
        if (Regex.IsMatch(command, @"\bgit\s+diff\b", RegexOptions.IgnoreCase)) return "git-diff";
        if (Regex.IsMatch(command, @"\bgit\s+status\b", RegexOptions.IgnoreCase)) return "git-status";
        if (Regex.IsMatch(command, @"\bgit\s+log\b", RegexOptions.IgnoreCase)) return "git-log";
        if (Regex.IsMatch(command, @"\bgit\s+commit\b", RegexOptions.IgnoreCase)) return "git-commit";
        if (Regex.IsMatch(command, @"\bgit\s+add\b", RegexOptions.IgnoreCase)) return "git-add";
        if (Regex.IsMatch(command, @"\bgit\s+init\b", RegexOptions.IgnoreCase)) return "git-init";
        if (Regex.IsMatch(command, @"\b(npm|pnpm|yarn)\s+test\b", RegexOptions.IgnoreCase)) return "npm-test";
        if (Regex.IsMatch(command, @"\bpytest\b", RegexOptions.IgnoreCase)) return "pytest";
        if (Regex.IsMatch(command, @"\btsc\b", RegexOptions.IgnoreCase)) return "tsc";
        if (Regex.IsMatch(command, @"\beslint\b", RegexOptions.IgnoreCase)) return "eslint";
        return "";
    }

    private static string CharBucket(int chars) => chars switch
    {
        <= 0 => "0",
        <= 200 => "1-200",
        <= 1000 => "201-1k",
        <= 4000 => "1k-4k",
        <= 12000 => "4k-12k",
        _ => "12k+"
    };

    private static string TurnBucket(int tools) => tools switch
    {
        <= 0 => "0",
        1 => "1",
        2 => "2",
        3 => "3",
        _ => "4+"
    };

    private static string FirstStr(JsonObject obj, params string[] names)
    {
        foreach (string name in names)
            if (Str(obj, name) is { Length: > 0 } value) return value;
        return "";
    }

    private static string Str(JsonObject obj, string name)
    {
        try { return obj[name]?.GetValue<string>() ?? ""; }
        catch { return ""; }
    }

    private static long callLong(JsonObject obj, string name)
    {
        try { return obj[name]?.GetValue<long>() ?? 0; }
        catch { return 0; }
    }

    private static string Trim(string value, int max)
        => value.Length <= max ? value : value[..max] + "...";
}
