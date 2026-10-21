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
        Run("Use a tool shell para rodar 'date +%F' e diga a data.", "202");
        return Task.CompletedTask;
    }

    public static Task Write()
    {
        string dir = Fresh("write");
        string f = Path.Combine(dir, "ok.txt");
        Run($"Use a tool write para criar o arquivo {f} com o conteudo 'ok' e depois leia com read para confirmar.", "ok");
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
            $"Use tools, uma por turno. (1) write no arquivo {a} a 1a linha 'DEVPIPE-OK' e uma 2a linha qualquer; " +
            $"(2) write no arquivo {b} o conteudo 'outro'; " +
            $"(3) use a tool glob para listar os arquivos *.md em {dir}; " +
            $"(4) use a tool read para ler {a}. Responda SOMENTE com a primeira linha de {a}.",
            "DEVPIPE-OK");
        TestContext.Check(File.Exists(a) && File.Exists(b), "devpipe: arquivos a.md/b.md nao existem");
        return Task.CompletedTask;
    }

    // Cenario CRITICO: ciencia COM tool (shell + python), nao texto puro.
    public static Task ScienceTools()
    {
        string dir = Fresh("sci");
        Run(
            "Use a tool shell (uma chamada) para rodar exatamente este comando e depois diga o resultado: " +
            "python3 -c \"import statistics as s; print(round(s.mean([2,4,4,4,5,5,7,9]),6))\"",
            "5");
        return Task.CompletedTask;
    }

    // Cenario CRITICO: missao C# real via opencode (dotnet new -> write -> dotnet run).
    public static Task MissionTools()
    {
        string dir = Path.Combine(Root, "mis");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Run(
            "Use tools, uma por turno. (1) shell: crie o projeto com " +
            $"`dotnet new console -o {dir} -n mis`; (2) write no arquivo {dir}/Program.cs o programa em C# que " +
            "calcula Simpson de sin em [0,pi] com n=100 e imprime com 6 casas (ToString(\"F6\", CultureInfo.InvariantCulture)); " +
            $"(3) shell: rode `dotnet run --project {dir}`. Responda SOMENTE com o numero impresso.",
            "2.000000");
        return Task.CompletedTask;
    }
}
