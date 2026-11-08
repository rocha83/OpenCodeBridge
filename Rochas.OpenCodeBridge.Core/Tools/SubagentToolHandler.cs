using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Core.Tools;

// Handler da tool "subagent"/"task": delega um prompt a um modelo (upstream)
// e executa o loop agéntico (tools reais) até obter a resposta final.
// Permite ao Build/Orch segmentar trabalho em sub-tarefas executadas de verdade.
public sealed class SubagentToolHandler : IToolHandler
{
    public string ToolName => "subagent";

    /// <summary>Tools do subagente: as liberadas no frontmatter do agente
    /// (`effect: allow`); sem agente/lista vazia = todas. Nunca inclui
    /// `subagent` (sem delegacao recursiva).</summary>
    static JsonArray ToolsFor(AgentSpec? agent)
    {
        if (agent is null || agent.Tools.Count == 0) return (JsonArray)ToolDefs.DeepClone()!;
        var arr = new JsonArray();
        foreach (var t in ToolDefs)
        {
            string n = t?["function"]?["name"]?.GetValue<string>() ?? "";
            string canon = n is "fetch" or "webfetch" ? "webfetch" : n;
            if (agent.Tools.Contains(canon) || agent.Tools.Contains(n)) arr.Add(t!.DeepClone());
        }
        return arr.Count > 0 ? arr : (JsonArray)ToolDefs.DeepClone()!;
    }

    static readonly JsonArray ToolDefs = ToolHelp.Apply(new()
    {
        new JsonObject { ["type"]="function", ["function"]=new JsonObject { ["name"]="shell", ["description"]="executa comando shell", ["parameters"]=new JsonObject { ["type"]="object", ["properties"]=new JsonObject { ["command"]=new JsonObject{["type"]="string"} }, ["required"]=new JsonArray("command") } } },
        new JsonObject { ["type"]="function", ["function"]=new JsonObject { ["name"]="read", ["description"]="le arquivo", ["parameters"]=new JsonObject { ["type"]="object", ["properties"]=new JsonObject { ["path"]=new JsonObject{["type"]="string"}, ["offset"]=new JsonObject{["type"]="integer"}, ["limit"]=new JsonObject{["type"]="integer"} }, ["required"]=new JsonArray("path") } } },
        new JsonObject { ["type"]="function", ["function"]=new JsonObject { ["name"]="write", ["description"]="grava/cria arquivo", ["parameters"]=new JsonObject { ["type"]="object", ["properties"]=new JsonObject { ["path"]=new JsonObject{["type"]="string"}, ["content"]=new JsonObject{["type"]="string"} }, ["required"]=new JsonArray("path","content") } } },
        new JsonObject { ["type"]="function", ["function"]=new JsonObject { ["name"]="edit", ["description"]="substitui trecho exato", ["parameters"]=new JsonObject { ["type"]="object", ["properties"]=new JsonObject { ["path"]=new JsonObject{["type"]="string"}, ["oldString"]=new JsonObject{["type"]="string"}, ["newString"]=new JsonObject{["type"]="string"}, ["replaceAll"]=new JsonObject{["type"]="boolean"} }, ["required"]=new JsonArray("path","oldString","newString") } } },
        new JsonObject { ["type"]="function", ["function"]=new JsonObject { ["name"]="glob", ["description"]="lista arquivos por padrao", ["parameters"]=new JsonObject { ["type"]="object", ["properties"]=new JsonObject { ["pattern"]=new JsonObject{["type"]="string"}, ["path"]=new JsonObject{["type"]="string"} }, ["required"]=new JsonArray("pattern") } } },
        new JsonObject { ["type"]="function", ["function"]=new JsonObject { ["name"]="grep", ["description"]="busca regex", ["parameters"]=new JsonObject { ["type"]="object", ["properties"]=new JsonObject { ["pattern"]=new JsonObject{["type"]="string"}, ["path"]=new JsonObject{["type"]="string"}, ["include"]=new JsonObject{["type"]="string"} }, ["required"]=new JsonArray("pattern") } } },
        new JsonObject { ["type"]="function", ["function"]=new JsonObject { ["name"]="fetch", ["description"]="GET http", ["parameters"]=new JsonObject { ["type"]="object", ["properties"]=new JsonObject { ["url"]=new JsonObject{["type"]="string"} }, ["required"]=new JsonArray("url") } } },
        new JsonObject { ["type"]="function", ["function"]=new JsonObject { ["name"]="git", ["description"]="git somente-leitura", ["parameters"]=new JsonObject { ["type"]="object", ["properties"]=new JsonObject { ["command"]=new JsonObject{["type"]="string"}, ["path"]=new JsonObject{["type"]="string"} }, ["required"]=new JsonArray("command") } } },
    });

