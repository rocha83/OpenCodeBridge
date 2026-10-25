// Desvio E2E para o opencode real: MESMOS enunciados da suite, executados por
// `opencode run` (agente/role build) em vez de HTTP direto na bridge.
// Ativa com --standalone (e --standalone-agent <agente>).
//
// Cenarios CRITICOS de tools (multi-turno, cadeia real): devpipe (write+glob+read),
// ciencia com tool (shell/python) e missao C# (dotnet new + write + dotnet run).
// Aceite verifica o TEXTO e os ARTEFATOS em disco.

namespace Rochas.OpenCodeBridge.Test;

internal static class E2EStandaloneTests
{
    const string Root = "/tmp/opencode/standalone";

    static string Run(string prompt, params string[] expect)
    {
        string outText = TestContext.OpencodeRun(prompt);
        TestContext.Check(!outText.Contains("Error:") && !outText.Contains("Timed out"),
            $"opencode retornou erro: {outText[..Math.Min(300, outText.Length)]}");
        if (expect.Length > 0)
            TestContext.Check(expect.Any(e => outText.Contains(e, StringComparison.OrdinalIgnoreCase)),
                $"esperado {string.Join("|", expect)}; veio: {outText[..Math.Min(300, outText.Length)]}");
        return outText;
    }

    static string Fresh(string rel)
    {
        string dir = Path.Combine(Root, rel);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static Task Oi()
    {
        Run("responda apenas: ok", "ok");
        return Task.CompletedTask;
    }

    public static Task Tools()
    {
        Run("Rode `date +%F` e me diga a data.", "202");
        return Task.CompletedTask;
    }

    public static Task Write()
    {
        string dir = Fresh("write");
        string f = Path.Combine(dir, "ok.txt");
        Run($"Crie o arquivo {f} com o conteudo 'ok' e leia para confirmar.", "ok");
        TestContext.Check(File.Exists(f), $"arquivo {f} nao foi criado");
        return Task.CompletedTask;
    }

    // Cenario CRITICO: cadeia write -> write -> glob -> read, com marcador verificado no texto E no disco.
    public static Task DevPipe()
    {
        string dir = Fresh("dp");
        string a = Path.Combine(dir, "a.md");
        string b = Path.Combine(dir, "b.md");
        string outText = Run(
            $"Em {dir}: crie {a} com a 1a linha 'DEVPIPE-OK' e uma 2a linha qualquer, e {b} com 'outro'. " +
            $"Liste os .md do diretorio (use o glob) e leia {a}; responda SOMENTE com a primeira linha de {a}.",
            "DEVPIPE-OK");
        TestContext.Check(File.Exists(a) && File.Exists(b), "devpipe: arquivos a.md/b.md nao existem");
        return Task.CompletedTask;
    }

    // Cenario CRITICO: ciencia COM tool (shell + python), nao texto puro.
    public static Task ScienceTools()
    {
        string dir = Fresh("sci");
        Run(
            "Rode exatamente `python3 -c \"import statistics as s; print(round(s.mean([2,4,4,4,5,5,7,9]),6))\"` " +
            "e me diga o resultado.",
            "5");
        return Task.CompletedTask;
    }

    // Cenario CRITICO: missao C# real via opencode (dotnet new -> write -> dotnet run).
    public static Task MissionTools()
    {
        string dir = Path.Combine(Root, "mis");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Run(
            $"Crie um projeto console .NET em {dir} com o nome mis, escreva em {dir}/Program.cs o calculo de " +
            "Simpson de sin em [0,pi] com n=100, imprimindo com 6 casas (ToString(\"F6\", CultureInfo.InvariantCulture)); " +
            $"rode `dotnet run --project {dir}` e responda SOMENTE com o numero impresso.",
            "2.000000");
        return Task.CompletedTask;
    }
}
