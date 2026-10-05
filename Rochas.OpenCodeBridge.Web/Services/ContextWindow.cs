using System.Collections.Generic;
using System.Linq;
using Rochas.OpenCodeBridge.Web.Models;

namespace Rochas.OpenCodeBridge.Web.Services;

// Janela de contexto por tokens (stateless). Comentários pt-BR.
public static class ContextWindow
{
    // Qwen3-8B: 36864 total, reservar 8192 saída = 28672 entrada.
    // Heurística: ~4 chars/token (pt-BR). Se bridge devolve usage.real, usar esse.
    public const int MaxInputTokens = 28672;
    public const double CharsPerToken = 4.0;

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