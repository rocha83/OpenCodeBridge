namespace Rochas.OpenCodeBridge.Web.Services;

// Orçamento de contexto por linha (appsettings "ContextWindow").
// Linha CPU: llama :4110 CPU puro (-ngl 0), Qwen3.5-4B, ctx 16384 em SLOT
// ÚNICO (-np 1, mín. 2 threads). CpuMaxInput = CpuContext - CpuMaxOutput.
public sealed class ContextWindowOptions
{
    public int CpuContextTokens { get; set; } = 16384;
    public int CpuMaxOutputTokens { get; set; } = 4096;
    public int CpuMaxInputTokens { get; set; } = 12288;
    public int GpuMaxInputTokens { get; set; } = 229376;
}
