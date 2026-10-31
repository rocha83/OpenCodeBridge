using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Core;

/// <summary>Configuração da guarda de contexto (lida de flags/appsettings pelo host).</summary>
/// <param name="Enabled">Guarda ligada.</param>
/// <param name="Threshold">Fração do ctx que dispara a guarda (ex.: 0.95).</param>
/// <param name="Tokens">Tamanho do contexto TOTAL do modelo servido (0 = inativa).</param>
/// <param name="Slots">Slots paralelos do llama (<c>-np</c>): o ctx total é dividido
/// entre eles e uma request só cabe em um slot. A API do llama não informa o número
/// de slots, então o host declara (<c>--context-slots</c>).</param>
public readonly record struct ContextGuardSettings(bool Enabled, double Threshold, int Tokens, int Slots = 1);

/// <summary>
/// Guarda de contexto: detecta, na resposta do upstream, que o prompt chegou ao
/// limiar do ctx do modelo. Regra do núcleo — cliente só informa os parâmetros.
/// </summary>
public static class ContextGuard
{
    /// <summary>Avalia se o prompt do upstream atingiu o limiar.</summary>
    /// <param name="json">Corpo JSON do upstream (com <c>usage.prompt_tokens</c>).</param>
    /// <param name="settings">Configuração em vigor.</param>
    /// <param name="promptTokens">Tokens de prompt lidos (0 quando ilegível).</param>
    /// <param name="fraction">Fração do ctx consumida.</param>
    /// <returns>True quando dispara (>= limiar).</returns>
    public static bool Tripped(string json, ContextGuardSettings settings, out int promptTokens, out double fraction)
    {
        promptTokens = 0;
        fraction = 0;
        if (!settings.Enabled || settings.Tokens <= 0) return false;
        try { promptTokens = JsonNode.Parse(json)?.AsObject()?["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0; }
        catch { return false; }
        // O llama divide o ctx TOTAL entre N slots paralelos; uma request só cabe no
        // slot. A API do llama não informa o número de slots, então dividimos pelo
        // configurado (--context-slots). Com o TOTAL o guard nunca dispara antes do
        // 400 seco do slot (medido 2026-10-30: 524288 total, slot 131072, request
        // de 142k barrada pelo llama antes de qualquer compactação).
        int slots = settings.Slots > 0 ? settings.Slots : 1;
        double perSlot = (double)settings.Tokens / slots;
        fraction = promptTokens / perSlot;
        return fraction >= settings.Threshold;
    }

    /// <summary>Fração do ctx por-slot que dispara a compactação (95%).</summary>
    public const double DefaultThreshold = 0.95;

    /// <summary>
    /// Rotina ÚNICA de compactação da sessão (lado cliente) — a resposta ao gatilho
    /// que <see cref="Tripped"/> detecta e que o host devolve como 413
    /// <c>context_compact_required</c>. Calcula o teto como <paramref name="threshold"/>
    /// do ctx por-slot — ctx TOTAL dividido pelos slots paralelos do llama (<c>-np</c>);
    /// a API do llama não informa os slots, então o cliente declara
    /// (<c>--context-slots</c>) — e, quando o histórico passa do teto, resume os turnos
    /// antigos: args de tool call viram marcador (o arquivo em disco é a fonte; o modelo
    /// relê com <c>read</c>) e resultados antigos são truncados preservando o início
    /// (onde ficam os marcadores de falha que o breaker lê). Não remove mensagens: o
    /// pareamento tool_call/tool_result segue válido. Acima do teto a próxima request
    /// estoura o slot e o llama devolve 400 seco (medido 2026-10-30: 524288/4 = 131072
    /// por slot, request de 142k barrada).
    /// </summary>
    /// <param name="messages">Mensagens no formato chat (mutadas).</param>
    /// <param name="contextTokens">Ctx TOTAL servido (ex.: 524288).</param>
    /// <param name="slots">Slots paralelos do llama (ex.: 4).</param>
    /// <param name="threshold">Fração do ctx por-slot que dispara (default 95%).</param>
    /// <returns>true quando compactou.</returns>
    public static bool Compact(JsonArray messages, int contextTokens, int slots, double threshold = DefaultThreshold)
    {
        int budgetTokens = contextTokens <= 0 ? 0
            : (int)((double)contextTokens / Math.Max(1, slots) * threshold);
        if (budgetTokens <= 0 || messages.Count <= 4) return false;
        int Estimate()
        {
            int n = 0;
            foreach (var m in messages) n += m?.ToJsonString().Length ?? 0;
            return n / 4;
        }
        if (Estimate() <= budgetTokens) return false;
        const int keepRecent = 8; // últimos 4 turnos (assistant+tool) intactos
        int cut = Math.Max(2, messages.Count - keepRecent);
        for (int i = 2; i < cut; i++)
        {
            if (messages[i] is not JsonObject mo) continue;
            if (mo["tool_calls"] is JsonArray tcs)
                foreach (var tc in tcs)
                {
                    if (tc?["function"] is not JsonObject fn) continue;
                    string args = fn["arguments"] is JsonValue av && av.TryGetValue<string>(out var s) ? s : "";
                    if (args.Length <= 300) continue;
                    string path = "";
                    try { path = JsonNode.Parse(args)?["path"]?.GetValue<string>() ?? ""; } catch { }
                    fn["arguments"] = JsonValue.Create(path.Length > 0
                        ? "{\"path\":\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\",\"content\":\"(omitido: o arquivo em disco e a fonte; use read)\"}"
                        : "{\"path\":\"(omitido: o arquivo em disco e a fonte; use read)\"}");
                }
            if (mo["content"] is JsonValue cv && cv.TryGetValue<string>(out var c) && c.Length > 600)
                mo["content"] = JsonValue.Create(c[..600] + "\n...(omitido)");
        }
        return true;
    }
}
