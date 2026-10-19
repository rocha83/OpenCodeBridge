// Desvio E2E para o opencode real: MESMOS enunciados da suite, executados por
// `opencode run` (agente/role build) em vez de HTTP direto na bridge.
// Ativa com --standalone (e --standalone-agent <agente>).

namespace Rochas.OpenCodeBridge.Test;

internal static class E2EStandaloneTests
{
    static string Run(string prompt, params string[] expect)
    {
        string outText = TestContext.OpencodeRun(prompt);
        if (expect.Length > 0)
            TestContext.Check(expect.Any(e => outText.Contains(e, StringComparison.OrdinalIgnoreCase)),
                $"esperado {string.Join("|", expect)}; veio: {outText[..Math.Min(300, outText.Length)]}");
        return outText;
    }

    public static Task Oi()
    {
        Run("responda apenas: ok", "ok");
        return Task.CompletedTask;
    }

    public static Task Tools()
    {
        Run("Use a tool shell para rodar 'date +%F' e diga a data.", "202");
        return Task.CompletedTask;
    }

    public static Task Write()
    {
        Run("Use a tool write para criar o arquivo /tmp/opencode/standalone/ok.txt com o conteudo 'ok' e depois leia com read para confirmar.", "ok");
        return Task.CompletedTask;
    }

    public static Task DevPipe()
    {
        Run("Use glob para listar os arquivos *.cs deste diretorio e diga quantos sao. Se nao houver, diga 0.", "0", "arquivo");
        return Task.CompletedTask;
    }

    public static Task Science()
    {
        Run("Qual a esperanca E[X] de uma exponencial de parametro lambda? Responda so texto, sem tools.", "1/lambda", "lambda");
        return Task.CompletedTask;
    }

    public static Task MissionTools()
    {
        Run("Use a tool shell para rodar exatamente: echo missao-ok; diga a saida.", "missao-ok");
        return Task.CompletedTask;
    }
}
