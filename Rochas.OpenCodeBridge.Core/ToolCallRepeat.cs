using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Guard de progresso ENTRE turnos: a MESMA tool call (nome + args) repetida no
/// turno seguinte e cujo resultado foi erro e' laco — o anti-repeat de stream
/// não vê isso (ele compara dentro de uma resposta só).
///
/// O aviso NÃO repete a descrição do erro: o modelo já tem o texto em
/// <c>role: "tool"</c> na mensagem anterior (regra de ouro da dica: não exibir o
/// literal que ele copiaria). Quando a call foi cortada e não existe resultado
/// (salvage), quem entrega o erro é o <see cref="ToolCallRepair.Nudge"/>.
/// </summary>
public static class ToolCallRepeat
{
    /// <summary>Prefixo do aviso: identifica a mensagem e evita empilhar cópias.</summary>
    public const string Marker = "BRIDGE (call repetida)";

    public const string Nudge =
        Marker + ": a mesma tool call (nome + args) ja' rodou no turno anterior e o resultado nao mudou. " +
        "Nao repita: leia o arquivo atual (`read`) e edite o trecho que o erro aponta, ou troque os args.";

    /// <summary>
    /// Detecta a repetição no histórico <c>chat</c> e anexa <see cref="Nudge"/>
    /// como mensagem de usuário. Idempotente: se o último texto já é o aviso,
    /// não acrescenta de novo.
    /// </summary>
    /// <param name="messages">Histórico do request (mutado só quando detecta).</param>
    /// <returns>true quando o aviso foi anexado.</returns>
    public static bool Apply(JsonArray messages)
    {
        if (messages.Count < 4) return false;
        if (LastText(messages).Contains(Marker, StringComparison.Ordinal)) return false;

        int last = -1, previous = -1;
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not JsonObject m) continue;
            if (m["role"]?.GetValue<string>() != "assistant") continue;
            if (m["tool_calls"] is not JsonArray { Count: > 0 }) continue;
            if (last < 0) last = i;
            else { previous = i; break; }
        }
        if (previous < 0 || last < 0) return false;

        string before = Signature((messages[previous] as JsonObject)?["tool_calls"] as JsonArray);
        string after = Signature((messages[last] as JsonObject)?["tool_calls"] as JsonArray);
        if (before.Length == 0 || before != after) return false;

        // O resultado do turno repetido (mensagens entre os dois assistant) tem
        // que ser erro: repetir chamada que funcionou é outra coisa (retry útil).
        if (!Errored(messages, previous + 1, last)) return false;

        messages.Add(new JsonObject { ["role"] = "user", ["content"] = Nudge });
        return true;
    }

    /// <summary>Assinatura estável de uma lista de tool calls (nome + args).</summary>
    public static string Signature(JsonArray? calls)
    {
        if (calls is null || calls.Count == 0) return "";
        var parts = new List<string>(calls.Count);
        foreach (var node in calls)
        {
            var c = node as JsonObject;
            var fn = c?["function"] as JsonObject;
            string name = fn?["name"]?.GetValue<string>() ?? "";
            string args = fn?["arguments"]?.GetValue<string>() ?? "";
            if (name.Length == 0) continue;
            parts.Add(name + "(" + Canonical(args) + ")");
        }
        return string.Join("|", parts);
    }

    /// <summary>
    /// Args canonicalizados para comparação: espaços externos fora e o JSON
    /// reescrito quando parseia (a mesma chamada pode variar em espaçamento).
    /// </summary>
    public static string Canonical(string args)
    {
        string a = (args ?? "").Trim();
        if (a.Length == 0) return "";
        try { return JsonNode.Parse(a)?.ToJsonString() ?? a; }
        catch { return a; }
    }

    /// <summary>Alguma mensagem <c>tool</c> no intervalo traz marca de erro?</summary>
    static bool Errored(JsonArray messages, int from, int to)
    {
        for (int i = Math.Max(0, from); i < to && i < messages.Count; i++)
        {
            if (messages[i] is not JsonObject t) continue;
            if (t["role"]?.GetValue<string>() != "tool") continue;
            if (LooksLikeError(t["content"]?.GetValue<string>() ?? "")) return true;
        }
        return false;
    }

    /// <summary>Marca-texto de erro de tool (build, shell, git, arquivo).</summary>
    public static bool LooksLikeError(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return text.Contains("ERRO", StringComparison.OrdinalIgnoreCase)
               || text.Contains("error", StringComparison.OrdinalIgnoreCase)
               || text.Contains("exit 1", StringComparison.OrdinalIgnoreCase)
               || text.Contains("exit 128", StringComparison.OrdinalIgnoreCase)
               || text.Contains("fail", StringComparison.OrdinalIgnoreCase);
    }

    static string LastText(JsonArray messages)
    {
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i] is not JsonObject m) continue;
            var c = m["content"];
            if (c is JsonValue v && v.TryGetValue<string>(out string? s) && !string.IsNullOrWhiteSpace(s))
                return s;
            if (c is JsonArray arr)
                foreach (var part in arr)
                    if (part?["text"]?.GetValue<string>() is string t && t.Length > 0) return t;
        }
        return "";
    }
}
