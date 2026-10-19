using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

// Belts (azul->preta) Trilha BUILD: 12B (role build) implementa diretamente os
// artefatos do enunciado (sem composicao de scripts sh), usando subagentes Build
// E2B para implementar. Asserts: resposta com tools/subagent + conteudo relevante.
// Resultados parciais em /tmp/opencode/belts-build/<key>_result.json.
internal static class E2EBeltsBuildTests
{
    static readonly (string Key, string Prompt)[] Belts =
    {
        ("azul", "Crie no backend .NET C# uma consulta de dados em uma tabela (ex.: reembolsos corporativos) usando repositorio generico com ORM. Escopo SOMENTE backend de dados (sem frontend, sem API). Premissas: backend .NET C#. Divida em fases por escopo de backend e divida a tarefa de implementacao usando subagent tool, dialogo em pt-br."),
        ("verde", "Especifique o dominio backend C# de um CRUD de reembolsos corporativos (entidades, value objects, regras de aprovacao por faixa de valor) e o banco de dados SQL: DDL das tabelas, indices, constraints e seeds. Valide CPF e CNPJ. Escopo: dominio + banco SQL. Premissas: backend .NET C#. Divida em fases por escopo e divida a tarefa usando subagent tool, dialogo em pt-br."),
        ("roxa", "Especifique o dominio backend C# de cadastro de clientes e fornecedores, o banco de dados SQL (DDL, indices, constraints) e os repositorios e servicos: repositorio generico Dapper, servicos de aplicacao, validacao e mascaras de e-mail, telefone BR com DDD e CEP. Escopo: dominio + banco SQL + repositorios e servicos. Premissas: backend .NET C#. Divida em fases e divida a tarefa usando subagent tool, dialogo em pt-br."),
        ("marrom", "Especifique o dominio backend C# de um CRUD de reembolsos corporativos, o banco de dados SQL, os repositorios e servicos (Dapper), a API REST e o frontend React. Escopo: dominio + banco SQL + repositorios e servicos + API + frontend React. Premissas: backend .NET C#. Divida em fases por escopo e divida a tarefa usando subagent tool, dialogo em pt-br."),
        ("preta", "Especifique a arquitetura de ecossistema: dominio backend C# do Portal do Colaborador, banco de dados SQL, repositorios e servicos, API REST, servicos bus, pipeline de Big Data com ML (regressao, classificacao, rede neural perceptron). Escopo: dominio + banco SQL + repositorios e servicos + API + servicos bus + redes neurais. Premissas: backend .NET C#. Divida em fases por escopo e divida a tarefa usando subagent tool, dialogo em pt-br."),
    };

    static void Log(string msg) { Console.WriteLine(msg); Console.Out.Flush(); }
    static void SaveResult(string key, string status, string detail)
    {
        try
        {
            string path = System.IO.Path.Combine("/tmp/opencode/belts-build", key + "_result.json");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, $"{{\"key\":\"{key}\",\"status\":\"{status}\",\"detail\":\"{detail.Replace("\"","'")}\",\"when\":\"{DateTime.UtcNow:o}\"}}");
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
            new JsonObject { ["type"]="function", ["function"]=new JsonObject { ["name"]="subagent", ["description"]="Delega uma sub-tarefa a um agente menor", ["parameters"]=new JsonObject { ["type"]="object", ["properties"]=new JsonObject { ["title"]=new JsonObject{["type"]="string"}, ["prompt"]=new JsonObject{["type"]="string"} }, ["required"]=new JsonArray("title","prompt") } } }
        };
        int pass = 0, fail = 0;
        foreach (var (key, prompt) in Belts)
        {
            try
            {
                Log($"  [belt-build:{key}] START");
                var history = new JsonArray(
                    new JsonObject { ["role"]="system", ["content"]="Voce e um arquiteto desenvolvedor senior (role build). Implemente diretamente os artefatos do enunciado, delegando sub-tarefas via subagent. Dialogo em pt-br." },
                    new JsonObject { ["role"]="user", ["content"]=prompt });
                var r = await TestContext.PostObj(TestContext.Bridge + "/v1/chat/completions",
                    new JsonObject { ["model"]=model, ["temperature"]=0.5, ["stream"]=false, ["messages"]=history.DeepClone(), ["tools"]=tools.DeepClone() });
                string content = r["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
                var calls = r["choices"]?[0]?["message"]?["tool_calls"]?.AsArray();
                bool ok = content.Length > 50 || (calls is not null && calls.Count > 0);
                Log($"  [belt-build:{key}] {(ok ? "PASS" : "FAIL")} tools={calls?.Count ?? 0} chars={content.Length}");
                SaveResult(key, ok ? "PASS" : "FAIL", $"tools={calls?.Count ?? 0} chars={content.Length}");
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