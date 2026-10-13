using System.Text;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Web.Services;

// Handler da tool "subagent": delega uma subtarefa ao MESMO modelo (sem recursão
// por construção: executor interno sem "subagent") e devolve resposta + thinking.
// Experimento: sub com write/edit/shell anunciadas; shell travado em validação
// (bash -n), zip ou navegação — ver SubShellAllowed. Aparece na UI como tool.
public sealed class SubagentToolHandler : IToolHandler
{
    public string ToolName => "subagent";

    private const double FixedTemperature = 0.4;
    private const string SubagentSystem =
        "Você é um agente desenvolvedor senior .net c# e react.js que nao dialoga. Para o enunciado, componha script shell sh de implementacao em batch com CAMINHOS alternativos: o script traz um questionario interativo com parametros para o executor escolher o caminho; cada caminho deve ser executável pelo usuario ou pelo agente menor qwen3.5-4b com parametros. Quando nao interativo, o executor segue as opcoes 'Recomendadas' do questionario (marque-as no script). Ao final escreva o script na pasta atual com a tool write: o nome do script deve conter o titulo da fase + data e hora juliana (<titulo-da-fase>_<dia-do-ano+hora-minuto>.sh, com dia/hora informados no enunciado, ex.: reembolsos_2860113), valide a sintaxe com a tool shell usando 'bash -n <arquivo>', se a validação acusar erro corrija o script e valide de novo até limpar, porém não execute o script. O uso das tools write e shell (bash -n) é OBRIGATÓRIO em toda fase, sem elas o script não existe; 'não executar' refere-se apenas a rodar o script final, validar a sintaxe é validação, não execução.";

    private readonly IBridgeClient _bridge;

    public SubagentToolHandler(IBridgeClient bridge)
    {
        _bridge = bridge;
    }

    public ToolResult Handle(string argumentsJson, ToolContext context)
    {
        return HandleInner(argumentsJson, context);
    }

    private static bool SubShellAllowed(string argsJson)
    {
        string cmd = "";
        try { cmd = JsonNode.Parse(argsJson)?.AsObject()?["command"]?.GetValue<string>() ?? ""; }
        catch { cmd = argsJson ?? ""; }
        cmd = cmd.Trim();
        if (cmd.StartsWith("bash -n ", StringComparison.Ordinal)) return true;
        string first = cmd.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return first is "zip" or "unzip" or "ls" or "cat" or "pwd" or "head" or "tail" or "wc" or "file";
    }

