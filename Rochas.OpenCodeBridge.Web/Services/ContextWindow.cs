using System.Collections.Generic;
using System.Linq;
using Rochas.OpenCodeBridge.Web.Models;

namespace Rochas.OpenCodeBridge.Web.Services;

// Janela de contexto por tokens (stateless). Comentários pt-BR.
// Linha CPU: llama :4110 GPU2-CPU, Gemma4 E2B, ctx 131072 em 2 slots
// (2x65536). Orçamento de entrada por slot = ctx_slot - saída (8192).
public static class ContextWindow
{
    // Orçamento por linha: GPU (ctx cheio menos saída) vs CPU (slot menos saída).
    // Heurística: ~4 chars/token (pt-BR). Se bridge devolve usage.real, usar esse.
    public const int MaxInputTokens = 122880;
    public const double CharsPerToken = 4.0;

    // ctx por slot da linha CPU e teto de saída: parametrizáveis via
    // appsettings "ContextWindow" (ver ContextWindowOptions). Padrão 64k/8k.
    public static int CpuContextTokens { get; set; } = 65536;
    public static int CpuMaxOutputTokens { get; set; } = 8192;
    public static int CpuMaxInputTokens { get; set; } = 57344;
    public static int GpuMaxInputTokens { get; set; } = MaxInputTokens;
    // Corte do args de tool ecoado no evento SSE de progresso: 0 = sem corte.
    public static int ToolArgLimit { get; set; } = 0;

    public static void Configure(int cpuContext, int cpuOutput, int cpuInput, int gpuInput, int toolArgLimit = 0)
    {
        if (cpuContext > 0) CpuContextTokens = cpuContext;
        if (cpuOutput > 0) CpuMaxOutputTokens = cpuOutput;
        if (cpuInput > 0) CpuMaxInputTokens = cpuInput;
        else if (cpuContext > 0 && cpuOutput > 0) CpuMaxInputTokens = cpuContext - cpuOutput;
        if (gpuInput > 0) GpuMaxInputTokens = gpuInput;
        if (toolArgLimit >= 0) ToolArgLimit = toolArgLimit;
    }

    // Orçamento de entrada por linha: CPU (:4125/Gemma4 E2B) = CpuMaxInputTokens
    // (57k = 64k por slot menos 8k saída), resto (GPU 12B 128k) = GpuMaxInputTokens.
    public static int ResolveMaxInput(string model, string bridgeUrl)
    {
        if ((bridgeUrl ?? "").Contains(":4125") || (model ?? "").Contains("4b")
            || (model ?? "").Contains("e2b"))
            return CpuMaxInputTokens;
        return GpuMaxInputTokens;
    }

    // Mensagem para envio à bridge (subset de SessionMessage).
    public sealed class ChatMessage
    {
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";
    }

    // Constrói lista de mensagens cronológicas que cabem no orçamento de tokens.
    // Itera do mais recente p/ mais antigo, soma tokens estimados, para quando estourar.
    // Retorna lista invertida (cronológica: system -> user/assistant...).
    public static List<ChatMessage> BuildContext(List<SessionMessage> history, int maxInputTokens = MaxInputTokens)
    {
        var result = new List<ChatMessage>();
        int used = 0;

        for (int i = history.Count - 1; i >= 0; i--)
        {
            var m = history[i];
            // Compactado = passado: fora do contexto, visível no histórico.
            if (m.Archived != 0) continue;
            int est = EstimateTokens(m.Content);
            if (used + est > maxInputTokens && result.Count > 0) break;

            result.Add(new ChatMessage { Role = m.Role, Content = m.Content });
            used += est;
        }

        result.Reverse();
        return result;
    }

    // Overload que aceita lista já no formato ChatMessage (ex.: para testes).
    public static List<ChatMessage> BuildContext(IEnumerable<ChatMessage> history, int maxInputTokens = MaxInputTokens)
        => BuildContext(history.Select(m => new SessionMessage { Role = m.Role, Content = m.Content }).ToList(), maxInputTokens);

    // Estimativa simples: chars / 4. Arredonda p/ cima.
    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 1;
        return (int)System.Math.Ceiling(text.Length / CharsPerToken);
    }

    // Estima tokens de uma lista completa (para telemetria).
    public static int EstimateTotalTokens(List<SessionMessage> history)
        => history.Sum(m => EstimateTokens(m.Content));
}