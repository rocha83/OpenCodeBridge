using System.Text.Json.Nodes;
using Rochas.OpenCodeBridge.Core;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>
/// Contrato do loop agentivo: toda tool chamada tem resposta no contexto do
/// turno seguinte, nos dois shapes (chat/completions e Responses), e o corte de
/// orçamento deixa marcador com o total. Sem isso o modelo "repete o mesmo erro"
/// sem nunca enxergar o output — a regressão que o componente nao pode ter.
/// </summary>
internal static class UnitToolLoopContextTests
{
    public static Task Run()
    {
        ShapeChat();
        ShapeResponses();
        Cutting();
        Unanswered();
        LoopIntegro();
        InputSequence();
        return Task.CompletedTask;
    }

    private static void ShapeChat()
    {
        var m = ToolLoopContext.ChatTool("call_1", "saida da tool");
        TestContext.Check(m["role"]?.GetValue<string>() == "tool", "chat: role=tool");
        TestContext.Check(m["tool_call_id"]?.GetValue<string>() == "call_1", "chat: tool_call_id ligado");
        TestContext.Check(m["content"]?.GetValue<string>() == "saida da tool", "chat: content integral");
    }

    private static void ShapeResponses()
    {
        var m = ToolLoopContext.ResponsesOutput("call_2", "erro FS0001: ...");
        TestContext.Check(m["type"]?.GetValue<string>() == "function_call_output", "responses: type correto");
        TestContext.Check(m["call_id"]?.GetValue<string>() == "call_2", "responses: call_id ligado");
        TestContext.Check(m["output"]?.GetValue<string>() == "erro FS0001: ...", "responses: output integral");
    }

    private static void Cutting()
    {
        TestContext.Check(ToolLoopContext.Cut(null) == "", "cut: null -> vazio");
        TestContext.Check(ToolLoopContext.Cut("") == "", "cut: vazio -> vazio");
        TestContext.Check(ToolLoopContext.Cut("curto", 100) == "curto", "cut: abaixo do orçamento intacto");
        string big = new('x', 500);
        string cut = ToolLoopContext.Cut(big, 100);
        TestContext.Check(cut.Length < big.Length && cut.Contains("cortado: 500"), "cut: marcador com o total");
        TestContext.Check(ToolLoopContext.Cut(big, 0) == big, "cut: max<=0 desliga o corte");
        var tool = ToolLoopContext.ChatTool("c", big, 100);
        TestContext.Check(tool["content"]!.GetValue<string>().Contains("cortado: 500"), "cut: aplicado no shape chat");
    }

    private static void Unanswered()
    {
        var msgs = new JsonArray
        {
            ToolLoopContext.ChatTool("a", "ok"),
            ToolLoopContext.ResponsesOutput("b", "ok"),
        };
        TestContext.Check(ToolLoopContext.Unanswered(msgs, new[] { "a", "b" }).Count == 0, "unanswered: par completo");
        var missing = ToolLoopContext.Unanswered(msgs, new[] { "a", "z" });
        TestContext.Check(missing.Count == 1 && missing[0] == "z", "unanswered: id orfao apontado");
        TestContext.Check(ToolLoopContext.Unanswered(null, new[] { "a" }).Count == 1, "unanswered: contexto nulo conta como orfao");
        TestContext.Check(ToolLoopContext.Unanswered(msgs, null).Count == 0, "unanswered: sem calls, nada a cobrar");
    }

    /// <summary>
    /// Simula dois turnos de um loop agentivo: a call do turno 1 entra no
    /// contexto como output e o turno 2 parte de um contexto sem órfão, mesmo
    /// quando o output é grande (corte com marcador).
    /// </summary>
    private static void LoopIntegro()
    {
        var msgs = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = "compile o projeto" },
            new JsonObject
            {
                ["role"] = "assistant", ["content"] = "",
                ["tool_calls"] = new JsonArray(new JsonObject
                {
                    ["id"] = "call_x", ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = "shell", ["arguments"] = "{\"command\":\"dotnet run\"}" }
                })
            },
        };
        string err = "error FS0001: Esperava-se 'a array, tem 'float list\n" + new string('e', 400);
        msgs.Add(ToolLoopContext.ChatTool("call_x", err, 120));
        TestContext.Check(ToolLoopContext.Unanswered(msgs, new[] { "call_x" }).Count == 0, "loop: call do turno 1 respondida");
        string body = msgs[2]!["content"]!.GetValue<string>();
        TestContext.Check(body.StartsWith("error FS0001") && body.Contains("cortado:"), "loop: erro do compilador chega ao modelo");
    }

    /// <summary>
    /// Caminho harness (Responses -> chat): a ordem usuário -> assistant(tool_calls)
    /// -> tool(outputs) atravessa a tradução, com N chamadas no MESMO turno do
    /// assistente; item de reasoning fica fora de propósito (só o thinking sai).
    /// </summary>
    private static void InputSequence()
    {
        var input = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "message", ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "rode os testes" })
            },
            new JsonObject { ["type"] = "reasoning", ["summary"] = new JsonArray() },
            new JsonObject
            {
                ["type"] = "function_call", ["call_id"] = "c1", ["name"] = "shell",
                ["arguments"] = "{\"command\":\"dotnet build\"}"
            },
            new JsonObject
            {
                ["type"] = "function_call", ["call_id"] = "c2", ["name"] = "read",
                ["arguments"] = "{\"path\":\"a.cs\"}"
            },
            new JsonObject { ["type"] = "function_call_output", ["call_id"] = "c1", ["output"] = "build ok" },
            new JsonObject { ["type"] = "function_call_output", ["call_id"] = "c2", ["output"] = "class A {}" },
            new JsonObject
            {
                ["type"] = "message", ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "agora commite" })
            },
        };

        var msgs = Rochas.OpenCodeBridge.Program.InputToMessages(input, null, profileClient: true);
        string[] roles = msgs.Select(m => m?["role"]?.GetValue<string>() ?? "").ToArray();
        TestContext.Check(roles.SequenceEqual(new[] { "user", "assistant", "tool", "tool", "user" }),
            "responses: ordem user/assistant/tool(s)/user preservada");
        TestContext.Check(!roles.Contains("reasoning") && msgs.Count == 5, "responses: reasoning fora do contexto");

        int calls = msgs[1]?["tool_calls"]?.AsArray().Count ?? 0;
        TestContext.Check(calls == 2, "responses: 2 calls no mesmo turno do assistente");
        TestContext.Check(ToolLoopContext.Unanswered(msgs, new[] { "c1", "c2" }).Count == 0,
            "responses: toda call respondida no turno seguinte");
        TestContext.Check(msgs[2]?["content"]?.GetValue<string>() == "build ok"
            && msgs[3]?["content"]?.GetValue<string>() == "class A {}", "responses: outputs na ordem das calls");
    }
}