    private static JsonArray SubTools()
    {
        static JsonObject Param(string name, string type, string desc, bool req) =>
            new() { ["name"] = name, ["type"] = type, ["description"] = desc, ["required"] = req };
        static JsonObject Tool(string name, string desc, params JsonObject[] pars)
        {
            var props = new JsonObject();
            var required = new JsonArray();
            foreach (var p in pars)
            {
                props[p["name"]!.GetValue<string>()] = new JsonObject
                {
                    ["type"] = p["type"]!.GetValue<string>(),
                    ["description"] = p["description"]!.GetValue<string>(),
                };
                if (p["required"]!.GetValue<bool>()) required.Add(p["name"]!.GetValue<string>());
            }
            return new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = name,
                    ["description"] = desc,
                    ["parameters"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = props,
                        ["required"] = required,
                    },
                },
            };
        }
        return new JsonArray
        {
            Tool("write", "Escreve arquivo no workspace (pasta atual).",
                Param("path", "string", "Nome do arquivo (ex.: 'fase1.sh').", true),
                Param("content", "string", "Conteúdo integral.", true)),
            Tool("edit", "Edita trecho de arquivo existente.",
                Param("path", "string", "Arquivo.", true),
                Param("oldString", "string", "Trecho exato.", true),
                Param("newString", "string", "Substituto.", true)),
            Tool("shell", "SÓ validação ('bash -n <arquivo>'), compactação (zip/unzip) ou navegação (ls/cat/pwd). NÃO executa scripts.",
                Param("command", "string", "Comando (ex.: 'bash -n fase1.sh').", true)),
        };
    }

    private ToolResult HandleInner(string argumentsJson, ToolContext context)
    {
        JsonObject? obj;
        try { obj = JsonNode.Parse(argumentsJson)?.AsObject(); }
        catch (Exception ex) { return ToolResult.Fail($"argumentos inválidos: {ex.Message}"); }
        if (obj is null) return ToolResult.Fail("argumentos inválidos: objeto JSON esperado");

        string prompt = obj["prompt"]?.GetValue<string>() ?? "";
        if (string.IsNullOrWhiteSpace(prompt)) return ToolResult.Fail("prompt é obrigatório");
        // Sem teto repassado: ignora pedido menor (o thinking come o orçamento e o texto morre).
        // null = sem max_tokens no body; a bridge aplica o teto (GPU 32k, CPU 4k).
        int? maxTokens = null;

        if (string.IsNullOrWhiteSpace(context.BridgeUrl) || string.IsNullOrWhiteSpace(context.Model))
            return ToolResult.Fail("subagente sem agente (bridge/modelo ausentes)");

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(30, context.TimeoutSeconds)));
            // Mesmas specs do chamador: mesmo modelo, 0.4, thinking on, mesmas tools.
            // Trava de profundidade estrutural: executor interno sem "subagent".
            // Id do subagente p/ UI ao vivo (card + labels próprios; execução serial).
            string subId = "sub-" + Guid.NewGuid().ToString("N")[..8];
            var inner = new ToolExecutor(
                ToolExecutor.DefaultHandlers().Where(h => h.ToolName != "subagent"),
                context.WorkspaceRoot, "/tmp/tool-executor-sub.log");
            inner.SubId = subId;
            inner.LogInnerAsync = context.LogInnerAsync;
            inner.ProgressAsync = context.ProgressAsync is null ? null
                : (p => context.ProgressAsync(p with { Sub = subId }));
            var chunkSink = context.SubChunkAsync;
            var now = System.DateTime.Now;
            var history = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = prompt.Trim() + $"\n(Data-hora-juliana atual: {now.DayOfYear}{now:HHmm}.)" } };
            // Sub É build (9B-build): temp 0.4 fixa, thinking OFF (doutrina plan
            // true / build false; pensar no executor come orçamento e murcha ato).
            // Trava: shell do sub só valida (`bash -n`), compacta (zip/unzip) ou
            // navega (ls/cat/pwd); nada executa — ver SubShellAllowed.
            JsonArray? tools = SubTools();
            var collected = new StringBuilder();
            string subthink = "";
            if (DeepSeekToolCoercion.IsDeepSeek(context.Model))
            {
                // DeepSeek sem tool_calls estruturadas: loop texto->parse->executa,
                // retorno como <tool_response> (padrão oficial). Sem mudar prompts.
                var dsq = new DeepSeekToolExecutor(inner, SubShellAllowed);
                var r = dsq.RunAsync(_bridge, context.BridgeUrl, context.Model, FixedTemperature,
                    SubagentSystem, history, new List<string> { "write", "edit", "shell", "subagent" },
                    7, chunkSink is null ? null : (async (kind, text) => await chunkSink(subId, kind, text)),
                    cts.Token).GetAwaiter().GetResult();
                if (!r.Ok) return ToolResult.Fail($"subagente falhou: {r.Error}");
                if (!string.IsNullOrWhiteSpace(r.Text)) collected.AppendLine(r.Text);
                subthink = r.Thinking;
            }
            else
            for (int turn = 0; turn < 5; turn++)
            {
                var t = BridgeHelper.ChatTurnAsync(_bridge, context.BridgeUrl, context.Model,
                    FixedTemperature, SubagentSystem, history, tools, cts.Token, enableThinking: false, maxTokens: maxTokens,
                    onChunk: chunkSink is null ? null : (async (kind, text) => await chunkSink(subId, kind, text)))
                    .GetAwaiter().GetResult();
                if (!t.Ok) return ToolResult.Fail($"subagente falhou: {t.Error}");
                if (!string.IsNullOrWhiteSpace(t.Thinking)) subthink = t.Thinking;
                if (!string.IsNullOrWhiteSpace(t.Content)) collected.AppendLine(t.Content);
                if (t.Calls.Count == 0) break;
                history.Add(new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = t.Content,
                    ["tool_calls"] = new JsonArray(t.Calls.Select(c => new JsonObject
                    {
                        ["id"] = c.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Args },
                    }).ToArray()),
                });
                foreach (var c in t.Calls)
                {
                    // Sub não delega: ignora sem erro (não polui o histórico com FALHOU).
                    if (c.Name == "subagent")
                    {
                        collected.AppendLine("[subagent] ignorado: subagente não delega.");
                        history.Add(new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = c.Id,
                            ["content"] = "ignorado: subagente não delega.",
                        });
                        continue;
                    }
                    // Trava do experimento: shell do sub não executa.
                    if (c.Name is "shell" or "bash" or "sh" && !SubShellAllowed(c.Args))
                    {
                        const string msg = "negado: shell do subagente só valida ('bash -n <arquivo>'), compacta (zip/unzip) ou navega (ls/cat/pwd); não execute scripts.";
                        collected.AppendLine($"[shell] FALHOU: {msg}");
                        history.Add(new JsonObject
                        {
                            ["role"] = "tool",
                            ["tool_call_id"] = c.Id,
                            ["content"] = "Error: " + msg,
                        });
                        continue;
                    }
                    var r = inner.Execute(c.Name, c.Args, 300);
                    string o = r.Success ? r.Output : $"Error: {r.Error}";
                    string line = $"[{c.Name}] {(r.Success ? "ok" : "FALHOU")}: {o.Replace("\n", " / ")}";
                    collected.AppendLine(line);
                    // Linha de retorno também ao vivo no card (além do label).
                    if (chunkSink is not null)
                        try { chunkSink(subId, "content", line + "\n").GetAwaiter().GetResult(); } catch { }
                    history.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["tool_call_id"] = c.Id,
                        ["content"] = o,
                    });
                }
            }
            string text = collected.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(subthink))
            {
                string th = subthink.Trim();
                text += "\n<SUBTHINK>" + th + "</SUBTHINK>";
            }
            if (string.IsNullOrWhiteSpace(text)) return ToolResult.Fail("subagente vazio");
            return ToolResult.Ok(text);
        }
        catch (OperationCanceledException)
        {
            return ToolResult.Fail("subagente estourou o tempo");
        }
        catch (Exception ex)
        {
            try { System.IO.File.AppendAllText("/tmp/opencode/subagent-errors.log", $"{System.DateTime.Now:HH:mm:ss} {ex}\n---\n"); } catch { }
            return ToolResult.Fail($"subagente errou: {ex.Message.Split('\n')[0]}");
        }
    }
}
