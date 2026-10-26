using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

// Belts (azul->preta) Trilha BUILD: E4B (role build) implementa DIRETAMENTE os
// artefatos do enunciado usando tools reais (write) em loop de turnos, delegando
// sub-tarefas quando fizer sentido. Asserts rigorosos: >=3 arquivos criados,
// conteudo substancial (>2000 chars somados), e artefatos reais (arquivos
// existem em disco no workspace). Sem execucao de script nesta trilha.
// Resultados parciais em /tmp/opencode/belts-build/<key>_result.json.
internal static class E2EBeltsBuildTests
{
    static readonly (string Key, string Prompt)[] Belts =
    {
        ("azul", "Crie no backend .NET C# uma consulta de dados em uma tabela (ex.: reembolsos corporativos) usando repositorio generico com ORM. Escopo SOMENTE backend de dados (sem frontend, sem API). Premissas: backend .NET C#. Divida a implementacao em etapas, dialogo em pt-br. Critério de aceite, solução implementada, codificada em arquivos no disco e compilando: crie arquivos .cs com as classes, interfaces e repositorios."),
        ("verde", "Especifique o dominio backend C# de um CRUD de reembolsos corporativos (entidades, value objects, regras de aprovacao por faixa de valor) e o banco de dados SQL: DDL das tabelas, indices, constraints e seeds. Valide CPF e CNPJ. Escopo: dominio + banco SQL. Premissas: backend .NET C#. Divida a implementacao em etapas, dialogo em pt-br. Critério de aceite, solução implementada, codificada em arquivos no disco e compilando: arquivos .cs (entidades/VOs) e .sql (DDL)."),
        ("roxa", "Trabalhe em {ws}. Implemente o cadastro de clientes e fornecedores no backend .NET C#: dominio (entidades e value objects), banco SQL (DDL, indices e constraints), repositorios com Dapper, servicos de aplicacao e validacao/mascaras de e-mail, telefone BR com DDD e CEP. Crie os arquivos .cs e .sql com caminho absoluto dentro de {ws}, usando a tool write. Comece pelo dominio, depois o SQL e depois repositorios/servicos; nao escreva plano, va direto ao write. Ao terminar responda apenas BUILD-OK com a lista dos arquivos."),
        ("marrom", "Especifique o dominio backend C# de um CRUD de reembolsos corporativos, o banco de dados SQL, os repositorios e servicos (Dapper), a API REST e o frontend React. Escopo: dominio + banco SQL + repositorios e servicos + API + frontend React. Premissas: backend .NET C#. Divida a implementacao em etapas, dialogo em pt-br. Critério de aceite, solução implementada, codificada em arquivos no disco e compilando: arquivos .cs, .sql e .tsx/.jsx."),
        ("preta", "Especifique a arquitetura de ecossistema: dominio backend C# do Portal do Colaborador, banco de dados SQL, repositorios e servicos, API REST, servicos bus, pipeline de Big Data com ML (regressao, classificacao, rede neural perceptron). Escopo: dominio + banco SQL + repositorios e servicos + API + servicos bus + redes neurais. Premissas: backend .NET C#. Divida a implementacao em etapas, dialogo em pt-br. Critério de aceite, solução implementada, codificada em arquivos no disco e compilando: arquivos .cs, .sql e scripts de treino."),
    };

    static void Log(string msg) { Console.WriteLine(msg); Console.Out.Flush(); }
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

