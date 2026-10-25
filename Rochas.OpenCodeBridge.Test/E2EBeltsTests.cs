using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

// Belts (azul→preta) SEM navegador: cadeia completa Orch -> Scripter -> Exec.
// Cada faixa grava resultado parcial em /tmp/opencode/belts/<key>_result.json
// para nao perder progresso em testes longos.
internal static class E2EBeltsTests
{
    static readonly (string Key, string Prompt)[] Belts =
    {
        ("azul", "Crie no backend .NET C# uma consulta de dados em uma tabela (ex.: reembolsos corporativos) usando repositorio generico com ORM. Escopo SOMENTE backend de dados (sem frontend, sem API). Premissas: backend .NET C#. Divida a implementacao em etapas, dialogo em pt-br."),
        ("verde", "Especifique o dominio backend C# de um CRUD de reembolsos corporativos (entidades, value objects, regras de aprovacao por faixa de valor) e o banco de dados SQL: DDL das tabelas, indices, constraints e seeds. Valide CPF e CNPJ. Escopo: dominio + banco SQL. Premissas: backend .NET C#. Divida a implementacao em etapas, dialogo em pt-br."),
        ("roxa", "Trabalhe no workspace da tarefa. Implemente o cadastro de clientes e fornecedores no backend .NET C#: dominio (entidades e value objects), banco SQL (DDL, indices e constraints), repositorios com Dapper, servicos de aplicacao e validacao/mascaras de e-mail, telefone BR com DDD e CEP. Entregue a implementacao em arquivos .cs e .sql no workspace da tarefa."),
        ("marrom", "Especifique o dominio backend C# de um CRUD de reembolsos corporativos, o banco de dados SQL, os repositorios e servicos (Dapper), a API REST e o frontend React. Escopo: dominio + banco SQL + repositorios e servicos + API + frontend React. Premissas: backend .NET C#. Divida a implementacao em etapas, dialogo em pt-br."),
        ("preta", "Especifique a arquitetura de ecossistema: dominio backend C# do Portal do Colaborador, banco de dados SQL, repositorios e servicos, API REST, servicos bus, pipeline de Big Data com ML (regressao, classificacao, rede neural perceptron). Escopo: dominio + banco SQL + repositorios e servicos + API + servicos bus + redes neurais. Premissas: backend .NET C#. Divida a implementacao em etapas, dialogo em pt-br."),
    };