    static string Slug(string raw)
    {
        var b = new System.Text.StringBuilder();
        foreach (char c in raw)
        {
            if (char.IsLetterOrDigit(c)) b.Append(char.ToLowerInvariant(c));
            else if (b.Length > 0 && b[^1] != '-') b.Append('-');
            if (b.Length >= 40) break;
        }
        return b.ToString().Trim('-');
    }

    public ToolResult Handle(string argumentsJson, ToolContext context)
    {
        JsonObject args;
        try { args = JsonNode.Parse(argumentsJson)?.AsObject() ?? new JsonObject(); }
        catch { args = new JsonObject(); }

        string prompt = args["prompt"]?.GetValue<string>() ?? args["task"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(prompt))
            return ToolResult.Fail("subagent: argumento 'prompt' obrigatorio");

        // Agente alvo: nome (ou parte) do diretorio de agentes. Sem isso o
        // subagente rodava como "dev generico" e a role pedida (ex.: scripter,
        // que exige .sh com shebang/heredoc) nao existia no caminho bridge.
        string agentName = args["agent"]?.GetValue<string>() ?? args["subagent_type"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(agentName))
            return ToolResult.Fail("subagent: argumento 'agent' obrigatorio (nome ou parte do agente a executar); "
                + "disponiveis: " + AgentCatalog.Describe());
        AgentSpec? agent = AgentCatalog.Resolve(agentName);
        if (agent is null)
            return ToolResult.Fail($"subagent: agente '{agentName}' nao existe no diretorio de agentes "
                + $"({AgentCatalog.Dir()}); disponiveis: {AgentCatalog.Describe()}");

        string system = args["system"]?.GetValue<string>()
            ?? (agent is not null && agent.System.Length > 0
                ? agent.System
                : "Voce e um agente desenvolvedor senior. Execute a tarefa usando as tools disponiveis e responda de forma concisa em pt-br.");
        string model = args["model"]?.GetValue<string>() ?? "";
        double temp = args["temperature"]?.GetValue<double>() ?? (agent?.Temperature ?? 0.1);

        // O handler posta DIRETO no upstream: o /v1/chat/completions da bridge (que
        // injeta as caudas do harness) fica fora do caminho, entao o subagente nunca
        // via a cauda de tools — nem a receita do script construtor. Composta aqui a
        // partir das tools DESTE agente (mesma fonte: HarnessTails).
        var toolsForAgent = ToolsFor(agent);
        var toolNames = new List<string>();
        foreach (var t in toolsForAgent) toolNames.Add(t?["function"]?["name"]?.GetValue<string>() ?? "");
        if (toolNames.Count > 0)
            system = system.TrimEnd() + "\n\n" + HarnessTails.ComposeTools(toolNames);

        // O contrato do role nomeia o entregavel como `{title}` (ex.: {title}.sh):
        // no caminho bridge ninguem substituia e o modelo escrevia o literal. O
        // titulo vem do `description` do Orch (3 palavras ASCII), como no opencode.
        string title = Slug((args["description"]?.GetValue<string>() ?? "").Trim());
        if (title.Length > 0) system = system.Replace("{title}", title);

        string ws = args["workspace"]?.GetValue<string>() ?? context.WorkspaceRoot;
        if (string.IsNullOrWhiteSpace(ws)) ws = context.WorkspaceRoot;
        string upstream = string.IsNullOrWhiteSpace(context.UpstreamUrl) ? "http://127.0.0.1:4100" : context.UpstreamUrl;
        var executor = new BridgeToolExecutor(ws, upstream, context.ApiKey);

        var messages = new JsonArray(
            new JsonObject { ["role"] = "system", ["content"] = system },
            new JsonObject { ["role"] = "user", ["content"] = prompt });

        // Observabilidade do Core: com um agente resolvido, o `/api/tools/execute`
        // so' devolvia ok/erro — nao dava para separar "agente errado" de "prompt
        // errado" sem esperar o step inteiro.
        context.Log?.Invoke(new JsonObject
        {
            ["event"] = "subagent",
            ["agent"] = agent?.Name ?? "",
            ["id"] = title,
            ["asked"] = agentName,
            ["temp"] = temp,
            ["tools"] = agent is null || agent.Tools.Count == 0 ? "(todas)" : string.Join(",", agent.Tools),
            ["chars"] = prompt.Length,
            ["system_chars"] = system.Length,
            ["prompt"] = prompt
        });

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Clamp(context.TimeoutSeconds, 30, 600)) };
            if (!string.IsNullOrWhiteSpace(context.ApiKey))
                http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", context.ApiKey);

            var delivered = new List<string>();
            bool reminded = false;
            for (int turn = 0; turn < 8; turn++)
            {
                var body = new JsonObject
                {
                    ["temperature"] = temp,
                    ["stream"] = false,
                    ["messages"] = messages.DeepClone(),
                    ["tools"] = toolsForAgent.DeepClone()
                };
                if (!string.IsNullOrWhiteSpace(model)) body["model"] = model;

                var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                var resp = http.PostAsync(upstream + "/v1/chat/completions", content).GetAwaiter().GetResult();
                string raw = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode)
                    return ToolResult.Fail($"subagent: upstream HTTP {(int)resp.StatusCode}: {raw[..Math.Min(300, raw.Length)]}");

                using var doc = JsonDocument.Parse(raw);
                var msgEl = doc.RootElement.GetProperty("choices")[0].GetProperty("message");
                string text = msgEl.TryGetProperty("content", out var c) ? (c.GetString() ?? "") : "";
                JsonArray? calls = null;
                if (msgEl.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
                {
                    calls = new JsonArray();
                    foreach (var e in tc.EnumerateArray())
                        calls.Add(JsonNode.Parse(e.GetRawText()));
                }

                if (calls is null || calls.Count == 0)
                {
                    // O role que so' grava .sh/.md nao terminou so' porque respondeu
                    // texto: o pedido do usuario (C#) puxa o modelo para o artefato
                    // errado e ele desiste. Uma correcao, no loop que ja' existe.
                    if (!reminded && agent is not null && agent.WriteExtensions.Count > 0 && delivered.Count == 0)
                    {
                        reminded = true;
                        messages.Add(new JsonObject
                        {
                            ["role"] = "user",
                            ["content"] = $"Entrega incompleta: nenhum arquivo {string.Join("/", agent.WriteExtensions)} foi gravado. "
                                + $"Grave AGORA com write o path \"{title}.sh\" (shebang #!/usr/bin/env bash) contendo os artefatos por heredoc "
                                + "e o dotnet build no fim. Nao responda texto antes de gravar."
                        });
                        context.Log?.Invoke(new JsonObject { ["event"] = "subagent_nudge", ["agent"] = agent.Name, ["id"] = title, ["turn"] = turn });
                        continue;
                    }
                    return ToolResult.Ok(text.Length > 0 ? text : "(sem resposta)");
                }

                var names = new List<string>();
                foreach (var tc0 in calls)
                    names.Add(tc0?["function"]?["name"]?.GetValue<string>() ?? "?");
                // echo assistant + execute tools
                var echo = new JsonArray();
                foreach (var tc1 in calls) echo.Add(new JsonObject { ["id"]=tc1?["id"]?.GetValue<string>()??"", ["type"]="function", ["function"]=new JsonObject { ["name"]=tc1?["function"]?["name"]?.GetValue<string>()??"", ["arguments"]=tc1?["function"]?["arguments"]?.GetValue<string>()??"{}" } });
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = text, ["tool_calls"] = echo });

                var written = new List<string>();
                // Resumo por call para a sonda: nome + path/command/pattern (curto).
                // Antes o turno dizia apenas QUAIS tools — nao dava para saber o que
                // cada subagente realmente fez nem onde falhou.
                var acts = new List<string>();
                int okCount = 0;
                foreach (var tc2 in calls)
                {
                    string name = tc2?["function"]?["name"]?.GetValue<string>() ?? "";
                    string ajson = tc2?["function"]?["arguments"]?.GetValue<string>() ?? "{}";

                    // Politica de escrita do role (frontmatter `write_ext`): o Scripter
                    // grava SO' o .sh/.md — o .cs/.sql vai DENTRO do .sh por heredoc.
                    // Recusar no turno (com a instrucao) faz o modelo corrigir em vez
                    // de entregar o artefato errado e so' falhar no aceite.
                    if (agent is not null && agent.WriteExtensions.Count > 0 && name is "write" or "edit")
                    {
                        string p = "";
                        try { p = JsonNode.Parse(ajson)?["path"]?.GetValue<string>() ?? ""; } catch { }
                        string ext = System.IO.Path.GetExtension(p).ToLowerInvariant();
                        if (!agent.WriteExtensions.Contains(ext))
                        {
                            acts.Add(name + ":" + p + "(recusado)");
                            messages.Add(new JsonObject
                            {
                                ["role"] = "tool",
                                ["tool_call_id"] = tc2?["id"]?.GetValue<string>() ?? "",
                                ["content"] = $"{name} recusado: o agente '{agent.Name}' grava apenas "
                                    + string.Join(",", agent.WriteExtensions) + $"; '{p}' nao e' permitido. "
                                    + $"Chame write com path=\"{title}.sh\" (raiz do workspace, shebang #!/usr/bin/env bash); "
                                    + $"o conteudo de '{p}' vai DENTRO do .sh, criado por heredoc, e o proprio script roda dotnet build."
                            });
                            continue;
                        }
                    }

                    var res = executor.Execute(name, ajson, context.TimeoutSeconds);
                    if (res.Success) okCount++;
                    {
                        string brief = "";
                        try
                        {
                            var aj = JsonNode.Parse(ajson)?.AsObject();
                            brief = aj?["path"]?.GetValue<string>() ?? aj?["command"]?.GetValue<string>() ?? aj?["pattern"]?.GetValue<string>() ?? "";
                        }
                        catch { }
                        brief = brief.Replace("\n", " ").Replace("|", "/");
                        if (brief.Length > 120) brief = brief[..120] + "\u2026";
                        acts.Add((res.Success ? "" : "FALHOU ") + name + (brief.Length > 0 ? ":" + brief : ""));
                    }
                    if (res.Success && name is "write" or "edit")
                    {
                        try { string wp = JsonNode.Parse(ajson)?["path"]?.GetValue<string>() ?? ""; if (wp.Length > 0) { written.Add(wp); delivered.Add(wp); } } catch { }
                    }
                    string outp = res.Success ? res.Output : res.Error;
                    if (outp.Length > 2000) outp = outp[..2000] + "...(truncado)";
                    messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = tc2?["id"]?.GetValue<string>() ?? "", ["content"] = outp });
                }

                context.Log?.Invoke(new JsonObject
                {
                    ["event"] = "subagent_turn",
                    ["agent"] = agent?.Name ?? "",
                    ["id"] = title,
                    ["turn"] = turn,
                    ["calls"] = calls.Count,
                    ["ok"] = okCount,
                    ["tools"] = string.Join(",", names),
                    ["acts"] = string.Join(" | ", acts),
                    ["paths"] = string.Join("|", written)
                });
            }
            return ToolResult.Ok("(loop esgotado)");
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"subagent: {ex.Message}");
        }
    }
}
