using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

// Unit do que nasceu da analise de 2026-10-23/24 (sessoes com tools quebradas):
//   - breaker de loop agentico (falha repetida no MESMO alvo mutado);
//   - higiene de escape "\n" literal no comando shell;
//   - cauda de tools cobrindo path/glob/simulacao;
//   - procedencia da temperatura (perfil do role vence o request).
// Sem modelo: os casos de HTTP usam /api/translate e /api/status.
internal static class UnitToolBreakerTests
{
    static JsonArray Chat(params (string Role, string Tool, string Args, string Content)[] steps)
    {
        var msgs = new JsonArray();
        string? lastId = null;
        foreach (var (role, tool, args, content) in steps)
        {
            if (role == "tool")
            {
                msgs.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = lastId ?? "call", ["content"] = content });
                continue;
            }
            string id = "call_" + Guid.NewGuid().ToString("N")[..8];
            lastId = id;
            msgs.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = "",
                ["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["id"] = id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = tool, ["arguments"] = args }
                })
            });
        }
        return msgs;
    }

    static string ShellArgs(string cmd) => new JsonObject { ["command"] = cmd }.ToJsonString();
    static string PathArgs(string p) => new JsonObject { ["path"] = p }.ToJsonString();

    public static Task NeedsStreak()
    {
        var msgs = Chat(
            ("assistant", "read", PathArgs("/home/mint/a.json"), ""),
            ("tool", "", "", "File not found: /home/mint/a.json"),
            ("assistant", "read", PathArgs("/home/mint/a.json"), ""),
            ("tool", "", "", "File not found: /home/mint/a.json"));
        var guard = new global::Rochas.OpenCodeBridge.Core.ToolsBreaker(3, 6);
        var r = guard.Evaluate(msgs);
        TestContext.Check(r.Verdict == global::Rochas.OpenCodeBridge.Core.BreakerVerdict.None, "2 falhas nao disparam");
        TestContext.Check(r.Streak == 2, "streak conta 2 falhas seguidas");
        return Task.CompletedTask;
    }

    public static Task NudgesSameTarget()
    {
        // Padrao real: o modelo reescreve o mesmo path com digitos diferentes.
        var msgs = Chat(
            ("assistant", "shell", ShellArgs("mkdir proj-teste-001 && cd proj-teste-999"), ""),
            ("tool", "", "", "/bin/bash: cd: proj-teste-999: Arquivo ou diretorio inexistente\nfatal: not a git repository"),
            ("assistant", "shell", ShellArgs("mkdir proj-teste-020 && cd proj-teste-200"), ""),
            ("tool", "", "", "/bin/bash: cd: proj-teste-200: Arquivo ou diretorio inexistente"),
            ("assistant", "shell", ShellArgs("mkdir proj-teste-099 && cd proj-teste-999"), ""),
            ("tool", "", "", "fatal: not a git repository (or any parent up to mount point /home)"));
        var guard = new global::Rochas.OpenCodeBridge.Core.ToolsBreaker(3, 6);
        var r = guard.Evaluate(msgs);
        TestContext.Check(r.Verdict == global::Rochas.OpenCodeBridge.Core.BreakerVerdict.Nudge, "3 falhas no mesmo alvo = nudge");
        TestContext.Check(r.DistinctTargets == 1, $"alvos mutados colapsam em 1 (veio {r.DistinctTargets})");
        TestContext.Check(r.Notice.Contains("BRIDGE-BREAKER"), "aviso identificavel");
        bool applied = guard.ApplyNudge(msgs, r.Notice);
        TestContext.Check(applied, "aviso anexado ao ultimo resultado");
        string last = msgs[msgs.Count - 1]?["content"]?.GetValue<string>() ?? "";
        TestContext.Check(last.Contains("BRIDGE-BREAKER") && last.Contains("fatal: not a git"), "conteudo original preservado + aviso");
        return Task.CompletedTask;
    }

    public static Task TripsAtHard()
    {
        var msgs = new JsonArray();
        for (int i = 0; i < 6; i++)
        {
            string id = "call" + i;
            msgs.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = "",
                ["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["id"] = id, ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = "write", ["arguments"] = PathArgs($"/usr/src/opencodedbidge/AgentSeedercs{i}") }
                })
            });
            msgs.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = "Unable to write /usr/src/opencodedbidge/AgentSeedercs" });
        }
        var guard = new global::Rochas.OpenCodeBridge.Core.ToolsBreaker(3, 6);
        var r = guard.Evaluate(msgs);
        TestContext.Check(r.Verdict == global::Rochas.OpenCodeBridge.Core.BreakerVerdict.Trip, "6 falhas encerram o turno");
        TestContext.Check(r.Notice.Contains("Nao houve chamada ao modelo"), "aviso de trip explica o curto-circuito");
        return Task.CompletedTask;
    }

    public static Task IgnoresDistinctTargets()
    {
        // Falhas em alvos DIFERENTES sao progresso (o modelo esta explorando).
        var msgs = Chat(
            ("assistant", "read", PathArgs("/a/um.json"), ""), ("tool", "", "", "File not found: /a/um.json"),
            ("assistant", "read", PathArgs("/b/dois.json"), ""), ("tool", "", "", "File not found: /b/dois.json"),
            ("assistant", "read", PathArgs("/c/tres.json"), ""), ("tool", "", "", "File not found: /c/tres.json"),
            ("assistant", "read", PathArgs("/d/quatro.json"), ""), ("tool", "", "", "File not found: /d/quatro.json"));
        var guard = new global::Rochas.OpenCodeBridge.Core.ToolsBreaker(3, 6);
        var r = guard.Evaluate(msgs);
        TestContext.Check(r.Verdict == global::Rochas.OpenCodeBridge.Core.BreakerVerdict.None, "alvos distintos nao disparam");
        TestContext.Check(r.DistinctTargets == 4, $"4 alvos distintos (veio {r.DistinctTargets})");
        return Task.CompletedTask;
    }

    public static Task NormalizeCollapsesMutations()
    {
        string a = global::Rochas.OpenCodeBridge.Core.ToolSig.Target("shell", ShellArgs("mkdir proj-teste-001 && cd proj-teste-001"));
        string b = global::Rochas.OpenCodeBridge.Core.ToolSig.Target("shell", ShellArgs("mkdir proj-teste-020 && cd proj-teste-200"));
        TestContext.Check(a == b, "digitos e separadores nao criam alvo novo");
        string c = global::Rochas.OpenCodeBridge.Core.ToolSig.Target("read", PathArgs("/usr/src/opencodebridge"));
        string d = global::Rochas.OpenCodeBridge.Core.ToolSig.Target("read", PathArgs("/usr/src/opencodedbidge"));
        TestContext.Check(c != d, "letra trocada ainda e outro alvo (nao colapsa transposicao)");
        return Task.CompletedTask;
    }

    public static Task DetectsFailureMarkers()
    {
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.ToolSig.IsFailure("Exited with code 128"), "exit!=0");
        // Formato REAL do ProcessRunner ("exit <code>: <saida>") + erros de
        // compilador: com so "exited with code" o breaker ficava mudo no loop
        // medido em E-mission-tools (10 edits com o mesmo FS0751).
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.ToolSig.IsFailure(
            "exit 1: /logs/e2e/mfs/Program.fs(10,92): error FS0751: Expressão incompleta ou uso inválido da sintaxe do indexador"),
            "exit 1 + error FS");
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.ToolSig.IsFailure(
            "exit 1: Program.cs(3,1): error CS1002: ; esperado"), "error CS");
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.ToolSig.IsFailure("exit 104: build falhou"), "exit 104 (2 digitos)");
        TestContext.Check(!global::Rochas.OpenCodeBridge.Core.ToolSig.IsFailure("exit 0: 3 numeros"), "exit 0 nao e falha");
        TestContext.Check(!global::Rochas.OpenCodeBridge.Core.ToolSig.IsFailure("o manual manda rodar `exit 1` la na frente"), "prosa que cita exit nao e falha");
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.ToolSig.IsFailure("File not found: /x"), "file not found");
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.ToolSig.IsFailure("Search path does not exist: /x"), "search path");
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.ToolSig.IsFailure("/bin/bash: linha 1: sqlite3: comando nao encontrado\ncommand not found"), "command not found");
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.ToolSig.IsFailure("/bin/bash: -c: linha 1: erro de sintaxe"), "erro de sintaxe");
        TestContext.Check(!global::Rochas.OpenCodeBridge.Core.ToolSig.IsFailure("PASS 12 testes"), "sucesso nao e falha");
        TestContext.Check(!global::Rochas.OpenCodeBridge.Core.ToolSig.IsFailure(""), "vazio nao e falha");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Loop medido em E-mission-tools (evidencia em
    /// logs/levels/L3-20261028-161849/evidence/mission-agent.log): o MESMO edit
    /// devolvendo o MESMO error do compilador repetiu 10x sem nudge e sem trip,
    /// porque o marcador cobria so "exited with code" (formato morto). Com o
    /// marcador real: 3 falhas avisam, 6 cortam o turno.
    /// </summary>
    public static Task BreaksCompilerEditLoop()
    {
        static JsonArray Turnos(int n, string edit, string erro)
        {
            var passos = new List<(string, string, string, string)>();
            for (int i = 0; i < n; i++)
            {
                passos.Add(("assistant", "edit", edit, ""));
                passos.Add(("tool", "", "", erro));
            }
            return Chat(passos.ToArray());
        }

        string erro = "exit 1: /logs/e2e/mfs/Program.fs(10,92): error FS0751: Expressão incompleta ou uso inválido da sintaxe do indexador";
        string edit = "{\"path\":\"/logs/e2e/mfs/Program.fs\",\"oldString\":\"let sumTerms\",\"newString\":\"let sumTerms = f a + f b + List.sum (List.map ...)\"}";
        var guard = new global::Rochas.OpenCodeBridge.Core.ToolsBreaker(3, 6);

        var r2 = guard.Evaluate(Turnos(2, edit, erro));
        TestContext.Check(r2.Verdict == global::Rochas.OpenCodeBridge.Core.BreakerVerdict.None,
            $"2 falhas de compilador nao disparam (veio {r2.Verdict})");

        var r3 = guard.Evaluate(Turnos(3, edit, erro));
        TestContext.Check(r3.Verdict == global::Rochas.OpenCodeBridge.Core.BreakerVerdict.Nudge,
            $"3 falhas de compilador disparam nudge (veio {r3.Verdict})");
        TestContext.Check(r3.Streak == 3 && r3.DistinctTargets == 1,
            $"streak={r3.Streak} targets={r3.DistinctTargets} (mesmo alvo)");

        var r6 = guard.Evaluate(Turnos(6, edit, erro));
        TestContext.Check(r6.Verdict == global::Rochas.OpenCodeBridge.Core.BreakerVerdict.Trip,
            $"6 falhas de compilador cortam o turno (veio {r6.Verdict})");
        TestContext.Check(r6.Notice.Contains("6", StringComparison.Ordinal),
            $"aviso cita o total de falhas (notice: '{r6.Notice[..Math.Min(70, r6.Notice.Length)]}')");

        // Saidas que so citam exit no meio do texto nao viram falha.
        TestContext.Check(!global::Rochas.OpenCodeBridge.Core.ToolSig.IsFailure("exit 0: build ok"), "exit 0 segue sem falha");

        // 10a amostra do E-mission-algo-fs (2026-10-29): 15 `edit` com `oldString`
        // obsoleto no MESMO arquivo. O controle de loop ja' cobre o caso (mesmo alvo,
        // mensagem de falha) — o que faltava era o consumidor chamar. Aqui so' se
        // prova que ele dispara: nenhuma mecanica nova.
        string stale = "{\"path\":\"/tmp/e2e-mission/mfs/Program.fs\",\"oldString\":\"let rec hanoi (n: int)\",\"newString\":\"let rec hanoi n = ...\"}";
        string naoAchou = "ERRO: oldString não encontrado no arquivo. Copie o trecho EXATO de um `read` (um trecho curto, de uma linha, e' mais facil de acertar) ou grave o arquivo inteiro com `write`. Primeira linha de '/tmp/e2e-mission/mfs/Program.fs': module Program";
        var rStale3 = guard.Evaluate(Turnos(3, stale, naoAchou));
        TestContext.Check(rStale3.Verdict == global::Rochas.OpenCodeBridge.Core.BreakerVerdict.Nudge,
            $"3 edits com oldString obsoleto no mesmo arquivo -> nudge (veio {rStale3.Verdict})");
        TestContext.Check(rStale3.DistinctTargets == 1, $"o alvo e' o path do arquivo (veio {rStale3.DistinctTargets})");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Loop medido em E-mission-tools (logs/levels/L3-20261028-163556/evidence):
    /// edit/write OK intercalado com shell falhando no MESMO arquivo, 10x. O
    /// streak classico via 1 a cada par (sucesso zera) e o breaker ficava mudo;
    /// agora a janela por alvo conta as falhas e corta no hard.
    /// </summary>
    public static Task CountsInterleavedLoop()
    {
        static JsonArray Turnos(int falhas)
        {
            var passos = new List<(string, string, string, string)>();
            string path = "{\"path\":\"/tmp/e2e-mission/mfs/Program.fs\",\"oldString\":\"let sumTerms\",\"newString\":\"let sumTerms = ...\"}";
            string cmd = "{\"command\":\"dotnet run --project /tmp/e2e-mission/mfs/mfs.fsproj\"}";
            string erro = "ERRO: exit 1: /tmp/e2e-mission/mfs/Program.fs(11,22): error FS0010: símbolo '|' inesperado em expressão.";
            for (int i = 0; i < falhas; i++)
            {
                passos.Add(("assistant", "edit", path, ""));   // edit OK nao zera o loop
                passos.Add(("tool", "", "", "1 substituição em '/tmp/e2e-mission/mfs/Program.fs'"));
                passos.Add(("assistant", "shell", cmd, ""));
                passos.Add(("tool", "", "", erro));
            }
            return Chat(passos.ToArray());
        }

        var guard = new global::Rochas.OpenCodeBridge.Core.ToolsBreaker(3, 6);

        var r2 = guard.Evaluate(Turnos(2));
        TestContext.Check(r2.Verdict == global::Rochas.OpenCodeBridge.Core.BreakerVerdict.None,
            $"2 falhas intercaladas nao disparam (veio {r2.Verdict}, contagem {r2.Streak})");

        var r3 = guard.Evaluate(Turnos(3));
        TestContext.Check(r3.Verdict == global::Rochas.OpenCodeBridge.Core.BreakerVerdict.Nudge,
            $"3 falhas intercaladas no mesmo arquivo disparam nudge (veio {r3.Verdict})");
        TestContext.Check(r3.Streak == 3, $"janela conta as 3 falhas mesmo com edit OK no meio (veio {r3.Streak})");
        // So o shell falhou, entao ha UM alvo; o edit de sucesso nao conta.
        TestContext.Check(r3.DistinctTargets == 1, $"so o shell falhou = 1 alvo (veio {r3.DistinctTargets})");

        var r6 = guard.Evaluate(Turnos(6));
        TestContext.Check(r6.Verdict == global::Rochas.OpenCodeBridge.Core.BreakerVerdict.Trip,
            $"6 falhas intercaladas cortam o turno (veio {r6.Verdict})");

        // Variante com o edit TAMBEM falhando (oldString nao achado): path e
        // command normalizam em 2 alvos e o nudge ainda dispara (<= 2 alvos).
        // 2 iteracoes = 4 falhas seguidas (abaixo do hard 6).
        static JsonArray TurnosComEdicaoFalhando(int falhas)
        {
            var passos = new List<(string, string, string, string)>();
            string path = "{\"path\":\"/tmp/e2e-mission/mfs/Program.fs\",\"oldString\":\"let sumTerms\",\"newString\":\"let sumTerms = ...\"}";
            string cmd = "{\"command\":\"dotnet run --project /tmp/e2e-mission/mfs/mfs.fsproj\"}";
            for (int i = 0; i < falhas; i++)
            {
                passos.Add(("assistant", "edit", path, ""));
                passos.Add(("tool", "", "", "arquivo não encontrado: 'Program.fs'"));
                passos.Add(("assistant", "shell", cmd, ""));
                passos.Add(("tool", "", "", "ERRO: exit 1: error FS0010: símbolo '|' inesperado em expressão."));
            }
            return Chat(passos.ToArray());
        }
        var r4b = guard.Evaluate(TurnosComEdicaoFalhando(2));
        TestContext.Check(r4b.Verdict == global::Rochas.OpenCodeBridge.Core.BreakerVerdict.Nudge,
            $"edit+shell falhando sobre o MESMO arquivo = 2 alvos, nudge (veio {r4b.Verdict}, contagem {r4b.Streak})");
        TestContext.Check(r4b.DistinctTargets == 2, $"2 alvos esperados (veio {r4b.DistinctTargets})");
        return Task.CompletedTask;
    }

    // ---- dois escopos: conteudo (AntiRepeatReply) x tools (ToolSig/ToolsBreaker/RepeatedCall) ----

    public static Task ContentScopeUsesHelperSubclasses()
    {
        string loop = string.Concat(Enumerable.Repeat("repete isso ", 20));
        var stream = new global::Rochas.OpenCodeBridge.Core.StreamAntiRepeatReply(64);
        var args = new global::Rochas.OpenCodeBridge.Core.ToolArgsAntiRepeatReply(64);
        TestContext.Check(stream.Scope == "stream" && args.Scope == "tool-args", "helpers declaram o escopo");
        TestContext.Check(stream is global::Rochas.OpenCodeBridge.Core.AntiRepeatReply, "stream e helper de conteudo");
        TestContext.Check(stream is global::Rochas.OpenCodeBridge.Core.ContentAntiRepeat, "helper responde a classe-mae");
        TestContext.Check(stream.IsRepeat(loop), "cauda repetida no stream");
        TestContext.Check(args.IsRepeat(loop), "mesma regra para os argumentos");
        TestContext.Check(!stream.IsRepeat("texto unico, sem repeticao alguma aqui"), "texto novo nao dispara");
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.ContentAntiRepeat.Normalize("  Ola   MUNDO ") == "ola mundo", "normalizacao canonica");
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.ContentAntiRepeat.TailDuplicateAt(loop, 64) >= 0, "posicao da duplicata para a sonda");
        return Task.CompletedTask;
    }

    public static Task ToolScopeUsesHelperSubclasses()
    {
        var msgs = Chat(
            ("assistant", "read", PathArgs("/tmp/opencode/escopo/a.json"), ""),
            ("tool", "", "", "File not found: /tmp/opencode/escopo/a.json"),
            ("assistant", "read", PathArgs("/tmp/opencode/escopo/a.json"), ""),
            ("tool", "", "", "File not found: /tmp/opencode/escopo/a.json"),
            ("assistant", "read", PathArgs("/tmp/opencode/escopo/a.json"), ""),
            ("tool", "", "", "File not found: /tmp/opencode/escopo/a.json"));
        var breaker = new global::Rochas.OpenCodeBridge.Core.ToolsBreaker(3, 6);
        var repeated = new global::Rochas.OpenCodeBridge.Core.RepeatedCall(2);
        TestContext.Check(breaker.Scope == "failure-loop" && repeated.Scope == "repeated-call", "helpers declaram o escopo");
        TestContext.Check(breaker is global::Rochas.OpenCodeBridge.Core.ToolsAntiRepeat, "breaker responde a classe-mae");
        TestContext.Check(repeated is global::Rochas.OpenCodeBridge.Core.ToolsAntiRepeat, "repeated responde a classe-mae");
        TestContext.Check(breaker.Evaluate(msgs).Verdict == global::Rochas.OpenCodeBridge.Core.BreakerVerdict.Nudge, "3 falhas no mesmo alvo = nudge");
        TestContext.Check(repeated.Find(msgs).Count == 1, "assinatura reemitida encontrada");
        TestContext.Check(
            global::Rochas.OpenCodeBridge.Core.ToolSig.Of("SHELL", ShellArgs("ls")) ==
            global::Rochas.OpenCodeBridge.Core.ToolSig.Of("shell", ShellArgs("ls")), "assinatura canonica (caixa)");
        // Caso real: historico com JSON espaçado (": ") e modelo emitindo compacto (":").
        string spaced = "{\"command\": \"ls\"}";
        TestContext.Check(
            global::Rochas.OpenCodeBridge.Core.ToolSig.Of("shell", spaced) ==
            global::Rochas.OpenCodeBridge.Core.ToolSig.Of("shell", ShellArgs("ls")), "assinatura ignora espacos do JSON");
        var spacedLoop = Chat(
            ("assistant", "shell", spaced, ""),
            ("tool", "", "", "unknown option: --add"),
            ("assistant", "shell", spaced, ""),
            ("tool", "", "", "unknown option: --add"));
        TestContext.Check(repeated.Find(spacedLoop).Count == 1, "repeticao com JSON espaçado");
        return Task.CompletedTask;
    }

    public static Task FixesLiteralNewlineEscape()
    {
        // Caso real: here-doc com "\n" de 2 caracteres -> bash quebra.
        string broken = new JsonObject { ["command"] = "python3 - <<'PY'\\nimport sqlite3\\nprint(1)\\nPY" }.ToJsonString();
        var (name, fixedArgs) = global::Rochas.OpenCodeBridge.Program.FixLiteralEscapes("shell", broken);
        TestContext.Check(name == "shell", "nome preservado");
        string cmd = JsonNode.Parse(fixedArgs)?["command"]?.GetValue<string>() ?? "";
        TestContext.Check(!cmd.Contains("\\n"), "sem escape literal");
        TestContext.Check(cmd.Split('\n').Length == 4, $"4 linhas reais (veio {cmd.Split('\n').Length})");
        TestContext.Check(cmd.Contains("import sqlite3"), "conteudo preservado");
        // Comando com quebra real: nao mexe.
        string okCmd = new JsonObject { ["command"] = "echo a\necho b" }.ToJsonString();
        var (_, untouched) = global::Rochas.OpenCodeBridge.Program.FixLiteralEscapes("shell", okCmd);
        TestContext.Check(untouched == okCmd, "comando multi-linha real passa intacto");
        // Outra tool: nao mexe.
        string writeArgs = new JsonObject { ["path"] = "a.txt", ["content"] = "a\\nb" }.ToJsonString();
        var (wn, wa) = global::Rochas.OpenCodeBridge.Program.FixLiteralEscapes("write", writeArgs);
        TestContext.Check(wn == "write" && wa == writeArgs, "write sem escape literal preserva conteudo");
        return Task.CompletedTask;
    }

    public static Task ToolsTailCarriesPathAndSimulationRules()
    {
        // A cauda e COMPLEMENTO do main harness: so a mecanica que o main nao
        // tem. Regra de comportamento continua no main (fonte unica) e este
        // teste garante que ninguem a duplique na cauda.
        string tail = global::Rochas.OpenCodeBridge.Core.HarnessTails.DefaultTools;
        TestContext.Check(tail.Contains("contrato de cada tool vai no schema"), "cauda aponta o schema como fonte");
        TestContext.Check(tail.Contains("bloqueio de role"), "cauda mantem bloqueio de role");
        TestContext.Check(tail.Contains("--project"), "cauda aponta o arquivo do projeto no dotnet");
        string fsMini = global::Rochas.OpenCodeBridge.Core.HarnessTails.FsMini;
        TestContext.Check(fsMini.Contains("module Program"), "FsMini explica o entrypoint de F#");
        // Regra que derrubou E-mission-tools (L3-20261028-161849): `;` nao e' passo;
        // o range com passo e' `[start .. step .. finish]`. O literal INVALIDO nao
        // pode aparecer na cauda: medido em #13 do DRIVER-FIXES, o modelo COPIA o
        // literal que a dica mostra. Por isso o teste exige a forma correta E a
        // ausencia da forma errada copiavel.
        TestContext.Check(fsMini.Contains("[for i in 1 .. 2 .. n-1]"), "FsMini ensina range com passo (forma correta)");
        TestContext.Check(fsMini.Contains("FS0751"), "FsMini nomeia o codigo do range com ';'");
        TestContext.Check(!fsMini.Contains("[1; 3 .. n-1]"), "FsMini nao repete o literal invalido (o modelo copia)");
        // 2a forma medida no L3-20261028-163556: o modelo escreveu comprehension
        // de Haskell (`[i | i <- ...]`) e errou FS0010 por 22 turnos.
        TestContext.Check(fsMini.Contains("[for i in 1 .. n-1 -> expr]"), "FsMini ensina comprehension de F#");
        TestContext.Check(fsMini.Contains("LISTA e ARRAY"), "FsMini separa array literal de comprehension de lista");
        // 3a e 4a formas medidas no L3-20261029-030937: `do` na lista sem `yield`
        // (lista tipada unit) e `step` no range (nao existe em F#).
        TestContext.Check(fsMini.Contains("do yield"), "FsMini ensina do+yield como alternativa");
        TestContext.Check(fsMini.Contains("PASSO e' o SEGUNDO termo"), "FsMini ensina a ordem do range (passo no meio)");
        TestContext.Check(fsMini.Contains("Array.sum"), "FsMini separa array de lista (List.* x Array.*)");
        TestContext.Check(!fsMini.Contains("1 .. n - 1 .. 2"), "FsMini nao exibe a ordem trocada (o modelo copia)");
        // Escopo inteiro (2026-10-29): sem ponto flutuante, o bloco MATH e as regras
        // de int/float sairam — cauda menor, sem literal que induza `** 2.0`.
        TestContext.Check(fsMini.Contains("printfn \"%d\""), "FsMini ensina saida inteira");
        TestContext.Check(!fsMini.Contains("System.Math"), "FsMini nao carrega mais regra de Math (escopo inteiro)");
        TestContext.Check(!fsMini.Contains("** 2.0"), "FsMini nao ensina potencia float");
        TestContext.Check(!fsMini.Contains("TIPOS NUMERICOS"), "FsMini nao carrega mais a regra int/float");
        // Estrutura (2026-10-29): `let` aninhado no ramo quebra o offside (FS0058) e
        // funcao recursiva sem `rec` da FS0039 — as duas formas medidas no isolado.
        TestContext.Check(fsMini.Contains("AUXILIAR"), "FsMini ensina onde vive a funcao auxiliar");
        TestContext.Check(fsMini.Contains("let rec"), "FsMini ensina recursao sem `rec` (FS0039)");
        TestContext.Check(fsMini.Contains("FS0039 nome nao definido") && fsMini.Contains("resto de inteiro e' `a % b`"),
            "tabela F# ensina resto real na MESMA linha do FS0039");
        TestContext.Check(fsMini.Split("FS0039").Length - 1 == 1,
            "FS0039 aparece UMA vez (sem linha duplicada)");
        TestContext.Check(fsMini.Contains("FS0037 nome repetido"), "tabela F# ensina nome unico (FS0037)");
        TestContext.Check(fsMini.Contains("no range os dois limites sao do MESMO tipo"), "tabela F# ensina range de tipo unico (FS0001)");
        TestContext.Check(fsMini.Contains("PRIMOS: divisores candidatos de 2"), "FsMini separa intervalo de entrada do de divisores");
        // Sem duplicidade ambigua: cada codigo da tabela tem UMA unica linha.
        foreach (var code in new[] { "FS0010", "FS0751", "FS0003", "FS0001", "FS0039", "FS0037", "FS0058", "FS0988", "FS0020", "FS3563", "FS0752" })
            TestContext.Check(fsMini.Split(code).Length - 1 == 1, $"FsMini cita {code} uma unica vez");
        TestContext.Check(!tail.Contains("GLOB: nao tem alternancia"), "cauda nao duplica doc de glob");
        TestContext.Check(!tail.Contains("git init <dir>"), "cauda nao duplica doc de git");
        TestContext.Check(!tail.Contains("CONCATENACAO DE COMANDOS"), "cauda nao duplica doc de shell");
        TestContext.Check(!tail.Contains("NUNCA simule execucao") && !tail.Contains("sudo"),
            "cauda nao duplica regra de comportamento (moram no main)");

        string main = global::Rochas.OpenCodeBridge.Core.MainHarness.Text;
        TestContext.Check(main.Contains("Nunca simule execução"), "main proibe build simulado");
        TestContext.Check(main.Contains("sudo"), "main proibe sudo sem ordem");
        TestContext.Check(main.Contains("ls -la"), "main exige probe com ls -la");
        return Task.CompletedTask;
    }

    public static Task BehaviorTailForcesToolFirst()
    {
        string tail = global::Rochas.OpenCodeBridge.Core.HarnessTails.DefaultBehavior;
        TestContext.Check(tail.Contains("ZERO linha de prosa"), "behavior exige tool antes de prosa");
        TestContext.Check(tail.Contains("PERSISTENCIA"), "behavior exige continuar no mesmo turno");
        TestContext.Check(tail.Contains("PRIORIDADE"), "behavior resolve conflito com o nativo");
        TestContext.Check(tail.Contains("execute primeiro"), "behavior executa antes de explicar");
        TestContext.Check(!tail.Contains("no maximo 2 linhas antes"), "behavior nao autoriza 2 linhas de prosa");
        return Task.CompletedTask;
    }

    // ---- HTTP: dependem do binario novo rodando (skip em bridge antiga) ----

    static async Task RequireNewBridge()
    {
        var st = await TestContext.GetObj(TestContext.Bridge + "/api/status");
        if (st["tail_scope"] is null) TestContext.Skip("bridge sem --tail-scope (binario antigo)");
    }

    public static async Task TranslateTempRespeitaTunel()
    {
        await RequireNewBridge();
        // Doutrina de temperatura por perfil (build 0.1) so vale no modo bridge
        // (a raia WAR/bench traz --main-harness 1 e perfil build 0.1). Na raia
        // opencode o deployment e opencode (main_harness 0, perfil proprio) e o
        // tunel nao segue essa doutrina: o teste passa como SKIP em vez de FAIL.
        if (TestContext.Mode == "opencode")
            TestContext.Skip("perfil build 0.1 e doutrina do modo bridge; a raia opencode usa o harness/perfil proprio");
        var harness = await TestContext.GetObj(TestContext.Bridge + "/api/harness");
        bool tailsEnabled = harness["harness_tails"]?.GetValue<bool>() ?? true;
        string scope = harness["tail_scope"]?.GetValue<string>() ?? "";
        int tailChars = (harness["behavior_chars"]?.GetValue<int>() ?? 0) + (harness["tools_chars"]?.GetValue<int>() ?? 0);
        // O tail-scope decide por CLIENTE (nao pelo sufixo do modelo): este teste
        // fala direto com a bridge (UA != opencode), logo recebe a cauda em "all"
        // e em "managed"; so "off" desliga.
        bool expectTails = tailsEnabled && scope is "all" or "managed" && tailChars > 0;
        string model = (TestContext.ServedModel.Length > 0 ? TestContext.ServedModel : "qualquer") + "-build";

        // 1) cliente informa temperatura: passa intacta (quem manda e o tunel)
        var comTemp = await TestContext.PostObj(TestContext.Bridge + "/api/translate", new JsonObject
        {
            ["model"] = model,
            ["temperature"] = 0.5,
            ["input"] = "oi"
        });
        double t1 = comTemp["temperature"]?.GetValue<double>() ?? -1;
        string s1 = comTemp["temperature_source"]?.GetValue<string>() ?? "";
        TestContext.Check(Math.Abs(t1 - 0.5) < 0.0001, $"temperatura do cliente preservada (veio {t1})");
        TestContext.Check(s1 == "request", $"procedencia request (veio '{s1}')");

        // 2) cliente omite: vale o perfil do role (-build = 0.1)
        var semTemp = await TestContext.PostObj(TestContext.Bridge + "/api/translate", new JsonObject
        {
            ["model"] = model,
            ["input"] = "oi"
        });
        double t2 = semTemp["temperature"]?.GetValue<double>() ?? -1;
        string s2 = semTemp["temperature_source"]?.GetValue<string>() ?? "";
        // Doutrina WAR: perfil build = 0.1 (thinking off) no appsettings de
        // ORIGEM; o tunel devolve o perfil, nao um default de 0.2.
        TestContext.Check(Math.Abs(t2 - 0.1) < 0.0001, $"sem temperatura vale o perfil build 0.1 (veio {t2})");
        TestContext.Check(s2.StartsWith("profile"), $"procedencia profile (veio '{s2}')");
        TestContext.Check(semTemp["tails_on"]?.GetValue<bool>() == expectTails,
            $"cauda no caminho opencode segue o deployment (scope={scope}, tails={tailsEnabled})");
    }

    public static async Task TranslateBreakerVerdicts()
    {
        await RequireNewBridge();
        string model = (TestContext.ServedModel.Length > 0 ? TestContext.ServedModel : "m") + "-build";

        JsonObject Body(int failures, string pathBase) => new()
        {
            ["model"] = model,
            ["input"] = new JsonArray(BuildInput(failures, pathBase))
        };

        var none = await TestContext.PostObj(TestContext.Bridge + "/api/translate", Body(2, "/tmp/opencode/breaker/a"));
        TestContext.Check(none["breaker"]?.GetValue<string>() == "none", "2 falhas = sem breaker");

        var nudge = await TestContext.PostObj(TestContext.Bridge + "/api/translate", Body(3, "/tmp/opencode/breaker/a"));
        TestContext.Check(nudge["breaker"]?.GetValue<string>() == "nudge", "3 falhas = nudge (sem curto-circuito)");
        string sys = nudge["chat_request"]?["messages"]?.AsArray().Last()?["content"]?.GetValue<string>() ?? "";
        TestContext.Check(sys.Contains("BRIDGE-BREAKER"), "nudge injetado no ultimo resultado");

        var trip = await TestContext.PostObj(TestContext.Bridge + "/api/translate", Body(6, "/tmp/opencode/breaker/a"));
        TestContext.Check(trip["breaker"]?.GetValue<string>() == "trip", "6 falhas = trip");
    }

    /// <summary>Input Responses com N chamadas de read que falham no MESMO alvo mutado.</summary>
    static JsonNode[] BuildInput(int failures, string pathBase)
    {
        var items = new List<JsonNode>();
        for (int i = 0; i < failures; i++)
        {
            string id = "call_" + i;
            items.Add(new JsonObject
            {
                ["type"] = "function_call", ["id"] = id, ["call_id"] = id,
                ["name"] = "read", ["arguments"] = PathArgs($"{pathBase}-{i:000}.json")
            });
            items.Add(new JsonObject
            {
                ["type"] = "function_call_output", ["call_id"] = id,
                ["output"] = $"File not found: {pathBase}-{i:000}.json"
            });
        }
        return items.ToArray();
    }

    // ---- reparo F#: o CERTO tem que compilar (medido no L3 de 2026-10-29) ----

    // A versao anterior devolvia `[for i in 1 .. n-1 do i -> ...]`, que nao compila,
    // e o modelo copia o literal da dica (DRIVER-FIXES #13). A tabela abaixo pina os
    // casos que ja' venciavam (comprehension com '|', `step`) e proibe a regressao.
    public static Task FsRepairComprehensionForms()
    {
        static string Fs(string src, string err)
            => global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(src, err);
        const string fs0010Pipe = "error FS0010: Unexpected symbol '|' in expression. Expected ']' or other token.";
        const string fs0010Arrow = "error FS0010: Unexpected symbol '->' in expression.";
        const string fs0001Unit = "error FS0001: The type 'unit' does not support the operator '+'";
        const string fs0039Math = "error FS0039: The namespace 'Math' is not defined.";

        // 1) Caso historico (#13): '|' de outra linguagem, com `do i` sobrando no range.
        string h1 = Fs("let xs = [f (a + float i * h) | for i in 1 .. n - 1 do i]", fs0010Pipe);
        TestContext.Check(h1.Contains("[for i in 1 .. n - 1 -> f (a + float i * h)]"),
            "pipe -> forma com '->' (veio: " + h1.Replace("\n", " | ") + ")");
        TestContext.Check(!h1.Contains("do i ->"), "CERTO nao mistura 'do' com '->'");

        // 2) Caso historico: `step` no range vira passo no MEIO.
        string h2 = Fs("let xs = [f i | for i in 1 .. n-1 step 2]", fs0010Pipe);
        TestContext.Check(h2.Contains("[for i in 1 .. 2 .. n-1 -> f i]"),
            "range com 'step' -> passo no meio (veio: " + h2.Replace("\n", " | ") + ")");

        // 3) Caso historico: comprehension sem filtro, Haskell/Python puro.
        string h3 = Fs("let sq = [ (x - mean) ** 2.0 | for x in data ]", fs0010Pipe);
        TestContext.Check(h3.Contains("[for x in data -> (x - mean) ** 2.0]"), "pipe simples -> '->'");

        // 4) `do` sem yield dentro de lista (FS0001 'unit') -> '->' com if/then/else.
        string n1 = Fs("let s = [for i in 1 .. n-1 do if i % 2 <> 0 then f (a + float i * h) else 0.0]", fs0001Unit);
        TestContext.Check(n1.Contains("[for i in 1 .. n-1 -> if i % 2 <> 0 then f (a + float i * h) else 0.0]"),
            "'do' puro -> '->' preservando o if/then/else");

        // 5) `do` + `->` juntos: a condicao continua sendo FILTRO (`do if ... then
        // yield`). Nao convertemos paridade em passo — foi essa conversao que ensinou
        // `1 .. n-1 .. 2` (ordem trocada, resultado errado) no isolado de 2026-10-29.
        string n2 = Fs("let s = [for i in 1 .. n - 1 do i =% 2 -> 4.0 * f (float i * h)]", fs0010Arrow);
        TestContext.Check(n2.Contains("[for i in 1 .. n - 1 do if i % 2 <> 0 then yield 4.0 * f (float i * h)]"),
            "do+-> com condicao -> 'do if ... then yield' (veio: " + n2.Replace("\n", " | ") + ")");
        string n3 = Fs("let e = [4.0 * f i | for i in 2 .. n - 2 do i % 2 = 0]", fs0010Pipe);
        TestContext.Check(n3.Contains("[for i in 2 .. n - 2 do if i % 2 = 0 then yield 4.0 * f i]"), "condicao vira filtro no yield");

        // 5) array-comprehension do modelo (`[| expr | for i in ... step 2 |]`): o
        // reparo antigo devolvia `[for i in 1 .. n - 1 step 2 | -> | expr]` (lixo com
        // `step` e barras) e o modelo copiava; medido no isolado de 2026-10-29.
        string n7 = Fs("let so = List.sum [| 2.0 * f (a + float i * h) | for i in 1 .. n - 1 step 2 |]",
            "error FS0010: Unexpected symbol '|' in expression. Expected '|]' or other token.");
        TestContext.Check(n7.Contains("[for i in 1 .. 2 .. n - 1 -> 2.0 * f (a + float i * h)]"),
            "array-comprehension -> lista com passo no meio (veio: " + n7.Replace("\n", " | ") + ")");
        // So' o CERTO e' contrato: o ERRADO ecoa o fonte do modelo (que tem `step`).
        string certo7 = n7[(n7.IndexOf("CERTO: ", StringComparison.Ordinal) + 7)..];
        TestContext.Check(!certo7.Contains("step"), "CERTO nunca ensina `step` (nao existe em F#)");
        TestContext.Check(!certo7.Contains("| ->"), "CERTO nao carrega a barra do array para o arrow");
        string n8 = Fs("let se = List.sum [| 2.0 * f (float i) | for i in 2 .. n - 2 step 2]",
            "error FS0010: Unexpected symbol '|' in expression. Expected '|]' or other token.");
        TestContext.Check(n8.Contains("[for i in 2 .. 2 .. n - 2 -> 2.0 * f (float i)]"),
            "array-comprehension sem barra final -> lista (veio: " + n8.Replace("\n", " | ") + ")");

        // 6) `open System.Math` (FS0039): sem conserto deterministico -> silencio.
        string n4 = Fs("module Program\nopen System.Math\nlet x = Sin 1.0", fs0039Math);
        TestContext.Check(n4 == "", "FS0039 sozinho nao recebe conserto inventado (silencio)");

        // 6a) `step` no range sai como FS0003: normaliza pela FONTE (passo no MEIO).
        string n9 = Fs("let so = [for i in 1 .. n-1 step 2 -> i]",
            "error FS0003: This value is not a function and cannot be applied. It has type 'int', which does not accept arguments.");
        TestContext.Check(n9.Contains("CERTO: 1 .. 2 .. n-1"), "FS0003 com 'step' -> passo no MEIO (veio: " + n9.Replace("\n", " | ") + ")");

        // 6b2) Saida multi-erro: quem decide e' o PRIMEIRO codigo. FS0039 na frente
        // com FS0001 (list/array) depois NAO pode virar dica de array.
        TestContext.Check(Fs("let data = [|2.0|]\nlet d = [for x in data -> x]",
            "error FS0039: The value or constructor 'data' is not defined.\nerror FS0001: This expression was expected to have type ''a list' but here has type ''b array'") == "",
            "primeiro erro FS0039 silencia (nao responde com dica de array)");

        // 6b4) Crase simples em nome/operador (FS3563): `mod` e' operador puro.
        string n11 = Fs("let rec gcd a b = if b = 0 then a else gcd b (a `mod` b)",
            "error FS3563: This is not a valid identifier");
        TestContext.Check(n11.Contains("CERTO: mod") && !n11.Contains("CERTO: `mod`"),
            "FS3563 -> operador sem crase (veio: " + n11.Replace("\n", " | ") + ")");

        // 6b6) `mod` (OCaml/Pascal) em F#: FS0039 nomeia o operador -> resto e' `%`.
        string n13 = Fs("let rec gcd a b = if b = 0 then a else gcd b (a mod b)",
            "error FS0039: The value or constructor 'mod' is not defined.");
        TestContext.Check(n13.Contains("CERTO: a % b"), "FS0039 'mod' -> operador % (veio: " + n13.Replace("\n", " | ") + ")");

        // 6b7) Nome definido duas vezes (FS0037) e range com limite float (FS0001).
        TestContext.Check(Fs("let isPrime n = n > 1\nlet isPrime n = n > 2",
            "error FS0037: Duplicate definition of value 'isPrime'").Contains("UMA vez"),
            "FS0037 -> nome definido uma vez");
        TestContext.Check(Fs("let ps = [1..System.Math.Sqrt(float n)]",
            "error FS0001: This expression was expected to have type 'int' but here has type 'float'").Contains("int (Math.Sqrt(float n))"),
            "FS0001 no range -> converte o limite");

        // 6b8) FS0001 com a MESMA mensagem de outro caso (ramos do `if`): a dica e'
        //      a do `if` — array junta com Array.append — e nao a do range (medido no
        //      isolado de 2026-10-29).
        string ramos = Fs("let rec h n =\n    if n = 1 then [| (1, 3) |]\n    else let a = h (n - 1)\n         [| (1, 2) |] @ a",
            "error FS0001: All branches of an 'if' expression must return values implicitly convertible to the type of the first branch, which here is '('a * 'a) array'. This branch returns a value of type ''b list'.");
        TestContext.Check(ramos.Contains("Array.append"),
            "FS0001 ramos do if -> array junta com Array.append (veio: " + ramos.Replace("\n", " | ") + ")");
        TestContext.Check(!ramos.Contains("Math.Sqrt"),
            "FS0001 ramos do if nao recebe a dica de range");
        TestContext.Check(Fs("let ps = [1..System.Math.Sqrt(float n)]",
            "error FS0001: All branches of an 'if' expression must return values implicitly convertible to the type of the first branch").Contains("Array.append"),
            "FS0001 com mensagem de if vence o range mesmo com range no fonte");

        // 6b5) `let` aninhado na linha do `else` (FS0058): o auxiliar vai para o
        // nivel do modulo, recebendo por parametro o que usa de fora.
        string n12 = Fs("let isPrime n =\n    if n < 2 then false\n    else let rec check i = i * i > n\n    in check 5",
            "error FS0058: Unexpected syntax or possible incorrect indentation: this token is offside of context started at position (20:10).");
        TestContext.Check(n12.Contains("ERRADO: else let rec check i") && n12.Contains("nivel do modulo"),
            "FS0058 -> auxiliar no nivel do modulo (veio: " + n12.Replace("\n", " | ") + ")");

        // 6b3) Auto-referencia sem `rec`: o nome que falta e' um `let` com parametro
        // do proprio arquivo -> falta `rec` (nao e' valor nem identificador inventado).
        string n10 = Fs("let factorial n = if n = 0 then 1 else n * factorial (n - 1)\nlet x = factorial 10",
            "error FS0039: The value or constructor 'factorial' is not defined.");
        TestContext.Check(n10.Contains("CERTO: let rec factorial"), "FS0039 de auto-referencia -> `let rec` (veio: " + n10.Replace("\n", " | ") + ")");
        TestContext.Check(Fs("let rec f n = if n = 0 then 1 else n * f (n - 1)",
            "error FS0039: The value or constructor 'f' is not defined.") == "", "ja' com `rec` nao e' tocado");

        // 6b) Filtro legitimo sem operador NAO pode ser descartado (perderia a
        // condicao em silencio: compila e calcula errado).
        string n5 = Fs("let a = [for x in xs do isAdult x -> x]", fs0010Arrow);
        TestContext.Check(n5.Contains("[for x in xs do if isAdult x then yield x]"),
            "condicao sem operador vira 'do if ... then yield' (veio: " + n5.Replace("\n", " | ") + ")");
        // 6c) Erro fora do mapa nao recebe conserto de comprehension (linha errada).
        TestContext.Check(Fs("let a = [x | for x in xs]", "error FS0410: The type 'int' is not compatible") == "",
            "erro alheio nao recebe conserto de comprehension");

        // 6d) `[| ... |]` (array) entregue a `List.sum` (FS0001 list vs array): a dica
        // nomeia a lista e o Array.* correto — 21 turnos perdidos no isolado de
        // 2026-10-29 reescrevendo o mesmo arquivo sem entender o erro.
        string n6 = Fs("let dataList = [|2.0; 4.0|]\nlet sum = List.sum dataList",
            "error FS0001: This expression was expected to have type    ''a list'    but here has type    ''b array'");
        TestContext.Check(n6.Contains("Array.sum"), "FS0001 list/array ensina Array.sum");
        TestContext.Check(n6.Contains("[ a; b ]"), "FS0001 list/array mostra a lista");

        // 7) Negativos: forma JA' valida nao pode ser 'corrigida'.
        TestContext.Check(Fs("let xs = [for i in 1 .. 3 -> float i]", fs0010Arrow) == "",
            "arrow puro (valido) nao e' tocado");
        TestContext.Check(Fs("let xs = [for i in 1 .. n do if i % 2 <> 0 then yield float i]", fs0010Arrow) == "",
            "do + yield (valido) nao e' tocado");
        TestContext.Check(Fs("let xs = [1 .. 2 .. 9]", "error FS0010: x") == "", "lista sem comprehension nao e' tocada");
        return Task.CompletedTask;
    }

    // L4 de 2026-10-29 (step algo-bridge): a 1a linha saiu `module Program open System`
    // e o `let` do topo virou FS0010 "Unexpected keyword 'let' or 'use' in implementation
    // file" — a mensagem do compilador nao diz a forma certa e o modelo repetiu. A dica
    // devolve SO' a forma que compila (module sozinho + cada `open` na propria linha).
    public static Task FsRepairModuleLineAlone()
    {
        const string err = "error FS0010: Unexpected keyword 'let' or 'use' in implementation file";
        string glued = global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(
            "module Program open System\n\n// teste\nlet soma a b = a + b\n", err);
        TestContext.Check(glued.Contains("module Program\nopen System"),
            "module + open colados -> uma diretiva por linha (veio: " + glued.Replace("\n", " | ") + ")");
        TestContext.Check(!glued.Contains("module Program open"), "a dica nao repete a forma que nao compila");
        // Dois `open` na mesma linha do modulo: cada um sai na sua linha.
        string dois = global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(
            "module Program open System open System.IO\nlet x = 1\n", err);
        TestContext.Check(dois.Contains("module Program\nopen System\nopen System.IO"),
            "dois `open` -> uma linha cada (veio: " + dois.Replace("\n", " | ") + ")");
        // Arquivo ja' com o cabecalho correto: nenhuma dica inventada.
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(
            "module Program\n\nopen System\n\nlet soma a b = a + b\n", err) == "",
            "cabecalho ja' correto nao recebe dica");
        // O MESMO cabecalho colado reaparece como 'open' inesperado quando a linha de
        // baixo tambem e' um open (medido no isolado de 2026-10-29, T2/T3).
        string asOpen = global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(
            "module Program open System\nopen System.Linq\n\nlet x = 1\n",
            "error FS0010: Unexpected keyword 'open' in implementation file");
        TestContext.Check(asOpen.Contains("module Program\nopen System"),
            "'open' inesperado no cabecalho colado -> mesma dica (veio: " + asOpen.Replace("\n", " | ") + ")");
        TestContext.Check(!asOpen.Contains("module Program open"), "a dica do 'open' tambem nao repete a forma colada");
        return Task.CompletedTask;
    }

    // L4/isolado de 2026-10-29: o F# do modelo declarava a funcao com `=` e engolia o
    // resultado num `let` interno (FS3118/FS0588) e usava array-comprehension com a
    // barra no meio (FS0603). A dica devolve a forma que compila, com o corpo alinhado.
    public static Task FsRepairFunctionBodyShape()
    {
        static string Fs(string src, string err)
            => global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(src, err);
        string corpo = Fs("let primesCount n =\n    let countPrimes limit =\n        limit\n",
            "error FS3118: Incomplete value or function definition.");
        TestContext.Check(corpo.Contains("let total xs =\n    let dobro") && corpo.Contains("List.sum dobro"),
            "FS3118 -> corpo com let local e ultima expressao como valor (veio: " + corpo.Replace("\n", " | ") + ")");
        string inacabado = Fs("let f n =\n    let g = n\n",
            "error FS0588: The block following this 'let' is unfinished.");
        TestContext.Check(inacabado.Contains("List.sum dobro"), "FS0588 -> mesma forma de corpo");
        string eof = Fs("let f n =\n    let g = n\n", "error FS0528: Unexpected end of input");
        TestContext.Check(eof.Contains("List.sum dobro"), "FS0528 -> mesma forma de corpo");
        string arr = Fs("let s = [| if p i then 1 else 0 | for i in 2 .. n |]",
            "error FS0603: ''[|'' incompatible.");
        TestContext.Check(arr.Contains("[| for i in 1 .. 3 -> i * i |]"),
            "FS0603 -> comprehension de array inteira entre [| e |] (veio: " + arr.Replace("\n", " | ") + ")");
        TestContext.Check(!arr.Contains("| for i in 2 .."), "a dica nao repete a barra no meio");
        return Task.CompletedTask;
    }

    // Isolado de 2026-10-29: o modelo repetiu a MESMA linha em 3 builds seguidos
    // (FS0597) porque o codigo nao tinha dica — cada build custa minutos de GPU.
    public static Task FsRepairCallArgumentParens()
    {
        string p = global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(
            "let rotulo movs = printfn \"%s\" String.concat \",\" movs\n",
            "error FS0597: This argument expression needs parentheses. Expressions involving function or method calls must be parenthesized when passed as arguments, e.g. 'printfn \"%s\" (arg.Trim())'.");
        TestContext.Check(p.Contains("(String.concat \",\" movs)"),
            "FS0597 -> argumento entre parenteses (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(p.Contains("printfn \"%s\" (") && !p.Contains("\" String.concat"),
            "FS0597 -> a dica mostra so a forma que compila");
        return Task.CompletedTask;
    }

    // Isolado de 2026-10-29 (4a amostra do F#): programa ja' calculando, travado em
    // FS0072 na linha da formatacao — o codigo nao tinha dica e a fase morreu nela.
    public static Task FsRepairInferredTypeAnnotation()
    {
        string p = global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(
            "printfn \"%s\" (String.concat \",\" (List.map (fun move -> sprintf \"%d->%d\" move.Item1 move.Item2) movs))\n",
            "error FS0072: The type of this expression cannot be determined here, as no further information is available.");
        TestContext.Check(p.Contains("(a: int, b: int)") && p.Contains("List.map"),
            "FS0072 -> anotacao no lambda (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(p.Contains("sprintf \"%d->%d\" a b"), "FS0072 -> tuple desmontado com a e b");
        return Task.CompletedTask;
    }

    // 5a amostra do F# (2026-10-29): o modelo escreveu `[s->d]` (o `->` do exemplo de
    // comprehension vazou para a lista literal) e nao havia dica: 30 turnos em zero.
    public static Task FsRepairListLiteralArrow()
    {
        string p = global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(
            "let rec solve k =\n    if k = 1 then\n        [s->d]\n    else\n        [s->d]\n",
            "error FS0010: Unexpected symbol '->' in expression. Expected ']' or other token.");
        TestContext.Check(p.Contains("[ (1, 2); (1, 3) ]"),
            "FS0010 '->' em lista -> lista com ; e tupla entre parenteses (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(p.Contains("[ for i in 1 .. 3 -> i * i ]"),
            "a dica separa comprehension (onde o -> existe) da lista literal");
        TestContext.Check(!p.Contains("[s->d]"), "a dica nao ecoa a forma errada");
        // Com comprehension no fonte (`for ... in`) a dica continua sendo a da
        // comprehension: o `->` ali e' legal e o defeito e' outro (`do`, `|`, `step`).
        string comp = global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(
            "let s = [for i in 1 .. n - 1 do i =% 2 -> 4.0 * f (float i * h)]",
            "error FS0010: Unexpected symbol '->' in expression.");
        TestContext.Check(comp.Contains("do if") || comp.Contains("yield"),
            "FS0010 '->' com comprehension mantem a dica de comprehension (veio: " + comp.Replace("\n", " | ") + ")");
        return Task.CompletedTask;
    }

    // 6a amostra do F# (2026-10-29): `|> List.append` sem o segundo operando
    // (FS0193, "missing arguments") repetido em 4 builds sem dica.
    public static Task FsRepairPartialOperator()
    {
        string p = global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(
            "let rec hanoi n a b c =\n    if n = 1 then [ a, b ]\n    else hanoi (n - 1) a c b |> List.append\n",
            "warning FS0193: This expression is a function value, i.e. is missing arguments. Its type is (^a * ^b) list -> (^a * ^b) list.");
        TestContext.Check(p.Contains("a @ b") && p.Contains("List.append a b"),
            "FS0193 -> operador exige os dois operandos (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(p.Contains("x |> List.append ys"),
            "FS0193 -> no pipe o segundo operando vem explicito");
        TestContext.Check(!p.Contains("|> List.append\n"), "a dica nao ecoa o pipe sem o segundo operando");
        return Task.CompletedTask;
    }

    // 7a amostra (2026-10-29): a fase F# so' injetava a dica quando a saida tinha
    // "error FS"; o FS0193 veio como WARNING e o reparo nunca entrou no fluxo.
    public static Task FsRepairDiagnosticDetection()
    {
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.HasDiagnostic(
            "ERRO: exit 1: /x/Program.fs(14,9): warning FS0193: This expression is a function value."),
            "warning FSdddd conta como diagnostico (era o caso que ficava sem dica)");
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.HasDiagnostic(
            "ERRO: exit 1: /x/Program.fs(3,1): error FS0010: Unexpected symbol."),
            "error FSdddd conta como diagnostico");
        TestContext.Check(!global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.HasDiagnostic(
            "ERRO: exit 1: MSBUILD : error MSB1009: Project file does not exist."),
            "erro sem codigo FS nao dispara reparo de sintaxe F#");
        TestContext.Check(!global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.HasDiagnostic(""),
            "saida vazia nao dispara reparo");
        return Task.CompletedTask;
    }

    // Garante que a tabela que a fase F# injeta no system cobre TODA regra que o
    // reparo sabe responder — sem isso o modelo recebe a forma certa no turno da
    // falha mas nao tem o contrato antes de agir.
    public static Task FsMiniCoversRepairCodes()
    {
        string mini = global::Rochas.OpenCodeBridge.Core.HarnessTails.FsMini;
        TestContext.Check(mini.StartsWith("F# MINI", StringComparison.Ordinal),
            "FsMini comeca com o marcador 'F# MINI' (o dump da fase F# confere ele)");
        string[] codes = { "FS0001", "FS0003", "FS0010", "FS0020", "FS0037", "FS0039", "FS0058",
            "FS0072", "FS0193", "FS0528", "FS0588", "FS0597", "FS0603", "FS0751", "FS3118", "FS3563" };
        foreach (string c in codes)
            TestContext.Check(mini.Contains(c, StringComparison.Ordinal), "FsMini cita " + c + " (tem regra de reparo)");
        return Task.CompletedTask;
    }

    // 8a amostra (2026-10-29): `(moves: ref seq<string>)` — `ref` e' tipo
    // POS-fixado; a mensagem FS0010 "Unexpected type application" nao tinha dica.
    public static Task FsRepairRefType()
    {
        string p = global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(
            "let rec hanoi (n: int) (moves: ref seq<string>) =\n    if n = 1 then ()\n",
            "error FS0010: Unexpected type application  in pattern. Expected ')' or other token.");
        TestContext.Check(p.Contains("string list ref"),
            "FS0010 ref -> tipo pos-fixado (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(p.Contains(":=") && p.Contains("!moves"),
            "FS0010 ref -> escrita com `:=` e leitura com `!`");
        TestContext.Check(!p.Contains("ref seq"), "a dica nao ecoa a forma prefixa errada");
        return Task.CompletedTask;
    }

    // Dica identica repetida nao ensina: a escalada e' do Core (repeatCount), nao do teste.
    public static Task FsRepairRepeatEscalation()
    {
        const string err = "error FS0010: Unexpected symbol '->' in expression. Expected ']' or other token.";
        const string src = "let rec solve k =\n    if k = 1 then\n        [s->d]\n    else\n        [s->d]\n";
        string first = global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(src, err);
        string third = global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(src, err, 2);
        TestContext.Check(first.Length > 0 && !first.Contains("reescreva o arquivo inteiro"),
            "1a dica sem escalada (veio: " + first.Replace("\n", " | ") + ")");
        TestContext.Check(third.StartsWith(first, StringComparison.Ordinal),
            "3a dica mantem a forma certa e acrescenta a escalada");
        TestContext.Check(third.Contains("reescreva o arquivo inteiro") && third.Contains("`write`"),
            "3a dica manda reescrever o arquivo com write");
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(src, "MSBUILD : error MSB1009", 5) == "",
            "sem dica conhecida nao ha escalada (nao inventa instrucao)");
        return Task.CompletedTask;
    }

    // 9a amostra (2026-10-29): lambda estilo Haskell (`\count p1 ->`) -> FS0010,
    // 0 reparo. A dica ensina `fun` e nao ecoa a barra invertida.
    public static Task FsRepairLambdaBackslash()
    {
        string p = global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(
            "let twins = List.fold (\\count p1 -> if List.contains (p1 + 2) primes then count + 1 else count) 0 primes\n",
            "error FS0010: Unexpected character '\\' in expression");
        TestContext.Check(p.Contains("fun count p1 ->"),
            "FS0010 barra invertida -> lambda com fun (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(p.Contains("List.fold (fun") && !p.Contains("\\count"),
            "a dica nao ecoa a barra invertida");
        return Task.CompletedTask;
    }

    // 10a amostra (2026-10-29): `: (count: int * string list)` — nome de campo
    // dentro da anotacao de retorno; o FS0010 "Unexpected symbol ':'" nao tinha dica
    // e o modelo repetiu o MESMO arquivo em 5 builds (0 reparo em 26 turnos).
    public static Task FsRepairReturnTupleAnnotation()
    {
        string p = global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(
            "let rec hanoi_calc (n: int) (source: int) : (count: int * string list) =\n    if n = 0 then (0, []) else hanoi_calc (n - 1) source\n",
            "error FS0010: Unexpected symbol ':' in binding. Expected ')' or other token.");
        TestContext.Check(p.Contains(": int * string list ="),
            "FS0010 ':' em binding -> anotacao leva so' o tipo (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(p.Contains("let rec hanoi (n: int)"), "a dica mostra a assinatura que compila");
        TestContext.Check(!p.Contains("count:"), "a dica nao ecoa `nome:` dentro da anotacao");
        return Task.CompletedTask;
    }

    // 10a amostra (2026-10-29): `else let limit = ...` na MESMA linha -> FS0588
    // ("The block following this 'let' is unfinished"). A regra generica de corpo
    // nao ensinava o `else`; a dica chega no turno da falha.
    public static Task FsRepairElseLetBlock()
    {
        string p = global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(
            "let isPrime n =\n    if n <= 1 then false\n    else let limit = int (Math.Floor(float n / 2.0))\n    List.forall (fun d -> n % d <> 0) [2 .. limit]\n",
            "error FS0588: The block following this 'let' is unfinished. Every code block is an expression and must have a result.");
        TestContext.Check(p.Contains("else\n        let limit"),
            "FS0588 `else let` -> `else` sozinho e bloco indentado (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(!p.Contains("else let"), "a dica nao ecoa `else let` na mesma linha");
        TestContext.Check(p.Contains("List.forall (fun d -> n % d <> 0)"), "a dica devolve o corpo completo que compila");
        string corpo = global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(
            "let f n =\n    let g = n\n",
            "error FS0588: The block following this 'let' is unfinished. Every code block is an expression and must have a result.");
        TestContext.Check(corpo.Contains("List.sum dobro"), "FS0588 sem `else let` mantem a dica de corpo (nada de regressao)");
        return Task.CompletedTask;
    }

    // 11a amostra (2026-10-29): `let rec hanoi (n: int)` chamado como `hanoi n 1 3 2`
    // -> FS0003 "This value is not a function and cannot be applied". A dica MEDE a
    // aridade na fonte (declaracao x chamada) e ensina a forma que compila; sem
    // posicao/medida ela se cala (nunca chuta).
    public static Task FsRepairArityMismatch()
    {
        string src = string.Join("\n", new[]
        {
            "module Program", "", "open System", "",
            "let rec hanoi (n: int) : int * string list =",
            "    if n = 0 then (0, [])",
            "    else",
            "        let (m1, s1) = hanoi n 1 3 2",
            "        (m1 + 1, s1)",
            "",
            "printfn \"%A\" (hanoi 4)"
        });
        string err = "/tmp/e2e-mission-algo-fs/mfs/Program.fs(8,24): error FS0003: "
                   + "This value is not a function and cannot be applied. It has type '(int * string list)'";
        string p = global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(src, err);
        TestContext.Check(p.Contains("'hanoi' foi declarada com 1 parametro") && p.Contains("passa 4"),
            "FS0003 aridade -> a dica mede declaracao x chamada (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(p.Contains("let rec soma (a: int) (b: int) (c: int)"), "a dica mostra a forma que compila");
        TestContext.Check(!p.Contains("hanoi n 1 3 2"), "a dica nao ecoa a chamada errada");

        string ok = global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(
            "let rec soma a b = a + b\nlet r = soma 1 2\n",
            "/tmp/p.fs(2,9): error FS0003: This value is not a function and cannot be applied.");
        TestContext.Check(ok.Length == 0, "aridade coerente nao recebe dica de aridade (veio: " + ok.Replace("\n", " | ") + ")");

        string semPos = global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(
            src, "error FS0003: This value is not a function and cannot be applied. It has type '(int * string list)'");
        TestContext.Check(semPos.Length == 0, "FS0003 sem linha/coluna se cala (nao chuta aridade)");
        return Task.CompletedTask;
    }

    // 12a amostra (2026-10-29): `dest_aux` usado na linha 11 e declarado na linha 19 ->
    // FS0039 "The value or constructor 'dest_aux' is not defined". F# le de cima para
    // baixo: a dica manda mover a declaracao para cima (ou receber por parametro).
    public static Task FsRepairUseBeforeDef()
    {
        string src = string.Join("\n", new[]
        {
            "module Program", "", "open System", "",
            "let rec hanoi n source dest =",
            "    if n = 1 then [ (source, dest) ]",
            "    else",
            "        let movimentosAux = hanoi n source dest_aux",
            "        let movimentosDest = hanoi n dest_aux source",
            "        movimentosAux @ [ (source, dest) ] @ movimentosDest",
            "",
            "let dest_aux = 3"
        });
        string err = "/tmp/p/Program.fs(8,44): error FS0039: The value or constructor 'dest_aux' is not defined. "
                   + "Maybe you want one of the following: dest";
        string p = global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(src, err);
        TestContext.Check(p.Contains("'dest_aux' e' usado na linha 8") && p.Contains("declarado na linha 12"),
            "FS0039 uso antes da declaracao -> dica com as duas linhas (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(p.Contains("let auxiliar = 3\n\nlet rec soma n ="), "a dica mostra a forma que compila");
        TestContext.Check(!p.Contains("dest_aux = 3\n"), "a dica nao manda copiar o bloco errado");

        string ok = global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(
            "let dest_aux = 3\n\nlet rec f n = if n = 0 then dest_aux else f (n - 1)\n",
            "/tmp/p/Program.fs(3,20): error FS0039: The value or constructor 'outro' is not defined.");
        TestContext.Check(ok.Length == 0,
            "nome sem declaracao no fonte -> silencio (veio: " + ok.Replace("\n", " | ") + ")");
        return Task.CompletedTask;
    }

    // 14a amostra (2026-10-29): `subSeq @ "," + nextMove` — ramo `string` e ramo lista no
    // mesmo `if`. A dica antiga mandava `Array.append` para qualquer ramo; agora ela le o
    // tipo NOMEADO na mensagem e ensina o jeito de juntar de cada um.
    public static Task FsRepairIfBranchesString()
    {
        string p = global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(
            "let hanoi_seq n =\n    if n <= 0 then \"\"\n    else\n        let subSeq = \"1->2\"\n        subSeq @ \",\" + \"1->3\"\n",
            "error FS0001: All branches of an 'if' expression must return values implicitly convertible to the type of the first branch, which here is 'string'. This branch returns a value of type ''a list'.");
        TestContext.Check(p.Contains("TEXTO com `+`") && p.Contains("LISTA com `@`"),
            "FS0001 ramo string -> dica ensina + para texto e @ para lista (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(p.Contains("let texto = a + \",\" + b"), "a dica mostra a forma que compila");
        TestContext.Check(!p.Contains("Array.append antes depois"), "ramo string nao recebe a dica de array");

        string q = global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(
            "let f xs = if xs = [] then [] else xs\n",
            "error FS0001: All branches of an 'if' expression must return values implicitly convertible to the type of the first branch, which here is ''a list'. This branch returns a value of type 'int'.");
        TestContext.Check(q.Contains("para juntar LISTA use `@`"),
            "ramo lista -> dica de `@` (veio: " + q.Replace("\n", " | ") + ")");
        return Task.CompletedTask;
    }

    // 16a amostra (2026-10-29): `(n: int; source: string; ...)` -> FS0010
    // "Unexpected symbol ';' in pattern". O `;` so' separa itens de lista/array.
    public static Task FsRepairParamSemicolon()
    {
        string p = global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(
            "let rec hanoi (n: int; source: string; destination: string; auxiliary: string) =\n    if n = 0 then [] else []\n",
            "error FS0010: Unexpected symbol ';' in pattern. Expected ')' or other token.");
        TestContext.Check(p.Contains("(n: int) (source: int)"),
            "FS0010 ';' -> dica ensina parametros curried (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(!p.Contains("(n: int;"), "a dica nao ecoa a lista de parametros com `;`");
        return Task.CompletedTask;
    }

    // 17a amostra (2026-10-29): `let mutable sequencia = []<string list>` -> FS3156 no
    // `>`; o modelo editou OUTRA linha 6 vezes. A forma que compila anota com `:`.
    public static Task FsRepairGenericSyntax()
    {
        string p = global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(
            "let mutable sequencia = []<string list>\n",
            "/tmp/x/Program.fs(34,43): error FS3156: Unexpected token '>' or incomplete expression");
        TestContext.Check(p.Contains("let vazia: string list = []"),
            "FS3156 generico -> anotacao pos-fixada com `:` (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(!p.Contains("[]<string list>"), "a dica nao ecoa o generico que nao compila");
        TestContext.Check(p.Contains("linha 34"), "a dica aponta a linha do erro (o modelo editava outra)");
        string seq = global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(
            "let mutable sequencia = []\nsequencia <- seq {+ source, dest}\n",
            "/tmp/x/Program.fs(37,24): error FS0010: Unexpected symbol '+' in expression");
        TestContext.Check(seq.Contains("yield") && seq.Contains("@ [ n ]"),
            "FS0010 `seq {+` -> comprehension exige yield; acumular e' lista com `@` (veio: " + seq.Replace("\n", " | ") + ")");
        TestContext.Check(!seq.Contains("seq {+"), "a dica nao ecoa a comprehension invalida");
        return Task.CompletedTask;
    }

    // Ponteiro da linha do erro: o compilador diz `arquivo(linha,coluna)` e o modelo
    // editava a linha vizinha (17a amostra). Sem linha na saida, nada muda.
    public static Task FsRepairErrorLinePointer()
    {
        const string err = "/tmp/x/Program.fs(12,5): error FS0010: Unexpected symbol '->' in expression.";
        string p = global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(
            "let xs = [ a -> b ]\n", err);
        TestContext.Check(p.Contains("linha 12"), "a dica nomeia a linha do compilador (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(p.Split("linha 12").Length - 1 == 1, "a linha aparece UMA vez na dica");
        string semLinha = global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(
            "let xs = [ a -> b ]\n", "error FS0010: Unexpected symbol '->' in expression.");
        TestContext.Check(!semLinha.Contains("O erro esta na linha"),
            "sem (linha,coluna) na saida nao inventa ponteiro");
        return Task.CompletedTask;
    }

    // 15a amostra (2026-10-29): `currentPrimes.[p]` com o parametro sem anotacao ->
    // FS0752 "operator 'expr.[idx]' has been used on an object of indeterminate type".
    // Sete ocorrencias e nenhuma dica; a forma que compila e' a anotada.
    public static Task FsRepairIndexerAnnotation()
    {
        string p = global::Rochas.OpenCodeBridge.Core.FsSyntaxRepair.Suggest(
            "let rec sieveRec p currentPrimes =\n    if currentPrimes.[p] then sieveRec (p + 1) currentPrimes else currentPrimes\n",
            "error FS0752: The operator 'expr.[idx]' has been used on an object of indeterminate type based on information prior to this program point. Consider adding further type constraints");
        TestContext.Check(p.Contains("(xs: bool array)") && p.Contains("xs.[i]"),
            "FS0752 -> dica anota o parametro indexado (veio: " + p.Replace("\n", " | ") + ")");
        TestContext.Check(!p.Contains("currentPrimes.[p]"), "a dica nao ecoa a linha sem anotacao");
        return Task.CompletedTask;
    }

    // ---- janela de args separada da janela de texto (opcao B do L3 2026-10-29) ----

    public static Task AntiRepeatArgsWindowIsIndependent()
    {
        // Payload de arquivo que se repete a DISTANCIA: a cauda de 256 chars ja'
        // apareceu no inicio (janela do texto corta), a de 1024 nao (janela de args
        // nao corta). O detector e' o mesmo — o que muda e' a janela.
        var rnd = new Random(42);
        string bloco = new string(Enumerable.Range(0, 260)
            .Select(_ => (char)('a' + rnd.Next(0, 26))).ToArray());
        string miolo = new string(Enumerable.Range(0, 2000)
            .Select(_ => (char)('a' + rnd.Next(0, 26))).ToArray());
        string payload = bloco + miolo + bloco;

        int texto = global::Rochas.OpenCodeBridge.Core.ContentAntiRepeat.DefaultWindow;
        int args = global::Rochas.OpenCodeBridge.Core.ToolArgsAntiRepeatReply.ArgsDefaultWindow;
        TestContext.Check(args > texto, $"janela de args ({args}) maior que a de texto ({texto})");
        TestContext.Check(new global::Rochas.OpenCodeBridge.Core.StreamAntiRepeatReply(texto).IsRepeat(payload),
            "cauda de texto repetida dispara na janela do conteudo");
        TestContext.Check(new global::Rochas.OpenCodeBridge.Core.ToolArgsAntiRepeatReply(texto).IsRepeat(payload),
            "mesmo payload dispara na janela curta (prova que o corte era a janela)");
        TestContext.Check(!new global::Rochas.OpenCodeBridge.Core.ToolArgsAntiRepeatReply(args).IsRepeat(payload),
            "payload de arquivo NAO e' cortado na janela de args");
        // Loop real de args continua detectado (a janela maior nao cega o detector).
        string loop = string.Concat(Enumerable.Repeat("linha repetida demais no args\n", 80));
        TestContext.Check(new global::Rochas.OpenCodeBridge.Core.ToolArgsAntiRepeatReply(args).IsRepeat(loop),
            "loop real de args continua detectado na janela de args");
        return Task.CompletedTask;
    }

    // ---- nudge de salvage nao e' turno do modelo (opcao C do L3 2026-10-29) ----

    public static Task SalvageNoticeIsRecognizable()
    {
        string nudge = global::Rochas.OpenCodeBridge.Core.ToolCallRepair.Nudge;
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.ToolCallRepair.IsSalvageNotice("BRIDGE (edit): " + nudge),
            "aviso do salvage reconhecido como instrucao da bridge");
        TestContext.Check(!global::Rochas.OpenCodeBridge.Core.ToolCallRepair.IsSalvageNotice("editei o arquivo e rodei o build"),
            "prosa normal nao e' aviso de salvage");
        TestContext.Check(!global::Rochas.OpenCodeBridge.Core.ToolCallRepair.IsSalvageNotice(""), "vazio nao e' aviso");
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.ToolCallRepair.Notice(new[] { "edit", "write" })
                .StartsWith("BRIDGE (edit, write): ", StringComparison.Ordinal),
            "aviso do salvage usa a tag unica com as tools recusadas");

        // 12a amostra (2026-10-29): 13 turnos perdidos em args truncados. O aviso cita
        // VARIOS exemplos validos (cada um parseia) + o help curado da tool recusada.
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.ToolCallRepair.Examples.Length >= 3,
            "o aviso cita varios exemplos de args");
        foreach (string ex in global::Rochas.OpenCodeBridge.Core.ToolCallRepair.Examples)
        {
            bool parses;
            try { parses = JsonNode.Parse(ex) is not null; } catch { parses = false; }
            TestContext.Check(parses, "exemplo do aviso e' JSON valido: " + ex);
        }
        string aviso = global::Rochas.OpenCodeBridge.Core.ToolCallRepair.Notice(new[] { "edit" });
        TestContext.Check(aviso.Contains("EXEMPLOS DE ARGS VALIDOS"), "o aviso traz a secao de exemplos");
        TestContext.Check(aviso.Contains("oldString"), "o aviso repete o help curado da tool recusada (ToolHelp)");
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.ToolCallRepair.IsSalvageNotice(aviso),
            "o aviso composto continua reconhecido como salvage");
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.ToolCallRepair.Notice(null).Contains("path"),
            "sem nome de tool o aviso cita o help de arquivo (write/edit/read)");

        // Apply: poda a assistant quebrada e anexa UMA copia (segunda chamada nao empilha).
        var hist = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "pedido" },
            new JsonObject { ["role"] = "assistant", ["content"] = "", ["tool_calls"] = new JsonArray(new JsonObject
            {
                ["id"] = "c1", ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = "edit", ["arguments"] = "{\"path\": " }
            }) }
        };
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.ToolCallRepair.Apply(hist, new[] { "edit" }),
            "apply poda a assistant com args invalido");
        int afterFirst = hist.Count;
        global::Rochas.OpenCodeBridge.Core.ToolCallRepair.Apply(hist, new[] { "edit" });
        TestContext.Check(hist.Count == afterFirst, "apply nao empilha aviso repetido (dedupe unico)");
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.ToolCallRepair.IsSalvageNotice(
                hist[^1]!["content"]!.GetValue<string>()),
            "aviso anexado pelo apply e' reconhecido como salvage");
        return Task.CompletedTask;
    }

    // ---- aviso da bridge: prefixo, tag e dedupe num lugar so' ----

    public static Task BridgeNoticeFormatIsSingleSource()
    {
        string notice = global::Rochas.OpenCodeBridge.Core.BridgeNotice.Notice("edit, write", "corpo");
        TestContext.Check(notice == "BRIDGE (edit, write): corpo", "tag do aviso: " + notice);
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.BridgeNotice.Is(notice),
            "aviso reconhecido pelo prefixo unico");
        TestContext.Check(!global::Rochas.OpenCodeBridge.Core.BridgeNotice.Is("editei o arquivo"),
            "prosa normal nao e' aviso");
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.BridgeNotice.Notice(null, "x") == "BRIDGE (tool): x",
            "escopo vazio cai na tag generica (nunca BRIDGE ())");

        var hist = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "pedido" },
            new JsonObject { ["role"] = "assistant", ["content"] = "", ["tool_calls"] = new JsonArray() }
        };
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.BridgeNotice.Append(hist, notice),
            "anexa o aviso uma vez");
        TestContext.Check(!global::Rochas.OpenCodeBridge.Core.BridgeNotice.Append(hist, notice),
            "aviso identico nao empilha copia");
        TestContext.Check(hist.Count == 3, "historico com uma unica copia do aviso");
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.BridgeNotice.Append(hist, "BRIDGE (tool): outro"),
            "aviso diferente apos o mesmo lugar ainda entra (nao suprime por prefixo)");
        return Task.CompletedTask;
    }
}
