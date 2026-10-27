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

    static List<double> SixDecimals(string text)
    {
        var list = new List<double>();
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(text, @"-?\d+[.,]\d{6}"))
            if (double.TryParse(m.Value.Replace(',', '.'),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v))
                list.Add(v);
        return list;
    }

    /// <summary>Exige Simpson=2, media=5 e desvio=2 NA ORDEM; evita o falso
    /// positivo de "contem 2 e 5" quando so media/desvio batem.</summary>
    static bool ThreeFloatsOk(string text)
    {
        var nums = SixDecimals(text);
        return nums.Count >= 3
            && Math.Abs(nums[0] - 2.0) < 1e-4
            && Math.Abs(nums[1] - 5.0) < 1e-4
            && Math.Abs(nums[2] - 2.0) < 1e-4;
    }

    // Hardcode do resultado: 2.000000/5.000000 como literal no fonte (com ou
    // sem aspas). Ignora comentarios para nao acusar a meta copiada do enunciado.
    static bool HasHardcodedResult(string src)
    {
        string code = string.Join("\n", src.Split('\n').Select(l =>
        {
            int i = l.IndexOf("//", StringComparison.Ordinal);
            return i >= 0 ? l[..i] : l;
        }));
        return System.Text.RegularExpressions.Regex.IsMatch(
            code, @"(?<!\d)(2[.,]000000|5[.,]000000)(?!\d)");
    }

    public static async Task MissionTools()
    {
        string bridge = TestContext.Bridge;
        string model = TestContext.SuiteModel;
        if (!model.EndsWith("-build")) model += "-build";
        string ws = Path.Combine(Path.GetTempPath(), "e2e-mission");
        if (Directory.Exists(ws)) Directory.Delete(ws, true);
        Directory.CreateDirectory(ws);
        // Scaffold dos projetos e' do harness (template `dotnet new`, estilo Visual Studio):
        // o modelo so escreve o fonte e roda; elimina a variabilidade do `dotnet new` no modelo.
        TestContext.ScaffoldProject("mcs", Path.Combine(ws, "mcs"));
        TestContext.ScaffoldProject("mfs", Path.Combine(ws, "mfs"), "F#");

        ResetCsScaffold();
        const string sysBase = "Voce e engenheiro senior C# e F#. Responda SOMENTE com tool calls, sem prosa.";
        var sys = new JsonObject { ["role"] = "system", ["content"] = sysBase };
        var history = new JsonArray(sys);
        history.Add(new JsonObject { ["role"] = "user", ["content"] = $"Em {ws}/mcs, implemente em Program.cs o calculo de Simpson de Math.Sin em [0,pi] (n=100) e a media e o desvio padrao populacional de {{2,4,4,4,5,5,7,9}}, imprimindo os 3 numeros com 6 casas. O projeto C# ja existe em {ws}/mcs/mcs.csproj; NAO crie Program.csproj, bin ou obj. Grave o arquivo completo com a tool write (nao use edit para criar o arquivo), enxuto e sem comentarios longos. Dica de Simpson: h=(b-a)/n e integral=(h/3)*(f0+fn+4*impares+2*pares). Rode `dotnet run --project {ws}/mcs/mcs.csproj` (nao use `dotnet build --project`) e ajuste ate a saida ser 2.000000, 5.000000 e 2.000000." });

        string fcode = "", final = "";
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
                history.Add(new JsonObject { ["role"] = "user", ["content"] = "A resposta anterior foi cortada pela bridge. Reenvie uma tool call curta: write com o arquivo completo, enxuto e sem comentarios longos." });
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
                if (fn == "write" && (args["path"]?.GetValue<string>() ?? "").EndsWith("Program.fs")) fcode = args["content"]?.GetValue<string>() ?? fcode;
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
            // Predicado estrito (2/5/2 NA ORDEM), igual ao F#: "contem 2 e 5" aceita
            // 3,332895 / 5,000000 / 2,000000 (2 e' o desvio) e mata o turno de reparo.
            if (echo.Any(e => ThreeFloatsOk(e?["content"]?.GetValue<string>() ?? ""))) break;
        }
        string csFile = Path.Combine(ws, "mcs", "Program.cs");
        string csSrc = File.Exists(csFile) ? File.ReadAllText(csFile) : "";
        TestContext.Check((csSrc.Contains("Sin") || csSrc.Contains("sin")) && csSrc.Length > 60,
            "mcs/Program.cs com Simpson (nao template), veio: " + csSrc[..Math.Min(100, csSrc.Length)]);
        ResetCsScaffold();
        string runOut = await RunShell(ws, model, "dotnet run --project mcs/mcs.csproj -c Release");
        LogT($"[C#] RUN -> {runOut[..Math.Min(220, runOut.Length)]}");
        TestContext.Check(!HasHardcodedResult(csSrc), "C# calcula (sem hardcode de 2.000000/5.000000 no fonte)");
        var csNums = SixDecimals(runOut);
        TestContext.Check(csNums.Count >= 3, "C# imprime 3 numeros com 6 casas, saiu: " + runOut[..Math.Min(160, runOut.Length)]);
        TestContext.Check(csNums.Count >= 1 && Math.Abs(csNums[0] - 2.0) < 1e-4, "C# Simpson = 2.0, saiu: " + runOut[..Math.Min(120, runOut.Length)]);
        TestContext.Check(csNums.Count >= 2 && Math.Abs(csNums[1] - 5.0) < 1e-4, "C# media = 5.0");
        TestContext.Check(csNums.Count >= 3 && Math.Abs(csNums[2] - 2.0) < 1e-4, "C# desvio = 2.0 (populacional)");
        Console.WriteLine($"  [mission] C# ok:\n{runOut.Trim()}");

        // F# tem system proprio: FsMini so entra aqui (C# nao recebe regra F#).
        var histFs = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = sysBase + "\n\n" + global::Rochas.OpenCodeBridge.Core.HarnessTails.FsMini });

        // Fase 2: portabilidade para F# (mesma matematica, System.Math qualificado).
        // O E4B Q4 nao infere F# de forma estavel: o enunciado entrega a META
        // (2.000000 / 5.000000) e as restricoes de sintaxe, sem ditar o algoritmo.
        // O retry nao "ensina" sintaxe: reseta o contexto F# e repassa so a linha
        // real do erro, para o modelo corrigir com o proprio codigo em vista.
        int historyBeforeFs = histFs.Count;
        string fsOut = "";
        string fsSrc = "";
        string lastErr = "";
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            ResetFsScaffold();
            if (attempt > 1)
            {
                while (histFs.Count > historyBeforeFs) histFs.RemoveAt(histFs.Count - 1);
                string errLine = lastErr.Split('\n').FirstOrDefault(l => l.Contains("error"))?.Trim()
                    ?? lastErr.Split('\n').FirstOrDefault()?.Trim() ?? "";
                LogT($"[F#] retry {attempt}: contexto F# resetado; erro real repassado ({errLine})");
                histFs.Add(new JsonObject { ["role"] = "user", ["content"] =
                    $"Sem prosa: o build do F# falhou: {errLine}. Corrija {ws}/mfs/Program.fs e rode `dotnet run --project {ws}/mfs/mfs.fsproj` ate sair 2.000000 e 5.000000." });
            }
            histFs.Add(new JsonObject { ["role"] = "user", ["content"] =
                $"Em {ws}/mfs, implemente em Program.fs (o projeto ja existe em {ws}/mfs/mfs.fsproj; NAO crie mfs.csproj, Program.cs, bin ou obj) um programa F# que calcule e imprima com 6 casas: (a) Simpson de System.Math.Sin em [0, pi] com n=100 -> meta 2.000000; (b) a media de [2.0;4.0;4.0;4.0;5.0;5.0;7.0;9.0] -> meta 5.000000; (c) o desvio padrao populacional da mesma lista -> meta 2.000000. Grave o arquivo completo com a tool write (nao use edit para criar o arquivo), enxuto e sem comentarios longos. Dica de Simpson: h=(b-a)/n e integral=(h/3)*(f0+fn+4*impares+2*pares). F# idiomatico: `System.Math.Sin/Sqrt/PI` qualificado, `printfn \"%.6f\"`, aritmetica float com `+ - * /` e `** 2.0`, listas com `List.sum/List.map/List.fold`. Rode `dotnet run --project {ws}/mfs/mfs.fsproj` e ajuste ate as metas baterem." });
        for (int turn = 0; turn < 22; turn++)
        {
            JsonObject rf;
            try
            {
                rf = await Chat(bridge, model, (JsonArray)histFs.DeepClone(), global::Rochas.OpenCodeBridge.Core.ToolCatalog.Chat());
            }
            catch (Exception ex)
            {
                LogT($"[F#] T{turn} HTTP falhou: {ex.Message.Split('\n')[0]}");
                histFs.Add(new JsonObject { ["role"] = "user", ["content"] = "A resposta anterior foi cortada pela bridge. Reenvie uma tool call curta: write com Program.fs completo, enxuto e sem comentarios longos." });
                continue;
            }
            var mf = rf["choices"]?[0]?["message"]?.AsObject() ?? throw new Exception("sem message (F#)");
            var cf = mf["tool_calls"]?.AsArray();
            if (cf is null || cf.Count == 0)
            {
                LogT($"[F#] T{turn} SEM CALLS: {(mf["content"]?.GetValue<string>() ?? "")[..Math.Min(200, (mf["content"]?.GetValue<string>() ?? "").Length)]}");
                histFs.Add(new JsonObject { ["role"] = "assistant", ["content"] = mf["content"]?.GetValue<string>() ?? "" });
                if (turn >= 4) break;
                histFs.Add(new JsonObject { ["role"] = "user", ["content"] = "Sem prosa: leia o erro de build (se houver) e emita ja a proxima tool call." });
                continue;
            }
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
                if (fnf == "write" && (af["path"]?.GetValue<string>() ?? "").EndsWith("Program.fs")) fcode = af["content"]?.GetValue<string>() ?? fcode;
                fres.Add(resf);
                histFs.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = c?["id"]?.GetValue<string>() ?? "", ["content"] = resf });
            }
            // Sucesso atingido: para de editar (o modelo tende a quebrar o F# que ja compila).
            if (fres.Any(ThreeFloatsOk)) break;
        }
            string fsFile = Path.Combine(ws, "mfs", "Program.fs");
            fsSrc = File.Exists(fsFile) ? File.ReadAllText(fsFile) : "";
            ResetFsScaffold();
            fsOut = await RunShell(ws, model, "dotnet run --project mfs/mfs.fsproj -c Release");
            lastErr = fsOut;
            LogT($"[F#] RUN -> {fsOut[..Math.Min(220, fsOut.Length)]}");
            bool ok = fsSrc.Contains("Math") && fcode.Contains("Math") && ThreeFloatsOk(fsOut);
            if (ok) break;
        }
        TestContext.Check(!HasHardcodedResult(fsSrc), "F# calcula (sem hardcode de 2.000000/5.000000 no fonte)");
        var fsNums = SixDecimals(fsOut);
        TestContext.Check(fsNums.Count >= 3, "F# imprime 3 numeros com 6 casas, saiu: " + fsOut[..Math.Min(160, fsOut.Length)]);
        TestContext.Check(fsNums.Count >= 1 && Math.Abs(fsNums[0] - 2.0) < 1e-4, "F# Simpson = 2.0, saiu: " + fsOut[..Math.Min(160, fsOut.Length)]);
        TestContext.Check(fsNums.Count >= 2 && Math.Abs(fsNums[1] - 5.0) < 1e-4, "F# media = 5.0");
        TestContext.Check(fsNums.Count >= 3 && Math.Abs(fsNums[2] - 2.0) < 1e-4, "F# desvio = 2.0 (populacional)");
        Console.WriteLine($"  [mission] F# ok:\n{fsOut.Trim()}");

        // Fase 3: versionamento com a tool git (command/path) + pacote via shell (zip).
        histFs.Add(new JsonObject { ["role"] = "user", ["content"] = "Conclua a entrega: versione o workspace com a tool git (command/path: init, config, add, commit) e gere o pacote entrega.zip na raiz do workspace com a tool shell (zip -qr). So tool calls ate concluir, sem texto." });
        for (int turn = 0; turn < 12; turn++)
        {
            var r2 = await Chat(bridge, model, (JsonArray)histFs.DeepClone(), global::Rochas.OpenCodeBridge.Core.ToolCatalog.Chat());
            var m2 = r2["choices"]?[0]?["message"]?.AsObject() ?? throw new Exception("sem message (git/zip)");
            var c2 = m2["tool_calls"]?.AsArray();
            if (c2 is null || c2.Count == 0)
            {
                // Modelo encerrou sem tool call: se a entrega ja existe, ok; senao cutuca so o que falta.
                if (Directory.GetFiles(ws, "entrega.zip").Length == 1) break;
                LogT($"[git/zip] T{turn + 1} sem tool call e sem entrega.zip; pedindo shell com zip");
                histFs.Add(new JsonObject { ["role"] = "user",
                    ["content"] = "Voce encerrou sem concluir a entrega. Verifique o que falta e emita a tool call correspondente, sem texto." });
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
        TestContext.Check(Directory.Exists(Path.Combine(ws, ".git")), "git init executado");
        TestContext.Check(File.Exists(Path.Combine(ws, ".git", "HEAD")), "repo git criado");
        TestContext.Check(Directory.GetFiles(ws, "entrega.zip").Length == 1, "entrega.zip gerado");
    }

    /// <summary>
    /// Missao algoritmica E2E (C# + F#): Simpson + media/desvio + Hanoi + primos.
    /// Step separado do E-mission-tools: falha de algoritmo nao se mistura com
    /// a entrega git/zip. Enunciado humano: meta + dica de formula, sem passo a passo.
    /// </summary>
    public static async Task MissionAlgo()
    {
        string bridge = TestContext.Bridge;
        string model = TestContext.SuiteModel;
        if (!model.EndsWith("-build")) model += "-build";
        string ws = Path.Combine(Path.GetTempPath(), "e2e-mission-algo");
        if (Directory.Exists(ws)) Directory.Delete(ws, true);
        Directory.CreateDirectory(ws);
        TestContext.ScaffoldProject("mcs", Path.Combine(ws, "mcs"));
        TestContext.ScaffoldProject("mfs", Path.Combine(ws, "mfs"), "F#");

        string csPrompt =
            "Preciso de um programa em " + ws + "/mcs/Program.cs, no projeto mcs.csproj que ja existe (nao crie outro projeto), resolvendo quatro coisas e imprimindo o resultado de um jeito simples para eu conferir.\n" +
            "1) A integral de sin(x) de 0 a pi com 100 intervalos, pela regra de Simpson. Tem que dar 2.000000.\n" +
            "2) A media e o desvio padrao populacional de [2,4,4,4,5,5,7,9]. A media e 5.000000 e o desvio e 2.000000.\n" +
            "3) A torre de Hanoi com 4 discos, da haste 1 para a 3. Me diga quantos movimentos foram necessarios e a sequencia completa no formato 1->2, 1->3, ...\n" +
            "4) Uma analise dos primos: quantos ha abaixo de 100 e abaixo de 1000, e quantos pares gemeos abaixo de 100. O crivo de Eratostenes e uma boa pedida.\n" +
            "Imprima os valores que o programa calculou; nao copie os numeros do enunciado.\n" +
            "Para eu comparar sem ambiguidade, termine com estas linhas, uma por linha e nessa ordem:\n" +
            "simpson=2.000000\nmedia=5.000000\ndesvio=2.000000\nhanoi=15\nhanoi_seq=1->2,1->3,...\nprimes100=25\nprimes1000=168\ntwin100=8\n" +
            "Pode rodar `dotnet run --project " + ws + "/mcs/mcs.csproj` e ajustar ate bater.";

        string fsPrompt = csPrompt
            .Replace(ws + "/mcs/Program.cs", ws + "/mfs/Program.fs")
            .Replace(ws + "/mcs/mcs.csproj", ws + "/mfs/mfs.fsproj")
            .Replace("projeto mcs.csproj", "projeto mfs.fsproj")
            .Replace("[2,4,4,4,5,5,7,9]", "[2.0;4.0;4.0;4.0;5.0;5.0;7.0;9.0]");

        var (csSrc, csOut) = await RunAlgoPhase(bridge, model, ws, "C#", "mcs/mcs.csproj", "Program.cs", csPrompt);
        TestContext.Check(!HasHardcodedAlgo(csSrc), "C# calcula (sem hardcode das metas)");
        TestContext.Check(AlgoOk(csOut), "C# entrega as chaves esperadas, saiu: " + csOut[..Math.Min(240, csOut.Length)]);
        Console.WriteLine($"  [algo] C# ok:\n{csOut.Trim()}");

        var (fsSrc, fsOut) = await RunAlgoPhase(bridge, model, ws, "F#", "mfs/mfs.fsproj", "Program.fs", fsPrompt);
        TestContext.Check(!HasHardcodedAlgo(fsSrc), "F# calcula (sem hardcode das metas)");
        TestContext.Check(AlgoOk(fsOut), "F# entrega as chaves esperadas, saiu: " + fsOut[..Math.Min(240, fsOut.Length)]);
        Console.WriteLine($"  [algo] F# ok:\n{fsOut.Trim()}");
    }

    /// <summary>Roda uma fase C#/F# de um agente com tools e devolve fonte + saida final.</summary>
    static async Task<(string Src, string Out)> RunAlgoPhase(
        string bridge, string model, string ws, string lang, string project, string file, string prompt, int turns = 30)
    {
        string sysBase = "Voce e engenheiro senior C# e F#. Responda SOMENTE com tool calls, sem prosa.";
        string sys = lang == "F#"
            ? sysBase + "\n\n" + global::Rochas.OpenCodeBridge.Core.HarnessTails.FsMini
            : sysBase;
        var hist = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = sys });
        hist.Add(new JsonObject { ["role"] = "user", ["content"] = prompt });
        for (int turn = 0; turn < turns; turn++)
        {
            JsonObject r;
            try
            {
                r = await Chat(bridge, model, (JsonArray)hist.DeepClone(), global::Rochas.OpenCodeBridge.Core.ToolCatalog.Chat());
            }
            catch (Exception ex)
            {
                hist.Add(new JsonObject { ["role"] = "user", ["content"] = "A resposta anterior foi cortada pela bridge. Reenvie uma tool call curta: write com o arquivo completo, enxuto e sem comentarios longos." });
                Console.WriteLine($"  [algo:{lang}] HTTP falhou: {ex.Message.Split('\n')[0]}");
                continue;
            }
            var m = r["choices"]?[0]?["message"]?.AsObject() ?? throw new Exception("sem message (" + lang + ")");
            var calls = m["tool_calls"]?.AsArray();
            if (calls is null || calls.Count == 0)
            {
                hist.Add(new JsonObject { ["role"] = "assistant", ["content"] = m["content"]?.GetValue<string>() ?? "" });
                if (turn >= 4) break;
                hist.Add(new JsonObject { ["role"] = "user", ["content"] = "Sem prosa: leia a saida e emita a proxima tool call." });
                continue;
            }
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
                hist.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = c?["id"]?.GetValue<string>() ?? "", ["content"] = res });
                if (fn == "shell" && AlgoOk(res)) done = true;
            }
            if (done) break;
        }
        string dir = lang == "F#" ? "mfs" : "mcs";
        string srcPath = Path.Combine(ws, dir, file);
        string src = File.Exists(srcPath) ? File.ReadAllText(srcPath) : "";
        string output = await RunShell(ws, model, "dotnet run --project " + project + " -c Release");
        return (src, output);
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
        static bool Num(string s, double target)
            => double.TryParse(s.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var v)
               && Math.Abs(v - target) < 1e-4;
        if (!kv.TryGetValue("simpson", out var s) || !Num(s, 2.0)) return false;
        if (!kv.TryGetValue("media", out var m) || !Num(m, 5.0)) return false;
        if (!kv.TryGetValue("desvio", out var d) || !Num(d, 2.0)) return false;
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
            code, @"(?<!\d)(2[.,]000000|5[.,]000000|hanoi\s*=\s*15|primes100\s*=\s*25|primes1000\s*=\s*168|twin100\s*=\s*8)(?!\d)");
    }

    static async Task<string> ExecTool(string ws, string model, string fn, JsonObject args)
    {
        JsonObject r = await TestContext.ExecuteTool(fn, args, ws, model);
        return r["ok"]?.GetValue<bool>() == true
            ? (r["output"]?.GetValue<string>() ?? "")
            : "ERRO: " + (r["error"]?.GetValue<string>() ?? "falha");
    }
}