    /// <summary>Compara nomes sem acento/caixa: o modelo nao repete o titulo ao pe do byte.</summary>
    static string NormName(string s)
    {
        var d = s.Normalize(System.Text.NormalizationForm.FormD);
        var b = new System.Text.StringBuilder();
        foreach (char c in d)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark
                && char.IsLetterOrDigit(c))
                b.Append(char.ToLowerInvariant(c));
        return b.ToString();
    }

    static string? FindByTitle(string ws, string safeTitle)
    {
        string want = NormName(safeTitle);
        foreach (var f in System.IO.Directory.GetFiles(ws, "*.sh"))
        {
            string name = NormName(System.IO.Path.GetFileNameWithoutExtension(f));
            if (name == want || name.Contains(want) || want.Contains(name)) return f;
        }
        return null;
    }

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
        string orchModel = "gemma-4-12b-orch";
        string scripterModel = "gemma-4-12b-scripter";
        string execModel = "gemma-4-e2b-exec";
        Log($"[belts:direct] Orch={orchModel} Scripter={scripterModel} Exec={execModel}");
        int pass = 0, fail = 0;
        var keysEnv = string.IsNullOrWhiteSpace(TestContext.BeltsFilter) ? Environment.GetEnvironmentVariable("BELTS_KEYS") : TestContext.BeltsFilter;
        var beltsToRun = string.IsNullOrWhiteSpace(keysEnv)
            ? Belts
            : Belts.Where(b => keysEnv.Split(',').Select(x=>x.Trim()).Contains(b.Key)).ToArray();
        foreach (var (key, prompt) in beltsToRun)
        {
            string ws = System.IO.Path.Combine("/tmp/opencode/belts", key);
            // Workspace limpo: sobras de uma belt anterior inflariam scriptsOk/execOk.
            if (System.IO.Directory.Exists(ws)) System.IO.Directory.Delete(ws, true);
            System.IO.Directory.CreateDirectory(ws);
            Log($"  [belt:{key}] START ws={ws}"); 
            try
            {
                // 1) Orch (E4B) segmenta o enunciado em tarefas atomicas.
                Log($"  [belt:{key}] 1/4 Orch segmentando...");
                var orchHistory = new JsonArray(
                    new JsonObject { ["role"]="system", ["content"]="Voce e o Orch (role orch, E4B). Segmenta o enunciado em tarefas atomicas (max 5) e responde SOMENTE com JSON array: [{\"title\":\"...\",\"prompt\":\"...\"}]. Nao execute nada." },
                    new JsonObject { ["role"]="user", ["content"]=prompt.Replace("{ws}", ws) });
                var orchR = await TestContext.PostObj(TestContext.Bridge + "/v1/chat/completions",
                    new JsonObject { ["model"]=orchModel, ["temperature"]=0.3, ["stream"]=false, ["messages"]=orchHistory.DeepClone() });
                string orchContent = Str(orchR["choices"]?[0]?["message"]?["content"]);
                int s = orchContent.IndexOf('['), e = orchContent.LastIndexOf(']');
                TestContext.Check(s >= 0 && e > s, $"[{key}] Orch segmentou em JSON array");
                var tasks = JsonNode.Parse(orchContent[s..(e+1)])?.AsArray() ?? new JsonArray();
                Log($"  [belt:{key}] Orch: {tasks.Count} tarefas");

                // Grounding: sem isso o Scripter escreve as cegas e inventa caminhos
                // (ex.: ./data/reembolsos.db, DataAccessLayer/*.csproj). O inventario
                // real entra no system prompt e vira contrato de path.
                var ground = new System.Text.StringBuilder();
                ground.AppendLine("FATOS DO AMBIENTE (nao invente caminhos fora desta lista):");
                ground.AppendLine("- workspace=" + ws + " e o cwd de execucao; ele comeca VAZIO.");
                ground.AppendLine("- /usr/src/opencodebridge existe (repo do produto), mas NAO e o workspace da tarefa.");
                foreach (var exe in new[] { "dotnet", "sqlite3", "python3", "psql", "sqlplus" })
                {
                    var (c, _) = Run("bash", ws, 10, "-lc", "command -v " + exe + " >/dev/null 2>&1");
                    ground.AppendLine($"- {(c == 0 ? "tem" : "NAO tem")} {exe} no PATH");
                }
                ground.AppendLine("- NAO existe banco previo, .csproj previo nem servidor SQL ativo.");
                ground.AppendLine("- Todo path citado deve ser relativo ao cwd ou criado pelo script (mkdir -p).");
                ground.AppendLine("- O script e bash puro: C#/SQL so via heredoc ou arquivo separado, NUNCA solto no .sh.");
                string grounded = ground.ToString();

                // 2) Scripter (E4B) compoe script bash + companion .md para cada tarefa.
                Log($"  [belt:{key}] 2/4 Scripter compondo scripts...");
                int scriptsOk = 0;
                foreach (var t in tasks)
                {
                    string title = Str(t?["title"], "tarefa");
                    string taskPrompt = Str(t?["prompt"], prompt).Replace("{ws}", ws);
                    string safeTitle = title.Replace(" ", "_").ToLowerInvariant();
                    string scriptPath = System.IO.Path.Combine(ws, safeTitle + ".sh");
                    string metaPath = System.IO.Path.Combine(ws, safeTitle + ".md");
                    var beforeFiles = new HashSet<string>(System.IO.Directory.GetFiles(ws, "*.sh"));
                    var writtenScripts = new List<string>();

                    var tools = new JsonArray
                    {
                        new JsonObject { ["type"]="function", ["function"]=new JsonObject { ["name"]="write_file", ["description"]="escreve arquivo", ["parameters"]=new JsonObject { ["type"]="object", ["properties"]=new JsonObject { ["path"]=new JsonObject{["type"]="string"}, ["content"]=new JsonObject{["type"]="string"} }, ["required"]=new JsonArray("path","content") } } }
                    };
                    var sh = new JsonArray(
                        new JsonObject { ["role"]="system", ["content"]="Voce e o Scripter (role scripter, E4B). Componha um script bash que executa a tarefa e o salve em " + safeTitle + ".sh com shebang. Tambem gere companion " + safeTitle + ".md descrevendo o que o script faz. Mantenha o script com no maximo 80 linhas e o companion com no maximo 20 linhas (saida curta cabe no teto de tokens). Use write_file. Ao terminar responda apenas SCRIPTER-OK.\n\n" + grounded + "REGRA: bash puro; nunca escreva C#/SQL dentro do .sh (use heredoc ou arquivo separado)." },
                        new JsonObject { ["role"]="user", ["content"]=taskPrompt });
                    for (int turn = 0; turn < 6; turn++)
                    {
                        JsonObject r;
                        try
                        {
                            r = await TestContext.PostObj(TestContext.Bridge + "/v1/chat/completions",
                                new JsonObject { ["model"]=scripterModel, ["temperature"]=0.1, ["stream"]=false, ["messages"]=sh.DeepClone(), ["tools"]=tools.DeepClone() });
                        }
                        catch (Exception ex)
                        {
                            // 500 do llama no parse da tool call (JSON invalido/truncado)
                            // nao pode derrubar a belt inteira: devolve o erro e tenta de novo.
                            Log($"  [belt:{key}] post scripter falhou: {ex.Message.Split('\n')[0]}");
                            sh.Add(new JsonObject { ["role"]="user", ["content"]="O turno anterior falhou no parser de tool call (JSON invalido/truncado). Reenvie com UM write_file de JSON valido; script com no maximo 60 linhas, sem C#/SQL solto e sem quebrar strings." });
                            continue;
                        }
                        var msg = r["choices"]?[0]?["message"]?.AsObject();
                        var calls = msg?["tool_calls"]?.AsArray();
                        if (calls is null || calls.Count == 0) break;
                        var echo = new JsonArray();
                        foreach (var c in calls) echo.Add(new JsonObject { ["id"]=Str(c?["id"]), ["type"]="function", ["function"]=new JsonObject { ["name"]=Str(c?["function"]?["name"]), ["arguments"]=Str(c?["function"]?["arguments"], "{}") } });
                        sh.Add(new JsonObject { ["role"]="assistant", ["content"]=Str(msg?["content"]), ["tool_calls"]=echo });
                        foreach (var c in calls)
                        {
                            string callId = Str(c?["id"]);
                            string argsJson = Str(c?["function"]?["arguments"], "{}");
                            JsonObject a;
                            try { a = JsonNode.Parse(argsJson)?.AsObject() ?? new JsonObject(); }
                            catch
                            {
                                // Args truncados/invalidos (max_tokens corta o JSON no meio)
                                // nao podem derrubar a belt: devolve erro ao modelo e segue.
                                Log($"  [belt:{key}] args invalidos ({argsJson.Length} chars): {argsJson[..Math.Min(60, argsJson.Length)]}");
                                sh.Add(new JsonObject { ["role"]="tool", ["tool_call_id"]=callId, ["content"]="ERRO: arguments invalidos/truncados" });
                                continue;
                            }
                            string path = Str(a["path"]);
                            string content = Str(a["content"]);
                            if (path.Length == 0 || path.Any(char.IsControl) || path.Contains("..") || System.IO.Path.IsPathRooted(path))
                            {
                                string why = path.Length == 0 ? "vazio"
                                    : path.Any(char.IsControl) ? "control-char"
                                    : path.Contains("..") ? ".."
                                    : "absoluto";
                                string shown = path.Replace("\n", "\\n").Replace("\r", "");
                                Log($"  [belt:{key}] path recusado ({why}): \"{shown[..Math.Min(50, shown.Length)]}\"");
                                sh.Add(new JsonObject { ["role"]="tool", ["tool_call_id"]=callId, ["content"]="ERRO: path invalido (" + why + ")" });
                                continue;
                            }
                            string full = System.IO.Path.Combine(ws, path);
                            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
                            System.IO.File.WriteAllText(full, content);
                            if (path.EndsWith(".sh", StringComparison.OrdinalIgnoreCase)) writtenScripts.Add(full);
                            sh.Add(new JsonObject { ["role"]="tool", ["tool_call_id"]=callId, ["content"]=$"escrito {path}" });
                        }
                    }
                    // O modelo escolhe o nome do arquivo (com/sem acento, as vezes com
                    // espacos/barra invertida). Resolve por titulo normalizado e, por
                    // ultimo, pelo .sh mais recente criado nesta tarefa.
                    string? ResolveScript()
                    {
                        // O path do arg e a fonte da verdade: foi o que o Scripter mandou escrever.
                        if (writtenScripts.Count > 0) return writtenScripts[^1];
                        if (System.IO.File.Exists(scriptPath)) return scriptPath;
                        var byTitle = FindByTitle(ws, safeTitle);
                        if (byTitle is not null) return byTitle;
                        return System.IO.Directory.GetFiles(ws, "*.sh")
                            .Where(f => !beforeFiles.Contains(f))
                            .OrderByDescending(System.IO.File.GetLastWriteTimeUtc)
                            .FirstOrDefault();
                    }
                    string? produced = ResolveScript();
                    if (produced is not null && !System.IO.File.Exists(scriptPath))
                        Log($"  [belt:{key}] script resolvido: {System.IO.Path.GetFileName(produced)}");
                    for (int gate = 0; produced is not null && gate < 3; gate++)
                    {
                        string script = System.IO.File.ReadAllText(produced);
                        string? err = null;
                        if (script.Contains("write_file{") || script.Contains("\"path\":") || script.Contains("```"))
                            err = "conteudo do .sh tem chamada de tool colada (write_file{...}) ou bloco markdown";
                        else
                        {
                            var (code, outp) = Run("bash", ws, 60, "-n", System.IO.Path.GetFileName(produced));
                            if (code != 0) err = outp.Trim();
                        }
                        if (err is null)
                        {
                            scriptsOk++;
                            Log($"  [belt:{key}] script OK: {System.IO.Path.GetFileName(produced)} ({script.Length} bytes)");
                            break;
                        }
                        Log($"  [belt:{key}] script SYNTAX FAIL: {System.IO.Path.GetFileName(produced)}: {err.Split('\n')[0]}");
                        if (gate == 2) break;

                        // Gate corretivo: erro de sintaxe volta ao Scripter (2 retries)
                        // em vez de executar um script que ja sabemos invalido.
                        Log($"  [belt:{key}] gate: devolvendo erro ao Scripter (retry {gate + 1}/2)");
                        sh.Add(new JsonObject { ["role"] = "assistant", ["content"] = "SCRIPTER-REWRITE" });
                        sh.Add(new JsonObject { ["role"] = "user", ["content"] = "O script falhou em `bash -n`:\n" + err + "\nReescreva o arquivo " + System.IO.Path.GetFileName(produced) + " via write_file corrigindo a sintaxe. Bash puro; nunca C#/SQL solto. Responda SCRIPTER-OK." });
                        for (int turn = 0; turn < 4; turn++)
                        {
                            JsonObject r;
                            try
                            {
                                r = await TestContext.PostObj(TestContext.Bridge + "/v1/chat/completions",
                                    new JsonObject { ["model"]=scripterModel, ["temperature"]=0.1, ["stream"]=false, ["messages"]=sh.DeepClone(), ["tools"]=tools.DeepClone() });
                            }
                            catch (Exception ex)
                            {
                                Log($"  [belt:{key}] post scripter (gate) falhou: {ex.Message.Split('\n')[0]}");
                                sh.Add(new JsonObject { ["role"]="user", ["content"]="O turno anterior falhou no parser de tool call (JSON invalido/truncado). Reenvie com UM write_file de JSON valido; script com no maximo 60 linhas, sem C#/SQL solto e sem quebrar strings." });
                                continue;
                            }
                            var msg = r["choices"]?[0]?["message"]?.AsObject();
                            var calls = msg?["tool_calls"]?.AsArray();
                            if (calls is null || calls.Count == 0) break;
                            var echo = new JsonArray();
                            foreach (var c in calls) echo.Add(new JsonObject { ["id"]=Str(c?["id"]), ["type"]="function", ["function"]=new JsonObject { ["name"]=Str(c?["function"]?["name"]), ["arguments"]=Str(c?["function"]?["arguments"], "{}") } });
                            sh.Add(new JsonObject { ["role"]="assistant", ["content"]=Str(msg?["content"]), ["tool_calls"]=echo });
                            foreach (var c in calls)
                            {
                                string callId = Str(c?["id"]);
                                string argsJson = Str(c?["function"]?["arguments"], "{}");
                                JsonObject a;
                                try { a = JsonNode.Parse(argsJson)?.AsObject() ?? new JsonObject(); }
                                catch { sh.Add(new JsonObject { ["role"]="tool", ["tool_call_id"]=callId, ["content"]="ERRO: arguments invalidos/truncados" }); continue; }
                                string p2 = Str(a["path"]);
                                string content2 = Str(a["content"]);
                                if (p2.Length == 0 || p2.Any(char.IsControl) || p2.Contains("..") || System.IO.Path.IsPathRooted(p2))
                                {
                                    sh.Add(new JsonObject { ["role"]="tool", ["tool_call_id"]=callId, ["content"]="ERRO: path invalido" });
                                    continue;
                                }
                                string full2 = System.IO.Path.Combine(ws, p2);
                                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full2)!);
                                System.IO.File.WriteAllText(full2, content2);
                                if (p2.EndsWith(".sh", StringComparison.OrdinalIgnoreCase)) writtenScripts.Add(full2);
                                sh.Add(new JsonObject { ["role"]="tool", ["tool_call_id"]=callId, ["content"]=$"escrito {p2}" });
                            }
                        }
                        produced = ResolveScript();
                    }
                }

                // 3) Exec (E4B) le companion .md + script e executa (bash script.sh).
                Log($"  [belt:{key}] 3/4 Exec lendo e executando scripts...");
                int execOk = 0;
                foreach (var f in System.IO.Directory.GetFiles(ws, "*.sh"))
                {
                    string name = System.IO.Path.GetFileName(f);
                    string body = System.IO.File.ReadAllText(f);
                    if (body.Contains("write_file{") || body.Contains("\"path\":") || body.Contains("```"))
                    {
                        Log($"  [belt:{key}] exec SKIP (tool colada no .sh): {name}");
                        continue;
                    }
                    var (syntax, syntaxErr) = Run("bash", ws, 60, "-n", name);
                    if (syntax != 0)
                    {
                        Log($"  [belt:{key}] exec SKIP (sintaxe): {name}: {syntaxErr.Trim().Split('\n')[0]}");
                        continue;
                    }
                    string meta = System.IO.Path.ChangeExtension(f, ".md");
                    if (System.IO.File.Exists(meta))
                    {
                        string md = System.IO.File.ReadAllText(meta);
                        string sh = System.IO.File.ReadAllText(f);
                        var execHistory = new JsonArray(
                            new JsonObject { ["role"]="system", ["content"]="Voce e o Exec (role exec, E4B). Leia o companion .md e o script. Responda apenas EXEC-READ-OK." },
                            new JsonObject { ["role"]="user", ["content"]="COMPANION:\n" + md + "\n\nSCRIPT:\n" + sh });
                        var execR = await TestContext.PostObj((Environment.GetEnvironmentVariable("WAR_CPU_BRIDGE") ?? "http://127.0.0.1:4125") + "/v1/chat/completions",
                            new JsonObject { ["model"]=execModel, ["temperature"]=0.1, ["stream"]=false, ["messages"]=execHistory.DeepClone() });
                        string execContent = Str(execR["choices"]?[0]?["message"]?["content"]);
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