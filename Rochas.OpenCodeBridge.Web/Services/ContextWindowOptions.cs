namespace Rochas.OpenCodeBridge.Web.Services;

// Orçamento de contexto por linha (appsettings "ContextWindow").
// Linha CPU: llama :4110 GPU2-CPU, Gemma4 E2B, ctx 131072 em 2 slots (2x65536).
// CpuMaxInput = ctx por slot - saída. GPU: 12B com ctx 131072 e saída 8192.
public sealed class ContextWindowOptions
{
    public int CpuContextTokens { get; set; } = 65536;
    public int CpuMaxOutputTokens { get; set; } = 8192;
    public int CpuMaxInputTokens { get; set; } = 57344;
    public int GpuMaxInputTokens { get; set; } = 122880;
    // Corte do args de tool ecoado no evento SSE de progresso: 0 = sem corte.
    public int ToolArgLimit { get; set; } = 0;
}
