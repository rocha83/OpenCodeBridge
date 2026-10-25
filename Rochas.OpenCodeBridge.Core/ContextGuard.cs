using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Core;

/// <summary>Configuração da guarda de contexto (lida de flags/appsettings pelo host).</summary>
/// <param name="Enabled">Guarda ligada.</param>
/// <param name="Threshold">Fração do ctx que dispara a guarda (ex.: 0.95).</param>
/// <param name="Tokens">Tamanho do contexto do modelo servido (0 = inativa).</param>
public readonly record struct ContextGuardSettings(bool Enabled, double Threshold, int Tokens);

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
        fraction = (double)promptTokens / settings.Tokens;
        return fraction >= settings.Threshold;
    }
}
