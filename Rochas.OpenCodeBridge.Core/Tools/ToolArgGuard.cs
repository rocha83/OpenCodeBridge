using System.Collections.Generic;

namespace Rochas.OpenCodeBridge.Core.Tools;

/// <summary>
/// Guarda de argumentos de tool call. O modelo pequeno, quando degenera, coloca
/// tokens do proprio template dentro dos args (medido no E-tools-full de
/// 2026-11-06: `}{<tool_call|>response:git{value:"[stdo`). Isso nao e' comando:
/// executar grava lixo no workspace e o loop nunca fecha.
/// </summary>
public static class ToolArgGuard
{
    // Marcadores do template Gemma/chat que nunca pertencem a um argumento.
    static readonly string[] Markers =
    {
        "<tool_call", "</tool_call", "<tool_response", "</tool_response",
        "<start_of_turn", "<end_of_turn", "<|",
    };

    /// <summary>
    /// Teto de tamanho dos argumentos. Medido no E-tools-full de 2026-11-06:
    /// uma rodada emitiu 48.936 chars de argumento e o upstream falhou ao
    /// parsear a call (500). Argumento de tool e' curto por natureza.
    /// </summary>
    public const int MaxChars = 16000;

    /// <summary>Marcador encontrado nos args (null = argumento limpo).</summary>
    public static string? InvalidReason(string? argumentsJson)
    {
        if (string.IsNullOrEmpty(argumentsJson)) return null;
        if (argumentsJson.Length > MaxChars) return "args>" + MaxChars;
        // System.Text.Json (JsonNode.ToJsonString) escapa '<' como \u003C por
        // default. Sem normalizar, o lixo `<|tool_call|>...` passava pelo guard
        // e o write gravava arquivo com nome podre (medido no belt-build azul de
        // 2026-11-26: `Reembolsos.csproj`}<tool_call|>_thought...`).
        string scan = argumentsJson.Replace("\\u003c", "<", System.StringComparison.OrdinalIgnoreCase);
        foreach (string m in Markers)
            if (scan.Contains(m, System.StringComparison.Ordinal)) return m;
        return null;
    }
}
