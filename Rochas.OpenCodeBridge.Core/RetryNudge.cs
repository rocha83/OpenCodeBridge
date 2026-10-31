using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Core;

/// <summary>
/// Mecânica única do retry por ABORTO do upstream: a conexão foi encerrada
/// antes da resposta (o llama cancela a task quando o parser rejeita a saída) e
/// não há corpo para salvaguardar. Aqui moram o texto do nudge, o predicado do
/// aborto e o preparo do corpo do retry (nudge + temperatura de desvio).
/// Consumidores não reimplementam o texto.
/// </summary>
/// <remarks>
/// Complementa <see cref="ToolCallRepair"/> (args quebrados dentro de uma
/// resposta RECEBIDA) e <see cref="ToolsAntiRepeat"/> (loop de tool): nenhum
/// dos dois cobre o caso "abortou sem corpo". O ponto em comum é a regra de
/// desvio — <see cref="Sampling.ForRetry"/> usa a temperatura do role Plan
/// como referência, não um literal.
/// </remarks>
public static class RetryNudge
{
    /// <summary>
    /// Aviso do retry: pede OUTRA abordagem (não a mesma reenviada às cegas).
    /// Genérico de propósito: descreve o fato e o escape, nunca o passo a passo
    /// da tarefa.
    /// </summary>
    public const string Abort =
        "O upstream encerrou a conexao antes de entregar a resposta deste turno. " +
        "Recomece com OUTRA abordagem, sem repetir a anterior: menos passos por call, " +
        "apenas o necessario para esta etapa, e escrita mais concisa. " +
        "Reemita UMA tool call com argumentos JSON completos.";

    /// <summary>Aviso no padrão único da bridge (escopo <c>retry</c>).</summary>
    /// <returns>Texto do aviso.</returns>
    public static string Notice() => BridgeNotice.Notice("retry", Abort);

    /// <summary>A exceção é aborto do upstream (conexão encerrada no meio)?</summary>
    /// <param name="ex">Exceção capturada no envio ao upstream.</param>
    /// <returns>True quando é queda de conexão do servidor, não erro do cliente.</returns>
    public static bool IsUpstreamAbort(Exception? ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.Net.Http.HttpIOException) return true;
            string m = e.Message;
            if (m.Contains("ResponseEnded", StringComparison.Ordinal)
                || m.Contains("response ended prematurely", StringComparison.OrdinalIgnoreCase))
                return true;
            if (e.InnerException is null) break;
        }
        return false;
    }

    /// <summary>
    /// A mensagem MAIS RECENTE é um nudge de retry/reconsideração? A temperatura
    /// efetiva sobe para a do role de desvio só nesse turno (ver
    /// <see cref="Sampling.ForRetry"/>). É deliberadamente a última mensagem e
    /// não uma janela: o desvio morre no turno seguinte, quando o nudge deixa de
    /// ser o topo do histórico — não existe estado de temperatura guardado.
    /// </summary>
    /// <param name="messages">Histórico chat da mensagem enviada.</param>
    /// <returns>True somente no turno imediatamente após o nudge.</returns>
    public static bool HasRetryHint(JsonArray? messages)
    {
        if (messages is null || messages.Count == 0) return false;
        string? c = messages[^1]?["content"]?.GetValue<string>();
        if (string.IsNullOrEmpty(c)) return false;
        return c.Contains(BridgeNotice.Prefix, StringComparison.Ordinal)
            || c.Contains(BridgeNotice.BreakerMarker, StringComparison.Ordinal);
    }

    /// <summary>
    /// Corpo do retry: nudge anexado ao turno e temperatura de desvio. Devolve
    /// null quando o corpo não tem histórico (não há onde anexar).
    /// </summary>
    /// <param name="body">Corpo JSON já transformado para o upstream.</param>
    /// <param name="currentTemperature">Temperatura efetiva do turno que abortou.</param>
    /// <param name="profiles">Tabela de roles em vigor (null = default do Core).</param>
    /// <returns>Corpo do retry ou null quando não dá para orientar o modelo.</returns>
    public static string? WithAbortNudge(string body, double currentTemperature,
        IReadOnlyDictionary<string, RoleProfile>? profiles = null)
    {
        JsonObject? node;
        try { node = JsonNode.Parse(body)?.AsObject(); }
        catch { return null; }
        if (node is null) return null;

        bool attached = false;
        if (node["messages"] is JsonArray msgs)
        {
            msgs.Add(new JsonObject { ["role"] = "user", ["content"] = Notice() });
            attached = true;
        }
        else if (node["input"] is JsonArray input)
        {
            input.Add(new JsonObject
            {
                ["type"] = "message",
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = Notice() })
            });
            attached = true;
        }
        if (!attached) return null;

        node["temperature"] = Sampling.ForRetry(currentTemperature, profiles);
        return node.ToJsonString();
    }

    /// <summary>Aborto do upstream que persistiu após o retry (vira 502 estruturado).</summary>
    public sealed class UpstreamAbortException : Exception
    {
        /// <summary>Cria a falha com a causa do transporte.</summary>
        /// <param name="message">Mensagem original do transporte.</param>
        /// <param name="inner">Exceção de origem.</param>
        public UpstreamAbortException(string message, Exception? inner = null) : base(message, inner) { }
    }
}
