using System.Collections.Generic;
using System.Linq;
using Rochas.OpenCodeBridge.Web.Models;

namespace Rochas.OpenCodeBridge.Web.Services;

// Janela de contexto por tokens (stateless). Comentários pt-BR.
// Linha CPU: llama :4110 CPU puro (-ngl 0), Qwen3.5-4B, ctx 16384 em
// SLOT ÚNICO (-np 1, mín. 2 threads). Orçamento de entrada = ctx - saída.
public static class ContextWindow
{
    // Orçamento por linha: GPU (ctx cheio menos saída) vs CPU (slot único menos saída).
    // Heurística: ~4 chars/token (pt-BR). Se bridge devolve usage.real, usar esse.
    public const int MaxInputTokens = 229376;
    public const double CharsPerToken = 4.0;

    // ctx total da linha CPU (slot único) e teto de saída: parametrizáveis via
    // appsettings "ContextWindow" (ver ContextWindowOptions). Padrão 16k/4k.
    public static int CpuContextTokens { get; set; } = 16384;
    public static int CpuMaxOutputTokens { get; set; } = 4096;
    public static int CpuMaxInputTokens { get; set; } = 12288;
    public static int GpuMaxInputTokens { get; set; } = MaxInputTokens;

    public static void Configure(int cpuContext, int cpuOutput, int cpuInput, int gpuInput)
    {
        if (cpuContext > 0) CpuContextTokens = cpuContext;
        if (cpuOutput > 0) CpuMaxOutputTokens = cpuOutput;
        if (cpuInput > 0) CpuMaxInputTokens = cpuInput;
        else if (cpuContext > 0 && cpuOutput > 0) CpuMaxInputTokens = cpuContext - cpuOutput;
        if (gpuInput > 0) GpuMaxInputTokens = gpuInput;
    }

    // Orçamento de entrada por linha: CPU (:4125/4b) = CpuMaxInputTokens (12k =
    // 16k ctx slot único menos 4k saída), resto (GPU 9B) = GpuMaxInputTokens.
    public static int ResolveMaxInput(string model, string bridgeUrl)
    {
        if ((bridgeUrl ?? "").Contains(":4125") || (model ?? "").Contains("4b"))
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