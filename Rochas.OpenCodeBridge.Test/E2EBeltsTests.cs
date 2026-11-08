using System.Text.Json.Nodes;
using Rochas.OpenCodeBridge.Core.Tools;

namespace Rochas.OpenCodeBridge.Test;

// Belts (azul→preta) SEM navegador: cadeia completa Orch -> Scripter -> Exec.
// Cada faixa grava resultado parcial em /tmp/opencode/belts/<key>_result.json
// para nao perder progresso em testes longos.
internal static class E2EBeltsTests
{
    static readonly (string Key, string Prompt)[] Belts =
    {
        ("azul", "Aplicacao backend .NET C# que realize uma consulta de dados em uma tabela (ex.: reembolsos corporativos) usando repositorio generico com ORM. Escopo SOMENTE backend de dados (sem frontend, sem API). Premissas: backend .NET C#."),
        ("verde", "Especifique o dominio backend C# de um CRUD de reembolsos corporativos (entidades, value objects, regras de aprovacao por faixa de valor) e o banco de dados SQL: DDL das tabelas, indices, constraints e seeds. Valide CPF e CNPJ. Escopo: dominio + banco SQL. Premissas: backend .NET C#. Divida a implementacao em etapas, dialogo em pt-br."),
        ("roxa", "Trabalhe no workspace da tarefa. Implemente o cadastro de clientes e fornecedores no backend .NET C#: dominio (entidades e value objects), banco SQL (DDL, indices e constraints), repositorios com Dapper, servicos de aplicacao e validacao/mascaras de e-mail, telefone BR com DDD e CEP. Entregue a implementacao completa no workspace da tarefa (backend .NET C# + banco SQL)."),
        ("marrom", "Especifique o dominio backend C# de um CRUD de reembolsos corporativos, o banco de dados SQL, os repositorios e servicos (Dapper), a API REST e o frontend React. Escopo: dominio + banco SQL + repositorios e servicos + API + frontend React. Premissas: backend .NET C#. Divida a implementacao em etapas, dialogo em pt-br."),
        ("preta", "Especifique a arquitetura de ecossistema: dominio backend C# do Portal do Colaborador, banco de dados SQL, repositorios e servicos, API REST, servicos bus, pipeline de Big Data com ML (regressao, classificacao, rede neural perceptron). Escopo: dominio + banco SQL + repositorios e servicos + API + servicos bus + redes neurais. Premissas: backend .NET C#. Divida a implementacao em etapas, dialogo em pt-br."),
    };

    const string OrchSystem = "Voce e o Orch (role orch, E4B). Segmenta o enunciado em tarefas menores e atomicas (max 5) e delegue cada tarefa pela tool `subagent` (argumento `prompt` com OBJETIVO+ESCOPO+PROIBIDO+ACEITE, `description` = titulo curto). Nao execute nada. Nao nomeie arquivo, path nem extensao: descreva o escopo por dominio tecnico e stack (linguagem, framework, banco, API).";

    /// <summary>
    /// Enunciado do Orch (ponta da cadeia de belts): o requisito entra no
    /// *** escopo *** e a ordem de entrega vem junto da segmentacao. O Build tem
    /// enunciado proprio (E2EBeltsBuildTests) e NAO usa este template.
    /// </summary>
    static string OrchEnunciado(string escopo) =>
        "Componha scripts shell .sh com shebang que construam usando internamente comandos para escrita dos arquivos que atendam o requisito do enunciado a seguir:  *** " + escopo +
        " ***, segmente em tarefas menores e delegue utilizando a tool subagent a agentes Scripter, a criacao de um script bash .sh que implemente o escopo da subtarefa enunciada";

