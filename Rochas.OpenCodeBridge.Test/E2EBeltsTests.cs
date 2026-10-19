using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

// Belts (azul→preta) SEM navegador: cadeia completa Orch -> Scripter -> Exec.
// Cada faixa grava resultado parcial em /tmp/opencode/belts/<key>_result.json
// para nao perder progresso em testes longos.
internal static class E2EBeltsTests
{
    static readonly (string Key, string Prompt)[] Belts =
    {
        ("azul", "Crie no backend .NET C# uma consulta de dados em uma tabela (ex.: reembolsos corporativos) usando repositorio generico com ORM. Escopo SOMENTE backend de dados (sem frontend, sem API). Premissas: backend .NET C#. Divida em fases por escopo de backend e divida a tarefa de implementacao usando subagent tool, dialogo em pt-br."),
        ("verde", "Especifique o dominio backend C# de um CRUD de reembolsos corporativos (entidades, value objects, regras de aprovacao por faixa de valor) e o banco de dados SQL: DDL das tabelas, indices, constraints e seeds. Valide CPF e CNPJ. Escopo: dominio + banco SQL. Premissas: backend .NET C#. Divida em fases por escopo e divida a tarefa usando subagent tool, dialogo em pt-br."),
        ("roxa", "Especifique o dominio backend C# de cadastro de clientes e fornecedores, o banco de dados SQL (DDL, indices, constraints) e os repositorios e servicos: repositorio generico Dapper, servicos de aplicacao, validacao e mascaras de e-mail, telefone BR com DDD e CEP. Escopo: dominio + banco SQL + repositorios e servicos. Premissas: backend .NET C#. Divida em fases e divida a tarefa usando subagent tool, dialogo em pt-br."),
        ("marrom", "Especifique o dominio backend C# de um CRUD de reembolsos corporativos, o banco de dados SQL, os repositorios e servicos (Dapper), a API REST e o frontend React. Escopo: dominio + banco SQL + repositorios e servicos + API + frontend React. Premissas: backend .NET C#. Divida em fases por escopo e divida a tarefa usando subagent tool, dialogo em pt-br."),
        ("preta", "Especifique a arquitetura de ecossistema: dominio backend C# do Portal do Colaborador, banco de dados SQL, repositorios e servicos, API REST, servicos bus, pipeline de Big Data com ML (regressao, classificacao, rede neural perceptron). Escopo: dominio + banco SQL + repositorios e servicos + API + servicos bus + redes neurais. Premissas: backend .NET C#. Divida em fases por escopo e divida a tarefa usando subagent tool, dialogo em pt-br."),
    };

