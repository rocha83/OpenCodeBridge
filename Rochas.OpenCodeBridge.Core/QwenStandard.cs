using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Core;

// Modo QwenStandard (--qwen-standard): harness mínimo no estilo ACP/Alibaba.
// ModelPrompt sintetizado + system do agente, sem o guardrail legado
// (o texto anti-repeat vive só aqui, uma vez). Nomes de tools traduzidos
// OpenAI ⇄ Alibaba nos dois sentidos; desconhecidas passam intactas.
public static class QwenStandard
{
    // Síntese do system Qwen Code (38k → ~2,5k): identidade, workflow,
    // uso de tools e 1 exemplo. Sem segurança, memória ou delegação.
    public const string ModelPrompt =
        "You are Qwen Code, a CLI agent through an ACP host, specialized in software engineering. " +
        "Help safely and efficiently with tools; communicate briefly.\n" +
        "MANDATES: never assume file contents — read code, imports, tests and config first; " +
        "follow project conventions; verify dependencies before using; no comments unless the why needs it; " +
        "fulfill thoroughly and add tests for code changes; do not act beyond the clear scope without asking; " +
        "never revert code unless asked or yours broke; denied tool call = report, do other safe work.\n" +
        "WORKFLOW: plan briefly when complex; implement only what was asked (no extra features, no premature abstraction); " +
        "on failure diagnose before retrying; verify with the project's own test/build/lint commands; " +
        "report failures faithfully, never claim green without evidence.\n" +
        "TOOLS: prefer dedicated tools — read_file (not cat), edit (not sed), write_file (not heredoc), " +
        "glob (not find), grep_search (not grep); run_shell_command only for real shell work; " +
        "absolute paths always; independent calls in parallel, dependent ones sequentially; " +
        "one tool call per step, wait for the result; on tool error read the message and vary approach (max 2 tries), " +
        "never repeat the identical failing call; respond in the user's language, concise.\n" +
        "Example: <tool_call>{\"name\": \"read_file\", \"arguments\": {\"file_path\": \"/path/to/file\"}}</tool_call>\n" +
        "TOOL FORMAT (mandatory): to call tools, output ONLY <tool_call>{\"name\": \"<exact-name>\", \"arguments\": {<args>}}</tool_call> " +
        "with the exact names from # Tools above; one call per step, no prose inside the block.";

    // OpenAI (opencode/bridge) -> Alibaba (modelo).
    static readonly Dictionary<string, string> ToAlibaba = new(StringComparer.OrdinalIgnoreCase)
    {
        ["read"] = "read_file",
        ["write"] = "write_file",
        ["edit"] = "edit",
        ["glob"] = "glob",
        ["grep"] = "grep_search",
        ["shell"] = "run_shell_command",
        ["skill"] = "skill",
        ["question"] = "ask_user_question",
        ["subagent"] = "create_sub_session",
    };

    static readonly Dictionary<string, string> ToOpenAI = new(StringComparer.OrdinalIgnoreCase)
    {
        ["read_file"] = "read",
        ["write_file"] = "write",
        ["edit"] = "edit",
        ["glob"] = "glob",
        ["grep_search"] = "grep",
        ["run_shell_command"] = "shell",
        ["skill"] = "skill",
        ["ask_user_question"] = "question",
        ["create_sub_session"] = "subagent",
        ["artifact"] = "display",
    };

    // Args OpenAI -> Alibaba.
    static readonly Dictionary<string, string> ArgsToAlibaba = new(StringComparer.OrdinalIgnoreCase)
    {
        ["path"] = "file_path",
        ["filePath"] = "file_path",
        ["file_path"] = "file_path",
        ["oldString"] = "old_string",
        ["newString"] = "new_string",
        ["command"] = "command",
        ["pattern"] = "pattern",
        ["include"] = "path",
        ["content"] = "content",
    };

    static readonly Dictionary<string, string> ArgsToOpenAI = new(StringComparer.OrdinalIgnoreCase)
    {
        ["file_path"] = "path",
        ["old_string"] = "oldString",
        ["new_string"] = "newString",
        ["command"] = "command",
        ["pattern"] = "pattern",
    };