    /// <summary>System do role Orch igual ao opencode: corpo do agents/orch.md
    /// (sem frontmatter). Sem isso a bridge usa so o OrchSystem curto, o main
    /// harness generico fala em "tool nativa" e o modelo vaza call:shell em vez
    /// de devolver o JSON array. Fallback = OrchSystem se o arquivo nao existir.</summary>
    /// <summary>
    /// Carrega um asset de prompt pinado do repo (<c>Prompts/&lt;file&gt;</c>), com
    /// fallback para <c>~/.config/opencode/agents/&lt;file&gt;</c>. O consumidor
    /// apenas LE o contrato do role: texto de role nao mora no codigo do teste.
    /// </summary>
    static string LoadPromptAsset(string file, string fallback)
    {
        var candidates = new List<string>
        {
            System.IO.Path.Combine(AppContext.BaseDirectory, "Prompts", file)
        };
        try
        {
            string home = Environment.GetEnvironmentVariable("HOME") ?? "";
            if (home.Length == 0)
                home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (home.Length > 0)
                candidates.Add(System.IO.Path.Combine(home, ".config", "opencode", "agents", file));
        }
        catch { }
        foreach (string path in candidates)
        {
            try
            {
                if (!System.IO.File.Exists(path)) continue;
                string raw = System.IO.File.ReadAllText(path);
                var parts = raw.Split("---", 3, StringSplitOptions.None);
                string body = parts.Length >= 3 ? parts[2].Trim() : raw.Trim();
                if (body.Length == 0) continue;
                Log($"  [prompt-asset] source={path} chars={body.Length}");
                return body;
            }
            catch { }
        }
        Log($"  [prompt-asset] FALLBACK curto ({file} ausente)");
        return fallback;
    }

    static string LoadOrchSystem()
    {
        var candidates = new List<string>
        {
            System.IO.Path.Combine(AppContext.BaseDirectory, "Prompts", "orch.md")
        };
        try
        {
            string home = Environment.GetEnvironmentVariable("HOME") ?? "";
            if (home.Length == 0)
                home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (home.Length > 0)
                candidates.Add(System.IO.Path.Combine(home, ".config", "opencode", "agents", "orch.md"));
        }
        catch { }
        foreach (string path in candidates)
        {
            try
            {
                if (!System.IO.File.Exists(path)) continue;
                string raw = System.IO.File.ReadAllText(path);
                var parts = raw.Split("---", 3, StringSplitOptions.None);
                string body = parts.Length >= 3 ? parts[2].Trim() : raw.Trim();
                if (body.Length == 0) continue;
                Log($"  [orch-system] source={path} chars={body.Length}");
                return body;
            }
            catch { }
        }
        Log("  [orch-system] FALLBACK curto (Prompts/orch.md e ~/.config ausentes)");
        return OrchSystem;
    }

    /// <summary>
    /// Contrato do Scripter — fonte unica: asset pinado <c>Prompts/scripter.md</c>
    /// (mesmo para bridge e opencode), com <c>{title}</c> substituido pelo titulo
    /// normalizado da tarefa. O ENTREGAVEL e' o script bash + companion; a tarefa
    /// do Orch e' o CONTEUDO que o script executa.
    /// Historico: sem essa separacao o modelo obedece a tarefa (que cita
    /// `src/Domain/*.cs` e `db/*.sql`) e nunca grava o `.sh` — medido no
    /// e-belts-marrom-orch de 2026-10-31 (0 scripts, 6 reescritas em 8 turnos) e
    /// reincidente em 2026-11-07 no e-belts-azul-orch (Orch segmentou citando
    /// `Data/*.cs`, Scripter gravou `.cs`, `scripts=0`).
    /// A REGRA UNICA DE ENTREGA (`.sh` com shebang construindo os artefatos via
    /// heredoc) e a lista de PROIBIDO por extensao vivem no ASSET, nao no codigo
    /// do consumidor (`TEST-DRIVER.md` §1: teste so' chama o Core com enunciado
    /// e parametros).
    /// </summary>
    internal static string ScripterSystem(string safeTitle, string grounded) =>
        LoadPromptAsset("scripter.md", ScripterFallback).Replace("{title}", safeTitle).TrimEnd()
        + "\n\n" + grounded;

