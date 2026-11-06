using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Test;

// E-tools-full como DIALOGO em duas fases, na ordem do enunciado humano:
//   FASE 1 — questionario: 8 perguntas, UMA por mensagem, esperando a resposta
//   (prosa, sem tools) antes da proxima:
//     1) voce sabe utilizar git?
//     2) sabe adicionar arquivos?
//     3) como faz para efetuar commit ja' com a mensagem?
//     4) como voce criaria uma subpasta a partir da pasta atual?
//     5) como voce acessaria a internet em busca de dados como meteorologia?
//     6) como verificaria a hora no Linux e no Windows?
//     7) como construiria um aplicativo console .NET C# hello world?
//     8) como compilaria um app .NET e faria compilacao, publicacao e deploy em Docker?
//   FASE 2 — depois do questionario, UMA mensagem em linguagem natural simples
//   manda fazer tudo em sequencia, terminando com o .NET compilado e
//   empacotado num zip (com prova de compilacao antes de zipar).
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

        /// <summary>Turno de pergunta: tools no schema, mas SEM call (tool_choice none).</summary>
        public async Task<string> Ask(string message)
        {
            Note("U", message);
            var input = Input();
            input.Add(new JsonObject { ["role"] = "user", ["content"] = message });
            var body = new JsonObject
            {
                ["model"] = TestContext.SuiteModel,
                ["input"] = input,
                // Sem `tools` o template do 4B despeja os tokens de tool call no
                // texto (`<|tool_call>...`) ou devolve vazio — medido 2026-11-06.
                // Com o schema presente e `tool_choice: none` ele responde prosa.
                ["tools"] = global::Rochas.OpenCodeBridge.Core.ToolCatalog.Responses(),
                ["tool_choice"] = "none",
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
        public async Task<string> Act(string message, int max = 16)
        {
            Note("U", message);
            _hist.Add(new JsonObject { ["role"] = "user", ["content"] = message });
            string text = "";
            int start = _ledger.Count;
            string prevSig = "";
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
                var sig = new StringBuilder();
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
                    sig.Append(name).Append(args).Append(';');
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
                // Repetir EXATAMENTE o mesmo conjunto da rodada anterior = tarefa
                // encerrada; sem isso o `required` faria o modelo encher rodadas.
                string cur = sig.ToString();
                if (cur.Length > 0 && cur == prevSig) break;
                prevSig = cur;
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
            Console.WriteLine($"  [toolsfull:qa] sabe {cap}? {(ok ? "SIM" : "NAO")} ({Compact(answer, 80)})");
        }
        bool Has(string t, string pattern) => Regex.IsMatch(t, pattern, RegexOptions.IgnoreCase);

        // ---------- FASE 1: questionario, uma pergunta por mensagem ----------
        string a1 = await dlg.Ask("Voce sabe utilizar git?");
        Know(0, "git", a1, Has(a1, @"git\s+init") && Has(a1, @"git\s+add") && Has(a1, @"git\s+commit"));

        string a2 = await dlg.Ask("Sabe adicionar arquivos ao controle de versao?");
        Know(1, "add", a2, Has(a2, @"git\s+add"));

        string a3 = await dlg.Ask("Como faz para efetuar commit ja' com a mensagem?");
        Know(2, "commit-m", a3, Has(a3, @"git\s+commit\s+[^\n]*-[a-z]*m"));

        string a4 = await dlg.Ask("Como voce criaria uma subpasta a partir da pasta atual?");
        Know(3, "subpasta", a4, Has(a4, @"mkdir"));

        string a5 = await dlg.Ask("Como voce acessaria a internet em busca de dados como meteorologia?");
        Know(4, "internet", a5, Has(a5, @"(curl|wget|fetch|webfetch|https?://|api)"));

        string a6 = await dlg.Ask("Como verificaria a hora utilizando o sistema operacional Linux e o Windows?");
        Know(5, "hora", a6, Has(a6, @"\bdate\b") && Has(a6, @"(Get-Date|time\s*/t|%time%|powershell|windows)"));

        string a7 = await dlg.Ask("Como construiria um aplicativo console .NET C# hello world?");
        Know(6, "console", a7, Has(a7, @"dotnet\s+new\s+console"));

        string a8 = await dlg.Ask("Como compilaria um app .NET e realizaria a compilacao, a publicacao e o deploy em container Docker?");
        Know(7, "publish", a8, Has(a8, @"dotnet\s+publish") && Has(a8, @"docker\s+build"));

        Console.WriteLine($"  [toolsfull:qa] conhecimento: {known.Count(k => k)}/8");

        // ---------- FASE 2: uma ordem em linguagem natural, tudo em sequencia ----------
        await dlg.Act(
            "Show. Agora faz tudo isso na pratica, nesta pasta, na ordem, um passo por chamada: " +
            "inicializa o git; cria o arquivo data.txt com a data de hoje do sistema e adiciona ele ao git; " +
            "faz o commit com a mensagem 'data de hoje' (se o git reclamar de identidade, configura o user.name e o user.email locais antes); " +
            "cria a subpasta dados; busca o clima de Sao Paulo na URL " +
            "https://api.open-meteo.com/v1/forecast?latitude=-23.55&longitude=-46.63&current=temperature_2m " +
            "e grava a temperatura em dados/clima.txt; " +
            "grava a data e a hora do sistema em dados/hora.txt; " +
            "cria o app console hello com dotnet new console, deixa o Program.cs imprimindo Hello World e roda ele; " +
            "compila e publica o hello; " +
            "e no fim junta o hello compilado num zip chamado hello.zip — garante que compilou antes de zipar, " +
            "e confere o zip com unzip -t e unzip -l.");

        // Aceite de tudo que a ordem pediu.
        Soft(Directory.Exists(Path.Combine(dir, ".git")), "1-git: repositorio git iniciado");
        string dataPath = Path.Combine(dir, "data.txt");
        Soft(File.Exists(dataPath) && ReadText(dataPath).Contains(hoje, StringComparison.Ordinal),
            $"2-data: data.txt com a data REAL ({hoje}) — conteudo='{Trecho(dataPath)}'");

        var (c1, log1) = GitLog(dir);
        Soft(c1.Length >= 1 && log1.Contains("data de hoje", StringComparison.Ordinal),
            $"3-commit: commit 'data de hoje' (log: {Compact(log1, 160)})");

        Soft(Directory.Exists(Path.Combine(dir, "dados")), "4-subpasta: 'dados' criada");

        string climaPath = Path.Combine(dir, "dados", "clima.txt");
        Soft(File.Exists(climaPath) && Regex.IsMatch(ReadText(climaPath), @"-?\d+([.,]\d+)?\s*(°|C\b|graus)", RegexOptions.IgnoreCase),
            $"5-clima: temperatura em dados/clima.txt (conteudo='{Trecho(climaPath)}')");

        string horaPath = Path.Combine(dir, "dados", "hora.txt");
        string hora = ReadText(horaPath);
        Soft(File.Exists(horaPath) && hora.Contains(hoje, StringComparison.Ordinal) && Regex.IsMatch(hora, @"\d{1,2}:\d{2}"),
            $"6-hora: data+hora reais em dados/hora.txt (conteudo='{Trecho(horaPath)}')");

        string proj = Path.Combine(dir, "hello");
        Soft(File.Exists(Path.Combine(proj, "hello.csproj")), "7-console: hello/hello.csproj criado");
        Soft(ReadText(Path.Combine(proj, "Program.cs")).Contains("Hello", StringComparison.Ordinal),
            "7-console: Program.cs com Hello World");
        if (File.Exists(Path.Combine(proj, "hello.csproj")))
        {
            var (rc, runOut) = Run("dotnet", dir, 180, "run", "--project", "hello");
            Soft(rc == 0 && runOut.Contains("Hello", StringComparison.Ordinal),
                $"7-console: `dotnet run` imprimiu Hello (rc={rc}, out='{Compact(runOut, 80)}')");
        }

        string pub = Path.Combine(dir, "publish");
        Soft(Directory.Exists(pub) && Directory.GetFiles(pub, "hello*.dll").Length > 0,
            $"8-publish: publicacao em publish/ ({string.Join(", ", Directory.Exists(pub) ? Directory.GetFiles(pub).Select(Path.GetFileName).Take(5) : Array.Empty<string>())})");

        string zip = Path.Combine(dir, "hello.zip");
        Soft(File.Exists(zip), $"9-zip: hello.zip criado ({Compact(dir, 60)})");
        if (File.Exists(zip))
        {
            var (lc, lout) = Run("unzip", dir, 30, "-l", "hello.zip");
            Soft(lc == 0 && lout.Contains(".dll", StringComparison.OrdinalIgnoreCase),
                $"9-zip: hello.zip contem o .NET compilado (.dll) — {(lc == 0 ? Compact(lout, 160) : "unzip -l falhou")}");
            var (tc, _) = Run("unzip", dir, 30, "-t", "hello.zip");
            Soft(tc == 0, "9-zip: unzip -t sem erro (pacote integro)");
        }

        File.AppendAllText(transcript, $"\nRESUMO: conhecimento {known.Count(k => k)}/8; execucao = {(fails.Count == 0 ? "VERDE" : fails.Count + " falha(s)")}\n");
        TestContext.Check(fails.Count == 0,
            $"[toolsfull] capacidades executadas com tools: {string.Join(" | ", fails)}");
        Console.WriteLine($"[toolsfull] dialogo concluido ({dlg.Calls.Count} calls) em {dir}");
    }
}
