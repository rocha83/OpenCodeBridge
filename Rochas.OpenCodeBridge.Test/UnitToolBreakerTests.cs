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
    // 2026-10-30 (missao K-Means F#): o loop de compilacao alterna `write` (que
    // "da certo") e `shell` (que repete o MESMO erro em outra linha). O streak
    // zerava no sucesso e o alvo alternado segurava em 4-5 — nudge eterno, ZERO
    // trip. A assinatura do erro (sem path/linha/numero) e' que conta o loop.
    public static Task TripsRepeatedSameError()
    {
        static JsonArray Turnos(int n, Func<int, string> erro)
        {
            var passos = new List<(string, string, string, string)>();
            for (int i = 0; i < n; i++)
            {
                // Turno real do loop medido: `write` ok, `read` ok e o build falhando
                // — os sucessos cosmeticos intercalados sao o que zerava o streak.
                passos.Add(("assistant", "write", "{\"path\":\"/ws/mfs/Program.fs\",\"content\":\"module Program\"}", ""));
                passos.Add(("tool", "", "", "ok atualizado 2866 chars em '/ws/mfs/Program.fs'"));
                passos.Add(("assistant", "read", "{\"path\":\"/ws/mfs/Program.fs\"}", ""));
                passos.Add(("tool", "", "", "ok module Program"));
                passos.Add(("assistant", "shell", "{\"command\":\"dotnet run --project /ws/mfs/mfs.fsproj\"}", ""));
                passos.Add(("tool", "", "", erro(i)));
            }
            return Chat(passos.ToArray());
        }

        // O MESMO diagnostico, so' a linha muda a cada reescrita do arquivo.
        static string MesmoErro(int i) =>
            $"ERRO: exit 1: /ws/mfs/Program.fs({55 + i},115): error FS0010: Unexpected symbol '|]' in expression. Expected '}}' or other token.";
        var guard = new global::Rochas.OpenCodeBridge.Core.ToolsBreaker(3, 6);

        var r3 = guard.Evaluate(Turnos(3, MesmoErro));
        TestContext.Check(r3.Verdict == global::Rochas.OpenCodeBridge.Core.BreakerVerdict.Nudge,
            $"3 builds com o mesmo erro ja' avisam (veio {r3.Verdict})");
        TestContext.Check(r3.Streak >= 3, $"streak conta o loop intercalado com `write` ok (veio {r3.Streak})");

        var r6 = guard.Evaluate(Turnos(6, MesmoErro));
        TestContext.Check(r6.Verdict == global::Rochas.OpenCodeBridge.Core.BreakerVerdict.Trip,
            $"6 builds com o MESMO erro cortam o turno (veio {r6.Verdict})");

        // Erros DIFERENTES (progresso real, o modelo corrige e cai no proximo):
        // nao somam como loop — no maximo o aviso, nunca o curto-circuito.
        var progride = guard.Evaluate(Turnos(6, i =>
            $"ERRO: exit 1: /ws/mfs/Program.fs({10 + i},3): error FS00{i + 1}{i}: diagnostico distinto numero {i}"));
        TestContext.Check(progride.Verdict != global::Rochas.OpenCodeBridge.Core.BreakerVerdict.Trip,
            $"erros distintos sao progresso e nao cortam (veio {progride.Verdict})");
        return Task.CompletedTask;
    }

    public static Task BreaksCompilerEditLoop()
    {    static JsonArray Turnos(int n, string edit, string erro)
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

    /// <summary>
    /// Tabela de erros C# na cauda de tools: o MESMO mapa do
    /// <see cref="global::Rochas.OpenCodeBridge.Core.CsSyntaxRepair"/> (fonte única,
    /// gerado de `Rules`), presente quando o request tem `shell` — só ali existe
    /// "build falhar". Instrui a forma CERTA ANTES do erro; o reparo in-flow entra
    /// no retry quando o modelo ignora. Formato espelha o FsMini arquivado.
    /// </summary>
    public static Task ToolsTailCarriesCsErrorTable()
    {
        string tail = global::Rochas.OpenCodeBridge.Core.HarnessTails.DefaultTools;
        TestContext.Check(tail.Contains("TABELA DE ERROS C#", StringComparison.Ordinal),
            "cauda traz a tabela de erros C#");
        // Cada código da tabela vem da fonte única (Rules), não de cópia à mão.
        foreach (var rule in global::Rochas.OpenCodeBridge.Core.CsSyntaxRepair.Rules)
            TestContext.Check(tail.Contains(rule.Code, StringComparison.Ordinal),
                "cauda cobre " + rule.Code + " (mesma fonte do CsSyntaxRepair)");
        // CS1003 (medido no hanoi de 2026-10-31): ensina a forma certa, nunca o literal errado.
        TestContext.Check(tail.Contains("if (flag[i] && flag[i + 2])", StringComparison.Ordinal),
            "CS1003 ensina a forma CERTA (um `)` por `(`)");
        TestContext.Check(!tail.Contains("flag[i + 2]))", StringComparison.Ordinal),
            "a tabela NUNCA exibe o literal errado (o modelo copia o que ve)");
        // CS1003 forma `for` com `in` (medido no kmeans do L4 de 2026-10-31: 617s de
        // loop). A INSTRUCAO da cauda tem que ensinar `foreach` ANTES do erro — e' a
        // prioridade do projeto (instruir primeiro, reparar so' como rede).
        TestContext.Check(tail.Contains("C# nao tem `for` com `in`", StringComparison.Ordinal),
            "CS1003 instrui que C# nao tem `for` com `in`");
        TestContext.Check(tail.Contains("foreach (var item in colecao)", StringComparison.Ordinal),
            "CS1003 mostra a forma CERTA com `foreach`");
        TestContext.Check(!tail.Contains("for (var point in", StringComparison.Ordinal),
            "a tabela nunca exibe a forma `for` com `in` como exemplo");
        TestContext.Check(tail.Contains("aplique a forma da tabela e edite SO'", StringComparison.Ordinal),
            "a tabela manda aplicar a forma e editar SÓ o trecho");
        // Só com shell: sem execução não existe "build falhando".
        TestContext.Check(!global::Rochas.OpenCodeBridge.Core.HarnessTails.ComposeTools(new[] { "read", "write" })
                .Contains("TABELA DE ERROS C#", StringComparison.Ordinal),
            "sem `shell` a tabela de erros nao entra (nao existe build falhando)");
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.HarnessTails.ComposeTools(new[] { "read", "shell" })
                .Contains("TABELA DE ERROS C#", StringComparison.Ordinal),
            "com `shell` a tabela de erros entra");
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

    // L4 de 2026-10-29 (step algo-bridge): a 1a linha saiu `module Program open System`
    // e o `let` do topo virou FS0010 "Unexpected keyword 'let' or 'use' in implementation
    // file" — a mensagem do compilador nao diz a forma certa e o modelo repetiu. A dica
    // devolve SO' a forma que compila (module sozinho + cada `open` na propria linha).

    // L4/isolado de 2026-10-29: o F# do modelo declarava a funcao com `=` e engolia o
    // resultado num `let` interno (FS3118/FS0588) e usava array-comprehension com a
    // barra no meio (FS0603). A dica devolve a forma que compila, com o corpo alinhado.

    // Isolado de 2026-10-29: o modelo repetiu a MESMA linha em 3 builds seguidos
    // (FS0597) porque o codigo nao tinha dica — cada build custa minutos de GPU.

    // Isolado de 2026-10-29 (4a amostra do F#): programa ja' calculando, travado em
    // FS0072 na linha da formatacao — o codigo nao tinha dica e a fase morreu nela.

    // 5a amostra do F# (2026-10-29): o modelo escreveu `[s->d]` (o `->` do exemplo de
    // comprehension vazou para a lista literal) e nao havia dica: 30 turnos em zero.

    // 6a amostra do F# (2026-10-29): `|> List.append` sem o segundo operando
    // (FS0193, "missing arguments") repetido em 4 builds sem dica.

    // 7a amostra (2026-10-29): a fase F# so' injetava a dica quando a saida tinha
    // "error FS"; o FS0193 veio como WARNING e o reparo nunca entrou no fluxo.

    /// <summary>
    /// CS1513 (`} expected`) nao tinha dica in-flow: o `btree` do L4 de 2026-10-30
    /// gastou 26 turnos e terminou com 26 abre-chave e 25 fecha-chave. A dica diz
    /// QUANTOS blocos faltam (contagem real do arquivo) e a forma sempre valida.
    /// </summary>
    public static Task CsRepairMissingBrace()
    {
        string src = "namespace mcs\n{\n    public class Program\n    {\n        static void Main()\n        {\n"
                   + "            var x = 1;\n        }\n    }\n"; // falta o `}` do namespace
        string err = "ERRO: exit 1: /tmp/mcs/Program.cs(10,2): error CS1513: } expected [/tmp/mcs/mcs.csproj]";
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.CsSyntaxRepair.HasDiagnostic(err),
            "error CSdddd conta como diagnostico C#");
        string hint = global::Rochas.OpenCodeBridge.Core.CsSyntaxRepair.Suggest(src, err);
        TestContext.Check(hint.Contains("1 bloco(s) aberto(s) sem fechar", StringComparison.Ordinal),
            "a dica diz QUANTOS blocos faltam fechar (saiu: '" + hint.Replace("\n", " | ") + "')");
        TestContext.Check(hint.Contains("3 abre-chave e 2 fecha-chave", StringComparison.Ordinal),
            "a dica usa a contagem real do arquivo (saiu: '" + hint.Replace("\n", " | ") + "')");
        TestContext.Check(hint.Contains("linha 10", StringComparison.Ordinal),
            "a dica aponta a linha que o compilador citou");
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.CsSyntaxRepair.Suggest(src, err, 2)
                .Contains("reescreva o arquivo inteiro", StringComparison.Ordinal),
            "na 3a mesma dica o Core manda reescrever o arquivo com `write`");
        // Arquivo balanceado nao recebe contagem inventada.
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.CsSyntaxRepair.Suggest(
                "class A { void M() { } }\n", err).Length == 0,
            "fonte balanceada nao gera dica de bloco (sem inventar correcao)");
        TestContext.Check(!global::Rochas.OpenCodeBridge.Core.CsSyntaxRepair.HasDiagnostic(
                "ERRO: exit 1: MSBUILD : error MSB1009: Project file does not exist."),
            "erro sem codigo CS nao dispara reparo de sintaxe C#");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Salvage determinístico do CS1513 = FALLBACK, só depois de 2 entregas da MESMA
    /// dica (o modelo fraco ignora a instrução: 2026-10-30, Q4 Base gastou 26 turnos
    /// com 22 abre-chave / 21 fecha-chave). <see cref="global::Rochas.OpenCodeBridge.Core.CsSyntaxRepair.TryRepair"/>
    /// fecha o bloco mecânico e devolve o fonte balanceado; antes do threshold não
    /// entra (a instrução ainda tem as 2 chances dela) e sem CS1513 ou com fonte já
    /// balanceado não inventa correção.
    /// </summary>
    public static Task CsRepairAppliesMissingBrace()
    {
        string src = "namespace mcs\n{\n    public class Program\n    {\n        static void Main()\n        {\n"
                   + "            var x = 1;\n        }\n    }\n"; // falta o `}` do namespace
        string err = "ERRO: exit 1: /tmp/mcs/Program.cs(10,2): error CS1513: } expected [/tmp/mcs/mcs.csproj]";
        // FALLBACK: antes de 2 entregas da MESMA dica o reparo fica de fora —
        // empurrar a correção cedo tira do modelo a chance de aprender.
        TestContext.Check(!global::Rochas.OpenCodeBridge.Core.CsSyntaxRepair.TryRepair(src, err, out _, 0),
            "1a entrega da dica: reparo NAO entra (instrucao ainda resolve)");
        TestContext.Check(!global::Rochas.OpenCodeBridge.Core.CsSyntaxRepair.TryRepair(src, err, out _, 1),
            "2a entrega da dica: reparo ainda NAO entra (2 chances p/ a instrucao)");
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.CsSyntaxRepair.TryRepair(src, err, out string fixedSrc, 2),
            "3a entrega da MESMA dica: o fallback deterministico entra");
        int open = 0, close = 0;
        foreach (char ch in fixedSrc) { if (ch == '{') open++; else if (ch == '}') close++; }
        TestContext.Check(open == close, $"fonte reparado fica balanceado ({open} abre / {close} fecha)");
        TestContext.Check(fixedSrc.EndsWith("}\n", StringComparison.Ordinal),
            "o reparo acrescenta a(s) linha(s) só com `}` no fim do arquivo");
        TestContext.Check(!global::Rochas.OpenCodeBridge.Core.CsSyntaxRepair.TryRepair(
                "class A { void M() { } }\n", err, out _, 2),
            "fonte ja balanceado nao gera reparo (sem inventar correcao)");
        TestContext.Check(!global::Rochas.OpenCodeBridge.Core.CsSyntaxRepair.TryRepair(
                src, "ERRO: exit 1: boom", out _, 2),
            "erro sem codigo CS nao dispara reparo determinístico");
        return Task.CompletedTask;
    }

    /// <summary>
    /// CS1003 (`Syntax error, ']' expected`) não tinha mapa: o hanoi do L4 de
    /// 2026-10-31 gastou 386s e FAIL com um `)` duplicado no fim do `if`
    /// (<c>if (isPrime[i] &amp;&amp; isPrime[i + 2]))</c>). A dica conta o excesso
    /// LOCAL e ensina só a forma válida — nunca exibe o literal errado copiável.
    /// </summary>
    public static Task CsRepairUnexpectedToken()
    {
        string src = "class P { void M(int[] flag) {\n"
                   + "    int count = 0;\n"
                   + "    for (int i = 0; i < flag.Length; i++) {\n"
                   + "        if (flag[i] > 0 && flag[i + 1] > 0))\n"
                   + "            count++;\n"
                   + "    }\n"
                   + "} }\n";
        string err = "ERRO: exit 1: /tmp/mcs/Program.cs(4,44): error CS1003: Syntax error, ']' expected [/tmp/mcs/mcs.csproj]";
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.CsSyntaxRepair.HasDiagnostic(err),
            "error CS1003 conta como diagnostico C#");
        string hint = global::Rochas.OpenCodeBridge.Core.CsSyntaxRepair.Suggest(src, err);
        TestContext.Check(hint.Contains("linha 4", StringComparison.Ordinal),
            "a dica aponta a linha que o compilador citou (saiu: '" + hint.Replace("\n", " | ") + "')");
        TestContext.Check(hint.Contains("1 `)` a mais", StringComparison.Ordinal),
            "a dica conta o `)` excedente da linha (saiu: '" + hint.Replace("\n", " | ") + "')");
        TestContext.Check(hint.Contains("if (flag[i] && flag[i + 2])", StringComparison.Ordinal),
            "a dica mostra a forma CERTA, com um `)` por `(`");
        TestContext.Check(!hint.Contains("flag[i + 2]))", StringComparison.Ordinal),
            "a dica NUNCA exibe o literal errado (o modelo copia o que ve)");
        // Fonte sem excesso na linha não recebe contagem inventada: cai no texto
        // genérico da tabela (sem "a mais do que `(`", que é a mensagem contada).
        TestContext.Check(!global::Rochas.OpenCodeBridge.Core.CsSyntaxRepair.Suggest(
                "class A { void M(int[] f) { if (f[0] > 0) f[0] = 0; } }\n", err)
                .Contains("a mais do que", StringComparison.Ordinal),
            "linha balanceada nao recebe contagem inventada");
        return Task.CompletedTask;
    }

    /// <summary>
    /// CS7036 (`no argument given that corresponds to the required parameter`): o
    /// hanoi do L4 de 2026-10-31 chamou `SolveHanoi(4, 1, 3)` com declaração de 4
    /// parâmetros e a dica genérica ("passe TODOS os parametros") não foi aplicada —
    /// o modelo reescreveu o arquivo inteiro e manteve a chamada errada. A dica
    /// concreta conta os DOIS lados com o valor real e lista os parâmetros, mas
    /// nunca exibe a chamada errada nem inventa uma chamada "correta" com nomes
    /// fora de escopo (seria literal que não compila — regra primordial).
    /// </summary>
    public static Task CsHintCountsCallArgs()
    {
        string src = "public class Program\n"
                   + "{\n"
                   + "    public static void Main()\n"
                   + "    {\n"
                   + "        SolveHanoi(4, 1, 3);\n"
                   + "    }\n"
                   + "\n"
                   + "    public static void SolveHanoi(int n, int source, int destination, int auxiliary)\n"
                   + "    {\n"
                   + "        if (n == 1) return;\n"
                   + "        SolveHanoi(n - 1, source, auxiliary, destination);\n"
                   + "    }\n"
                   + "}\n";
        string err = "ERRO: exit 1: /tmp/mcs/Program.cs(5,9): error CS7036: There is no argument given that corresponds to the required parameter 'auxiliary' of 'Program.SolveHanoi(int, int, int, int)' [/tmp/mcs/mcs.csproj]";
        string hint = global::Rochas.OpenCodeBridge.Core.CsSyntaxRepair.Suggest(src, err);
        TestContext.Check(hint.Contains("linha 5", StringComparison.Ordinal),
            "a dica aponta a linha da chamada (saiu: '" + hint.Replace("\n", " | ") + "')");
        TestContext.Check(hint.Contains("com 3 argumento(s)", StringComparison.Ordinal),
            "a dica conta os argumentos REAIS da chamada (saiu: '" + hint.Replace("\n", " | ") + "')");
        TestContext.Check(hint.Contains("exige 4 parametro(s)", StringComparison.Ordinal),
            "a dica conta os parâmetros REAIS da declaração");
        TestContext.Check(hint.Contains("n, source, destination, auxiliary", StringComparison.Ordinal),
            "a dica lista os nomes dos parâmetros que faltam");
        TestContext.Check(!hint.Contains("SolveHanoi(4, 1, 3)", StringComparison.Ordinal),
            "a dica NUNCA exibe a chamada errada (o modelo copia o que ve)");
        TestContext.Check(!hint.Contains("SolveHanoi(n, source, destination, auxiliary)", StringComparison.Ordinal),
            "a dica nao inventa chamada com nomes de parametro fora de escopo (nao compila)");
        // Sem declaração conhecida cai no texto genérico da tabela (não inventa contagem).
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.CsSyntaxRepair.Suggest(
                "class A { void M() { } }\n", err).Contains("passe TODOS os parametros", StringComparison.Ordinal),
            "sem achado da declaracao cai na dica generica da tabela");
        return Task.CompletedTask;
    }

    /// <summary>
    /// CS1003 na forma `for` com `in` (o kmeans do L4 de 2026-10-31 escreveu
    /// `for (var point in _points)`, habito de Python/JS, e ficou 617s em loop porque
    /// a dica de CS1003 ensinava parenteses — o caso do hanoi). A dica identifica a
    /// forma pelo cabecalho SEM `;` e mostra SÓ o `foreach` correto, montado da
    /// propria linha do fonte.
    /// </summary>
    public static Task CsHintDetectsForIn()
    {
        string src = "class K { void M() {\n"
                   + "    foreach (var c in _clusters) { }\n"
                   + "    for (var point in _points)\n"
                   + "        point.Assign();\n"
                   + "} }\n";
        string err = "ERRO: exit 1: /tmp/mcs/Program.cs(3,13): error CS1003: Syntax error, 'foreach' expected [/tmp/mcs/mcs.csproj]";
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.CsSyntaxRepair.HasDiagnostic(err),
            "error CS1003 conta como diagnostico C#");
        string hint = global::Rochas.OpenCodeBridge.Core.CsSyntaxRepair.Suggest(src, err);
        TestContext.Check(hint.Contains("linha 3", StringComparison.Ordinal),
            "a dica aponta a linha do `for` (saiu: '" + hint.Replace("\n", " | ") + "')");
        TestContext.Check(hint.Contains("foreach (var point in _points)", StringComparison.Ordinal),
            "a dica mostra a forma CERTA com `foreach` (saiu: '" + hint.Replace("\n", " | ") + "')");
        TestContext.Check(!hint.Contains("for (var point in _points)", StringComparison.Ordinal),
            "a dica NUNCA exibe o `for` errado (o modelo copia o que ve)");
        // O caso de fechamento (hanoi) continua sendo ensinado como antes.
        string paren = "class P { void M(int[] f) {\n"
                     + "    if (f[0] > 0 && f[1] > 0))\n"
                     + "        f[0] = 0;\n"
                     + "} }\n";
        string parenErr = "ERRO: exit 1: /tmp/mcs/Program.cs(2,31): error CS1003: Syntax error, ']' expected [/tmp/mcs/mcs.csproj]";
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.CsSyntaxRepair.Suggest(paren, parenErr)
                .Contains("a mais", StringComparison.Ordinal),
            "CS1003 com parentese excedente continua na dica de fechamento (sem regressao)");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Reparo deterministico do `for` com `in` = FALLBACK no MESMO threshold do
    /// CS1513/CS1003 (2 entregas da mesma dica). Troca `for` por `foreach` na linha
    /// apontada; um `for` legitimo (com `;` no cabecalho) NUNCA e' tocado.
    /// </summary>
    public static Task CsRepairForInOnlyAfterTwoRepeats()
    {
        string src = "class K { void M() {\n"
                   + "    for (var point in _points)\n"
                   + "        point.Assign();\n"
                   + "} }\n";
        string err = "ERRO: exit 1: /tmp/mcs/Program.cs(2,13): error CS1003: Syntax error, 'foreach' expected [/tmp/mcs/mcs.csproj]";
        TestContext.Check(!global::Rochas.OpenCodeBridge.Core.CsSyntaxRepair.TryRepair(src, err, out _, 0),
            "1a entrega da dica: reparo NAO entra (instrucao ainda resolve)");
        TestContext.Check(!global::Rochas.OpenCodeBridge.Core.CsSyntaxRepair.TryRepair(src, err, out _, 1),
            "2a entrega da dica: reparo ainda NAO entra");
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.CsSyntaxRepair.TryRepair(src, err, out string fixedSrc, 2),
            "3a entrega da MESMA dica: o fallback deterministico entra");
        TestContext.Check(fixedSrc.Contains("foreach (var point in _points)", StringComparison.Ordinal),
            "o reparo troca `for` por `foreach` na linha apontada (saiu: '" + fixedSrc.Replace("\n", " | ") + "')");
        TestContext.Check(!fixedSrc.Contains("for (var point in _points)", StringComparison.Ordinal),
            "o `for` errado desaparece do fonte");
        // `for` legitimo (com `;`) nao vira `foreach`.
        string legit = "class K { void M(int n) {\n"
                     + "    for (int i = 0; i < n; i++)\n"
                     + "        i++;\n"
                     + "} }\n";
        TestContext.Check(!global::Rochas.OpenCodeBridge.Core.CsSyntaxRepair.TryRepair(legit, err, out _, 2),
            "`for` com `;` no cabecalho NAO e' tocado (nao e' for..in)");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Salvage determinístico do CS1003 = FALLBACK (mesmo threshold do CS1513): só
    /// depois de 2 entregas da MESMA dica. Remove o fechamento excedente da linha do
    /// erro quando ela é a ÚNICA culpada (local == global) e o excesso está no fim da
    /// linha. Fora dessa medida, não mexe — parêntese legítimo de bloco multilinha
    /// não pode ser comido.
    /// </summary>
    public static Task CsRepairAppliesExtraClose()
    {
        string src = "class P { void M(int[] flag) {\n"
                   + "    int count = 0;\n"
                   + "    for (int i = 0; i < flag.Length; i++) {\n"
                   + "        if (flag[i] > 0 && flag[i + 1] > 0))\n"
                   + "            count++;\n"
                   + "    }\n"
                   + "} }\n";
        string err = "ERRO: exit 1: /tmp/mcs/Program.cs(4,44): error CS1003: Syntax error, ']' expected [/tmp/mcs/mcs.csproj]";
        TestContext.Check(!global::Rochas.OpenCodeBridge.Core.CsSyntaxRepair.TryRepair(src, err, out _, 1),
            "antes de 2 entregas da MESMA dica o reparo de CS1003 nao entra (instrucao resolve)");
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.CsSyntaxRepair.TryRepair(src, err, out string fixedSrc, 2),
            "3a entrega da MESMA dica: o fallback de CS1003 entra");
        TestContext.Check(fixedSrc.Contains("if (flag[i] > 0 && flag[i + 1] > 0)", StringComparison.Ordinal),
            "o reparo remove o `)` excedente e mantem a condicao valida");
        int open = 0, close = 0;
        foreach (char ch in fixedSrc) { if (ch == '(') open++; else if (ch == ')') close++; }
        TestContext.Check(open == close, $"fonte reparado fica balanceado em parenteses ({open} abre / {close} fecha)");
        // Fonte já balanceado não gera reparo.
        TestContext.Check(!global::Rochas.OpenCodeBridge.Core.CsSyntaxRepair.TryRepair(
                "class A { void M(int[] f) { if (f[0] > 0) f[0] = 0; } }\n", err, out _, 2),
            "fonte ja balanceado nao gera reparo de CS1003");
        // Excesso que NÃO é da linha (global != local) não é tocado.
        TestContext.Check(!global::Rochas.OpenCodeBridge.Core.CsSyntaxRepair.TryRepair(
                "class A {\n  void M(int[] f) {\n    int x = (1 + 2;\n    if (f[0] > 0)) x++;\n  }\n}\n",
                "ERRO: exit 1: /tmp/mcs/Program.cs(4,20): error CS1003: Syntax error, ']' expected [/tmp/mcs/mcs.csproj]", out _, 2),
            "desbalanceamento global diferente do local nao e' reparado (sem comer parentese legitimo)");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Guard de ctx do Core (rotina ÚNICA): teto = 95% do ctx por-slot — ctx total
    /// dividido pelos slots do llama (524288/4 = 131072 -> 124518, mesmo teto de
    /// 131072/1). Acima do teto resume o histórico (args antigos viram marcador) e
    /// preserva os últimos turnos (o marcador de falha recente continua legível
    /// para o breaker).
    /// </summary>
    public static Task ContextCompact()
    {
        // ~126k tokens (504k chars / 4): acima de 95% de 131072 (124518) e abaixo do
        // ctx do slot (131072) — é o intervalo que prova a fração de 95%.
        static System.Text.Json.Nodes.JsonArray BuildHistory(int pairs, int argChars, int toolChars)
        {
            var hist = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonObject { ["role"] = "system", ["content"] = "s" });
            hist.Add(new System.Text.Json.Nodes.JsonObject { ["role"] = "user", ["content"] = "u" });
            for (int i = 0; i < pairs; i++)
            {
                hist.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["role"] = "assistant", ["content"] = "",
                    ["tool_calls"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                    {
                        ["id"] = "c" + i, ["type"] = "function",
                        ["function"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["name"] = "write",
                            ["arguments"] = "{\"path\":\"a.cs\",\"content\":\"" + new string('x', argChars) + "\"}"
                        }
                    })
                });
                hist.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["role"] = "tool", ["tool_call_id"] = "c" + i,
                    ["content"] = "ERRO: exit 1: error CS1513: } expected " + new string('y', toolChars)
                });
            }
            return hist;
        }

        static bool Core(System.Text.Json.Nodes.JsonArray m, int ctx, int slots)
            => global::Rochas.OpenCodeBridge.Core.ContextGuard.Compact(m, ctx, slots);

        // Estimativa calibrada do Core (chars/3.2, pior caso de codigo medido):
        // ~517k chars = ~162k tokens. Com teto de 95% de 200000 (190000) nao compacta.
        TestContext.Check(!Core(BuildHistory(12, 42000, 1000), 200000, 1),
            "acima do ctx mas abaixo de 95% dele nao compacta (teto 190000 > ~162k)");
        TestContext.Check(Core(BuildHistory(12, 42000, 1000), 131072, 1),
            "passa de 95% de 131072 (124518) e compacta");
        TestContext.Check(Core(BuildHistory(12, 42000, 1000), 524288, 4),
            "ctx total dividido pelos slots (524288/4) tem o mesmo teto de 131072/1");
        TestContext.Check(!Core(BuildHistory(12, 42000, 1000), 0, 4),
            "sem ctx nao ha teto (nao compacta)");
        TestContext.Check(!Core(BuildHistory(2, 100, 100), 5000, 1),
            "historico pequeno nao compacta");

        var hist = BuildHistory(12, 42000, 1000);
        TestContext.Check(Core(hist, 131072, 1), "historico acima do teto compacta");
        string firstArgs = hist[2]?["tool_calls"]?[0]?["function"]?["arguments"]?.GetValue<string>() ?? "";
        TestContext.Check(firstArgs.Contains("omitido", StringComparison.Ordinal),
            "args antigos viram marcador (o arquivo em disco é a fonte)");
        string lastTool = hist[^1]?["content"]?.GetValue<string>() ?? "";
        TestContext.Check(lastTool.Contains("CS1513", StringComparison.Ordinal),
            "resultado recente preserva o marcador de falha para o breaker");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Corte RECURSIVO: quando os turnos recentes intactos ja passam do teto, um passe
    /// so' vira no-op e a proxima request estoura o slot (medido no hanoi: 140146
    /// tokens contra 131072 por slot). A janela preservada encolhe (8 -> 4 -> 2) ate'
    /// caber; o turno em curso (ultimo par) fica intacto.
    /// </summary>
    public static Task ContextCompactRecursive()
    {
        // ~912k chars = ~285k tokens: 4 pares (8 mensagens) ja passam do teto de
        // 124518 (95% de 131072), entao o 1o passe (preserva 8) nao basta.
        var hist = new System.Text.Json.Nodes.JsonArray(
            new System.Text.Json.Nodes.JsonObject { ["role"] = "system", ["content"] = "s" });
        hist.Add(new System.Text.Json.Nodes.JsonObject { ["role"] = "user", ["content"] = "u" });
        for (int i = 0; i < 6; i++)
        {
            hist.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["role"] = "assistant", ["content"] = "",
                ["tool_calls"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                {
                    ["id"] = "c" + i, ["type"] = "function",
                    ["function"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["name"] = "write",
                        ["arguments"] = "{\"path\":\"a.cs\",\"content\":\"" + new string('x', 150_000) + "\"}"
                    }
                })
            });
            hist.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["role"] = "tool", ["tool_call_id"] = "c" + i,
                ["content"] = "ok " + new string('y', 2_000)
            });
        }
        static int Estimate(System.Text.Json.Nodes.JsonArray m)
        {
            int chars = 0;
            foreach (var x in m) chars += x?.ToJsonString().Length ?? 0;
            return (int)Math.Ceiling(chars / 3.2);
        }
        int before = Estimate(hist);
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.ContextGuard.Compact(hist, 131072, 1),
            "historico com turnos recentes acima do teto compacta");
        TestContext.Check(Estimate(hist) < before / 2,
            $"corte recursivo encolhe mais de metade (antes={before}, depois={Estimate(hist)})");
        string midArgs = hist[6]?["tool_calls"]?[0]?["function"]?["arguments"]?.GetValue<string>() ?? "";
        TestContext.Check(midArgs.Contains("omitido", StringComparison.Ordinal),
            "o 2o passe corta tambem o bloco do meio (nao so' o mais antigo)");
        string lastArgs = hist[^2]?["tool_calls"]?[0]?["function"]?["arguments"]?.GetValue<string>() ?? "";
        TestContext.Check(lastArgs.Length > 100_000,
            "o turno em curso (ultimo par) fica intacto");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Detector do 400 seco do llama: o guard reativo (usage.prompt_tokens) nao ve'
    /// nada num corpo de erro, entao a bridge usa este parse para a escalada
    /// nudge -> compactacao -> 413. Texto exato medido no hanoi.
    /// </summary>
    public static Task ContextOverflowDetect()
    {
        const string body = "{\"error\":{\"code\":400,\"message\":\"the request (140146 tokens) "
            + "exceeds the available context size (131072 tokens), try increasing it\","
            + "\"type\":\"exceed_context_size\"}}";
        TestContext.Check(global::Rochas.OpenCodeBridge.Core.ContextGuard.LooksLikeOverflow(body, out int t),
            "400 exceed_context_size e' detectado como estouro");
        TestContext.Check(t == 140146, $"tokens do prompt lidos da mensagem (veio {t})");
        TestContext.Check(!global::Rochas.OpenCodeBridge.Core.ContextGuard.LooksLikeOverflow(
                "{\"error\":{\"message\":\"invalid api key\"}}", out _),
            "outro 400 nao e' estouro");
        return Task.CompletedTask;
    }

    // O pin da cauda F# (FsMini) foi ARQUIVADO em 2026-10-30 junto da trilha
    // F#: ver Archived/FsMiniPin.cs. Ele compila lá, mas não é registrado no
    // Program.cs — teste arquivado não entra no placar ativo.

    // 8a amostra (2026-10-29): `(moves: ref seq<string>)` — `ref` e' tipo
    // POS-fixado; a mensagem FS0010 "Unexpected type application" nao tinha dica.

    // Dica identica repetida nao ensina: a escalada e' do Core (repeatCount), nao do teste.

    // 9a amostra (2026-10-29): lambda estilo Haskell (`\count p1 ->`) -> FS0010,
    // 0 reparo. A dica ensina `fun` e nao ecoa a barra invertida.

    // 10a amostra (2026-10-29): `: (count: int * string list)` — nome de campo
    // dentro da anotacao de retorno; o FS0010 "Unexpected symbol ':'" nao tinha dica
    // e o modelo repetiu o MESMO arquivo em 5 builds (0 reparo em 26 turnos).

    // 10a amostra (2026-10-29): `else let limit = ...` na MESMA linha -> FS0588
    // ("The block following this 'let' is unfinished"). A regra generica de corpo
    // nao ensinava o `else`; a dica chega no turno da falha.

    // 11a amostra (2026-10-29): `let rec hanoi (n: int)` chamado como `hanoi n 1 3 2`
    // -> FS0003 "This value is not a function and cannot be applied". A dica MEDE a
    // aridade na fonte (declaracao x chamada) e ensina a forma que compila; sem
    // posicao/medida ela se cala (nunca chuta).

    // 12a amostra (2026-10-29): `dest_aux` usado na linha 11 e declarado na linha 19 ->
    // FS0039 "The value or constructor 'dest_aux' is not defined". F# le de cima para
    // baixo: a dica manda mover a declaracao para cima (ou receber por parametro).

    // 14a amostra (2026-10-29): `subSeq @ "," + nextMove` — ramo `string` e ramo lista no
    // mesmo `if`. A dica antiga mandava `Array.append` para qualquer ramo; agora ela le o
    // tipo NOMEADO na mensagem e ensina o jeito de juntar de cada um.

    // 18a/19a amostra (2026-10-30, suite algo-fs): `List.contains node.Keys key`
    // inverte a assinatura (`'T -> 'T list`) e o compilador pede `'T list list`;
    // sem dica o fonte nunca foi corrigido (btree: 8 turnos no mesmo erro).

    // FS0043 medido em 2026-10-30 (E-mission-algo-fs-btree): o modelo escreveu
    // `if value == node.Value then` (C#-ismo) e o compilador recusou o operador.
    // A dica ensina o `=` de F# e aponta a linha; erro alheio nao recebe a dica.

    // 19a amostra (2026-10-30, suite algo-fs): `^` usado como potencia sai como
    // FS0101 "deprecated ... Use the '+' operator instead" e nao havia mapa.

    // 16a amostra (2026-10-29): `(n: int; source: string; ...)` -> FS0010
    // "Unexpected symbol ';' in pattern". O `;` so' separa itens de lista/array.

    // 17a amostra (2026-10-29): `let mutable sequencia = []<string list>` -> FS3156 no
    // `>`; o modelo editou OUTRA linha 6 vezes. A forma que compila anota com `:`.

    // 20a amostra (2026-10-30, missao K-Means F# — saiu do suite, a regra fica):
    // o array de registros ficou
    // sem o `}` do ultimo elemento e o compilador pediu `}` antes do `|]`.

    // Portao de fluxo bridge (PENDINGS P1): um unico check, com a causa exata,
    // recusa o run sem harness/tails/cauda e deixa passar o fluxo completo.
    // Um caso feliz + negativos — nao se cria camada de teste para testar o teste.
    public static Task BridgeFlowGateCases()
    {
        static JsonObject Valid() => new()
        {
            ["event"] = "system-sent", ["path"] = "chat", ["model"] = "Gemma4-E4B-Q4-build",
            ["chars"] = 4096, ["main_harness"] = true, ["has_tools"] = true, ["tools_tail"] = true,
            ["tail_scope"] = "all", ["harness_tails"] = true, ["fsmini"] = false
        };
        TestContext.Check(BridgeFlowGate.Check(Valid()) == "",
            "fluxo completo passa (veio: '" + BridgeFlowGate.Check(Valid()) + "')");

        var noHarness = Valid();
        noHarness["main_harness"] = false;
        TestContext.Check(BridgeFlowGate.Check(noHarness) == "ausente main_harness",
            "sem main_harness recusa com a causa certa (veio: '" + BridgeFlowGate.Check(noHarness) + "')");

        var noToolsTail = Valid();
        noToolsTail["tools_tail"] = false;
        TestContext.Check(BridgeFlowGate.Check(noToolsTail).StartsWith("ausente tools_tail", StringComparison.Ordinal),
            "request com tools e sem cauda recusa (veio: '" + BridgeFlowGate.Check(noToolsTail) + "')");

        var noToolsRequest = Valid();
        noToolsRequest["has_tools"] = false;
        noToolsRequest["tools_tail"] = false;
        TestContext.Check(BridgeFlowGate.Check(noToolsRequest) == "",
            "request sem tools nao exige cauda (a cauda sai de proposito)");

        var opencodeNoScope = Valid();
        opencodeNoScope["tail_scope"] = "off";
        TestContext.Check(BridgeFlowGate.Check(opencodeNoScope).Contains("tail_scope=off"),
            "tail_scope=off recusa (veio: '" + BridgeFlowGate.Check(opencodeNoScope) + "')");

        TestContext.Check(BridgeFlowGate.Check(null).Length > 0,
            "sem registro na bridge recusa (instrumentacao ausente)");

        var otherClient = Valid();
        otherClient["lane"] = "opencode";
        TestContext.Check(BridgeFlowGate.Check(otherClient).StartsWith("INCONCLUSIVO", StringComparison.Ordinal),
            "registro de outro cliente nao acusa o teste (veio: '" + BridgeFlowGate.Check(otherClient) + "')");

        var stale = Valid();
        stale["ts"] = 1000;
        TestContext.Check(BridgeFlowGate.Check(stale, 2000).StartsWith("registro anterior ao teste", StringComparison.Ordinal),
            "registro anterior ao teste recusa (veio: '" + BridgeFlowGate.Check(stale, 2000) + "')");
        TestContext.Check(BridgeFlowGate.Check(stale, 500) == "",
            "registro dentro da janela do teste passa");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Portao de fluxo SEM request na bridge do gate: INCONCLUSIVO, nunca FAIL.
    /// Fase que manda o modelo para a bridge secundaria (Q3 na edicao do front) ou
    /// teste so' de leitura (E-front-verify) caem aqui — o registro da rodada
    /// anterior nao pode virar vermelho (medido no front-bridge de 2026-10-30).
    /// </summary>
    /// <summary>
    /// Quem o portao P1 julga: E2E em modo bridge que serviu request de modelo. Teste
    /// de caminho de erro/leitura declara `NoModelFlow` e sai do julgamento — nao e'
    /// afrouxar o aceite, e' nao julgar fluxo que nao existe (2026-10-30).
    /// </summary>
    public static Task FlowGateApplies()
    {
        TestContext.Check(BridgeFlowGate.Applies("bridge", "E-error-mitigation", false),
            "E- em modo bridge sem declaracao entra no portao");
        TestContext.Check(!BridgeFlowGate.Applies("bridge", "E-error-mitigation", true),
            "teste que declara NoModelFlow sai do portao");
        TestContext.Check(!BridgeFlowGate.Applies("opencode", "E-tools-full", false),
            "modo opencode nao tem portao de fluxo da bridge");
        TestContext.Check(!BridgeFlowGate.Applies("bridge", "U-status", false),
            "U-* nao entra no portao (sem request de modelo)");
        return Task.CompletedTask;
    }

    public static Task FlowGateNoRequest()
    {
        var (noRequest, dump) = BridgeFlowGate.Probe(TestContext.Bridge, 0, long.MaxValue);
        TestContext.Check(noRequest.StartsWith("INCONCLUSIVO", StringComparison.Ordinal),
            "sem request no run o portao e' INCONCLUSIVO (veio: '" + noRequest + "')");
        TestContext.Check(dump.Contains("model_requests", StringComparison.Ordinal),
            "dump do INCONCLUSIVO traz o contador (veio: '" + dump + "')");
        return Task.CompletedTask;
    }

    // 17a amostra (2026-10-29): `printfn $"hanoi_seq={String.concat "," seq}"` ->
    // FS3373 "Invalid interpolated string". O CERTO separa num `let` e usa formato.

    // Ponteiro da linha do erro: o compilador diz `arquivo(linha,coluna)` e o modelo
    // editava a linha vizinha (17a amostra). Sem linha na saida, nada muda.

    // 15a amostra (2026-10-29): `currentPrimes.[p]` com o parametro sem anotacao ->
    // FS0752 "operator 'expr.[idx]' has been used on an object of indeterminate type".
    // Sete ocorrencias e nenhuma dica; a forma que compila e' a anotada.

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
        // Core e' a fonte da instrucao e a trilha F# foi ARQUIVADA em 2026-10-30. O
        // aviso diz "copie a forma": exemplo em F# (Program.fs/printfn/module) faz o
        // modelo copiar a forma ERRADA — medido no kmeans do L4 de 2026-10-31, que
        // escreveu `for (var point in _points)` (habito de linguagem dinamica).
        foreach (string marker in new[] { ".fs\"", ".fsproj", "printfn", "module Program", "open System" })
            TestContext.Check(!aviso.Contains(marker, StringComparison.Ordinal),
                "o aviso nao ensina sintaxe F# (achou '" + marker + "')");
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