    /// <summary>
    /// Contrato do role quando o asset pinado nao existe. Curto de proposito:
    /// o texto real mora em <c>Prompts/scripter.md</c>; estoque de role no
    /// codigo do teste e' o que a regra "teste so' chama o Core" proibe.
    /// </summary>
    const string ScripterFallback =
        "Voce e o Scripter (role scripter, E4B). Voce NAO executa a tarefa: escreve o script .sh com shebang "
        + "(#!/usr/bin/env bash) que realiza a implementacao dos artefatos do requisito, via heredoc. "
        + "Entregue {title}.sh e o companion {title}.md. Responda apenas SCRIPTER-OK.";

    /// <summary>Enunciado entregue ao Scripter: a tarefa do Orch passa intacta.</summary>
    internal static string ScripterTask(string taskPrompt) => taskPrompt;

    static (int Code, string Out) Run(string exe, string cwd, int secs, params string[] args)
    {
        var r = ProcessRunner.Run(exe, args, cwd, secs);
        return (r.Success ? 0 : 1, r.Success ? r.Output : r.Error);
    }

    static void Log(string msg) { Console.WriteLine(msg); Console.Out.Flush(); }

    // Tag do run: a pasta de trabalho de cada belt e unica por execucao. O path
    // fixo /tmp/opencode/belts/<key> colidia com sobras escritas pela bridge
    // (que roda como root) e o Directory.Delete estourava
    // UnauthorizedAccessException fora do try — derrubava a belt inteira em 1 s
    // (medido 2026-10-29 02:39, E-belts-direct em belts-orch-bridge).
    static readonly string RunTag = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Environment.ProcessId;

    /// <summary>
    /// Cria a pasta de trabalho vazia da belt: sobras de uma belt anterior
    /// inflariam scriptsOk/execOk. Com path unico por run a colisao deixa de
    /// existir; se ainda assim a remocao falhar, segue no dir novo (unico) em
    /// vez de matar a belt por causa de lixo de outro dono.
    /// </summary>
    static void FreshWorkspace(string ws)
    {
        try
        {
            if (System.IO.Directory.Exists(ws)) System.IO.Directory.Delete(ws, true);
        }
        catch (Exception ex)
        {
            Log($"  [belt] cleanup ignorado em {ws}: {ex.GetType().Name}");
        }
        System.IO.Directory.CreateDirectory(ws);
    }

    /// <summary>String tolerante: o modelo as vezes manda `false`/numero onde o contrato pede string.</summary>
    static string Str(JsonNode? n, string fallback = "")
        => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;

