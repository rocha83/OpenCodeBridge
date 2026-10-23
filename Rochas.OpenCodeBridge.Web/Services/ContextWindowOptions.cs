namespace Rochas.OpenCodeBridge.Web.Services;

// Orçamento de contexto por linha (appsettings "ContextWindow").
// Linha CPU: llama :4110 Gemma4 E4B UD-Q3, ctx 131072 em slot único (-np 1).
// CpuMaxInput = 131072 - 16384 = 114688. GPU: E4B Q4 com mesmo ctx/saída.
public sealed class ContextWindowOptions
{
    public int CpuContextTokens { get; set; } = 131072;
    public int CpuMaxOutputTokens { get; set; } = 16384;
    public int CpuMaxInputTokens { get; set; } = 114688;
    public int GpuMaxInputTokens { get; set; } = 114688;
    // Corte do args de tool ecoado no evento SSE de progresso: 0 = sem corte.
    public int ToolArgLimit { get; set; } = 0;
}
