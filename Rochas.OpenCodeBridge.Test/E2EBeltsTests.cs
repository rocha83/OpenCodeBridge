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

    const string OrchSystem = "Voce e o Orch (role orch, E4B). Segmenta o enunciado em tarefas atomicas (max 5) e responde SOMENTE com JSON array: [{\"title\":\"...\",\"prompt\":\"...\"}]. Nao execute nada.";

    /// <summary>Extrai o JSON array de tarefas do texto do Orch (bridge ou opencode com logs).</summary>
    static JsonArray ExtractTaskArray(string text)
    {
        JsonArray best = new();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '[') continue;
            int depth = 0, end = -1;
            bool inStr = false, esc = false;
            for (int j = i; j < text.Length; j++)
            {
                char c = text[j];
                if (inStr)
                {
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') { inStr = true; continue; }
                if (c == '[') depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0) { end = j; break; }
                }
            }
            if (end < 0) continue;
            try
            {
                var arr = JsonNode.Parse(text[i..(end + 1)])?.AsArray();
                if (arr is null || arr.Count == 0) continue;
                bool ok = true;
                foreach (var t in arr)
                    if (t is not JsonObject o || o["title"] is null || o["prompt"] is null) { ok = false; break; }
                if (ok && arr.Count > best.Count) best = arr;
            }
            catch { /* fragmento JSON de log: tenta o proximo array */ }
        }
        return best;
    }

    /// <summary>Scripter via opencode real: o CLI executa a tool write e entrega .sh/.md no workspace.</summary>
    /// <returns>true se o .sh existe e passa em bash -n.</returns>
    static bool ScriptViaOpencode(string key, string ws, string safeTitle, string taskPrompt, string grounded)
    {
        string scriptName = safeTitle + ".sh";
        string prompt =
            "Voce e o Scripter (role scripter, E4B). Componha UM script bash que executa a tarefa e grave no cwd " + ws + " o arquivo " + scriptName + " com shebang. " +
            "Gere tambem o companion " + safeTitle + ".md descrevendo o script. Use a tool write UMA vez por arquivo; script com no maximo 80 linhas, companion com no maximo 20. " +
            "Bash puro; nunca escreva C#/SQL solto no .sh (use heredoc ou arquivo separado). Ao terminar responda apenas SCRIPTER-OK.\n\n" +
            grounded + "\nTAREFA:\n" + taskPrompt;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Log($"  [belt:{key}] scripter via opencode (agent=scripter) tentativa {attempt + 1}/3...");
            try
            {
                TestContext.OpencodeRun(prompt, ws, agent: "scripter");
            }
            catch (Exception ex)
            {
                Log($"  [belt:{key}] scripter opencode falhou: {ex.Message.Split('\n')[0]}");
                prompt = "O turno anterior falhou. Reenvie com UMA tool write de JSON valido; script com no maximo 60 linhas, sem C#/SQL solto e sem quebrar strings.\n\nTAREFA:\n" + taskPrompt;
                continue;
            }
            string? produced = ResolveScript(ws, safeTitle, scriptName);
            if (produced is null)
            {
                Log($"  [belt:{key}] scripter opencode nao gerou .sh");
                prompt = "Voce nao gravou o arquivo " + scriptName + ". Use a tool write AGORA para criar " + scriptName + " (bash puro, no maximo 80 linhas) e responda apenas SCRIPTER-OK.\n\nTAREFA:\n" + taskPrompt;
                continue;
            }
            string? err = ValidateScript(ws, produced);
            if (err is null)
            {
                string body = System.IO.File.ReadAllText(produced);
                Log($"  [belt:{key}] script OK: {System.IO.Path.GetFileName(produced)} ({body.Length} bytes)");
                return true;
            }
            Log($"  [belt:{key}] script SYNTAX FAIL: {System.IO.Path.GetFileName(produced)}: {err.Split('\n')[0]}");
            prompt = "O script " + System.IO.Path.GetFileName(produced) + " falhou em `bash -n`:\n" + err +
                "\nReescreva o arquivo via tool write corrigindo a sintaxe. Bash puro; nunca C#/SQL solto. Responda SCRIPTER-OK.\n\nTAREFA:\n" + taskPrompt;
        }
        return false;
    }

    static string? ResolveScript(string ws, string safeTitle, string scriptName)
    {
        string exact = System.IO.Path.Combine(ws, scriptName);
        if (System.IO.File.Exists(exact)) return exact;
        var byTitle = FindByTitle(ws, safeTitle);
        if (byTitle is not null) return byTitle;
        return System.IO.Directory.GetFiles(ws, "*.sh")
            .OrderByDescending(System.IO.File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    static string? ValidateScript(string ws, string full)
    {
        string body = System.IO.File.ReadAllText(full);
        if (body.Length == 0 || !body.StartsWith("#!", StringComparison.Ordinal))
            return "sem shebang";
        if (body.Contains("write_file{") || body.Contains("\"path\":") || body.Contains("```"))
            return "conteudo do .sh tem chamada de tool colada (write_file{...}) ou bloco markdown";
        var (code, outp) = Run("bash", ws, 60, "-n", System.IO.Path.GetFileName(full));
        return code == 0 ? null : outp.Trim();
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
            string ws = System.IO.Path.Combine("/tmp/opencode/belts", key);
            // Workspace limpo: sobras de uma belt anterior inflariam scriptsOk/execOk.
            if (System.IO.Directory.Exists(ws)) System.IO.Directory.Delete(ws, true);
            System.IO.Directory.CreateDirectory(ws);
            Log($"  [belt:{key}] START ws={ws}"); 
            try
            {
                // 1) Orch (E4B) segmenta o enunciado em tarefas atomicas.
                // No modo opencode o Orch roda pelo CLI real (agent=orch);
                // no bridge, chamada direta /v1/chat/completions.
                string orchContent;
                if (TestContext.Mode == "opencode")
                {
                    Log($"  [belt:{key}] 1/3 Orch via opencode (agent=orch)...");
                    string orchPrompt = OrchSystem + "\n\nENUNCIADO:\n" + prompt.Replace("{ws}", ws);
                    orchContent = TestContext.OpencodeRun(orchPrompt, ws, agent: "orch");
                }
                else
                {
                    Log($"  [belt:{key}] 1/3 Orch via bridge...");
                    var orchHistory = new JsonArray(
                        new JsonObject { ["role"]="system", ["content"]=OrchSystem },
                        new JsonObject { ["role"]="user", ["content"]=prompt.Replace("{ws}", ws) });
                    var orchR = await TestContext.PostObj(TestContext.Bridge + "/v1/chat/completions",
                        new JsonObject { ["model"]=orchModel, ["temperature"]=0.3, ["stream"]=false, ["messages"]=orchHistory.DeepClone() });
                    orchContent = Str(orchR["choices"]?[0]?["message"]?["content"]);
                }
                var tasks = ExtractTaskArray(orchContent);
                TestContext.Check(tasks.Count > 0, $"[{key}] Orch segmentou em JSON array");
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
                Log($"  [belt:{key}] 2/3 Scripter compondo scripts...");
                int scriptsOk = 0;
                foreach (var t in tasks)
                {
                    string title = Str(t?["title"], "tarefa");
                    string taskPrompt = Str(t?["prompt"], prompt).Replace("{ws}", ws);
                    string safeTitle = title.Replace(" ", "_").ToLowerInvariant();
                    // No modo opencode o Scripter tambem roda pelo CLI real (agent=scripter),
                    // com as tools de escrita do proprio opencode; nada de PostObj direto.
                    if (TestContext.Mode == "opencode")
                    {
                        if (ScriptViaOpencode(key, ws, safeTitle, taskPrompt, grounded)) scriptsOk++;
                        continue;
                    }
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
                    // Watchers extras na roxa orch: o Scripter pode abrir com
                    // prosa e recuperar no turno seguinte; 6 turnos/2 nudges
                    // eram curtos. Aceite (.sh válido no `bash -n`) inalterado;
                    // execução fica fora deste teste.
                    for (int turn = 0; turn < 8; turn++)
                    {
                        JsonObject r;
                        try
                        {
                            r = await TestContext.PostObj(TestContext.Bridge + "/v1/chat/completions",
                                new JsonObject { ["model"]=scripterModel, ["temperature"]=0.2, ["stream"]=false, ["messages"]=sh.DeepClone(), ["tools"]=tools.DeepClone() });
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
                        if (calls is null || calls.Count == 0)
                        {
                            // Prosa sem tool_call no primeiro turno acontece (medido em
                            // belts-orch roxa 19:56: 0 scripts em 26s). Watchers: ate
                            // 4 nudges; o aceite continua exigindo .sh valido no
                            // `bash -n` (execucao fica fora deste teste).
                            if (turn < 4)
                            {
                                Log($"  [belt:{key}] scripter sem tool (chars={Str(msg?["content"]).Length}) — nudge {turn + 1}");
                                sh.Add(new JsonObject { ["role"]="assistant", ["content"]=Str(msg?["content"]) });
                                sh.Add(new JsonObject { ["role"]="user", ["content"]="Voce nao chamou nenhuma tool. Use write_file AGORA para gravar " + safeTitle + ".sh (bash puro, no maximo 80 linhas) e responda apenas SCRIPTER-OK." });
                                continue;
                            }
                            break;
                        }
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
                                    new JsonObject { ["model"]=scripterModel, ["temperature"]=0.2, ["stream"]=false, ["messages"]=sh.DeepClone(), ["tools"]=tools.DeepClone() });
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

                // 3) Integridade dos .sh gerados: shebang, sem tool colada e
                // `bash -n` limpo. Execucao fica fora deste teste (testar depois).
                Log($"  [belt:{key}] 3/3 integridade dos scripts...");
                int integrityOk = 0, discarded = 0;
                foreach (var f in System.IO.Directory.GetFiles(ws, "*.sh"))
                {
                    string name = System.IO.Path.GetFileName(f);
                    string body = System.IO.File.ReadAllText(f);
                    string? why = null;
                    if (body.Length == 0 || !body.StartsWith("#!", StringComparison.Ordinal))
                        why = "sem shebang";
                    else if (body.Contains("write_file{") || body.Contains("\"path\":")
                             || body.Contains("```"))
                        why = "tool/markdown colados";
                    else
                    {
                        var (syntax, syntaxErr) = Run("bash", ws, 60, "-n", name);
                        if (syntax != 0) why = syntaxErr.Trim().Split('\n')[0];
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

                // 4) Valida artefatos: Scripter compôs e todo .sh restante e integro.
                Log($"  [belt:{key}] validando: scripts={scriptsOk} integros={integrityOk} descartados={discarded}");
                TestContext.Check(scriptsOk > 0, $"[{key}] Scripter compôs {scriptsOk} scripts válidos");
                TestContext.Check(integrityOk > 0,
                    $"[{key}] scripts .sh íntegros no workspace ({integrityOk} ok, {discarded} descartados)");
                Log($"  [belt:{key}] PASS: {scriptsOk} scripts, {integrityOk} integros, {discarded} descartados");
                SaveResult(key, "PASS", $"{scriptsOk} scripts, {integrityOk} integros, {discarded} descartados");
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