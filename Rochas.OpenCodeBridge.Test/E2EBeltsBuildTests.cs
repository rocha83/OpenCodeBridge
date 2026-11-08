using System.Text.Json.Nodes;
using Rochas.OpenCodeBridge.Core.Tools;

namespace Rochas.OpenCodeBridge.Test;

// Belts (azul->preta) Trilha BUILD: E4B (role build) implementa DIRETAMENTE os
// artefatos do enunciado usando tools reais (write) em loop de turnos, delegando
// sub-tarefas quando fizer sentido. Aceite: o CONTEUDO atende o enunciado e o
// workspace compila (`dotnet build` rc=0). Quantidade de artefatos nunca e'
// assert nem instrucao ao modelo. Sem execucao de script nesta trilha.
// Resultados parciais em /tmp/opencode/belts-build/<key>_result.json.
internal static class E2EBeltsBuildTests
{
    static readonly (string Key, string Prompt)[] Belts =
    {
        ("azul", "Crie no backend .NET C# uma consulta de dados em uma tabela (ex.: reembolsos corporativos) usando repositorio generico com ORM. Escopo SOMENTE backend de dados (sem frontend, sem API). Premissas: backend .NET C#. Divida a implementacao em etapas, dialogo em pt-br. Critério de aceite, solução implementada, codificada em arquivos no disco e compilando: crie um arquivo de projeto com nome (ex.: `Reembolsos.csproj`, nao `.csproj` sem nome) e os .cs (classes, interfaces, repositorios) e rode `dotnet build` ate rc=0."),
        ("verde", "Implemente o dominio backend C# de um CRUD de reembolsos corporativos (entidades, value objects, regras de aprovacao por faixa de valor) e o banco de dados SQL: DDL das tabelas, indices, constraints e seeds. Valide CPF e CNPJ. Escopo: dominio + banco SQL. Premissas: backend .NET C#. Divida a implementacao em etapas, dialogo em pt-br. Critério de aceite, solução implementada, codificada em arquivos no disco e compilando: arquivos .cs (entidades/VOs) e .sql (DDL)."),
        ("roxa", "Trabalhe em {ws}. Implemente o cadastro de clientes e fornecedores no backend .NET C#: dominio (entidades e value objects), banco SQL (DDL, indices e constraints), repositorios com Dapper, servicos de aplicacao e validacao/mascaras de e-mail, telefone BR com DDD e CEP. Crie os arquivos .cs e .sql com caminho absoluto dentro de {ws}, usando a tool write. Comece pelo dominio, depois o SQL e depois repositorios/servicos; nao escreva plano, va direto ao write. Ao terminar responda apenas BUILD-OK com a lista dos arquivos."),
        ("marrom", "Implemente o dominio backend C# de um CRUD de reembolsos corporativos, o banco de dados SQL, os repositorios e servicos (Dapper), a API REST e o frontend React. Escopo: dominio + banco SQL + repositorios e servicos + API + frontend React. Premissas: backend .NET C#. Divida a implementacao em etapas, dialogo em pt-br. Cada arquivo deve conter UMA responsabilidade (um artefato por arquivo: um .cs de dominio, um .sql de DDL, um .tsx de frontend); nao misture dominio, banco, API e frontend no mesmo arquivo. Critério de aceite, solução implementada, codificada em arquivos no disco e compilando: arquivos .cs, .sql e .tsx/.jsx."),
        ("preta", "Implemente a arquitetura de ecossistema: dominio backend C# do Portal do Colaborador, banco de dados SQL, repositorios e servicos, API REST, servicos bus, pipeline de Big Data com ML (regressao, classificacao, rede neural perceptron). Escopo: dominio + banco SQL + repositorios e servicos + API + servicos bus + redes neurais. Premissas: backend .NET C#. Divida a implementacao em etapas, dialogo em pt-br. Cada arquivo deve conter UMA responsabilidade (um artefato por arquivo: um .cs de dominio, um .sql de DDL, um .py de treino); nao misture dominio, banco, API, bus e ML no mesmo arquivo. Critério de aceite, solução implementada, codificada em arquivos no disco e compilando: arquivos .cs, .sql e scripts de treino."),
    };

    static void Log(string msg) { Console.WriteLine(msg); Console.Out.Flush(); }

