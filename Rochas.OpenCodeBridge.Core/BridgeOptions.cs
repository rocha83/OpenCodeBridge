namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Configuração de UMA instância lógica da bridge. É passada por parâmetro nos
/// métodos do núcleo — nunca por args de processo nem por query HTTP.
/// </summary>
public sealed record BridgeOptions
{
    /// <summary>URL do servidor llama.cpp (ex.: http://127.0.0.1:4100).</summary>
    public string UpstreamUrl { get; init; } = "http://127.0.0.1:4100";

    /// <summary>Modelo servido pelo llama (alias do --alias).</summary>
    public string Model { get; init; } = "Gemma4-E4B-Q4";

    /// <summary>Chave do llama (--api-key). Vazio = sem Authorization.</summary>
    public string ApiKey { get; init; } = "";

    /// <summary>Temperatura padrão quando o corpo não informa.</summary>
    public double Temperature { get; init; } = 0.1;

    /// <summary>Teto de saída quando o corpo não informa (0 = não injeta).</summary>
    public int MaxTokens { get; init; } = 16384;

    /// <summary>Thinking do turno; null = respeita o que veio no corpo.</summary>
    public bool? EnableThinking { get; init; }

    /// <summary>Liga as caudas de harness (comportamento + uso de tools).</summary>
    public bool Harness { get; init; } = true;

    /// <summary>Cauda de comportamento (default do núcleo; pode ser sobrescrita).</summary>
    public string HarnessBehaviorTail { get; init; } = HarnessTails.DefaultBehavior;

    /// <summary>Cauda de uso de tools (default do núcleo; pode ser sobrescrita).</summary>
    public string HarnessToolsTail { get; init; } = HarnessTails.DefaultTools;

    /// <summary>Reanima o harness legado (guardrail/coerce/strip/retry antigos).</summary>
    public bool LegacyHarness { get; init; }

    /// <summary>Coerção texto→tool_calls (modelos sem parser no upstream).</summary>
    public bool CoerceTextTools { get; init; }

    /// <summary>Coerção de tool call narrada no corpo da mensagem.</summary>
    public bool CoerceNarrated { get; init; }

    /// <summary>Resgate determinístico do padrão Code Mode.</summary>
    public bool ToolStrategy { get; init; }

    /// <summary>Tamanho de contexto servido (informativo/guard).</summary>
    public int ContextTokens { get; init; } = 131072;

    /// <summary>Timeout do upstream; null = infinito (stream longo).</summary>
    public TimeSpan? Timeout { get; init; }
}