    static (int Code, string Out) Run(string exe, string cwd, int secs, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi) ?? throw new Exception("sem processo: " + exe);
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(secs * 1000)) { try { p.Kill(true); } catch { } throw new Exception("timeout " + exe); }
        return (p.ExitCode, so.Result + se.Result);
    }

    static void Log(string msg) { Console.WriteLine(msg); Console.Out.Flush(); }

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
        string orchModel = "gemma-4-12b-orch";
        string scripterModel = "gemma-4-12b-scripter";
        string execModel = "gemma-4-e2b-exec";
        Log($"[belts:direct] Orch={orchModel} Scripter={scripterModel} Exec={execModel}");
        int pass = 0, fail = 0;
        var keysEnv = Environment.GetEnvironmentVariable("BELTS_KEYS");
        var beltsToRun = string.IsNullOrWhiteSpace(keysEnv)
            ? Belts
            : Belts.Where(b => keysEnv.Split(',').Select(x=>x.Trim()).Contains(b.Key)).ToArray();
        foreach (var (key, prompt) in beltsToRun)
        {
            string ws = System.IO.Path.Combine("/tmp/opencode/belts", key);
            System.IO.Directory.CreateDirectory(ws);
            Log($"  [belt:{key}] START ws={ws}"); 
            try
            {
                // 1) Orch (12B) segmenta o enunciado em tarefas atomicas.
                Log($"  [belt:{key}] 1/4 Orch segmentando...");
                var orchHistory = new JsonArray(
                    new JsonObject { ["role"]="system", ["content"]="Voce e o Orch (role orch, 12B). Segmenta o enunciado em tarefas atomicas (max 5) e responde SOMENTE com JSON array: [{\"title\":\"...\",\"prompt\":\"...\"}]. Nao execute nada." },
                    new JsonObject { ["role"]="user", ["content"]=prompt });
                var orchR = await TestContext.PostObj(TestContext.Bridge + "/v1/chat/completions",
                    new JsonObject { ["model"]=orchModel, ["temperature"]=0.6, ["stream"]=false, ["messages"]=orchHistory.DeepClone() });
                string orchContent = orchR["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
                int s = orchContent.IndexOf('['), e = orchContent.LastIndexOf(']');
                TestContext.Check(s >= 0 && e > s, $"[{key}] Orch segmentou em JSON array");
                var tasks = JsonNode.Parse(orchContent[s..(e+1)])?.AsArray() ?? new JsonArray();
                Log($"  [belt:{key}] Orch: {tasks.Count} tarefas");

                // 2) Scripter (12B) compoe script bash + companion .md para cada tarefa.
                Log($"  [belt:{key}] 2/4 Scripter compondo scripts...");
                int scriptsOk = 0;
                foreach (var t in tasks)
                {
                    string title = t?["title"]?.GetValue<string>() ?? "tarefa";
                    string taskPrompt = t?["prompt"]?.GetValue<string>() ?? prompt;
                    string safeTitle = title.Replace(" ", "_").ToLowerInvariant();
                    string scriptPath = System.IO.Path.Combine(ws, safeTitle + ".sh");
                    string metaPath = System.IO.Path.Combine(ws, safeTitle + ".md");

                    var tools = new JsonArray
                    {
                        new JsonObject { ["type"]="function", ["function"]=new JsonObject { ["name"]="write_file", ["description"]="escreve arquivo", ["parameters"]=new JsonObject { ["type"]="object", ["properties"]=new JsonObject { ["path"]=new JsonObject{["type"]="string"}, ["content"]=new JsonObject{["type"]="string"} }, ["required"]=new JsonArray("path","content") } } }
                    };
                    var sh = new JsonArray(
                        new JsonObject { ["role"]="system", ["content"]="Voce e o Scripter (role scripter, 12B). Componha um script bash que executa a tarefa e o salve em " + safeTitle + ".sh com shebang. Tambem gere companion " + safeTitle + ".md descrevendo o que o script faz. Use write_file. Ao terminar responda apenas SCRIPTER-OK." },
                        new JsonObject { ["role"]="user", ["content"]=taskPrompt });
                    for (int turn = 0; turn < 6; turn++)
                    {
                        var r = await TestContext.PostObj(TestContext.Bridge + "/v1/chat/completions",
                            new JsonObject { ["model"]=scripterModel, ["temperature"]=0.3, ["stream"]=false, ["messages"]=sh.DeepClone(), ["tools"]=tools.DeepClone() });
                        var msg = r["choices"]?[0]?["message"]?.AsObject();
                        var calls = msg?["tool_calls"]?.AsArray();
                        if (calls is null || calls.Count == 0) break;
                        var echo = new JsonArray();
                        foreach (var c in calls) echo.Add(new JsonObject { ["id"]=c?["id"]?.GetValue<string>()??"", ["type"]="function", ["function"]=new JsonObject { ["name"]=c?["function"]?["name"]?.GetValue<string>()??"", ["arguments"]=c?["function"]?["arguments"]?.GetValue<string>()??"{}" } });
                        sh.Add(new JsonObject { ["role"]="assistant", ["content"]=msg?["content"]?.GetValue<string>()??"", ["tool_calls"]=echo });
                        foreach (var c in calls)
                        {
                            var a = JsonNode.Parse(c?["function"]?["arguments"]?.GetValue<string>() ?? "{}")?.AsObject() ?? new JsonObject();
                            string path = a["path"]?.GetValue<string>() ?? "";
                            string content = a["content"]?.GetValue<string>() ?? "";
                            string full = System.IO.Path.Combine(ws, path);
                            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
                            System.IO.File.WriteAllText(full, content);
                            sh.Add(new JsonObject { ["role"]="tool", ["tool_call_id"]=c?["id"]?.GetValue<string>()??"", ["content"]=$"escrito {path}" });
                        }
                    }
                    if (System.IO.File.Exists(scriptPath))
                    {
                        string script = System.IO.File.ReadAllText(scriptPath);
                        var (code, outp) = Run("bash", ws, 60, "-n", System.IO.Path.GetFileName(scriptPath));
                        if (code == 0) { scriptsOk++; Log($"  [belt:{key}] script OK: {System.IO.Path.GetFileName(scriptPath)} ({script.Length} bytes)"); }
                        else Log($"  [belt:{key}] script SYNTAX FAIL: {System.IO.Path.GetFileName(scriptPath)}: {outp}");
                    }
                }

                // 3) Exec (E2B) le companion .md + script e executa (bash script.sh).
                Log($"  [belt:{key}] 3/4 Exec lendo e executando scripts...");
                int execOk = 0;
                foreach (var f in System.IO.Directory.GetFiles(ws, "*.sh"))
                {
                    string name = System.IO.Path.GetFileName(f);
                    string meta = System.IO.Path.ChangeExtension(f, ".md");
                    if (System.IO.File.Exists(meta))
                    {
                        string md = System.IO.File.ReadAllText(meta);
                        string sh = System.IO.File.ReadAllText(f);
                        var execHistory = new JsonArray(
                            new JsonObject { ["role"]="system", ["content"]="Voce e o Exec (role exec, E2B). Leia o companion .md e o script. Responda apenas EXEC-READ-OK." },
                            new JsonObject { ["role"]="user", ["content"]="COMPANION:\n" + md + "\n\nSCRIPT:\n" + sh });
                        var execR = await TestContext.PostObj("http://127.0.0.1:4125/v1/chat/completions",
                            new JsonObject { ["model"]=execModel, ["temperature"]=0.3, ["stream"]=false, ["messages"]=execHistory.DeepClone() });
                        string execContent = execR["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
                        Log($"  [belt:{key}] exec read: {name} -> {execContent[..Math.Min(40, execContent.Length)]}");
                    }
                    var (ecode, eout) = Run("bash", ws, 120, name);
                    Log($"  [belt:{key}] exec {name}: exit={ecode} {eout.Trim().Split('\n').LastOrDefault()}");
                    if (ecode == 0) execOk++;
                }

                // 4) Valida artefatos resultantes.
                Log($"  [belt:{key}] 4/4 validando artefatos...");
                TestContext.Check(scriptsOk > 0, $"[{key}] Scripter compôs {scriptsOk} scripts válidos");
                TestContext.Check(execOk > 0, $"[{key}] Exec executou {execOk} scripts com sucesso");
                Log($"  [belt:{key}] PASS: {scriptsOk} scripts, {execOk} executados");
                SaveResult(key, "PASS", $"{scriptsOk} scripts, {execOk} executados");
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
        Log($"[belts:direct] {pass}/{Belts.Length} PASS");
    }
}