    // Uma chamada, sem retry: 500 peg-gemma4 / 502 upstream_aborted sao resultado
    // do turno e viram FAIL honesto (invariante docs/TUNEL-LLAMA.md).
    static Task<JsonObject> PostChat(string key, int turn, JsonObject body, string toolName) =>
        TestContext.PostObj(TestContext.Bridge + "/v1/chat/completions", body);

    /// <summary>500 de parser (peg-gemma4) ou 502 de aborto do upstream.</summary>
    static bool IsUpstreamFormatFailure(Exception ex)
    {
        string m = ex.Message;
        return (m.Contains("-> 500", StringComparison.Ordinal)
                && m.Contains("peg-gemma4", StringComparison.OrdinalIgnoreCase))
            || m.Contains("-> 502", StringComparison.Ordinal)
            || m.Contains("upstream_aborted", StringComparison.OrdinalIgnoreCase);
    }

    static string FirstLine(string text) => text.Split('\n')[0];
    static void SaveResult(string key, string status, string detail)
    {
        try
        {
            string path = System.IO.Path.Combine("/tmp/opencode/belts-build", key + "_result.json");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, $"{{\"key\":\"{key}\",\"status\":\"{status}\",\"detail\":\"{detail.Replace("\"","'").Replace("\\","/")}\",\"when\":\"{DateTime.UtcNow:o}\"}}");
        }
        catch { }
    }

    static (int Code, string Out) Run(string exe, string cwd, int secs, params string[] args)
    {
        var r = ProcessRunner.Run(exe, args, cwd, secs);
        return (r.Success ? 0 : 1, r.Success ? r.Output : r.Error);
    }

    /// <summary>
    /// O conteudo do workspace atende o enunciado do belt? Le o texto (codigo e
    /// DDL) e exige os elementos que o enunciado pede. NUNCA conta artefatos.
    /// </summary>
    static bool ContentMatchesEnunciado(string key, string ws)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var f in System.IO.Directory.GetFiles(ws, "*", System.IO.SearchOption.AllDirectories))
        {
            string ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
            if (ext is not (".cs" or ".sql" or ".csproj" or ".tsx")) continue;
            try { sb.AppendLine(System.IO.File.ReadAllText(f)); } catch { }
        }
        string all = sb.ToString();
        if (all.Trim().Length == 0) return false;
        bool hasClass = all.Contains("class ", StringComparison.Ordinal);
        bool hasTable = all.Contains("CREATE TABLE", StringComparison.OrdinalIgnoreCase);
        bool hasRepo = all.Contains("Repository", StringComparison.OrdinalIgnoreCase)
                    || all.Contains("Repositorio", StringComparison.OrdinalIgnoreCase);
        bool hasOrm = all.Contains("DbContext", StringComparison.Ordinal)
                   || all.Contains("EntityFrameworkCore", StringComparison.Ordinal)
                   || all.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)
                   || all.Contains("Dapper", StringComparison.Ordinal);
        bool hasReembolso = all.Contains("reembolso", StringComparison.OrdinalIgnoreCase)
                         || all.Contains("reimbursement", StringComparison.OrdinalIgnoreCase);
        bool hasCadastro = all.Contains("cliente", StringComparison.OrdinalIgnoreCase)
                        && all.Contains("fornecedor", StringComparison.OrdinalIgnoreCase);
        bool hasPortal = all.Contains("colaborador", StringComparison.OrdinalIgnoreCase)
                      || all.Contains("portal", StringComparison.OrdinalIgnoreCase);
        return key switch
        {
            "azul" => hasClass && hasRepo && hasOrm && hasReembolso,
            "verde" => hasClass && hasTable && hasReembolso,
            "roxa" => hasClass && hasCadastro,
            "marrom" => hasClass && hasTable && hasReembolso,
            "preta" => hasClass && hasPortal,
            _ => hasClass
        };
    }

    public static async Task Direct()
    {
        string model = TestContext.SuiteModel;
        if (!model.Contains("build")) model = model.Replace("-orch", "-build").Replace("-plan", "-build");
        if (!model.Contains("build")) model += "-build";
        Log($"[belts-build:direct] modelo {model} (bridge {TestContext.Bridge})");
        // Array de tools COMPLETO: a regra do ToolBlacklist e' "bloqueia a chamada,
        // nao esconde a tool" — o schema nao e' o lugar de restringir. O role build
        // escreve/edita/le, compila e valida (shell) e versiona (git).
        var tools = TestContext.Catalog();
        int pass = 0, fail = 0;
        var keysEnv = string.IsNullOrWhiteSpace(TestContext.BeltsFilter) ? Environment.GetEnvironmentVariable("BELTS_KEYS") : TestContext.BeltsFilter;
        var beltsToRun = string.IsNullOrWhiteSpace(keysEnv)
            ? Belts
            : Belts.Where(b => keysEnv.Split(',').Select(x=>x.Trim()).Contains(b.Key)).ToArray();
        // Workspace unico por run: o bridge executa write como root, entao
        // reutilizar a mesma pasta faz a limpeza do mint falhar com acesso
        // negado no run seguinte. Pasta nova tambem preserva a evidencia.
        string runTag = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}";
        string baseWs = System.IO.Path.Combine("/tmp/opencode/belts-build", runTag);
        foreach (var (key, prompt) in beltsToRun)
        {
            string ws = System.IO.Path.Combine(baseWs, key);
            System.IO.Directory.CreateDirectory(ws);
            Log($"  [belt-build:{key}] START ws={ws}");
            try
            {
                var history = new JsonArray(
                    new JsonObject { ["role"]="system", ["content"]=$"Voce e arquiteto desenvolvedor senior (role build), dialogo em pt-br. Trabalhe em {ws}. Implemente os artefatos pedidos direto no disco, sem plano e sem prosa, usando caminhos absolutos dentro de {ws}. O entregavel tem de compilar: comece escrevendo o .csproj (com nome) na raiz do workspace e, ao final, rode `dotnet build` ate rc=0. Ao terminar responda apenas BUILD-OK com o resumo do que entregou." },
                    new JsonObject { ["role"]="user", ["content"]=prompt.Replace("{ws}", ws) });
                int filesCreated = 0;
                int totalChars = 0;
                int lastFiles = 0;
                int stall = 0;
                // Sem nudge no cliente (docs/TUNEL-LLAMA.md): turno sem tool call
                // ou com args invalidos encerra a coleta e o aceite (conteudo +
                // `dotnet build`) decide. Nada de meta de quantidade.
                for (int turn = 0; turn < 14; turn++)
                {
                    var r = await PostChat(key, turn,
                        new JsonObject { ["model"]=model, ["stream"]=false, ["max_tokens"]=16384, ["messages"]=history.DeepClone(), ["tools"]=tools.DeepClone() }, "write");
                    var msg = r["choices"]?[0]?["message"]?.AsObject();
                    string content = msg?["content"]?.GetValue<string>() ?? "";
                    totalChars += content.Length;
                    var calls = msg?["tool_calls"]?.AsArray();
                    if (calls is null || calls.Count == 0)
                    {
                        Log($"  [belt-build:{key}] turno {turn}: sem tools (chars={content.Length}) — encerrando sem nudge");
                        break;
                    }
                    // Parse primeiro: tool call truncada/invalida nao entra no historico.
                    // Reenviar JSON quebrado ao llama costuma virar 500 peg-gemma4.
                    var parsed = new System.Collections.Generic.List<(JsonObject Call, JsonObject Args)>();
                    bool badArgs = false;
                    string badName = "", badRaw = "";
                    foreach (var c in calls)
                    {
                        try
                        {
                            var co = c as JsonObject ?? throw new FormatException("tool call nao e objeto");
                            string raw = co["function"]?["arguments"]?.GetValue<string>() ?? "{}";
                            var a = JsonNode.Parse(raw)?.AsObject();
                            if (a is null) throw new FormatException("args nao e objeto");
                            parsed.Add((co, a));
                        }
                        catch (Exception ex)
                        {
                            badArgs = true;
                            badName = c?["function"]?["name"]?.GetValue<string>() ?? "?";
                            badRaw = c?["function"]?["arguments"]?.GetValue<string>() ?? "<sem arguments>";
                            // Instrumentacao (2026-11-07): o veredito dizia so
                            // "args invalidos/truncados" e o diagnostico virava
                            // adivinhacao (384s de step, 0 arquivos, sem o cru).
                            // Grava o arguments COMO VEIO + a excecao e loga o
                            // inicio — comportamento do dente nao muda.
                            try
                            {
                                string dump = Path.Combine("/tmp/opencode",
                                    $"belts-badargs-{key}-turn{turn}.txt");
                                File.WriteAllText(dump,
                                    $"tool={badName}\nlen={badRaw.Length}\n" +
                                    $"exception={ex.GetType().Name}: {ex.Message}\n\n{badRaw}\n");
                                Log($"  [belt-build:{key}] turno {turn}: args invalidos em {dump} " +
                                    $"(tool={badName}, len={badRaw.Length}, {ex.GetType().Name}: {ex.Message})");
                                Log($"  [belt-build:{key}] turno {turn}: inicio dos args: " +
                                    (badRaw.Length > 300 ? badRaw[..300] : badRaw).Replace("\n", " \\n "));
                            }
                            catch { }
                            break;
                        }
                    }
                    if (badArgs)
                    {
                        Log($"  [belt-build:{key}] turno {turn}: tool call com args invalidos/truncados — encerrando sem nudge");
                        break;
                    }
                    bool progressed = false;
                    int wroteNow = 0;
                    var echo = new JsonArray();
                    foreach (var (c, _) in parsed) echo.Add(new JsonObject { ["id"]=c["id"]?.GetValue<string>()??"", ["type"]="function", ["function"]=new JsonObject { ["name"]=c["function"]?["name"]?.GetValue<string>()??"", ["arguments"]=c["function"]?["arguments"]?.GetValue<string>()??"{}" } });
                    history.Add(new JsonObject { ["role"]="assistant", ["content"]=content, ["tool_calls"]=echo });
                    foreach (var (c, a) in parsed)
                    {
                        string tname = c["function"]?["name"]?.GetValue<string>() ?? "write";
                        string ctt = a["content"]?.GetValue<string>() ?? "";
                        var exec = await TestContext.ExecuteTool(tname, a, ws, model);
                        bool execOk = exec["ok"]?.GetValue<bool>() == true;
                        string outText = execOk
                            ? exec["output"]?.GetValue<string>() ?? "ok"
                            : "ERRO: " + (exec["error"]?.GetValue<string>() ?? "falha");
                        if (execOk) progressed = true;
                        if (execOk && tname == "write")
                        {
                            filesCreated++;
                            wroteNow++;
                            // Metrica de substancia: o artefato real e o conteudo
                            // gravado em disco, nao o texto do assistant (que pode
                            // ser so um resumo curto).
                            totalChars += ctt.Length;
                        }
                        history.Add(new JsonObject { ["role"]="tool", ["tool_call_id"]=c["id"]?.GetValue<string>()??"", ["content"]=outText });
                    }
                    Log($"  [belt-build:{key}] turno {turn}: {parsed.Count} call(s) [{wroteNow} write], total arquivos={filesCreated}");
                    // Progresso = arquivo novo OU tool executada com sucesso (build,
                    // leitura, git): turno de validacao nao e' travamento.
                    if (filesCreated == lastFiles && !progressed) stall++; else stall = 0;
                    lastFiles = filesCreated;
                    if (stall >= 4) { Log($"  [belt-build:{key}] stall detectado (4 turnos sem progresso), encerrando"); break; }
                }

                // Aceite por CONTEUDO + build. Quantidade de artefatos NAO e' assert:
                // o que vale e' o codigo atender o enunciado e compilar.
                var (brc, bout) = Run("dotnet", ws, 300, "build", "--nologo", "-v:q");
                bool buildOk = brc == 0;
                bool contentOk = ContentMatchesEnunciado(key, ws);
                bool ok = buildOk && contentOk;
                Log($"  [belt-build:{key}] {(ok ? "PASS" : "FAIL")} build={(buildOk ? "ok" : "rc=" + brc)} " +
                    $"conteudo={(contentOk ? "atende" : "nao atende")}");
                if (!buildOk)
                {
                    string one = bout.Replace("\n", " | ");
                    Log($"  [belt-build:{key}] build: {one[..Math.Min(600, one.Length)]}");
                }
                SaveResult(key, ok ? "PASS" : "FAIL", $"build={(buildOk ? "ok" : "rc" + brc)} conteudo={(contentOk ? "ok" : "nao")}");
                if (ok) pass++; else fail++;
            }
            catch (Exception ex)
            {
                Log($"  [belt-build:{key}] FAIL: {ex.Message.Split('\n')[0]}");
                SaveResult(key, "FAIL", ex.Message.Split('\n')[0]);
                fail++;
            }
        }
        TestContext.Check(fail == 0, $"belts-build direct: {fail} falharam");
        Log($"[belts-build:direct] {pass}/{Belts.Length} PASS");
    }
}
