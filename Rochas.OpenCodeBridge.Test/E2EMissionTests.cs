// Missao tools E2E (C# + F# + git + zip) na suite: modelo constroi, suite executa e confere numeros.

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

internal static class E2EMissionTests
{
    static async Task<string> RunShell(string ws, string model, string command)
    {
        var r = await TestContext.ExecuteTool("shell", new JsonObject { ["command"] = command }, ws, model);
        return r["ok"]?.GetValue<bool>() == true
            ? (r["output"]?.GetValue<string>() ?? "")
            : "ERRO: " + (r["error"]?.GetValue<string>() ?? "falha");
    }

    static async Task<JsonObject> Chat(string bridge, string model, JsonArray messages, JsonArray? tools)
    {
        var body = new JsonObject { ["model"] = model, ["messages"] = messages };
        if (tools is not null) body["tools"] = tools;
        return await TestContext.PostObj(bridge + "/v1/chat/completions", body);
    }

    /// <summary>Contrato inteiro do E-mission-tools (a saida esperada, na ordem).</summary>
    static readonly long[] MissionExpected = { 3628800, 21, 15 };

    /// <summary>Linhas da saida que sao UM inteiro puro, na ordem em que sairam.</summary>
    /// <param name="text">Saida do programa.</param>
    /// <returns>Inteiros das linhas puras.</returns>
    static List<long> IntLines(string text)
    {
        var list = new List<long>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            string l = raw.Trim().Trim('\r');
            if (System.Text.RegularExpressions.Regex.IsMatch(l, @"^-?\d+$")
                && long.TryParse(l, System.Globalization.NumberStyles.Integer,
                       System.Globalization.CultureInfo.InvariantCulture, out var v))
                list.Add(v);
        }
        return list;
    }

    /// <summary>10! = 3628800, mdc(1071,462) = 21 e primos &lt;= 50 = 15, NA ORDEM.</summary>
    /// <param name="text">Saida do programa.</param>
    /// <returns>true quando as tres primeiras linhas inteiras batem.</returns>
    static bool MissionIntsOk(string text)
    {
        var nums = IntLines(text);
        return nums.Count >= 3
            && nums[0] == MissionExpected[0]
            && nums[1] == MissionExpected[1]
            && nums[2] == MissionExpected[2];
    }

    // Hardcode: a meta como literal no fonte (com ou sem aspas). Ignora comentarios
    // para nao acusar a meta copiada do enunciado.
    static bool HasHardcodedResult(string src)
    {
        string code = string.Join("\n", src.Split('\n').Select(l =>
        {
            int i = l.IndexOf("//", StringComparison.Ordinal);
            return i >= 0 ? l[..i] : l;
        }));
        return System.Text.RegularExpressions.Regex.IsMatch(code, @"(?<!\d)3628800(?!\d)")
            || System.Text.RegularExpressions.Regex.IsMatch(code, "\"21\"")
            || System.Text.RegularExpressions.Regex.IsMatch(code, "\"15\"");
    }

    public static async Task MissionTools()
    {
        string bridge = TestContext.Bridge;
        string model = TestContext.SuiteModel;
        if (!model.EndsWith("-build")) model += "-build";
        string ws = TestContext.FreshWorkspace("e2e-mission");
        // Scaffold dos projetos e' do harness (template `dotnet new`, estilo Visual Studio):
        // o modelo so escreve o fonte e roda; elimina a variabilidade do `dotnet new` no modelo.
        TestContext.ScaffoldProject("mcs", Path.Combine(ws, "mcs"));
        TestContext.ScaffoldProject("mfs", Path.Combine(ws, "mfs"), "F#");

        ResetCsScaffold();
        const string sysBase = "Voce e engenheiro senior C# e F#. Responda SOMENTE com tool calls, sem prosa.";
        var sys = new JsonObject { ["role"] = "system", ["content"] = sysBase };
        var history = new JsonArray(sys);
        history.Add(new JsonObject { ["role"] = "user", ["content"] =
            $"Em {ws}/mcs existe o projeto mcs.csproj. Escreva Program.cs para calcular e imprimir, um valor por linha e sem rotulos, tres numeros inteiros: o fatorial de 10; o maximo divisor comum de 1071 e 462; e quantos numeros primos existem de 1 a 50. " +
            $"O programa tem que compilar e rodar com `dotnet run --project {ws}/mcs/mcs.csproj` sem erro e imprimir exatamente esses tres numeros, nessa ordem (calcule, nao copie os numeros). " +
            $"Conforme as instrucoes previas da sessao (main harness e tails), use as tools no formato entregue, uma acao por vez." });

        string final = "";
        string logPath = Path.Combine(ws, "_mission.log");
        void LogT(string s) { try { File.AppendAllText(logPath, s + "\n"); } catch { } }
        // O modelo as vezes cria mfs.csproj/Program.cs no diretorio F# e o
        // `dotnet run --project mfs` fica ambiguo. O scaffold e mfs.fsproj;
        // limpa so os artefatos estranhos, preservando Program.fs.
        void ResetFsScaffold()
        {
            string dir = Path.Combine(ws, "mfs");
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "*.csproj")) { try { File.Delete(f); } catch { } }
            string pc = Path.Combine(dir, "Program.cs");
            if (File.Exists(pc)) { try { File.Delete(pc); } catch { } }
            foreach (var d in new[] { "bin", "obj" })
            {
                string p = Path.Combine(dir, d);
                if (Directory.Exists(p)) { try { Directory.Delete(p, true); } catch { } }
            }
        }
        // Mesmo problema no C#: o modelo cria Program.csproj ao lado do mcs.csproj.
        // O scaffold e mcs.csproj; limpa os csproj extras e bin/obj, preserva Program.cs.
        void ResetCsScaffold()
        {
            string dir = Path.Combine(ws, "mcs");
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.GetFiles(dir, "*.csproj"))
                if (!Path.GetFileName(f).Equals("mcs.csproj", StringComparison.OrdinalIgnoreCase))
                    { try { File.Delete(f); } catch { } }
            foreach (var d in new[] { "bin", "obj" })
            {
                string p = Path.Combine(dir, d);
                if (Directory.Exists(p)) { try { Directory.Delete(p, true); } catch { } }
            }
        }
        int csHistoryBefore = history.Count;
        string csRunOut = "";
        string csLastErr = "";
        for (int csAttempt = 1; csAttempt <= 3; csAttempt++)
        {
            ResetCsScaffold();
            if (csAttempt > 1)
            {
                while (history.Count > csHistoryBefore) history.RemoveAt(history.Count - 1);
                string errLine = ErrLine(csLastErr);
                bool compileErr = errLine.Contains("error", StringComparison.OrdinalIgnoreCase);
                LogT($"[C#] retry {csAttempt}: contexto C# resetado; {(compileErr ? "erro de build" : "saida errada")} repassado ({errLine})");
                string guidance = compileErr
                    ? $"Sem prosa: o build C# falhou: {errLine}. Corrija {ws}/mcs/Program.cs e rode `dotnet run --project {ws}/mcs/mcs.csproj`."
                    : $"Sem prosa: a saida C# nao bateu: {errLine}. Metas, uma por linha: 3628800 (fatorial de 10), 21 (mdc de 1071 e 462) e 15 (primos de 1 a 50). Corrija {ws}/mcs/Program.cs e rode `dotnet run --project {ws}/mcs/mcs.csproj`.";
                history.Add(new JsonObject { ["role"] = "user", ["content"] = guidance });
            }
            for (int turn = 0; turn < 14; turn++)
            {
                JsonObject r;
                try
                {
                    r = await Chat(bridge, model, (JsonArray)history.DeepClone(), global::Rochas.OpenCodeBridge.Core.ToolCatalog.Chat());
                }
                catch (Exception ex)
                {
                    LogT($"[C#] T{turn} HTTP falhou: {ex.Message.Split('\n')[0]}");
                    bool dropped = global::Rochas.OpenCodeBridge.Core.ToolCallRepair.Apply(history);
                    LogT($"[C#] T{turn} reparo: assistant quebrada podada={dropped}; nudge JSON anexado");
                    continue;
                }
                var msg = r["choices"]?[0]?["message"]?.AsObject() ?? throw new Exception("sem message");
                var calls = msg["tool_calls"]?.AsArray();
                if (calls is null || calls.Count == 0)
                {
                    final = msg["content"]?.GetValue<string>() ?? "";
                    LogT($"[C#] T{turn} SEM CALLS: {final[..Math.Min(200, final.Length)]}");
                    history.Add(new JsonObject { ["role"] = "assistant", ["content"] = final });
                    if (turn >= 2) break;
                    history.Add(new JsonObject { ["role"] = "user", ["content"] = "Sem prosa: emita a proxima tool call agora." });
                    continue;
                }
                var echo = new JsonArray();
                foreach (var tc in calls)
                {
                    var fn = tc?["function"]?["name"]?.GetValue<string>() ?? "";
                    JsonObject args;
                    try { args = JsonNode.Parse(tc?["function"]?["arguments"]?.GetValue<string>() ?? "{}")?.AsObject() ?? new JsonObject(); }
                    catch { args = new JsonObject(); }
                    string res = await ExecTool(ws, model, fn, args);
                    LogT($"[C#] T{turn} {fn} {args.ToJsonString()[..Math.Min(160, args.ToJsonString().Length)]} -> {res[..Math.Min(180, res.Length)]}");
                    echo.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = tc?["id"]?.GetValue<string>() ?? "", ["content"] = res });
                }
                var freshCalls = new JsonArray();
                foreach (var c in calls)
                    freshCalls.Add(new JsonObject { ["id"] = c?["id"]?.GetValue<string>() ?? "", ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = c?["function"]?["name"]?.GetValue<string>() ?? "",
                            ["arguments"] = c?["function"]?["arguments"]?.GetValue<string>() ?? "{}" } });
                history.Add(new JsonObject { ["role"] = "assistant", ["content"] = msg["content"]?.GetValue<string>() ?? "", ["tool_calls"] = freshCalls });
                foreach (var e in echo) history.Add((JsonObject)e!.DeepClone());
                // Sucesso atingido: para de editar (o modelo tende a quebrar o que ja funciona).
                // Predicado estrito (3628800/21/15 NA ORDEM), igual ao F#.
                if (echo.Any(e => MissionIntsOk(e?["content"]?.GetValue<string>() ?? ""))) break;
            }
            string csFile = Path.Combine(ws, "mcs", "Program.cs");
            string csSrc = File.Exists(csFile) ? File.ReadAllText(csFile) : "";
            ResetCsScaffold();
            string runOut = await RunShell(ws, model, "dotnet run --project mcs/mcs.csproj -c Release");
            LogT($"[C#] RUN -> {runOut[..Math.Min(220, runOut.Length)]}");
            csRunOut = runOut;
            csLastErr = runOut;
            bool csOk = csSrc.Length > 60 && !HasHardcodedResult(csSrc) && MissionIntsOk(runOut);
            if (csOk) break;
        }
        string csFileFinal = Path.Combine(ws, "mcs", "Program.cs");
        string csSrcFinal = File.Exists(csFileFinal) ? File.ReadAllText(csFileFinal) : "";
        TestContext.Check(csSrcFinal.Length > 60,
            "mcs/Program.cs com codigo real (nao template), veio: " + csSrcFinal[..Math.Min(100, csSrcFinal.Length)]);
        TestContext.Check(!HasHardcodedResult(csSrcFinal), "C# calcula (sem hardcode de 3628800/21/15 no fonte)");
        var csNums = IntLines(csRunOut);
        TestContext.Check(csNums.Count >= 3, "C# imprime 3 linhas inteiras, saiu: " + csRunOut[..Math.Min(160, csRunOut.Length)]);
        TestContext.Check(csNums.Count >= 1 && csNums[0] == 3628800, "C# fatorial de 10 = 3628800, saiu: " + csRunOut[..Math.Min(120, csRunOut.Length)]);
        TestContext.Check(csNums.Count >= 2 && csNums[1] == 21, "C# mdc(1071,462) = 21");
        TestContext.Check(csNums.Count >= 3 && csNums[2] == 15, "C# primos de 1 a 50 = 15");
        Console.WriteLine($"  [mission] C# ok:\n{csRunOut.Trim()}");

        // F# tem system proprio: FsMini so entra aqui (C# nao recebe regra F#).
        var histFs = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = sysBase + "\n\n" + global::Rochas.OpenCodeBridge.Core.HarnessTails.FsMini });

        // Fase 2: portabilidade para F# (mesma teoria inteira, sem ponto flutuante).
        // O E4B Q4 nao infere F# de forma estavel: o enunciado entrega a META
        // (3628800 / 21 / 15) e as restricoes de sintaxe, sem ditar o algoritmo.
        // O retry nao "ensina" sintaxe: reseta o contexto F# e repassa so a linha
        // real do erro, para o modelo corrigir com o proprio codigo em vista.
        int historyBeforeFs = histFs.Count;
        string fsOut = "";
        string fsSrc = "";
        string lastErr = "";
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            ResetFsScaffold();
            if (attempt > 1)
            {
                while (histFs.Count > historyBeforeFs) histFs.RemoveAt(histFs.Count - 1);
                string errLine = ErrLine(lastErr);
                bool compileErr = errLine.Contains("error", StringComparison.OrdinalIgnoreCase);
                LogT($"[F#] retry {attempt}: contexto F# resetado; {(compileErr ? "erro de build" : "saida errada")} repassado ({errLine})");
                string fsSrcNow = File.Exists(Path.Combine(ws, "mfs", "Program.fs"))
                    ? File.ReadAllText(Path.Combine(ws, "mfs", "Program.fs")) : "";
                string fsHint = compileErr
                    ? global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(fsSrcNow, errLine) : "";
                string guidance = compileErr
                    ? $"Sem prosa: o build do F# falhou: {errLine}. " + (fsHint.Length > 0 ? fsHint + " " : "")
                      + $"Corrija {ws}/mfs/Program.fs e rode `dotnet run --project {ws}/mfs/mfs.fsproj`."
                    : $"Sem prosa: a saida do F# nao bateu: {errLine}. Metas, uma por linha: 3628800 (fatorial de 10), 21 (mdc de 1071 e 462) e 15 (primos de 1 a 50). Corrija {ws}/mfs/Program.fs e rode `dotnet run --project {ws}/mfs/mfs.fsproj`.";
                histFs.Add(new JsonObject { ["role"] = "user", ["content"] = guidance });
            }
            histFs.Add(new JsonObject { ["role"] = "user", ["content"] =
                $"Em {ws}/mfs existe o projeto mfs.fsproj. Escreva Program.fs para calcular e imprimir, um valor por linha e sem rotulos, tres numeros inteiros: o fatorial de 10; o maximo divisor comum de 1071 e 462; e quantos numeros primos existem de 1 a 50. " +
                $"O programa tem que compilar e rodar com `dotnet run --project {ws}/mfs/mfs.fsproj` sem erro e imprimir exatamente esses tres numeros, nessa ordem (calcule, nao copie os numeros)." });
        // Orcamento de turnos: o aviso do salvage da bridge (ToolCallRepair.Nudge) NAO
        // e' turno do modelo — ele nao raciocinou, recebeu instrucao. Debitar isso
        // encurtava a janela no meio do reparo (medido no L3 de 2026-10-29: turno
        // gasto com `BRIDGE (edit)` logo apos o anti-repeat cortar a call). O guard
        // separado impede laco infinito.
        int fsUsed = 0, fsGuard = 0;
        while (fsUsed < 22 && fsGuard++ < 44)
        {
            int turn = fsUsed;
            JsonObject rf;
            try
            {
                rf = await Chat(bridge, model, (JsonArray)histFs.DeepClone(), global::Rochas.OpenCodeBridge.Core.ToolCatalog.Chat());
            }
            catch (Exception ex)
            {
                LogT($"[F#] T{turn} HTTP falhou: {ex.Message.Split('\n')[0]}");
                bool dropped = global::Rochas.OpenCodeBridge.Core.ToolCallRepair.Apply(histFs);
                LogT($"[F#] T{turn} reparo: assistant quebrada podada={dropped}; nudge JSON anexado");
                continue;
            }
            var mf = rf["choices"]?[0]?["message"]?.AsObject() ?? throw new Exception("sem message (F#)");
            var cf = mf["tool_calls"]?.AsArray();
            if (cf is null || cf.Count == 0)
            {
                string txtFs = mf["content"]?.GetValue<string>() ?? "";
                LogT($"[F#] T{turn} SEM CALLS: {txtFs[..Math.Min(200, txtFs.Length)]}");
                histFs.Add(new JsonObject { ["role"] = "assistant", ["content"] = txtFs });
                if (global::Rochas.OpenCodeBridge.Core.ToolCallRepair.IsSalvageNotice(txtFs))
                {
                    LogT($"[F#] T{turn} aviso de salvage (nao consome turno)");
                    continue;
                }
                fsUsed++;
                if (fsUsed > 4) break;
                histFs.Add(new JsonObject { ["role"] = "user", ["content"] = "Sem prosa: leia o erro de build (se houver) e emita ja a proxima tool call." });
                continue;
            }
            fsUsed++;   // turno com tool call consome o orcamento
            var ff = new JsonArray();
            foreach (var c in cf)
                ff.Add(new JsonObject { ["id"] = c?["id"]?.GetValue<string>() ?? "", ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = c?["function"]?["name"]?.GetValue<string>() ?? "",
                        ["arguments"] = c?["function"]?["arguments"]?.GetValue<string>() ?? "{}" } });
            histFs.Add(new JsonObject { ["role"] = "assistant", ["content"] = mf["content"]?.GetValue<string>() ?? "", ["tool_calls"] = ff });
            var fres = new List<string>();
            foreach (var c in cf)
            {
                JsonObject af;
                try { af = JsonNode.Parse(c?["function"]?["arguments"]?.GetValue<string>() ?? "{}")?.AsObject() ?? new JsonObject(); }
                catch { af = new JsonObject(); }
                string fnf = c?["function"]?["name"]?.GetValue<string>() ?? "";
                string resf = await ExecTool(ws, model, fnf, af);
                LogT($"[F#] T{turn} {fnf} {af.ToJsonString()[..Math.Min(160, af.ToJsonString().Length)]} -> {resf[..Math.Min(180, resf.Length)]}");
                // Reparo in-flow: erro de sintaxe volta com ERRO->CERTO concreto (mesma
                // mecanica do retry, no Core) para o modelo corrigir no MESMO turno.
                if (resf.Contains("error FS", StringComparison.Ordinal))
                {
                    string curFs = File.Exists(Path.Combine(ws, "mfs", "Program.fs"))
                        ? File.ReadAllText(Path.Combine(ws, "mfs", "Program.fs")) : "";
                    string inFlow = global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(curFs, resf);
                    if (inFlow.Length > 0)
                    {
                        resf += "\n\n" + inFlow;
                        LogT($"[F#] T{turn} reparo in-flow: {inFlow.Replace("\n", " | ")}");
                    }
                }
                fres.Add(resf);
                histFs.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = c?["id"]?.GetValue<string>() ?? "", ["content"] = resf });
            }
            // Sucesso atingido: para de editar (o modelo tende a quebrar o F# que ja compila).
            if (fres.Any(MissionIntsOk)) break;
        }
            string fsFile = Path.Combine(ws, "mfs", "Program.fs");
            fsSrc = File.Exists(fsFile) ? File.ReadAllText(fsFile) : "";
            ResetFsScaffold();
            fsOut = await RunShell(ws, model, "dotnet run --project mfs/mfs.fsproj -c Release");
            lastErr = fsOut;
            LogT($"[F#] RUN -> {fsOut[..Math.Min(220, fsOut.Length)]}");
            bool ok = fsSrc.Length > 60 && !HasHardcodedResult(fsSrc) && MissionIntsOk(fsOut);
            if (ok) break;
        }
        // Atestacao honesta: o system final do request F# (registrado pela bridge)
        // realmente carregou o FsMini? Roda ANTES dos asserts semanticos para o
        // log provar o fluxo mesmo quando o modelo erra a matematica/sintaxe.
        try
        {
            var att = await TestContext.GetObj(bridge + "/api/test/last-system");
            LogT($"[F#] atestacao system: {att.ToJsonString()}");
            TestContext.Check(att["fsmini"]?.GetValue<bool>() == true, "F# atestado: system final contem FsMini (modelo nao orfao)");
            TestContext.Check(att["int_regra"]?.GetValue<bool>() == true, "F# atestado: regra INTEIROS no system enviado");
            TestContext.Check(att["tail_scope"]?.GetValue<string>() == "all", "F# atestado: tail_scope=all na bridge");
            TestContext.Check(att["harness_tails"]?.GetValue<bool>() == true, "F# atestado: harness_tails=on na bridge");
        }
        catch (Exception ex) { TestContext.Check(false, "F# atestacao falhou: " + ex.Message); }
        TestContext.Check(!HasHardcodedResult(fsSrc), "F# calcula (sem hardcode de 3628800/21/15 no fonte)");
        var fsNums = IntLines(fsOut);
        TestContext.Check(fsNums.Count >= 3, "F# imprime 3 linhas inteiras, saiu: " + fsOut[..Math.Min(160, fsOut.Length)]);
        TestContext.Check(fsNums.Count >= 1 && fsNums[0] == 3628800, "F# fatorial de 10 = 3628800, saiu: " + fsOut[..Math.Min(160, fsOut.Length)]);
        TestContext.Check(fsNums.Count >= 2 && fsNums[1] == 21, "F# mdc(1071,462) = 21");
        TestContext.Check(fsNums.Count >= 3 && fsNums[2] == 15, "F# primos de 1 a 50 = 15");
        Console.WriteLine($"  [mission] F# ok:\n{fsOut.Trim()}");

        // Fase 3: versionamento com a tool git (command/path) + pacote via shell (zip).
        // O nome do pacote NAO e' contrato (decisao de 2026-10-29): o que vale e'
        // existir um .zip na raiz. Em 10:18 o modelo gerou `delivery.zip` e o
        // assert por nome reprovou uma entrega que estava pronta.
        histFs.Add(new JsonObject { ["role"] = "user", ["content"] =
            $"Conclua a entrega em duas partes, so' tool calls, sem texto. " +
            $"1) Versione com a tool git (command/path: init, config, add, commit) — vale a raiz {ws} ou a pasta do projeto {ws}/mfs. " +
            $"2) Gere um pacote .zip na raiz {ws} com a tool shell (zip -qr); o nome do arquivo nao importa." });
        for (int turn = 0; turn < 14; turn++)
        {
            var r2 = await Chat(bridge, model, (JsonArray)histFs.DeepClone(), global::Rochas.OpenCodeBridge.Core.ToolCatalog.Chat());
            var m2 = r2["choices"]?[0]?["message"]?.AsObject() ?? throw new Exception("sem message (git/zip)");
            var c2 = m2["tool_calls"]?.AsArray();
            if (c2 is null || c2.Count == 0)
            {
                // Modelo encerrou sem tool call: se a entrega ja existe, ok; senao cutuca so o que falta.
                if (Directory.GetFiles(ws, "*.zip").Length >= 1) break;
                LogT($"[git/zip] T{turn + 1} sem tool call e sem .zip; pedindo shell com zip");
                histFs.Add(new JsonObject { ["role"] = "user",
                    ["content"] =
                        $"Ainda falta o pacote: gere um arquivo .zip na raiz {ws} com a tool shell " +
                        $"(qualquer nome, ex.: `zip -qr pacote.zip .` a partir de {ws})." });
                continue;
            }
            var f2 = new JsonArray();
            foreach (var c in c2)
                f2.Add(new JsonObject { ["id"] = c?["id"]?.GetValue<string>() ?? "", ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = c?["function"]?["name"]?.GetValue<string>() ?? "",
                        ["arguments"] = c?["function"]?["arguments"]?.GetValue<string>() ?? "{}" } });
            histFs.Add(new JsonObject { ["role"] = "assistant", ["content"] = m2["content"]?.GetValue<string>() ?? "", ["tool_calls"] = f2 });
            foreach (var c in c2)
            {
                JsonObject a2;
                try { a2 = JsonNode.Parse(c?["function"]?["arguments"]?.GetValue<string>() ?? "{}")?.AsObject() ?? new JsonObject(); }
                catch { a2 = new JsonObject(); }
                string fnName = c?["function"]?["name"]?.GetValue<string>() ?? "";
                string res2 = await ExecTool(ws, model, fnName, a2);
                LogT($"[git/zip] T{turn + 1} {fnName} -> {res2.Split('\n')[0]}");
                histFs.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = c?["id"]?.GetValue<string>() ?? "", ["content"] = res2 });
            }
        }
        // Entrega robusta: o modelo pode versionar a raiz do workspace ou a pasta
        // do projeto (ele trabalhou em mfs/). Nao fixar onde — so que exista.
        bool gitOk = Directory.Exists(Path.Combine(ws, ".git")) || Directory.Exists(Path.Combine(ws, "mfs", ".git"));
        TestContext.Check(gitOk, "git init executado (raiz ou pasta do projeto)");
        bool headOk = File.Exists(Path.Combine(ws, ".git", "HEAD")) || File.Exists(Path.Combine(ws, "mfs", ".git", "HEAD"));
        TestContext.Check(headOk, "repo git criado");
        // Entrega e' o pacote, nao o nome: qualquer .zip na raiz (ou no projeto).
        string projectDir = Path.Combine(ws, "mfs");
        string[] zipFiles = Directory.GetFiles(ws, "*.zip")
            .Concat(Directory.Exists(projectDir) ? Directory.GetFiles(projectDir, "*.zip") : Array.Empty<string>())
            .ToArray();
        TestContext.Check(zipFiles.Length >= 1,
            "pacote .zip gerado (saiu: " + string.Join(", ", zipFiles.Select(Path.GetFileName)) + ")");
    }

    /// <summary>Linha de erro real do build (ignora `warning` e linhas de resumo).</summary>
    /// <param name="log">Saida do build/run.</param>
    /// <returns>Linha com `: error` (ou a primeira nao vazia).</returns>
    static string ErrLine(string log)
    {
        var lines = (log ?? "").Split('\n').Select(l => l.Trim()).ToList();
        return lines.FirstOrDefault(l => l.Contains(": error", StringComparison.OrdinalIgnoreCase)
                                         && !l.Contains("warning", StringComparison.OrdinalIgnoreCase))
            ?? lines.FirstOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)
                                         && !l.Contains("warning", StringComparison.OrdinalIgnoreCase))
            ?? lines.FirstOrDefault(l => l.Length > 0) ?? "";
    }

    /// <summary>
    /// Missao algoritmica E2E (C# + F#): Hanoi + crivo de Eratostenes, so inteiros.
    /// Step separado do E-mission-tools: falha de algoritmo nao se mistura com
    /// a entrega git/zip. Enunciado humano: meta + dica de formula, sem passo a passo.
    /// </summary>
    /// <summary>Missao de algoritmos em C#: metas e formato conferidos pela saida real.</summary>
    public static async Task MissionAlgoCs()
    {
        string bridge = TestContext.Bridge;
        string model = AlgoModel();
        string ws = TestContext.FreshWorkspace("e2e-mission-algo-cs");
        TestContext.ScaffoldProject("mcs", Path.Combine(ws, "mcs"));

        string csPrompt = AlgoPrompt(ws, "mcs");
        var (src, output) = await RunAlgoPhase(bridge, model, ws, "C#", "mcs/mcs.csproj", "Program.cs", new[] { csPrompt });
        TestContext.Check(!HasHardcodedAlgo(src), "C# calcula (sem hardcode das metas)");
        TestContext.Check(AlgoOk(output), "C# entrega as chaves esperadas, saiu: " + output[..Math.Min(240, output.Length)]);
        Console.WriteLine($"  [algo] C# ok:\n{output.Trim()}");
    }

    /// <summary>Missao de algoritmos em F#: mesmo aceite, fonte e projeto proprios.</summary>
    public static async Task MissionAlgoFs()
    {
        string bridge = TestContext.Bridge;
        string model = AlgoModel();
        string ws = TestContext.FreshWorkspace("e2e-mission-algo-fs");
        TestContext.ScaffoldProject("mfs", Path.Combine(ws, "mfs"), "F#");
        // O template do `dotnet new` para F# nao traz diretiva de modulo, e o modelo
        // gastava turnos no cabecalho (FS0010 `let` fora de posicao). O arquivo inicial
        // ja' compila — o pedido (algoritmo e metas) continua inteiro.
        File.WriteAllText(Path.Combine(ws, "mfs", "Program.fs"),
            "module Program\n\nopen System\n\n// resolva o pedido aqui\nprintfn \"ok\"\n");

        string[] fsStages = AlgoStages(ws, "mfs");
        var (src, output) = await RunAlgoPhase(bridge, model, ws, "F#", "mfs/mfs.fsproj", "Program.fs", fsStages);
        TestContext.Check(!HasHardcodedAlgo(src), "F# calcula (sem hardcode das metas)");
        TestContext.Check(AlgoOk(output), "F# entrega as chaves esperadas, saiu: " + output[..Math.Min(240, output.Length)]);
        Console.WriteLine($"  [algo] F# ok:\n{output.Trim()}");
    }

    static string AlgoModel()
    {
        string model = TestContext.SuiteModel;
        return model.EndsWith("-build") ? model : model + "-build";
    }

    /// <summary>Mesmo enunciado de missao para o projeto C# (mcs) e o F# (mfs).</summary>
    static string AlgoPrompt(string ws, string dir)
    {
        bool cs = dir == "mcs";
        string ext = cs ? "cs" : "fs";
        string proj = dir + (cs ? ".csproj" : ".fsproj");
        return "Preciso de um programa em " + ws + "/" + dir + "/Program." + ext + ", no projeto " + proj
            + " que ja existe (nao crie outro projeto), resolvendo duas coisas e imprimindo o resultado de um jeito simples para eu conferir, so com numeros inteiros.\n"
            + "1) A torre de Hanoi com 4 discos, da haste 1 para a 3. Me diga quantos movimentos foram necessarios e a sequencia completa no formato 1->2, 1->3, ...\n"
            + "2) Uma analise dos primos: quantos ha abaixo de 100 e abaixo de 1000, e quantos pares gemeos abaixo de 100. O crivo de Eratostenes e uma boa pedida.\n"
            + "Imprima os valores que o programa calculou; nao copie os numeros do enunciado.\n"
            + "Para eu comparar sem ambiguidade, termine com estas linhas, uma por linha e nessa ordem:\n"
            + "hanoi=15\nhanoi_seq=1->2,1->3,...\nprimes100=25\nprimes1000=168\ntwin100=8\n"
            + "Pode rodar `dotnet run --project " + ws + "/" + dir + "/" + proj + "` e ajustar ate bater.";
    }

    /// <summary>
    /// Enunciado da missao em duas etapas sobre o MESMO arquivo: Hanoi primeiro,
    /// primos depois. O aceite final (as cinco chaves) e' o mesmo; a segmentacao so'
    /// reduz o que o modelo precisa acertar de uma vez (regra de micro-runs).
    /// </summary>
    static string[] AlgoStages(string ws, string dir)
    {
        bool cs = dir == "mcs";
        string ext = cs ? "cs" : "fs";
        string proj = dir + (cs ? ".csproj" : ".fsproj");
        string run = "`dotnet run --project " + ws + "/" + dir + "/" + proj + "`";
        string head = "Preciso de um programa em " + ws + "/" + dir + "/Program." + ext + ", no projeto " + proj
            + " que ja existe (nao crie outro projeto), imprimindo o resultado de um jeito simples para eu conferir, so com numeros inteiros.\n";
        string hanoi = "1) A torre de Hanoi com 4 discos, da haste 1 para a 3. Imprima quantos movimentos foram necessarios em `hanoi=<n>` e a sequencia completa em `hanoi_seq=`, no formato 1->2,1->3,...\n"
            + "Rode " + run + " e ajuste ate imprimir as duas linhas com os valores que o programa calculou (nao copie numeros do enunciado).";
        string primes = "2) No MESMO programa, acrescente a analise dos primos: quantos ha abaixo de 100 (`primes100=`), abaixo de 1000 (`primes1000=`) e quantos pares gemeos abaixo de 100 (`twin100=`). O crivo de Eratostenes e uma boa pedida.\n"
            + "Mantenha as duas linhas de Hanoi ja' corretas e termine com estas cinco linhas, uma por linha e nessa ordem:\n"
            + "hanoi=<n>\nhanoi_seq=1->2,1->3,...\nprimes100=<n>\nprimes1000=<n>\ntwin100=<n>\n"
            + "Rode " + run + " e ajuste ate bater; nao copie os numeros do enunciado.";
        return new[] { head + hanoi, head + primes };
    }

    /// <summary>
    /// Roda uma fase C#/F# de um agente com tools e devolve fonte + saida final.
    /// Cada item de <paramref name="stages"/> e' um enunciado com contexto novo sobre
    /// o MESMO workspace (segmentacao: o modelo local nao entrega um programa F#
    /// inteiro num laco so'; o aceite final e' o mesmo).
    /// </summary>
    static async Task<(string Src, string Out)> RunAlgoPhase(
        string bridge, string model, string ws, string lang, string project, string file, string[] stages, int turns = 26)
    {
        string sysBase = "Voce e engenheiro senior C# e F#. Responda SOMENTE com tool calls, sem prosa.";
        string sys = lang == "F#"
            ? sysBase + "\n\n" + global::Rochas.OpenCodeBridge.Core.HarnessTails.FsMini
            : sysBase;
        // Guarda de fluxo: a fase F# so' age com a tabela de sintaxe no system. Se o
        // enunciado/scaffold mudar e a tabela sair, a fase falha com a causa certa em
        // vez de gastar 30 turnos sem o contrato (medido em 2026-10-29).
        if (lang == "F#" && !sys.Contains("F# MINI", StringComparison.Ordinal))
            throw new Exception("fase F# sem FsMini no system: o modelo agiria sem a tabela de sintaxe");
        bool dump = Environment.GetEnvironmentVariable("TCTX_DUMP") == "1";
        if (dump)
            Console.WriteLine($"  [algo:{lang}] system={sys.Length} chars; regras F# presentes={sys.Contains("F# MINI")}");
        string feedback = "";
        // Uma rodada por etapa, com contexto resetado: um programa inteiro em F# custa
        // mais de uma passada. O aceite (metas e formato) nao muda entre as etapas.
        for (int attempt = 1; attempt <= stages.Length; attempt++)
        {
        var hist = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = sys });
        hist.Add(new JsonObject { ["role"] = "user", ["content"] = stages[attempt - 1] + feedback });
        // Retry de tool: o controle de loop que a bridge usa no caminho de chat
        // (ToolsBreaker, soft/hard) vale aqui tambem — o POST /api/tools/execute nao
        // passa por ele. Nenhuma mecanica nova: 3 falhas no MESMO alvo viram aviso no
        // ultimo resultado e 6 encerram a tentativa. Medido em 2026-10-29 (10a amostra):
        // 15 edits com o mesmo oldString obsoleto, ~20 dos 52 turnos em retry.
        var loopGuard = new global::Rochas.OpenCodeBridge.Core.ToolsBreaker(3, 6);
        int prosaSeguida = 0;
        // Dica repetida nao ensina: medido em 2026-10-29 (17 reparos identicos, 30
        // turnos). Na 3a vez a mesma dica vira instrucao de reescrever o arquivo.
        string lastHint = "";
        int hintRepeats = 0;
        int httpFalhas = 0;
        for (int turn = 0; turn < turns; turn++)
        {
            JsonObject r;
            try
            {
                r = await Chat(bridge, model, (JsonArray)hist.DeepClone(), global::Rochas.OpenCodeBridge.Core.ToolCatalog.Chat());
            }
            catch (Exception ex)
            {
                global::Rochas.OpenCodeBridge.Core.ToolCallRepair.Apply(hist);
                Console.WriteLine($"  [algo:{lang}] HTTP falhou: {ex.Message.Split('\n')[0]}");
                // Ponte fora do ar nao e' falha de raciocinio: 3 seguidas encerram a
                // fase com a causa certa (antes o laco gastou 15 turnos em Connection
                // refused durante um restart — medido 2026-10-29).
                if (++httpFalhas >= 3) throw new Exception($"bridge indisponivel na fase {lang}: {ex.Message.Split('\n')[0]}");
                continue;
            }
            httpFalhas = 0;
            var m = r["choices"]?[0]?["message"]?.AsObject() ?? throw new Exception("sem message (" + lang + ")");
            var calls = m["tool_calls"]?.AsArray();
            if (dump)
                Console.WriteLine($"  [algo:{lang}] T{turn}: calls={calls?.Count ?? 0} texto={(m["content"]?.GetValue<string>() ?? "").Trim().Length} chars");
            if (calls is null || calls.Count == 0)
            {
                hist.Add(new JsonObject { ["role"] = "assistant", ["content"] = m["content"]?.GetValue<string>() ?? "" });
                // Um turno sem tool call nao e' desistencia: pode ser o aviso mecanico
                // da bridge (BRIDGE (...)) que o modelo repetiu. Antes o laco parava no
                // T4 com 26 turnos sobrando (medido 2026-10-29); agora a cutucada volta
                // e so' duas prosa seguida encerram a tentativa.
                prosaSeguida++;
                if (dump)
                    Console.WriteLine($"  [algo:{lang}] T{turn} sem tool call ({prosaSeguida}): {(m["content"]?.GetValue<string>() ?? "").Replace("\n", " | ")[..Math.Min(200, (m["content"]?.GetValue<string>() ?? "").Replace("\n", " | ").Length)]}");
                if (prosaSeguida >= 3) break;
                hist.Add(new JsonObject { ["role"] = "user", ["content"] = "Sem prosa: emita UMA tool call valida (uma por vez)." });
                continue;
            }
            prosaSeguida = 0;
            var fc = new JsonArray();
            foreach (var c in calls)
                fc.Add(new JsonObject { ["id"] = c?["id"]?.GetValue<string>() ?? "", ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = c?["function"]?["name"]?.GetValue<string>() ?? "",
                        ["arguments"] = c?["function"]?["arguments"]?.GetValue<string>() ?? "{}" } });
            hist.Add(new JsonObject { ["role"] = "assistant", ["content"] = m["content"]?.GetValue<string>() ?? "", ["tool_calls"] = fc });
            bool done = false;
            foreach (var c in calls)
            {
                JsonObject a;
                try { a = JsonNode.Parse(c?["function"]?["arguments"]?.GetValue<string>() ?? "{}")?.AsObject() ?? new JsonObject(); }
                catch { a = new JsonObject(); }
                string fn = c?["function"]?["name"]?.GetValue<string>() ?? "";
                string res = await ExecTool(ws, model, fn, a);
                // Reparo in-flow: erro de sintaxe F# volta com a forma correta concreta
                // (mesma mecanica do mission-tools, texto no Core) para o modelo corrigir
                // no MESMO turno, em vez de repetir o cabecalho colado (medido 2026-10-29).
                if (lang == "F#" && global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.HasDiagnostic(res))
                {
                    string curFs = File.Exists(Path.Combine(ws, "mfs", file)) ? File.ReadAllText(Path.Combine(ws, "mfs", file)) : "";
                    // A contagem da MESMA dica e' do teste; a escalada (3a vez vira
                    // "reescreva o arquivo com `write`") e' do Core, em Suggest(repeatCount).
                    string baseHint = global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(curFs, res);
                    if (baseHint.Length > 0)
                    {
                        if (baseHint == lastHint) hintRepeats++; else { lastHint = baseHint; hintRepeats = 0; }
                        string hint = global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(curFs, res, hintRepeats);
                        res += "\n\n" + hint;
                        if (dump) Console.WriteLine($"  [algo:{lang}] T{turn} reparo: {hint.Replace("\n", " | ")}");
                    }
                }
                if (dump)
                {
                    string seen = res.StartsWith("ERRO", StringComparison.Ordinal) ? res : "ok " + res;
                    Console.WriteLine($"  [algo:{lang}] T{turn} {fn} args={a.ToJsonString()[..Math.Min(120, a.ToJsonString().Length)]} -> {seen.Replace("\n", " | ")[..Math.Min(300, seen.Replace("\n", " | ").Length)]}");
                }
                hist.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = c?["id"]?.GetValue<string>() ?? "", ["content"] = res });
                if (fn == "shell" && AlgoOk(res)) done = true;
            }
            var verdict = loopGuard.Evaluate(hist);
            if (verdict.Verdict == global::Rochas.OpenCodeBridge.Core.BreakerVerdict.Nudge)
            {
                loopGuard.ApplyNudge(hist, verdict.Notice);
                if (dump) Console.WriteLine($"  [algo:{lang}] T{turn} breaker: nudge ({verdict.Streak} falhas no mesmo alvo)");
            }
            else if (verdict.Verdict == global::Rochas.OpenCodeBridge.Core.BreakerVerdict.Trip)
            {
                if (dump) Console.WriteLine($"  [algo:{lang}] T{turn} breaker: trip ({verdict.Streak} falhas no mesmo alvo)");
                break;
            }
            if (done) break;
        }
        string dir = lang == "F#" ? "mfs" : "mcs";
        string srcPath = Path.Combine(ws, dir, file);
        string src = File.Exists(srcPath) ? File.ReadAllText(srcPath) : "";
        string output = await RunShell(ws, model, "dotnet run --project " + project + " -c Release");
        if (dump)
            Console.WriteLine($"  [algo:{lang}] tentativa {attempt}: {(AlgoOk(output) ? "bateu as metas" : "fora das metas")} | {output.Replace("\n", " | ")[..Math.Min(180, output.Replace("\n", " | ").Length)]}");
        if (AlgoOk(output) || attempt == stages.Length) return (src, output);
        // Saida real alimenta a etapa seguinte junto do enunciado dela (sem dica de passo).
        feedback = "\n\nA saida anterior nao bateu. Saida real:\n" + (output.Length > 900 ? output[..900] : output)
                 + "\nCorrija " + Path.Combine(ws, dir, file) + " e rode de novo; as metas e o formato continuam os mesmos.";
        }
        return (string.Empty, string.Empty);
    }

    static bool AlgoOk(string output)
    {
        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in output.Split('\n'))
        {
            int i = line.IndexOf('=');
            if (i <= 0) continue;
            string k = line[..i].Trim();
            string v = line[(i + 1)..].Trim();
            if (k.Length > 0 && v.Length > 0) kv[k] = v;
        }
        if (!kv.TryGetValue("hanoi", out var h) || h.Trim() != "15") return false;
        if (!kv.TryGetValue("primes100", out var p1) || p1.Trim() != "25") return false;
        if (!kv.TryGetValue("primes1000", out var p2) || p2.Trim() != "168") return false;
        if (!kv.TryGetValue("twin100", out var t) || t.Trim() != "8") return false;
        if (!kv.TryGetValue("hanoi_seq", out var seq)) return false;
        var moves = seq.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Replace(" ", "")).ToArray();
        return moves.Length == 15 && moves[0] == "1->2" && moves[^1] == "2->3";
    }

    static bool HasHardcodedAlgo(string src)
    {
        string code = string.Join("\n", src.Split('\n').Select(l =>
        {
            int i = l.IndexOf("//", StringComparison.Ordinal);
            return i >= 0 ? l[..i] : l;
        }));
        return System.Text.RegularExpressions.Regex.IsMatch(
            code, @"hanoi\s*=\s*15|primes100\s*=\s*25|primes1000\s*=\s*168|twin100\s*=\s*8");
    }

    static async Task<string> ExecTool(string ws, string model, string fn, JsonObject args)
    {
        JsonObject r = await TestContext.ExecuteTool(fn, args, ws, model);
        return r["ok"]?.GetValue<bool>() == true
            ? (r["output"]?.GetValue<string>() ?? "")
            : "ERRO: " + (r["error"]?.GetValue<string>() ?? "falha");
    }
}