    public static string ComposeSystem(string agentSystem)
    {
        string a = (agentSystem ?? "").Trim();
        return a.Length == 0 ? ModelPrompt : ModelPrompt + "\n\n" + a;
    }

    static JsonObject RenameArgs(JsonObject args, Dictionary<string, string> map)
    {
        var out_ = new JsonObject();
        foreach (var kv in args)
        {
            string key = kv.Key;
            if (map.TryGetValue(key, out string? nk)) key = nk;
            out_[key] = kv.Value?.DeepClone();
        }
        return out_;
    }

    // Traduz definitions de tools p/ o upstream (retorna quantas traduziu).
    public static int TranslateToolsForUpstream(JsonArray tools)
    {
        int n = 0;
        foreach (var t in tools)
        {
            if (t is not JsonObject o) continue;
            var fn = o["function"] as JsonObject;
            if (fn is null) continue;
            string name = fn["name"]?.GetValue<string>() ?? "";
            if (ToAlibaba.TryGetValue(name, out string? alibaba))
            {
                fn["name"] = alibaba;
                if (fn["parameters"] is JsonObject prm && prm["properties"] is JsonObject props)
                    fn["parameters"]!["properties"] = RenameArgs(props, ArgsToAlibaba);
                n++;
            }
        }
        return n;
    }

    // Traduz tool_calls da resposta p/ o cliente (retorna quantas traduziu).
    public static int TranslateToolCallsForClient(JsonArray toolCalls)
    {
        int n = 0;
        foreach (var tc in toolCalls)
        {
            if (tc is not JsonObject o) continue;
            var fn = o["function"] as JsonObject;
            if (fn is null) continue;
            string name = fn["name"]?.GetValue<string>() ?? "";
            if (ToOpenAI.TryGetValue(name, out string? openai))
            {
                fn["name"] = openai;
                if (fn["arguments"] is JsonObject args)
                    fn["arguments"] = RenameArgs(args, ArgsToOpenAI);
                else if (fn["arguments"] is JsonValue v && v.TryGetValue<string>(out string? s))
                {
                    try
                    {
                        var jo = System.Text.Json.Nodes.JsonNode.Parse(s) as JsonObject;
                        if (jo is not null) fn["arguments"] = RenameArgs(jo, ArgsToOpenAI).ToJsonString();
                    }
                    catch { /* mantém original */ }
                }
                n++;
            }
        }
        return n;
    }

    // Completion chat inteira (non-stream): traduz tool_calls de todas choices.
    public static int TranslateCompletionForClient(JsonObject root)
    {
        int n = 0;
        if (root?["choices"] is JsonArray choices)
            foreach (var c in choices)
                if (c?["message"]?["tool_calls"] is JsonArray tcs)
                    n += TranslateToolCallsForClient(tcs);
        return n;
    }

    // Chunks SSE: substituições exatas nome/chave (chegam inteiros no chunk).
    static readonly (string From, string To)[] ChunkPairs =
    [
        ("\"name\":\"read_file\"", "\"name\":\"read\""),
        ("\"name\":\"write_file\"", "\"name\":\"write\""),
        ("\"name\":\"grep_search\"", "\"name\":\"grep\""),
        ("\"name\":\"run_shell_command\"", "\"name\":\"shell\""),
        ("\"name\":\"ask_user_question\"", "\"name\":\"question\""),
        ("\"name\":\"create_sub_session\"", "\"name\":\"subagent\""),
        ("\"name\": \"read_file\"", "\"name\": \"read\""),
        ("\"name\": \"write_file\"", "\"name\": \"write\""),
        ("\"name\": \"grep_search\"", "\"name\": \"grep\""),
        ("\"name\": \"run_shell_command\"", "\"name\": \"shell\""),
        ("\"file_path\":", "\"path\":"),
        ("\"file_path\" :", "\"path\":"),
        ("\"old_string\":", "\"oldString\":"),
        ("\"new_string\":", "\"newString\":"),
    ];

    public static string TranslateChunkNames(string line)
    {
        foreach (var (from, to) in ChunkPairs)
            if (line.Contains(from, StringComparison.Ordinal)) line = line.Replace(from, to, StringComparison.Ordinal);
        return line;
    }
}