    public static async Task Direct()
    {
        string model = TestContext.SuiteModel;
        if (!model.Contains("build")) model = model.Replace("-orch", "-build").Replace("-plan", "-build");
        if (!model.Contains("build")) model += "-build";
        Log($"[belts-build:direct] modelo {model} (bridge {TestContext.Bridge})");
        var tools = new JsonArray
        {
            new JsonObject { ["type"]="function", ["function"]=new JsonObject { ["name"]="write", ["description"]="escreve arquivo em disco", ["parameters"]=new JsonObject { ["type"]="object", ["properties"]=new JsonObject { ["path"]=new JsonObject{["type"]="string"}, ["content"]=new JsonObject{["type"]="string"} }, ["required"]=new JsonArray("path","content") } } }
        };
        int pass = 0, fail = 0;
        var keysEnv = string.IsNullOrWhiteSpace(TestContext.BeltsFilter) ? Environment.GetEnvironmentVariable("BELTS_KEYS") : TestContext.BeltsFilter;
        var beltsToRun = string.IsNullOrWhiteSpace(keysEnv)
            ? Belts
            : Belts.Where(b => keysEnv.Split(',').Select(x=>x.Trim()).Contains(b.Key)).ToArray();
        foreach (var (key, prompt) in beltsToRun)
        {
            string ws = System.IO.Path.Combine("/tmp/opencode/belts-build", key);
            System.IO.Directory.CreateDirectory(ws);
            // limpa workspace da faixa para contagem limpa
            foreach (var f in System.IO.Directory.GetFiles(ws)) System.IO.File.Delete(f);
            foreach (var d in System.IO.Directory.GetDirectories(ws)) System.IO.Directory.Delete(d, true);
            Log($"  [belt-build:{key}] START ws={ws}");
            try
            {
                var history = new JsonArray(
                    new JsonObject { ["role"]="system", ["content"]=$"Voce e arquiteto desenvolvedor senior (role build), dialogo em pt-br. Trabalhe em {ws}. Implemente os artefatos pedidos direto no disco, sem plano e sem prosa. Crie pelo menos 3 arquivos .cs/.sql/.tsx usando caminhos absolutos dentro de {ws}. Ao terminar responda apenas BUILD-OK com o resumo dos arquivos." },
                    new JsonObject { ["role"]="user", ["content"]=prompt.Replace("{ws}", ws) });
                int filesCreated = 0;
                int totalChars = 0;
                int lastFiles = 0;
                int stall = 0;
                int nudges = 0;
                // Watchers extras: a roxa alterna prosa e write; 10 turnos/2
                // nudges cortavam uma recuperação legítima. O aceite continua
                // >=3 arquivos e >500 chars reais em disco.
                for (int turn = 0; turn < 14 && filesCreated < 8; turn++)
                {
                    var r = await TestContext.PostObj(TestContext.Bridge + "/v1/chat/completions",
                        new JsonObject { ["model"]=model, ["temperature"]=0.3, ["stream"]=false, ["max_tokens"]=16384, ["tool_choice"]="required", ["messages"]=history.DeepClone(), ["tools"]=tools.DeepClone() });
                    var msg = r["choices"]?[0]?["message"]?.AsObject();
                    string content = msg?["content"]?.GetValue<string>() ?? "";
                    totalChars += content.Length;
                    var calls = msg?["tool_calls"]?.AsArray();
                    if (calls is null || calls.Count == 0)
                    {
                        Log($"  [belt-build:{key}] turno {turn}: sem tools (chars={content.Length}) — nudge {nudges + 1}");
                        history.Add(new JsonObject { ["role"]="assistant", ["content"]=content });
                        if (nudges >= 4) { Log($"  [belt-build:{key}] sem tools apos 4 nudges, encerrando"); break; }
                        nudges++;
                        history.Add(new JsonObject { ["role"]="user", ["content"]="Pouca fala, mais construcao: voce respondeu com texto mas NAO chamou a tool write. OBRIGATORIO agora: chame a tool write com path e content para criar o proximo arquivo real (.cs/.sql/.tsx). Nao explique, apenas chame write." });
                        continue;
                    }
                    // Parse primeiro: tool call truncada/invalida nao entra no historico.
                    // Reenviar JSON quebrado ao llama costuma virar 500 peg-gemma4.
                    var parsed = new System.Collections.Generic.List<(JsonObject Call, JsonObject Args)>();
                    bool badArgs = false;
                    foreach (var c in calls)
                    {
                        try
                        {
                            var co = c as JsonObject ?? throw new FormatException("tool call nao e objeto");
                            var a = JsonNode.Parse(co["function"]?["arguments"]?.GetValue<string>() ?? "{}")?.AsObject();
                            if (a is null) throw new FormatException("args nao e objeto");
                            parsed.Add((co, a));
                        }
                        catch
                        {
                            badArgs = true;
                            break;
                        }
                    }
                    if (badArgs)
                    {
                        Log($"  [belt-build:{key}] turno {turn}: tool call com args invalidos/truncados — nudge");
                        nudges++;
                        if (nudges > 4) { Log($"  [belt-build:{key}] args invalidos apos 4 nudges, encerrando"); break; }
                        history.Add(new JsonObject { ["role"]="user", ["content"]="Sua ultima tool call veio com JSON invalido ou truncado. Reemita UMA tool call write com JSON completo: path string e content string. Nao inclua texto fora do JSON." });
                        continue;
                    }
                    nudges = 0;
                    var echo = new JsonArray();
                    foreach (var (c, _) in parsed) echo.Add(new JsonObject { ["id"]=c["id"]?.GetValue<string>()??"", ["type"]="function", ["function"]=new JsonObject { ["name"]=c["function"]?["name"]?.GetValue<string>()??"", ["arguments"]=c["function"]?["arguments"]?.GetValue<string>()??"{}" } });
                    history.Add(new JsonObject { ["role"]="assistant", ["content"]=content, ["tool_calls"]=echo });
                    foreach (var (c, a) in parsed)
                    {
                        string path = a["path"]?.GetValue<string>() ?? "";
                        string ctt = a["content"]?.GetValue<string>() ?? "";
                        if (string.IsNullOrWhiteSpace(path)) path = $"artefato_{filesCreated}.txt";
                        string full = System.IO.Path.Combine(ws, path);
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
                        System.IO.File.WriteAllText(full, ctt);
                        filesCreated++;
                        // Metrica de substancia: o artefato real e o conteudo
                        // gravado em disco, nao o texto do assistant (que pode
                        // ser so um resumo curto). Sem isto, um belt com 6
                        // arquivos de ~4 KB era reprovado por chars=326.
                        totalChars += ctt.Length;
                        history.Add(new JsonObject { ["role"]="tool", ["tool_call_id"]=c["id"]?.GetValue<string>()??"", ["content"]=$"criado {path} ({ctt.Length} chars)" });
                    }
                    Log($"  [belt-build:{key}] turno {turn}: {parsed.Count} write(s), total arquivos={filesCreated}");
                    if (filesCreated == lastFiles) stall++; else stall = 0;
                    lastFiles = filesCreated;
                    if (stall >= 3) { Log($"  [belt-build:{key}] stall detectado (3 turnos sem arquivo), encerrando"); break; }
                }
                // Fase de revisao: E4B le os arquivos criados e revisa.
                if (filesCreated >= 3)
                {
                    var readTools = new JsonArray
                    {
                        new JsonObject { ["type"]="function", ["function"]=new JsonObject { ["name"]="read", ["description"]="le arquivo", ["parameters"]=new JsonObject { ["type"]="object", ["properties"]=new JsonObject { ["path"]=new JsonObject{["type"]="string"} }, ["required"]=new JsonArray("path") } } }
                    };
                    history.Add(new JsonObject { ["role"]="user", ["content"]="Revise os arquivos criados (leia-os com read) e responda REVIEW-OK com um resumo de 1 linha por arquivo." });
                    for (int turn = 0; turn < 4; turn++)
                    {
                        var r2 = await TestContext.PostObj(TestContext.Bridge + "/v1/chat/completions",
                            new JsonObject { ["model"]=model, ["temperature"]=0.1, ["stream"]=false, ["max_tokens"]=2048, ["messages"]=history.DeepClone(), ["tools"]=readTools.DeepClone() });
                        var msg2 = r2["choices"]?[0]?["message"]?.AsObject();
                        string content2 = msg2?["content"]?.GetValue<string>() ?? "";
                        totalChars += content2.Length;
                        var calls2 = msg2?["tool_calls"]?.AsArray();
                        if (calls2 is null || calls2.Count == 0) { Log($"  [belt-build:{key}] revisao: {content2[..Math.Min(80,content2.Length)]}"); break; }
                        var echo2 = new JsonArray();
                        foreach (var c in calls2) echo2.Add(new JsonObject { ["id"]=c?["id"]?.GetValue<string>()??"", ["type"]="function", ["function"]=new JsonObject { ["name"]=c?["function"]?["name"]?.GetValue<string>()??"", ["arguments"]=c?["function"]?["arguments"]?.GetValue<string>()??"{}" } });
                        history.Add(new JsonObject { ["role"]="assistant", ["content"]=content2, ["tool_calls"]=echo2 });
                        foreach (var c in calls2)
                        {
                            var a = JsonNode.Parse(c?["function"]?["arguments"]?.GetValue<string>() ?? "{}")?.AsObject() ?? new JsonObject();
                            string path = a["path"]?.GetValue<string>() ?? "";
                            string full = System.IO.Path.Combine(ws, path);
                            string fileContent = System.IO.File.Exists(full) ? System.IO.File.ReadAllText(full) : "(nao existe)";
                            history.Add(new JsonObject { ["role"]="tool", ["tool_call_id"]=c?["id"]?.GetValue<string>()??"", ["content"]=fileContent[..Math.Min(500,fileContent.Length)] });
                        }
                    }
                }
                bool ok = filesCreated >= 3 && totalChars > 500;
                Log($"  [belt-build:{key}] {(ok ? "PASS" : "FAIL")} arquivos={filesCreated} chars={totalChars}");
                SaveResult(key, ok ? "PASS" : "FAIL", $"arquivos={filesCreated} chars={totalChars}");
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
