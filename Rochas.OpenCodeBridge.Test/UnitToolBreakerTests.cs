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
        TestContext.Check(fsMini.Contains("[1 .. 2 .. n-1]"), "FsMini ensina range com passo (forma correta)");
        TestContext.Check(fsMini.Contains("FS0751"), "FsMini nomeia o codigo do range com ';'");
        TestContext.Check(!fsMini.Contains("[1; 3 .. n-1]"), "FsMini nao repete o literal invalido (o modelo copia)");
        // 2a forma medida no L3-20261028-163556: o modelo escreveu comprehension
        // de Haskell (`[i | i <- ...]`) e errou FS0010 por 22 turnos.
        TestContext.Check(fsMini.Contains("[for i in 1 .. n-1 -> expr]"), "FsMini ensina comprehension de F#");
        TestContext.Check(fsMini.Contains("[x | x <- xs]"), "FsMini nomeia a comprehension alheia (FS0010)");
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

        // 2) cliente omite: vale o perfil do role (-build = 0.2)
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
}
