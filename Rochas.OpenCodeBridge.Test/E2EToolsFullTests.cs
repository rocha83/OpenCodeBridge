using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Rochas.OpenCodeBridge.Test;

// E-tools-full: UMA mensagem em linguagem natural simples, sem questionario
// previo. O enunciado manda fazer tudo em sequencia, um passo por chamada
// (git init/add/commit, data.txt, dados/clima.txt, dados/hora.txt, console
// hello com dotnet new/run, publish, zip com prova de compilacao), e o aceite
// verifica o efeito real no disco. Custo alvo: ~1 min (so as tool calls).
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

        /// <summary>Turno de pergunta: sem tools no request, resposta em prosa.</summary>
        public async Task<string> Ask(string message)
        {
            Note("U", message);
            // Sem `tools` o schema nao entra; a frase abaixo pede prosa explicita.
            // Com tool_choice "none" o 4B devolvia 1-50 tokens (quase vazio) —
            // medido 2026-11-06; com a instrucao em texto ele responde.
            string ask = message + "\n\nResponda-me aqui em TEXTO.";
            var input = Input();
            input.Add(new JsonObject { ["role"] = "user", ["content"] = ask });
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
        public async Task<string> Act(string message, int max = 16, bool oneShot = false)
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
                if (oneShot || step < max - 1) body["tool_choice"] = "required";
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
                if (oneShot) break;
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
        string hoje = DateTime.Now.ToString("yyyy-MM-dd");

        void Soft(bool ok, string label)
        {
            Console.WriteLine($"  [toolsfull:exec] {(ok ? "PASS" : "FAIL")} {label}");
            if (!ok) fails.Add(label);
        }
        // ---------- enunciado unico em linguagem natural, tudo em sequencia ----------
        await dlg.Act(
            "Show. Agora faz tudo isso na pratica, nesta pasta, na ordem, um passo por chamada: " +
            "inicializa o git; configura a identidade local (user.name Teste e user.email teste@local); " +
            "cria o arquivo data.txt com a data de hoje do sistema e adiciona ele ao git; " +
            "faz o commit com a mensagem 'data de hoje'; " +
            "cria a subpasta dados; busca o clima de Sao Paulo na URL " +
            "https://api.open-meteo.com/v1/forecast?latitude=-23.55&longitude=-46.63&current=temperature_2m " +
            "e grava SO a temperatura em graus Celsius, no formato 24.3 C, em dados/clima.txt; " +
            "grava a data e a hora do sistema em dados/hora.txt; " +
            "cria o app console com dotnet new console -o hello, deixa hello/Program.cs completo e valido " +
            "imprimindo Hello World e roda com dotnet run --project hello; " +
            "publica com dotnet publish hello -c Release -o publish; " +
            "e no fim junta o hello compilado num zip hello.zip — garante que compilou antes de zipar, " +
            "e confere o zip com unzip -t e unzip -l. " +
            "Em cada arquivo grava SO o conteudo pedido: nunca cole saida de comando, bloco de codigo " +
            "ou mensagem de erro dentro do arquivo.", max: 40);

        // Aceite de tudo que a ordem pediu.
        Soft(Directory.Exists(Path.Combine(dir, ".git")), "1-git: repositorio git iniciado");
        string dataPath = Path.Combine(dir, "data.txt");
        string dataBody = ReadText(dataPath);
        Soft(File.Exists(dataPath) && dataBody.Contains(hoje, StringComparison.Ordinal)
             && !dataBody.Contains("[stderr]", StringComparison.Ordinal),
            $"2-data: data.txt com a data REAL e sem lixo ({hoje}) — conteudo='{Trecho(dataPath)}'");

        var (c1, log1) = GitLog(dir);
        Soft(c1.Length >= 1 && log1.Contains("data de hoje", StringComparison.Ordinal),
            $"3-commit: commit 'data de hoje' (log: {Compact(log1, 160)})");

        Soft(Directory.Exists(Path.Combine(dir, "dados")), "4-subpasta: 'dados' criada");

        string climaPath = Path.Combine(dir, "dados", "clima.txt");
        string clima = ReadText(climaPath);
        Soft(File.Exists(climaPath) && !clima.TrimStart().StartsWith("{", StringComparison.Ordinal)
             && !clima.Contains("[stderr]", StringComparison.Ordinal)
             && Regex.IsMatch(clima, @"-?\d+([.,]\d+)?\s*(°|C\b|graus)", RegexOptions.IgnoreCase),
            $"5-clima: so a temperatura em dados/clima.txt (conteudo='{Trecho(climaPath)}')");

        string horaPath = Path.Combine(dir, "dados", "hora.txt");
        string hora = ReadText(horaPath);
        Soft(File.Exists(horaPath) && hora.Contains(hoje, StringComparison.Ordinal)
             && Regex.IsMatch(hora, @"\d{1,2}:\d{2}")
             && !hora.Contains("[stderr]", StringComparison.Ordinal),
            $"6-hora: data+hora reais e sem lixo em dados/hora.txt (conteudo='{Trecho(horaPath)}')");

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

        File.AppendAllText(transcript, $"\nRESUMO: execucao = {(fails.Count == 0 ? "VERDE" : fails.Count + " falha(s)")}\n");
        TestContext.Check(fails.Count == 0,
            $"[toolsfull] capacidades executadas com tools: {string.Join(" | ", fails)}");
        Console.WriteLine($"[toolsfull] dialogo concluido ({dlg.Calls.Count} calls) em {dir}");
    }
}
