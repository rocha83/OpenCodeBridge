using System.Text;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge;

/// <summary>
/// Shim generico de tool calling para upstreams que recusam grammar customizada
/// junto com `tools` (llama.cpp: "Cannot use custom grammar constraints with tools").
///
/// Remove o array `tools` do request, injeta a declaracao no system e forca um
/// envelope JSON via grammar. A resposta e convertida de volta para o formato
/// OpenAI `tool_calls`. Nao conhece cenario: usa os nomes e schemas do proprio
/// request, entao serve para write/read/grep/shell/subagent e qualquer outro.
/// </summary>
public static class ToolShim
{
    /// <summary>
    /// Aplica o shim no body de /v1/chat/completions (mutacao no lugar).
    /// Requer tools com nome, stream=false e ao menos um tool.
    /// </summary>
    public static bool Apply(JsonObject body, int maxString, int minCalls, out string? reason)
    {
        reason = null;
        if (body["tools"] is not JsonArray tools || tools.Count == 0)
        {
            reason = "sem tools";
            return false;
        }
        if (body["stream"]?.GetValue<bool>() == true)
        {
            reason = "stream=true nao suportado pelo shim";
            return false;
        }

        var names = new List<string>();
        foreach (var t in tools)
        {
            if (t is JsonObject o
                && o["function"]?["name"]?.GetValue<string>() is string n
                && !string.IsNullOrWhiteSpace(n))
                names.Add(n);
        }
        if (names.Count == 0)
        {
            reason = "tools sem nome";
            return false;
        }

        // minCalls: enquanto o historico nao tiver N tool calls, o grammar NAO
        // oferece a saida {"final":...}. Isso impede o modelo de encerrar com
        // texto antes de construir os artefatos; depois de N calls, final libera.
        int calls = 0;
        if (body["messages"] is JsonArray msgs)
            foreach (var m in msgs)
                if (m is JsonObject mo
                    && mo["role"]?.GetValue<string>() == "assistant"
                    && mo["tool_calls"] is JsonArray tc)
                    calls += tc.Count;
        bool allowFinal = calls >= minCalls;
        body["grammar"] = BuildGrammar(tools, maxString, allowFinal);
        AppendSystem(body, BuildSystemPrompt(tools, allowFinal));
        body.Remove("tools");
        body.Remove("tool_choice");
        body.Remove("parallel_tool_calls");
        body["stream"] = false;
        body.Remove("stream_options");
        // O grammar comeca direto no objeto JSON; thinking gastaria tokens e
        // poderia conflitar com o template nativo de tools.
        body["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false };
        return true;
    }

    /// <summary>
    /// Converte o content JSON do modelo em tool_calls OpenAI (ou em content
    /// final). Se nao for JSON reconhecivel, devolve a resposta como veio para
    /// o cliente poder lembrar o modelo.
    /// </summary>
    public static JsonObject TransformResponse(JsonObject response)
    {
        if (response["choices"] is not JsonArray choices || choices.Count == 0) return response;
        if (choices[0] is not JsonObject choice || choice["message"] is not JsonObject msg) return response;
        string? content = msg["content"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(content)) return response;

        JsonObject? parsed = null;
        try { parsed = JsonNode.Parse(content.Trim())?.AsObject(); }
        catch { }
        if (parsed is null) return response;

        if (parsed["name"]?.GetValue<string>() is string name && parsed["arguments"] is JsonNode args)
        {
            string argJson = args is JsonObject
                ? args.ToJsonString()
                : args.GetValue<string>() ?? "{}";
            var call = new JsonObject
            {
                ["id"] = "call_" + Guid.NewGuid().ToString("N"),
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = name, ["arguments"] = argJson }
            };
            msg["content"] = "";
            msg["tool_calls"] = new JsonArray(call);
            choice["finish_reason"] = "tool_calls";
            return response;
        }

        if (parsed["final"]?.GetValue<string>() is string final)
        {
            msg["content"] = final;
            choice["finish_reason"] = "stop";
        }
        return response;
    }

    static void AppendSystem(JsonObject body, string text)
    {
        if (body["messages"] is not JsonArray msgs) return;
        for (int i = 0; i < msgs.Count; i++)
        {
            if (msgs[i] is not JsonObject m) continue;
            string? role = m["role"]?.GetValue<string>();
            if (role is not ("system" or "developer")) continue;
            if (m["content"] is JsonValue v && v.TryGetValue<string>(out string? s))
            {
                m["content"] = s.TrimEnd() + "\n\n" + text;
                return;
            }
            if (m["content"] is JsonArray parts)
            {
                parts.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                return;
            }
        }
        msgs.Insert(0, new JsonObject { ["role"] = "system", ["content"] = text });
    }

