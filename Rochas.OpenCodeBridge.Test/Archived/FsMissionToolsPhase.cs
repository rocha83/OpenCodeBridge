using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using static Rochas.OpenCodeBridge.Test.E2EMissionTests;

namespace Rochas.OpenCodeBridge.Test.Archived;

/// <summary>
/// ARQUIVADO em 2026-10-30 — fase F# da missão <c>E-mission-tools</c>.
/// </summary>
/// <remarks>
/// <para><b>Cenário.</b> A missão de tools sempre teve duas fases: C# (escrever
/// e rodar um programa) e F# (mesma meta, outro projeto). A fase F# foi a que
/// mais gastou turnos em reparo de sintaxe e, mesmo quando passava, o PASS era
/// intermitente: o modelo alterna entre a forma correta e um literal inválido
/// no mesmo enunciado (FS0043, FS0001, FS0010, FS0039, FS0058 — ver
/// <see cref="ArchivedFsMini"/> e <c>docs/DRIVER-FIXES.md</c>). Como o L3 roda
/// com fail-fast, um PASS/FAIL aleatório da fase F# derrubava o step inteiro e
/// escondia o resultado real da fase C# e da entrega git/zip.</para>
/// <para><b>Decisão (2026-10-30).</b> A fase F# sai do teste ativo, inclusive
/// as amostras que já tinham PASS. <c>E-mission-tools</c> passa a medir só C#
/// (escrita, execução e entrega git/zip); a fase F# fica aqui, compilando e
/// preservada, sem registro no <c>Program.cs</c> e sem consumir GPU.</para>
/// <para><b>Como reativar.</b> Chamar <see cref="Run"/> a partir de uma missão
/// registrada, com o workspace já scaffoldado (<c>mfs.fsproj</c>) e o FsMini
/// reinjetado no system (ver <see cref="ArchivedFsMini"/>). Exigir PASS estável
/// em 3 rodadas antes de voltar ao gate.</para>
/// </remarks>
public static class ArchivedFsMissionToolsPhase
{
    /// <summary>Executa a fase F# arquivada: escreve <c>mfs/Program.fs</c>, roda e confere as três metas.</summary>
    /// <param name="bridge">Base HTTP da bridge.</param>
    /// <param name="model">Modelo do role build.</param>
    /// <param name="ws">Workspace já com <c>mfs/mfs.fsproj</c> scaffoldado.</param>
    /// <param name="log">Coletor de log da missão (o chamador decide onde gravar).</param>
    public static async Task Run(string bridge, string model, string ws, Action<string> log)
    {
        const string sysBase = "Voce e engenheiro senior C# e F#. Responda SOMENTE com tool calls, sem prosa.";
        // F# tem system proprio: FsMini so entra aqui (C# nao recebe regra F#).
        var histFs = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = sysBase + "\n\n" + global::Rochas.OpenCodeBridge.Test.Archived.ArchivedFsMini.FsMini });

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
            ResetFsScaffold(ws);
            if (attempt > 1)
            {
                while (histFs.Count > historyBeforeFs) histFs.RemoveAt(histFs.Count - 1);
                string errLine = ErrLine(lastErr);
                bool compileErr = errLine.Contains("error", StringComparison.OrdinalIgnoreCase);
                log($"[F#] retry {attempt}: contexto F# resetado; {(compileErr ? "erro de build" : "saida errada")} repassado ({errLine})");
                string fsSrcNow = File.Exists(Path.Combine(ws, "mfs", "Program.fs"))
                    ? File.ReadAllText(Path.Combine(ws, "mfs", "Program.fs")) : "";
                string fsHint = compileErr
                    ? global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(fsSrcNow, errLine) : "";
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
                log($"[F#] T{turn} HTTP falhou: {ex.Message.Split('\n')[0]}");
                bool dropped = global::Rochas.OpenCodeBridge.Core.ToolCallRepair.Apply(histFs);
                log($"[F#] T{turn} reparo: assistant quebrada podada={dropped}; nudge JSON anexado");
                continue;
            }
            var mf = rf["choices"]?[0]?["message"]?.AsObject() ?? throw new Exception("sem message (F#)");
            var cf = mf["tool_calls"]?.AsArray();
            if (cf is null || cf.Count == 0)
            {
                string txtFs = mf["content"]?.GetValue<string>() ?? "";
                log($"[F#] T{turn} SEM CALLS: {txtFs[..Math.Min(200, txtFs.Length)]}");
                histFs.Add(new JsonObject { ["role"] = "assistant", ["content"] = txtFs });
                if (global::Rochas.OpenCodeBridge.Core.ToolCallRepair.IsSalvageNotice(txtFs))
                {
                    log($"[F#] T{turn} aviso de salvage (nao consome turno)");
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
                log($"[F#] T{turn} {fnf} {af.ToJsonString()[..Math.Min(160, af.ToJsonString().Length)]} -> {resf[..Math.Min(180, resf.Length)]}");
                // Reparo in-flow: erro de sintaxe volta com ERRO->CERTO concreto (mesma
                // mecanica do retry, no Core) para o modelo corrigir no MESMO turno.
                if (resf.Contains("error FS", StringComparison.Ordinal))
                {
                    string curFs = File.Exists(Path.Combine(ws, "mfs", "Program.fs"))
                        ? File.ReadAllText(Path.Combine(ws, "mfs", "Program.fs")) : "";
                    string inFlow = global::Rochas.OpenCodeBridge.Test.Archived.FsSyntaxRepair.Suggest(curFs, resf);
                    if (inFlow.Length > 0)
                    {
                        resf += "\n\n" + inFlow;
                        log($"[F#] T{turn} reparo in-flow: {inFlow.Replace("\n", " | ")}");
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
            ResetFsScaffold(ws);
            fsOut = await RunShell(ws, model, "dotnet run --project mfs/mfs.fsproj -c Release");
            lastErr = fsOut;
            log($"[F#] RUN -> {fsOut[..Math.Min(220, fsOut.Length)]}");
            bool ok = fsSrc.Length > 60 && !HasHardcodedResult(fsSrc) && MissionIntsOk(fsOut);
            if (ok) break;
        }
        // Atestacao honesta: o system final do request F# (registrado pela bridge)
        // realmente carregou o FsMini? Roda ANTES dos asserts semanticos para o
        // log provar o fluxo mesmo quando o modelo erra a matematica/sintaxe.
        try
        {
            var att = await TestContext.GetObj(bridge + "/api/test/last-system");
            log($"[F#] atestacao system: {att.ToJsonString()}");
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
    }

    /// <summary>Limpa artefatos C# que o modelo cria dentro da pasta F# (o scaffold é <c>mfs.fsproj</c>).</summary>
    static void ResetFsScaffold(string ws)
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
}
