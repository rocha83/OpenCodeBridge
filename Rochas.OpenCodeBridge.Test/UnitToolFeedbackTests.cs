using Rochas.OpenCodeBridge.Core.Tools;

namespace Rochas.OpenCodeBridge.Test;

// Feedback das tools que ensina o passo certo no momento do erro medido:
//   - o shell diz ONDE a chamada rodou (o modelo assumia que o `cd` persistia
//     e gravava `a.txt` na raiz achando que estava na subpasta);
//   - o git devolve a forma certa quando recebe cadeia (`add . && commit`
//     chegava ao git como pathspec e o modelo abandonava o segundo passo).
internal static class UnitToolFeedbackTests
{
    static string NewDir(string prefix)
    {
        string d = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    public static Task ShellReportsCwd()
    {
        string ws = NewDir("tctx-cwd-");
        try
        {
            var ctx = new ToolContext { WorkspaceRoot = ws, Role = "build", TimeoutSeconds = 60 };
            var shell = new ShellToolHandler();

            var chain = shell.Handle("""{"command":"mkdir -p sub && cd sub"}""", ctx);
            TestContext.Check(chain.Success, "cadeia mkdir && cd roda (err: " + chain.Error + ")");
            TestContext.Check(chain.Output.Contains("[shell] cwd desta chamada: sub", StringComparison.Ordinal),
                "shell devolve o cwd efetivo da chamada (out: " + chain.Output + ")");
            TestContext.Check(chain.Output.Contains("a proxima chamada comeca na raiz do workspace", StringComparison.Ordinal),
                "shell avisa que a proxima chamada comeca na raiz");
            TestContext.Check(chain.Output.Contains("path \"sub/arquivo\"", StringComparison.Ordinal),
                "shell ensina o path do arquivo da pasta");

            var single = shell.Handle("""{"command":"pwd"}""", ctx);
            TestContext.Check(single.Output.Contains("[shell] cwd desta chamada: .", StringComparison.Ordinal),
                "chamada sem cd reporta a raiz do workspace (out: " + single.Output + ")");

            var fail = shell.Handle("""{"command":"cd sub && ls pasta_que_nao_existe"}""", ctx);
            TestContext.Check(!fail.Success && fail.Error.Contains("[shell] cwd desta chamada: sub", StringComparison.Ordinal),
                "falha de shell tambem diz onde rodou (err: " + fail.Error + ")");
        }
        finally { try { Directory.Delete(ws, true); } catch { } }
        return Task.CompletedTask;
    }

    public static Task GitChainTeaching()
    {
        string ws = NewDir("tctx-chain-");
        try
        {
            var ctx = new ToolContext { WorkspaceRoot = ws, Role = "build", TimeoutSeconds = 60 };
            var git = new GitToolHandler();

            var chain = git.Handle("""{"command":"add . && commit -m \"x\"","path":"."}""", ctx);
            TestContext.Check(!chain.Success && chain.Error.Contains("uma suboperação por chamada", StringComparison.Ordinal),
                "git com cadeia devolve a forma certa (err: " + chain.Error + ")");
            TestContext.Check(chain.Error.Contains("git commit -m", StringComparison.Ordinal),
                "git com cadeia ensina o commit isolado");

            // `;` dentro de aspas e' mensagem, nao cadeia: o passo segue adiante.
            var semi = git.Handle("""{"command":"commit -m \"a; b\"","path":"."}""", ctx);
            TestContext.Check(!semi.Error.Contains("uma suboperação por chamada", StringComparison.Ordinal),
                "ponto e virgula dentro da mensagem nao conta como cadeia (err: " + semi.Error + ")");
        }
        finally { try { Directory.Delete(ws, true); } catch { } }
        return Task.CompletedTask;
    }
}