    static string BuildSystemPrompt(JsonArray tools, bool allowFinal)
    {
        string json = tools.ToJsonString();
        return
            "[TOOL SHIM JSON]\n" +
            "Ferramentas disponiveis (nome, descricao, parametros JSON):\n" +
            json + "\n" +
            "Responda SOMENTE com um objeto JSON, sem markdown e sem texto fora:\n" +
            "- Para chamar uma ferramenta: {\"name\":\"<nome>\",\"arguments\":{...}}\n" +
            (allowFinal
                ? "- Para encerrar sem ferramenta: {\"final\":\"<texto>\"}\n"
                : "- OBRIGATORIO chamar uma ferramenta agora; nao use final.\n") +
            "Use exatamente os nomes e parametros declarados. Nao invente ferramenta. " +
            "Se o usuario pedir para ler, revisar ou inspecionar arquivos, chame a ferramenta de leitura antes de finalizar.";
    }

    static string BuildGrammar(JsonArray tools, int maxString, bool allowFinal)
    {
        var defs = new List<(string Name, List<(string Key, string Rule)> Required)>();
        foreach (var t in tools)
        {
            if (t is not JsonObject o || o["function"] is not JsonObject fn) continue;
            string? name = fn["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;
            var required = new List<(string Key, string Rule)>();
            if (fn["parameters"] is JsonObject pars
                && pars["properties"] is JsonObject props
                && pars["required"] is JsonArray req)
            {
                foreach (var r in req)
                {
                    string? key = r?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    required.Add((key, TypeRule(props[key] as JsonObject)));
                }
            }
            defs.Add((name, required));
        }
        if (defs.Count == 0) return "";

        var sb = new StringBuilder();
        sb.Append(allowFinal ? "root ::= ws? (call | final) ws?\n" : "root ::= ws? call ws?\n");
        sb.Append("call ::= ");
        for (int i = 0; i < defs.Count; i++)
        {
            if (i > 0) sb.Append(" | ");
            var (name, required) = defs[i];
            sb.Append("\"{\" ws ").Append(GbnfJsonString("name")).Append(" ws \":\" ws ")
              .Append(GbnfJsonString(name)).Append(" ws \",\" ws ")
              .Append(GbnfJsonString("arguments")).Append(" ws \":\" ws ");
            if (required.Count == 0)
                sb.Append("object");
            else
            {
                sb.Append("\"{\" ws ");
                for (int j = 0; j < required.Count; j++)
                {
                    if (j > 0) sb.Append("ws \",\" ws ");
                    sb.Append(GbnfJsonString(required[j].Key)).Append(" ws \":\" ws ")
                      .Append(required[j].Rule).Append(" ws ");
                }
                sb.Append("\"}\"");
            }
            sb.Append(" ws \"}\"");
        }
        sb.Append('\n');
        if (allowFinal)
            sb.Append("final ::= \"{\" ws ").Append(GbnfJsonString("final")).Append(" ws \":\" ws string ws \"}\"\n");
        sb.Append("object ::= \"{\" ws (pair (ws \",\" ws pair)*)? ws \"}\"\n");
        sb.Append("pair ::= string ws \":\" ws value\n");
        sb.Append("value ::= string | number | boolean | \"null\" | object | array\n");
        sb.Append("boolean ::= \"true\" | \"false\"\n");
        sb.Append("array ::= \"[\" ws (value (ws \",\" ws value)*)? ws \"]\"\n");
        sb.Append("string ::= \"\\\"\" schar{0,").Append(maxString).Append("} \"\\\"\"\n");
        sb.Append("schar ::= [^\"\\\\] | \"\\\\\" [\"\\\\/bfnrt] | \"\\\\u\" [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F]\n");
        sb.Append("number ::= \"-\"? [0-9]+ (\".\" [0-9]+)? (\"e\" (\"+\" | \"-\")? [0-9]+)?\n");
        // ws capado: `*` deixava o modelo loopar em newlines entre "name" e
        // ":" (1000 tokens de espaco). 0..2 cobre indentacao compacta sem loop.
        sb.Append("ws ::= [ \\t\\n\\r]{0,2}\n");
        return sb.ToString();
    }

    static string TypeRule(JsonObject? schema)
    {
        if (schema?["enum"] is JsonArray en && en.Count > 0)
        {
            var parts = new List<string>();
            foreach (var e in en)
            {
                if (e is JsonValue v && v.TryGetValue<string>(out string? s))
                    parts.Add(GbnfJsonString(s));
            }
            if (parts.Count > 0) return string.Join(" | ", parts);
        }
        string? type = schema?["type"] is JsonValue tv && tv.TryGetValue<string>(out string? ts) ? ts : null;
        return type switch
        {
            "string" => "string",
            "number" or "integer" => "number",
            "boolean" => "boolean",
            "array" => "array",
            "object" => "object",
            _ => "value"
        };
    }

    static string GbnfJsonString(string s) => "\"" + EscapeGbnf("\"" + s + "\"") + "\"";

    static string EscapeGbnf(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
