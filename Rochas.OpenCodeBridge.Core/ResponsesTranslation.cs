using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Tradução Responses &lt;-&gt; chat e leitura do tráfego JSON (arrays de tools,
/// input/instructions, output items). É a gramática do protocolo: nenhum
/// cliente (API, Web UI, console, testes) reimplementa estas regras — ele passa
/// as opções do seu modo em <see cref="TranslationOptions"/>.
/// </summary>
public static class ResponsesTranslation
{
    static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);

    /// <summary>Apelido canônico da tool (bash/sh/exec/proc = shell).</summary>
    /// <param name="n">Nome recebido.</param>
    /// <returns>Nome canônico.</returns>
    public static string CanonToolAlias(string n) => n.Trim().ToLowerInvariant() switch
    {
        "bash" or "sh" or "exec" or "proc" => "shell",
        _ => n.Trim(),
    };

    /// <summary>Extrai texto de content/output seja string, lista de partes ou objeto.</summary>
    /// <param name="node">Campo de texto.</param>
    /// <returns>Texto concatenado.</returns>
    public static string ExtractText(JsonNode? node)
    {
        if (node is null) return "";
        if (node is JsonValue value)
            return value.TryGetValue<string>(out string? s) ? s : value.ToJsonString(Opts);
        if (node is JsonArray parts)
        {
            var builder = new StringBuilder();
            foreach (var part in parts)
            {
                if (part is JsonValue partValue && partValue.TryGetValue<string>(out string? partText)) { builder.Append(partText); continue; }
                if (part is JsonObject partObj && partObj["text"] is JsonValue textNode && textNode.TryGetValue<string>(out string? nestedText))
                    builder.Append(nestedText);
            }
            return builder.ToString();
        }
        if (node is JsonObject obj && obj["text"] is JsonValue text && text.TryGetValue<string>(out string? objText))
            return objText;
        return node.ToJsonString(Opts);
    }

    /// <summary>Texto de content Responses (string ou lista de partes input/output_text).</summary>
    /// <param name="node">Campo content.</param>
    /// <returns>Texto concatenado por linha.</returns>
    public static string ExtractResponseText(JsonNode? node)
    {
        if (node is null) return "";
        if (node is JsonValue v && v.TryGetValue<string>(out string? s)) return s ?? "";
        if (node is JsonArray arr)
        {
            var b = new StringBuilder();
            foreach (var part in arr)
                if (part is JsonObject o && o["text"] is JsonValue tv && tv.TryGetValue<string>(out string? t) && !string.IsNullOrWhiteSpace(t))
                { if (b.Length > 0) b.Append('\n'); b.Append(t); }
            return b.ToString();
        }
        return "";
    }

    /// <summary>Nomes das tools anunciadas (diagnóstico: o que o cliente mandou).</summary>
    /// <param name="tools">Array de tools.</param>
    /// <returns>Até 24 nomes.</returns>
    public static JsonArray ToolNames(JsonArray? tools)
    {
        var arr = new JsonArray();
        if (tools is null) return arr;
        foreach (var t in tools)
        {
            string n = (t as JsonObject)?["function"]?["name"]?.GetValue<string>()
                ?? (t as JsonObject)?["name"]?.GetValue<string>() ?? "?";
            if (arr.Count < 24) arr.Add(n);
        }
        return arr;
    }

    /// <summary>Request com tools anunciadas? (llama /v1/responses não suporta tools).</summary>
    /// <param name="req">Request.</param>
    /// <returns>True quando há tools.</returns>
    public static bool ReqHasTools(JsonObject req) => req["tools"] is JsonArray a && a.Count > 0;

    /// <summary>Descarta itens de reasoning do input (o upstream não os aceita).</summary>
    /// <param name="req">Request mutado.</param>
    /// <returns>Quantidade removida.</returns>
    public static int StripReasoningItems(JsonObject req)
    {
        if (req["input"] is not JsonArray list) return 0;
        var clean = new JsonArray();
        int dropped = 0;
        foreach (var it in list)
        {
            if (it is JsonObject o && o["type"]?.GetValue<string>() == "reasoning") { dropped++; continue; }
            clean.Add(it?.DeepClone());
        }
        if (dropped > 0) req["input"] = clean;
        return dropped;
    }

    /// <summary>Converte o input Responses em messages do formato chat.</summary>
    /// <param name="input">Campo input (string ou array de itens).</param>
    /// <param name="instructions">System prompt do request.</param>
    /// <param name="profileClient">Cliente com modelo local (mantém a call narrada).</param>
    /// <param name="options">Opções do modo (harness, limite de saída, política de risco).</param>
    /// <returns>Array de messages.</returns>
    public static JsonArray InputToMessages(JsonNode? input, JsonNode? instructions,
        bool profileClient = false, TranslationOptions? options = null)
    {
        var opt = options ?? TranslationOptions.Plain;
        var messages = new JsonArray();

        // "instructions" + todo system/developer do meio viram UM system inicial
        // (o template exige system primeiro; resumo de compactacao chega no meio).
        var sysText = new StringBuilder();
        if (instructions is JsonValue instructionNode && instructionNode.TryGetValue<string>(out string? systemText) && !string.IsNullOrWhiteSpace(systemText))
            sysText.Append(systemText.Trim());

        if (input is JsonValue stringValue && stringValue.TryGetValue<string>(out string? directText))
        {
            // input pode ser uma string pura em vez de lista
            if (!string.IsNullOrWhiteSpace(directText))
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = directText });
            // Sem este insert o "instructions" era perdido quando input vinha como
            // string (caminho usado pelo /api/translate e por clientes simples).
            if (sysText.Length > 0)
                messages.Insert(0, new JsonObject { ["role"] = "system", ["content"] = sysText.ToString() });
            return messages;
        }

        if (input is not JsonArray list) return messages;

        foreach (var entryNode in list)
        {
            if (entryNode is null) continue;

            // input as vezes chega como string simples
            if (entryNode is JsonValue textValue && textValue.TryGetValue<string>(out string? s))
            {
                if (!string.IsNullOrWhiteSpace(s))
                    messages.Add(new JsonObject { ["role"] = "user", ["content"] = s });
                continue;
            }
            if (entryNode is not JsonObject entry) continue;

            string type = entry["type"]?.GetValue<string>() ?? "message";

            if (type == "message")
            {
                string role = entry["role"]?.GetValue<string>() ?? "user";
                string content = ExtractText(entry["content"]);
                if (string.IsNullOrWhiteSpace(content)) continue;
                if (role is ("system" or "developer"))
                {
                    if (sysText.Length > 0) sysText.Append("\n\n");
                    sysText.Append(content.Trim());
                    continue;
                }
                if (role is not ("user" or "assistant")) role = "user";
                // O opencode reenvia a tool call TAMBEM como texto
                // ("[Assistant tool call]: nome({...})"). Mantido no historico, o
                // modelo copia esse formato em vez de emitir a chamada nativa; a
                // chamada estruturada (function_call) continua logo abaixo.
                if (role == "assistant" && !profileClient && opt.LegacyHarness) content = TextGrammar.StripNarratedCalls(content);
                if (string.IsNullOrWhiteSpace(content)) continue;
                messages.Add(new JsonObject { ["role"] = role, ["content"] = content });
            }
            else if (type == "function_call")
            {
                // O opencode reenvia a tool call que ele EXECUTOU: preservamos o
                // call_id original para o role="tool" abaixo casar com ele.
                string callId = entry["call_id"]?.GetValue<string>() ?? entry["id"]?.GetValue<string>() ?? ("call_" + TextGrammar.NewId());
                string name = entry["name"]?.GetValue<string>() ?? "";
                string arguments = TextGrammar.JsonToString(entry["arguments"]);
                var call = new JsonObject
                {
                    ["id"] = callId, ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments }
                };
                // Chamadas consecutivas sao UM turno do assistente (N tool_calls
                // no mesmo item). Sem o merge o template upstream ve N turnos de
                // 1 call e o modelo perde a conta de "ja chamei isto na rodada".
                if (messages.Count > 0 && messages[^1] is JsonObject prevMsg
                    && prevMsg["role"]?.GetValue<string>() == "assistant"
                    && prevMsg["content"] is null
                    && prevMsg["tool_calls"] is JsonArray prevCalls)
                    prevCalls.Add(call);
                else
                    messages.Add(new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = null,
                        ["tool_calls"] = new JsonArray(call)
                    });
            }
            else if (type is "function_call_output" or "custom_tool_call_output" or "shell_call_output"
                     or "computer_call_output" or "local_shell_call_output" or "apply_patch_call_output")
            {
                string callId = entry["call_id"]?.GetValue<string>() ?? entry["id"]?.GetValue<string>() ?? "";
                string resultText = ExtractText(entry["output"] ?? entry["result"] ?? entry["content"]);
                if (opt.ToolOutputLimit > 0 && resultText.Length > opt.ToolOutputLimit) resultText = resultText[..opt.ToolOutputLimit];
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = callId, ["content"] = resultText });
            }
            // Outros tipos (reasoning, item_reference, calls ainda nao executados)
            // sao ignorados de proposito: nao fazem falta no fluxo chat.
        }
        // Respostas as perguntas de risco do turno anterior viram instrucao no
        // historico do MODELO (nao texto de prosa): e o que o reemite a chamada.
        if (opt.RiskAnswers is not null)
            foreach (string notice in opt.RiskAnswers(input))
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = notice });

        if (sysText.Length > 0)
            messages.Insert(0, new JsonObject
            {
                ["role"] = "system",
                ["content"] = opt.LegacyHarness
                    ? TextGrammar.WithSystemGuardrail(sysText.ToString(), opt.QwenStandard)
                    : sysText.ToString()
            });
        return messages;
    }
}
