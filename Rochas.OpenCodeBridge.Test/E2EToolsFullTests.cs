using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Test;

// E-tools-full como DIALOGO de verdade: uma mensagem por vez, espera a resposta
// e so' entao manda a proxima — todas na MESMA conversa (historico acumulado no
// /v1/responses). Nao e' questionario de 8 itens num turno so', nem prompt com
// lista de passos: cada ordem reage ao que o modelo acabou de responder, como
// num chat humano.
//   1) pergunta de conhecimento (SEM tools) -> resposta em prosa;
//   2) ordem de execucao (COM tools, function_call obrigatorio) -> artefato;
//   3) aceite do artefato; volta para 1 com a proxima pergunta.
// O transcript completo fica em <workspace>/dialogo.txt (vira exemplo do help
// quando a rodada fechar verde).
internal static class E2EToolsFullTests
{
    static (int Code, string Out) Run(string exe, string cwd, int secs, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new Exception("sem processo: " + exe);
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(secs * 1000)) { try { p.Kill(true); } catch { } throw new Exception("timeout " + exe); }
        return (p.ExitCode, so.Result + se.Result);
    }

    static string ReadText(string f)
    {
        try { return new FileInfo(f).Length <= 2_000_000 ? File.ReadAllText(f) : ""; }
        catch { return ""; }
    }

    static string Compact(string s, int max)
    {
        string t = (s ?? "").Replace("\r", "").Replace("\n", " ").Trim();
        return t.Length <= max ? t : t[..max] + "...";
    }

    static string Trecho(string f) => Compact(ReadText(f), 70);

    static (string[] Commits, string Log) GitLog(string repo)
    {
        string[] args = { "-c", "safe.directory=" + repo, "log", "--oneline" };
        var (code, outp) = Run("git", repo, 30, args);
        string log = code == 0 ? Run("git", repo, 30, "-c", "safe.directory=" + repo, "log", "--format=%s").Out : "";
        var commits = code == 0 ? outp.Split('\n', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
        return (commits, log);
    }

    /// <summary>
    /// Uma conversa com o modelo: historico no formato Responses. `Ask` = turno
    /// sem tools (resposta em prosa); `Act` = turno com tools e function_call
    /// obrigatorio, executando as calls e devolvendo o resultado ao modelo.
    /// </summary>
    sealed class Dialog
    {
        readonly JsonArray _hist = new();
        readonly string? _cwd;
        readonly List<string> _ledger = new();
        readonly string _log;

        public List<string> Calls => _ledger;

        public Dialog(string? cwd, string transcript)
        {
            _cwd = cwd;
            _log = transcript;
            _hist.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = "<env>\n  Working directory: " + cwd +
                              "\n  Is directory a git repo: no\n  Platform: linux\n</env>"
            });
        }

        public void Note(string who, string text)
        {
            File.AppendAllText(_log, $"{who}: {text}\n\n");
            Console.WriteLine($"  [dlg] {who}: {Compact(text, 300)}");
        }

        static string TextOf(JsonObject res)
        {
            var sb = new StringBuilder();
            if (res["output"] is JsonArray output)
                foreach (var item in output)
                    if (item?["type"]?.GetValue<string>() == "message")
                        foreach (var c in item?["content"]?.AsArray() ?? new JsonArray())
                            if (c?["type"]?.GetValue<string>() == "output_text")
                                sb.Append(c?["text"]?.GetValue<string>() ?? "");
            return sb.ToString();
        }

        JsonArray Input()
        {
            var arr = new JsonArray();
            foreach (var n in _hist) arr.Add(n?.DeepClone());
            return arr;
        }

        void Append(JsonObject res)
        {
            if (res["output"] is JsonArray output)
                foreach (var item in output) _hist.Add(item?.DeepClone());
        }

        static List<JsonObject> CallItems(JsonObject res) =>
            (res["output"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
                .Where(i => i["type"]?.GetValue<string>() == "function_call").ToList();

        /// <summary>Turno de pergunta: sem tools, o modelo responde em prosa.</summary>
        public async Task<string> Ask(string message)
        {
            Note("U", message);
            var input = Input();
            input.Add(new JsonObject { ["role"] = "user", ["content"] = message });
            var body = new JsonObject
            {
                ["model"] = TestContext.SuiteModel,
                ["input"] = input,
                ["stream"] = false
            };
            var res = await TestContext.PostObj(TestContext.Bridge + "/v1/responses", body);
            _hist.Add(new JsonObject { ["role"] = "user", ["content"] = message });
            Append(res);
            string text = TextOf(res);
            Note("A", text);
            return text;
        }

        /// <summary>Turno de execucao: with tools + function_call obrigatorio.</summary>
        public async Task<string> Act(string message, int max = 8)
        {
            Note("U", message);
            _hist.Add(new JsonObject { ["role"] = "user", ["content"] = message });
            string text = "";
            int start = _ledger.Count;
            for (int step = 0; step < max; step++)
            {
                var body = new JsonObject
                {
                    ["model"] = TestContext.SuiteModel,
                    ["input"] = Input(),
                    ["tools"] = global::Rochas.OpenCodeBridge.Core.ToolCatalog.Responses(),
                    ["stream"] = false
                };
                if (step < max - 1) body["tool_choice"] = "required";
                var res = await TestContext.PostObj(TestContext.Bridge + "/v1/responses", body);
                text = TextOf(res);
                int callsInTurn = 0;
                // Ordem do historico = ordem do output (call -> output -> call...),
                // igual ao driver: o proximo request precisa ver o resultado
                // imediatamente depois da call que o produziu.
                foreach (var item in res["output"] as JsonArray ?? new JsonArray())
                {
                    _hist.Add(item?.DeepClone());
                    if (item?["type"]?.GetValue<string>() != "function_call") continue;
                    callsInTurn++;
                    string name = item["name"]?.GetValue<string>() ?? "";
                    string args = item["arguments"]?.GetValue<string>() ?? "{}";
                    string callId = item["call_id"]?.GetValue<string>() ?? item["id"]?.GetValue<string>() ?? "";
                    JsonNode? parsed = null;
                    try { parsed = JsonNode.Parse(args); } catch { }
                    var exec = await TestContext.ExecuteTool(name, parsed, _cwd);
                    bool ok = exec["ok"]?.GetValue<bool>() == true;
                    _ledger.Add($"{name}({args}) -> {(ok ? "ok" : "fail")}");
                    string outText = global::Rochas.OpenCodeBridge.Core.Tools.BridgeToolExecutor.FormatResultForHistory(
                        new global::Rochas.OpenCodeBridge.Core.Tools.ToolResult
                        {
                            Success = ok,
                            Output = exec["output"]?.GetValue<string>() ?? "",
                            Error = exec["error"]?.GetValue<string>() ?? "falha"
                        });
                    _hist.Add(new JsonObject
                    {
                        ["type"] = "function_call_output",
                        ["call_id"] = callId,
                        ["output"] = outText
                    });
                }
                if (callsInTurn == 0) break;
            }
            var turn = _ledger.Skip(start).ToList();
            Note("A(tools)", (turn.Count == 0 ? "(nenhuma call) " : string.Join(" ; ", turn)) +
                              (text.Trim().Length > 0 ? " | " + text : ""));
            return text;
        }
    }

    public static async Task FullTools()
    {
        string dir = Path.Combine("/tmp/opencode/toolsfull-agent", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);
        string transcript = Path.Combine(dir, "dialogo.txt");
        File.WriteAllText(transcript, "DIALOGO E-tools-full (" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + ")\n\n");
        var dlg = new Dialog(dir, transcript);
        var fails = new List<string>();
        var known = new bool[8];

        void Soft(bool ok, string label)
        {
            Console.WriteLine($"  [toolsfull:exec] {(ok ? "PASS" : "FAIL")} {label}");
            if (!ok) fails.Add(label);
        }
        void Know(int i, string cap, string answer, bool ok)
        {
            known[i] = ok;
            Console.WriteLine($"  [toolsfull:qa] sabe {cap}? {(ok ? "SIM" : "NAO")}");
        }
        bool Has(string t, string pattern) => Regex.IsMatch(t, pattern, RegexOptions.IgnoreCase);

        // ---- 1) git: pergunta -> resposta -> ordem de execucao ----
        string a1 = await dlg.Ask("Voce sabe utilizar git? Se sim, me diz quais comandos usa, na ordem.");
        Know(0, "git", a1, Has(a1, @"git\s+init") && Has(a1, @"git\s+add") && Has(a1, @"git\s+commit"));

        string hoje = DateTime.Now.ToString("yyyy-MM-dd");
        await dlg.Act("Otimo. Entao faz agora nesta pasta, um passo por chamada: cria `data.txt` com a data de hoje " +
                      "do sistema (AAAA-MM-DD, so' a data), `git init` nesta pasta, `git add .`, commit com a mensagem " +
                      "`data de hoje` e confirma com `git log`.");
        Soft(Directory.Exists(Path.Combine(dir, ".git")), "git: repositorio iniciado na pasta atual");
        string dataBody = ReadText(Path.Combine(dir, "data.txt"));
        Soft(File.Exists(Path.Combine(dir, "data.txt")) && dataBody.Contains(hoje, StringComparison.Ordinal),
            $"git: data.txt com a data REAL do sistema ({hoje}) — conteudo='{Trecho(Path.Combine(dir, "data.txt"))}'");
        var (c1, log1) = GitLog(dir);
        Soft(c1.Length >= 1 && log1.Contains("data de hoje", StringComparison.Ordinal),
            $"git: commit 'data de hoje' (log: {Compact(log1, 160)})");

        // ---- 2) subpasta ----
        string a2 = await dlg.Ask("Como voce criaria uma subpasta a partir da pasta atual?");
        Know(3, "subpasta", a2, Has(a2, @"mkdir"));

        await dlg.Act("Faz agora: cria a subpasta `dados` e grava em `dados/hora.txt` a data e a hora do sistema.");
        string horaPath = Path.Combine(dir, "dados", "hora.txt");
        Soft(Directory.Exists(Path.Combine(dir, "dados")), "subpasta: 'dados' criada");
        Soft(File.Exists(horaPath) && ReadText(horaPath).Contains(hoje, StringComparison.Ordinal),
            $"subpasta: dados/hora.txt com a data REAL ({hoje}) — conteudo='{Trecho(horaPath)}'");

        // ---- 3) internet/meteorologia ----
        string a3 = await dlg.Ask("Como voce acessaria a internet para buscar dados de meteorologia?");
        Know(4, "internet", a3, Has(a3, @"(curl|wget|fetch|webfetch|https?://|api)"));

        await dlg.Act("Entao busca agora o clima de Sao Paulo na URL " +
                      "https://api.open-meteo.com/v1/forecast?latitude=-23.55&longitude=-46.63&current=temperature_2m , " +
                      "grava a temperatura em `dados/clima.txt` e commita com a mensagem `Added sao paulo weather`.");
        string climaPath = Path.Combine(dir, "dados", "clima.txt");
        string clima = ReadText(climaPath);
        Soft(File.Exists(climaPath), "internet: dados/clima.txt criado");
        Soft(Regex.IsMatch(clima, @"-?\d+([.,]\d+)?\s*(°|C\b|graus)", RegexOptions.IgnoreCase),
            $"internet: temperatura real no clima.txt (conteudo='{Trecho(climaPath)}')");
        var (c2, log2) = GitLog(dir);
        Soft(c2.Length >= 2 && log2.Contains("Added sao paulo weather", StringComparison.Ordinal),
            $"internet: 2o commit 'Added sao paulo weather' (log: {Compact(log2, 200)})");

        // ---- 4) hora Linux/Windows (conhecimento) ----
        string a4 = await dlg.Ask("E a hora: como voce verificaria no Linux e no Windows?");
        Know(5, "hora", a4, Has(a4, @"\bdate\b") && Has(a4, @"(Get-Date|time\s*/t|%time%|powershell|windows)"));

        // ---- 5) console .NET ----
        string a5 = await dlg.Ask("Como voce construiria um aplicativo console .NET C# hello world?");
        Know(6, "console", a5, Has(a5, @"dotnet\s+new\s+console"));

        await dlg.Act("Faz agora: `dotnet new console -o hello`, deixa `hello/Program.cs` imprimindo `Hello World` e roda com `dotnet run --project hello`.");
        string proj = Path.Combine(dir, "hello");
        Soft(File.Exists(Path.Combine(proj, "hello.csproj")), "dotnet: hello/hello.csproj criado");
        Soft(ReadText(Path.Combine(proj, "Program.cs")).Contains("Hello", StringComparison.Ordinal),
            "dotnet: Program.cs com Hello World");
        if (File.Exists(Path.Combine(proj, "hello.csproj")))
        {
            var (rc, runOut) = Run("dotnet", dir, 180, "run", "--project", "hello");
            Soft(rc == 0 && runOut.Contains("Hello", StringComparison.Ordinal),
                $"dotnet: `dotnet run` imprimiu Hello (rc={rc}, out='{Compact(runOut, 90)}')");
        }

        // ---- 6) publicacao + Docker ----
        string a6 = await dlg.Ask("Como voce compilaria e publicaria um app .NET e faria o deploy em container Docker?");
        Know(7, "publish", a6, Has(a6, @"dotnet\s+publish") && Has(a6, @"docker\s+build"));

        await dlg.Act("Faz agora: publica com `dotnet publish hello -c Release -o publish` e cria `hello/Dockerfile` " +
                      "com `FROM mcr.microsoft.com/dotnet/runtime` e `COPY publish /app`.");
        string pub = Path.Combine(dir, "publish");
        Soft(Directory.Exists(pub) && Directory.GetFiles(pub, "hello*.dll").Length > 0,
            $"dotnet: publicacao em publish/ ({string.Join(", ", Directory.Exists(pub) ? Directory.GetFiles(pub).Select(Path.GetFileName).Take(5) : Array.Empty<string>())})");
        string docker = ReadText(Path.Combine(dir, "hello", "Dockerfile"));
        Soft(docker.Contains("FROM", StringComparison.OrdinalIgnoreCase) && docker.Contains("dotnet", StringComparison.OrdinalIgnoreCase),
            $"docker: hello/Dockerfile com FROM dotnet (conteudo='{Compact(docker, 70)}')");

        // ---- 7) add (conhecimento, sem ordem propria: ja' exercitado no git) ----
        string a7 = await dlg.Ask("E como se adiciona arquivos ao controle de versao?");
        Know(1, "add", a7, Has(a7, @"git\s+add"));

        // ---- 8) commit com mensagem (conhecimento, ja' exercitado) ----
        string a8 = await dlg.Ask("E como se faz um commit ja' com a mensagem?");
        Know(2, "commit-m", a8, Has(a8, @"git\s+commit\s+[^\n]*-[a-z]*m"));

        Console.WriteLine($"  [toolsfull:qa] conhecimento: {known.Count(k => k)}/8");
        File.AppendAllText(transcript, $"\nRESUMO: conhecimento {known.Count(k => k)}/8; execucao {(fails.Count == 0 ? "4/4" : (4 - Math.Min(4, fails.Count)) + "/4")}\n");
        TestContext.Check(fails.Count == 0,
            $"[toolsfull] capacidades executadas com tools: {string.Join(" | ", fails)}");
        Console.WriteLine($"[toolsfull] dialogo concluido ({dlg.Calls.Count} calls) em {dir}");
    }
}