    static void SaveResult(string key, string status, string detail)
    {
        try
        {
            string path = System.IO.Path.Combine("/tmp/opencode/belts", key + "_result.json");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, $"{{\"key\":\"{key}\",\"status\":\"{status}\",\"detail\":\"{detail.Replace("\"","'")}\",\"when\":\"{DateTime.UtcNow:o}\"}}");
        }
        catch { }
    }

    public static async Task Direct()
    {
        // Modelos das roles derivados do modelo realmente servido pela bridge
        // (ex.: Gemma4-E4B-Q4 -> Gemma4-E4B-Q4-orch/-scripter/-exec). Nomes
        // fixos de outra familia (12b/e2b) nao existem neste lab e faziam o
        // Orch falhar com 0 tarefas antes de qualquer medida do modelo.
        string baseModel = TestContext.ServedModel;
        foreach (var r in new[] { "-plan", "-build", "-orch", "-moe", "-scripter", "-exec" })
            if (baseModel.EndsWith(r)) { baseModel = baseModel[..^r.Length]; break; }
        if (baseModel.Length == 0) baseModel = "Gemma4-E4B-Q4";
        string orchModel = baseModel + "-orch";
        string scripterModel = baseModel + "-scripter";
        Log($"[belts:direct] mode={TestContext.Mode} Orch={orchModel} Scripter={scripterModel} (Exec fora deste teste)");
        int pass = 0, fail = 0;
        var keysEnv = string.IsNullOrWhiteSpace(TestContext.BeltsFilter) ? Environment.GetEnvironmentVariable("BELTS_KEYS") : TestContext.BeltsFilter;
        var beltsToRun = string.IsNullOrWhiteSpace(keysEnv)
            ? Belts
            : Belts.Where(b => keysEnv.Split(',').Select(x=>x.Trim()).Contains(b.Key)).ToArray();
        foreach (var (key, prompt) in beltsToRun)
        {
            string ws = System.IO.Path.Combine("/tmp/opencode/belts", key + "-" + RunTag);
            // Workspace limpo: sobras de uma belt anterior inflariam scriptsOk/execOk.
            FreshWorkspace(ws);
            Log($"  [belt:{key}] START ws={ws}"); 
            try
            {
                // 1) Orch: quem orquestra E' O MODELO. O consumidor so' conduz o
                // loop de transporte — envia a mensagem, executa a tool call no
                // Core (/api/tools/execute) e devolve o resultado. Nenhuma
                // segmentacao, nenhum JSON array de tarefas, nenhum prompt de
                // Scripter montado no cliente.
                string ask = OrchEnunciado(prompt.Replace("{ws}", ws));
                if (TestContext.Mode == "opencode")
                {
                    Log($"  [belt:{key}] Orch via opencode (agent=orch)...");
                    string orchPrompt = LoadOrchSystem() + "\n\n" + ask;
                    string outCli = TestContext.OpencodeRun(orchPrompt, ws, agent: "orch");
                    string cliLog = outCli.Replace("\r", " ").Replace("\n", "\\n");
                    Log($"  [belt:{key}] opencode: {cliLog[..Math.Min(200, cliLog.Length)]}");
                }
                else
                {
                    Log($"  [belt:{key}] Orch via bridge (tools do role coordinator)...");
                    var history = new JsonArray(
                        new JsonObject { ["role"]="system", ["content"]=LoadOrchSystem() },
                        new JsonObject { ["role"]="user", ["content"]=ask });
                    // Role coordinator: leitura + delegacao (sem write/shell).
                    var orchTools = TestContext.Catalog("read", "glob", "grep", "subagent");
                    int calls = 0, subagentCalls = 0, stall = 0;
                    for (int turn = 0; turn < 12; turn++)
                    {
                        JsonObject r;
                        try
                        {
                            r = await TestContext.PostObj(TestContext.Bridge + "/v1/chat/completions",
                                new JsonObject { ["model"]=orchModel, ["stream"]=false, ["messages"]=history.DeepClone(), ["tools"]=orchTools.DeepClone() });
                        }
                        catch (Exception ex)
                        {
                            Log($"  [belt:{key}] post orch falhou: {ex.Message.Split('\n')[0]}");
                            break;
                        }
                        var msg = r["choices"]?[0]?["message"]?.AsObject();
                        string content = Str(msg?["content"]);
                        var rawCalls = msg?["tool_calls"]?.AsArray();
                        if (rawCalls is null || rawCalls.Count == 0)
                        {
                            Log($"  [belt:{key}] turno {turn}: sem tool (chars={content.Length}) — encerrando sem nudge");
                            break;
                        }
                        var parsed = new List<(JsonObject Call, JsonObject Args)>();
                        bool badArgs = false;
                        foreach (var c in rawCalls)
                        {
                            try
                            {
                                string raw = c?["function"]?["arguments"]?.GetValue<string>() ?? "{}";
                                var a = JsonNode.Parse(raw)?.AsObject() ?? throw new FormatException("args nao e objeto");
                                parsed.Add((c!.AsObject(), a));
                            }
                            catch (Exception ex)
                            {
                                Log($"  [belt:{key}] turno {turn}: args invalidos ({ex.GetType().Name}) — encerrando sem nudge");
                                badArgs = true;
                                break;
                            }
                        }
                        if (badArgs) break;
                        var echo = new JsonArray();
                        foreach (var (c, _) in parsed)
                            echo.Add(new JsonObject { ["id"]=Str(c["id"]), ["type"]="function", ["function"]=new JsonObject { ["name"]=Str(c["function"]?["name"]), ["arguments"]=Str(c["function"]?["arguments"], "{}") } });
                        history.Add(new JsonObject { ["role"]="assistant", ["content"]=content, ["tool_calls"]=echo });
                        bool progressed = false;
                        int subsNow = 0;
                        foreach (var (c, a) in parsed)
                        {
                            string name = Str(c["function"]?["name"]);
                            var exec = await TestContext.ExecuteTool(name, a, ws, orchModel);
                            bool ok = exec["ok"]?.GetValue<bool>() == true;
                            string outText = ok
                                ? exec["output"]?.GetValue<string>() ?? "ok"
                                : "ERRO: " + (exec["error"]?.GetValue<string>() ?? "falha");
                            if (outText.Length > 4000) outText = outText[..4000] + "...(truncado)";
                            if (ok) progressed = true;
                            if (name is "subagent" or "task") { subagentCalls++; subsNow++; }
                            calls++;
                            history.Add(new JsonObject { ["role"]="tool", ["tool_call_id"]=Str(c["id"]), ["content"]=outText });
                        }
                        Log($"  [belt:{key}] turno {turn}: {parsed.Count} call(s) [{subsNow} subagent]");
                        if (!progressed)
                        {
                            if (++stall >= 2) { Log($"  [belt:{key}] stall: {stall} turnos sem progresso"); break; }
                        }
                        else stall = 0;
                    }
                    TestContext.Check(subagentCalls > 0,
                        $"[{key}] o modelo delegou pela tool `subagent` (calls={calls}, subagent={subagentCalls})");
                    Log($"  [belt:{key}] Orch: {calls} call(s), {subagentCalls} delegacao(oes)");
                }

                // 2) Integridade dos .sh gerados: shebang, sem tool colada e
                // `bash -n` limpo. Execucao fica fora deste teste (testar depois).
                Log($"  [belt:{key}] 3/3 integridade dos scripts...");
                int integrityOk = 0, discarded = 0;
                // O Scripter escolhe o diretorio do entregavel (o Orch segmenta em
                // pastas, ex. src/Financeiro): o aceite tem de olhar o workspace
                // inteiro, nao so' a raiz — senao o artefato existe e o veredito
                // conta zero (medido 2026-11-07 20:59).
                foreach (var f in System.IO.Directory.GetFiles(ws, "*.sh", System.IO.SearchOption.AllDirectories))
                {
                    string name = System.IO.Path.GetFileName(f);
                    string body = System.IO.File.ReadAllText(f);
                    string? why = null;
                    if (body.Length == 0 || !body.StartsWith("#!", StringComparison.Ordinal))
                        why = "sem shebang";
                    else if (body.Contains("write{") || body.Contains("\"path\":")
                             || body.Contains("```"))
                        why = "tool/markdown colados";
                    else
                    {
                        // O .sh pode estar em subpasta (o modelo escolhe o layout):
                        // roda o `bash -n` com o path RELATIVO ao ws. Com so' o
                        // nome do arquivo o bash da exit 127 e o artefato bom era
                        // descartado (medido 2026-11-07 23:39, create_schema.sh).
                        var (syntax, syntaxErr) = Run("bash", ws, 60, "-n",
                            System.IO.Path.GetRelativePath(ws, f));
                        if (syntax != 0)
                        {
                            // `ProcessRunner` devolve stdout+`[stderr]`+stderr no mesmo
                            // campo: cortar na 1a linha jogava fora a mensagem do bash
                            // (que vem depois de "exit 2: \n[stderr]") — medido em
                            // 2026-11-07 20:55, create_model.sh DESCARTADO sem motivo.
                            string full = syntaxErr.Trim().Replace("\n", " | ");
                            why = full[..Math.Min(240, full.Length)];
                        }
                    }

                    if (why is not null)
                    {
                        // Script que nao passa em bash -n nao e artefato: sai do
                        // workspace (fica no log) e nao conta como gerado.
                        discarded++;
                        Log($"  [belt:{key}] script DESCARTADO ({why}): {name}");
                        try { System.IO.File.Delete(f); } catch { }
                        continue;
                    }
                    integrityOk++;
                }

                // 3) Veredito por CONTEUDO do artefato (nunca por quantidade):
                // todo .sh restante passou no gate acima; alem disso o script tem
                // que CONSTRUIR os artefatos .NET do enunciado (heredoc + codigo).
                bool shOk = false;
                string shWhy = "nenhum .sh constroi artefato .NET";
                foreach (var f in System.IO.Directory.GetFiles(ws, "*.sh", System.IO.SearchOption.AllDirectories))
                {
                    string body = System.IO.File.ReadAllText(f);
                    bool heredoc = body.Contains("<<", StringComparison.Ordinal);
                    bool dotnetContent = body.Contains("class ", StringComparison.Ordinal)
                                      || body.Contains("namespace ", StringComparison.Ordinal)
                                      || body.Contains(".csproj", StringComparison.OrdinalIgnoreCase)
                                      || body.Contains("dotnet ", StringComparison.Ordinal);
                    if (heredoc && dotnetContent) { shOk = true; shWhy = ""; break; }
                }
                Log($"  [belt:{key}] artefatos: integros={integrityOk} descartados={discarded} " +
                    $"conteudoNet={(shOk ? "ok" : shWhy)}");
                TestContext.Check(shOk,
                    $"[{key}] .sh atende ao enunciado (shebang, bash -n, heredoc criando artefato .NET)");
                SaveResult(key, "PASS", $"integros={integrityOk} descartados={discarded} conteudoNet={(shOk ? "ok" : "nao")}");
                pass++;
            }
            catch (Exception ex)
            {
                Log($"  [belt:{key}] FAIL: {ex.Message.Split('\n')[0]}");
                SaveResult(key, "FAIL", ex.Message.Split('\n')[0]);
                fail++;
            }
        }
        TestContext.Check(fail == 0, $"belts direct: {fail} falharam");
        Log($"[belts:direct] {pass}/{beltsToRun.Length} PASS");
    }

    static string WebTestDll()
    {
        var probe = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (probe is not null)
        {
            string bin = System.IO.Path.Combine(probe.FullName, "Rochas.OpenCodeBridge.Web.Test", "bin", "Release");
            if (System.IO.Directory.Exists(bin))
            {
                string? hit = System.IO.Directory.GetFiles(bin, "Rochas.OpenCodeBridge.Web.Test.dll", System.IO.SearchOption.AllDirectories).FirstOrDefault();
                if (hit is not null) return hit;
            }
            probe = probe.Parent;
        }
        throw new Exception("Web.Test nao buildado (dotnet build -c Release Rochas.OpenCodeBridge.slnx)");
    }

    /// <summary>Belts pela Web UI real (stream, tools e quadros gerados na sessao) via Web.Test --ui.</summary>
    public static async Task WebUi()
    {
        if (!TestContext.WebDriver)
            TestContext.Skip("WebDriver desligado: use --webdriver (HEADED=1 para acompanhar)");
        string keys = string.IsNullOrWhiteSpace(TestContext.BeltsFilter) ? "" : TestContext.BeltsFilter;
        var args = new List<string>
        {
            "exec", WebTestDll(), "--web", TestContext.WebDriverUrl, "--ui",
            "--skip-units", "--skip-integration", "--mode", "plan", "--phase", "full",
        };
        if (keys.Length > 0) { args.Add("--belts"); args.Add(keys); }
        var (code, outp) = Run("dotnet", "/tmp/opencode", 5400, args.ToArray());
        Log($"[belts:webui] {TestContext.WebDriverUrl} keys={(keys.Length == 0 ? "all" : keys)} exit={code}");
        Log(outp.Length > 4000 ? outp[^4000..] : outp);
        TestContext.Check(code == 0, $"belts via Web UI ({TestContext.WebDriverUrl}) exit={code}");
        await Task.CompletedTask;
    }
}