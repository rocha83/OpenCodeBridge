using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Test;

// E-tools-full como DIALOGO, na ordem exata do enunciado humano:
//   1) voce sabe utilizar git?
//   2) sabe adicionar arquivos?
//   3) como faz para efetuar commit ja' com a mensagem?
//   4) como voce criaria uma subpasta a partir da pasta atual?
//   5) como voce acessaria a internet em busca de dados como meteorologia?
//   6) como verificaria a hora no Linux e no Windows?
//   7) como construiria um aplicativo console .NET C# hello world?
//   8) como compilaria um app .NET e faria compilacao, publicacao e deploy em Docker?
// Cada pergunta e' UMA mensagem; espera a resposta (prosa, sem tools) e so'
// entao manda a ordem correspondente (com tools, function_call obrigatorio),
// na MESMA conversa. A ordem reflete exatamente a pergunta anterior.
// Transcript completo em <workspace>/dialogo.txt.
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

        /// <summary>Turno de execucao: com tools + function_call obrigatorio.</summary>
        public async Task<string> Act(string message, int max = 4)
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
        string hoje = DateTime.Now.ToString("yyyy-MM-dd");

        void Soft(bool ok, string label)
        {
            Console.WriteLine($"  [toolsfull:exec] {(ok ? "PASS" : "FAIL")} {label}");
            if (!ok) fails.Add(label);
        }
        void Know(int i, string cap, string answer, bool ok)
        {
            known[i] = ok;
            Console.WriteLine($"  [toolsfull:qa] sabe {cap}? {(ok ? "SIM" : "NAO")} ({Compact(answer, 90)})");
        }
        bool Has(string t, string pattern) => Regex.IsMatch(t, pattern, RegexOptions.IgnoreCase);

        // 1) git -> inicializa repositorio
        string a1 = await dlg.Ask("Voce sabe utilizar git?");
        Know(0, "git", a1, Has(a1, @"git\s+init") && Has(a1, @"git\s+add") && Has(a1, @"git\s+commit"));
        await dlg.Act("Entao usa o git agora nesta pasta, um passo por chamada: inicializa um repositorio e confirma com `git status`.", 3);
        Soft(Directory.Exists(Path.Combine(dir, ".git")), "1-git: repositorio git iniciado na pasta atual");

        // 2) adicionar arquivos -> cria + git add
        string a2 = await dlg.Ask("Sabe adicionar arquivos ao controle de versao?");
        Know(1, "add", a2, Has(a2, @"git\s+add"));
        await dlg.Act("Agora adiciona um arquivo: cria `data.txt` com a data de hoje do sistema no formato AAAA-MM-DD " +
                      "(so' a data) e adiciona ao controle de versao com `git add`.", 3);
        string dataPath = Path.Combine(dir, "data.txt");
        Soft(File.Exists(dataPath) && ReadText(dataPath).Contains(hoje, StringComparison.Ordinal),
            $"2-add: data.txt com a data REAL ({hoje}) — conteudo='{Trecho(dataPath)}'");
        var (stCode, stOut) = Run("git", dir, 30, "-c", "safe.directory=" + dir, "diff", "--cached", "--name-only");
        Soft(stCode == 0 && stOut.Contains("data.txt", StringComparison.Ordinal),
            $"2-add: data.txt no stage do git (staged='{Compact(stOut, 80)}')");

        // 3) commit com mensagem -> commit + log
        string a3 = await dlg.Ask("Como faz para efetuar commit ja' com a mensagem?");
        Know(2, "commit-m", a3, Has(a3, @"git\s+commit\s+[^\n]*-[a-z]*m"));
        await dlg.Act("Agora efetua o commit com a mensagem `data de hoje` e confirma com `git log`.", 3);
        var (c1, log1) = GitLog(dir);
        Soft(c1.Length >= 1 && log1.Contains("data de hoje", StringComparison.Ordinal),
            $"3-commit: commit 'data de hoje' (log: {Compact(log1, 160)})");

        // 4) subpasta
        string a4 = await dlg.Ask("Como voce criaria uma subpasta a partir da pasta atual?");
        Know(3, "subpasta", a4, Has(a4, @"mkdir"));
        await dlg.Act("Agora cria a subpasta `dados` nesta pasta.", 3);
        Soft(Directory.Exists(Path.Combine(dir, "dados")), "4-subpasta: 'dados' criada");

        // 5) internet/meteorologia
        string a5 = await dlg.Ask("Como voce acessaria a internet em busca de dados como meteorologia?");
        Know(4, "internet", a5, Has(a5, @"(curl|wget|fetch|webfetch|https?://|api)"));
        await dlg.Act("Agora busca o clima atual de Sao Paulo na URL " +
                      "https://api.open-meteo.com/v1/forecast?latitude=-23.55&longitude=-46.63&current=temperature_2m " +
                      "e grava a temperatura em `dados/clima.txt`.", 3);
        string climaPath = Path.Combine(dir, "dados", "clima.txt");
        string clima = ReadText(climaPath);
        Soft(File.Exists(climaPath), "5-internet: dados/clima.txt criado");
        Soft(Regex.IsMatch(clima, @"-?\d+([.,]\d+)?\s*(°|C\b|graus)", RegexOptions.IgnoreCase),
            $"5-internet: temperatura no clima.txt (conteudo='{Trecho(climaPath)}')");

        // 6) hora Linux/Windows
        string a6 = await dlg.Ask("Como verificaria a hora utilizando o sistema operacional Linux e o Windows?");
        Know(5, "hora", a6, Has(a6, @"\bdate\b") && Has(a6, @"(Get-Date|time\s*/t|%time%|powershell|windows)"));
        await dlg.Act("Aqui estamos no Linux. Agora grava em `dados/hora.txt` a data e a hora do sistema.", 3);
        string horaPath = Path.Combine(dir, "dados", "hora.txt");
        string hora = ReadText(horaPath);
        Soft(File.Exists(horaPath) && hora.Contains(hoje, StringComparison.Ordinal),
            $"6-hora: dados/hora.txt com a data REAL ({hoje}) — conteudo='{Trecho(horaPath)}'");
        Soft(Regex.IsMatch(hora, @"\d{1,2}:\d{2}"), $"6-hora: hora HH:MM no arquivo ('{Trecho(horaPath)}')");

        // 7) console .NET hello world
        string a7 = await dlg.Ask("Como construiria um aplicativo console .NET C# hello world?");
        Know(6, "console", a7, Has(a7, @"dotnet\s+new\s+console"));
        await dlg.Act("Agora cria o app console `hello` com `dotnet new console -o hello`, deixa o `hello/Program.cs` " +
                      "imprimindo `Hello World` e roda com `dotnet run --project hello`.", 4);
        string proj = Path.Combine(dir, "hello");
        Soft(File.Exists(Path.Combine(proj, "hello.csproj")), "7-console: hello/hello.csproj criado");
        Soft(ReadText(Path.Combine(proj, "Program.cs")).Contains("Hello", StringComparison.Ordinal),
            "7-console: Program.cs com Hello World");
        if (File.Exists(Path.Combine(proj, "hello.csproj")))
        {
            var (rc, runOut) = Run("dotnet", dir, 180, "run", "--project", "hello");
            Soft(rc == 0 && runOut.Contains("Hello", StringComparison.Ordinal),
                $"7-console: `dotnet run` imprimiu Hello (rc={rc}, out='{Compact(runOut, 90)}')");
        }

        // 8) compilacao + publicacao + docker
        string a8 = await dlg.Ask("Como voce compilaria um app .NET e realizaria a compilacao, a publicacao e o deploy em container Docker?");
        Know(7, "publish", a8, Has(a8, @"dotnet\s+publish") && Has(a8, @"docker\s+build"));
        await dlg.Act("Agora compila (`dotnet build hello`), publica (`dotnet publish hello -c Release -o publish`) e cria " +
                      "`hello/Dockerfile` com `FROM mcr.microsoft.com/dotnet/runtime` e `COPY publish /app`.", 5);
        string pub = Path.Combine(dir, "publish");
        Soft(Directory.Exists(pub) && Directory.GetFiles(pub, "hello*.dll").Length > 0,
            $"8-publish: publicacao em publish/ ({string.Join(", ", Directory.Exists(pub) ? Directory.GetFiles(pub).Select(Path.GetFileName).Take(5) : Array.Empty<string>())})");
        string docker = ReadText(Path.Combine(proj, "Dockerfile"));
        Soft(docker.Contains("FROM", StringComparison.OrdinalIgnoreCase) && docker.Contains("dotnet", StringComparison.OrdinalIgnoreCase),
            $"8-docker: hello/Dockerfile com FROM dotnet (conteudo='{Compact(docker, 70)}')");
        var (dockCode, _) = Run("docker", dir, 15, "info");
        if (dockCode == 0)
        {
            var (bCode, bOut) = Run("docker", dir, 300, "build", "-t", "toolsfull-hello", "hello");
            Soft(bCode == 0, $"8-docker: imagem construida (rc={bCode}, out='{Compact(bOut, 90)}')");
        }
        else Console.WriteLine("  [toolsfull:exec] SKIP 8-docker: daemon docker indisponivel nesta maquina");

        Console.WriteLine($"  [toolsfull:qa] conhecimento: {known.Count(k => k)}/8");
        File.AppendAllText(transcript, $"\nRESUMO: conhecimento {known.Count(k => k)}/8; execucao DIALOGO = {(fails.Count == 0 ? "VERDE" : fails.Count + " falha(s)")}\n");
        TestContext.Check(fails.Count == 0,
            $"[toolsfull] capacidades executadas com tools: {string.Join(" | ", fails)}");
        Console.WriteLine($"[toolsfull] dialogo concluido ({dlg.Calls.Count} calls) em {dir}");
    }
}
