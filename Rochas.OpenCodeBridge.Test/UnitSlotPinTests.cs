using System.Text.Json.Nodes;
using Rochas.OpenCodeBridge.Core;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>
/// Pins do pino de slot (<c>--slot N</c>): o llama roda 4 slots de 114688 no
/// <c>:4100</c> e a bateria roda um level por slot. O <c>id_slot</c> tem que
/// entrar no corpo enviado ao upstream sem mexer no resto do request.
/// </summary>
public static class UnitSlotPinTests
{
    /// <summary>Objeto montado pela bridge recebe `id_slot` e preserva os campos.</summary>
    public static Task PinObject()
    {
        var body = new JsonObject
        {
            ["model"] = "Gemma4-E4B-Q4",
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = "acentuação"
            }),
            ["stream"] = false,
            ["temperature"] = 0.2
        };
        SlotPin.Apply(body, 2);
        TestContext.Check(body["id_slot"]?.GetValue<int>() == 2,
            $"id_slot deveria ser 2 (saiu {body["id_slot"]?.ToJsonString() ?? "nulo"})");
        TestContext.Check(body["model"]?.GetValue<string>() == "Gemma4-E4B-Q4",
            "pino nao pode trocar o model servido");
        TestContext.Check(body["stream"]?.GetValue<bool>() == false,
            "pino nao pode ligar stream no upstream");

        var semPino = new JsonObject { ["model"] = "Gemma4-E4B-Q4" };
        SlotPin.Apply(semPino, SlotPin.None);
        TestContext.Check(semPino["id_slot"] is null,
            "sem pino o corpo tem que ficar sem id_slot (llama escolhe o slot livre)");
        return Task.CompletedTask;
    }

    /// <summary>Body cru do cliente (string) recebe `id_slot` sem perder acentos.</summary>
    public static Task PinString()
    {
        string body = "{\"model\":\"m\",\"messages\":[{\"role\":\"user\",\"content\":\"acentuação\"}]}";
        string pinned = SlotPin.Apply(body, 3);
        var parsed = JsonNode.Parse(pinned)?.AsObject();
        TestContext.Check(parsed?["id_slot"]?.GetValue<int>() == 3,
            $"body cru deveria sair com id_slot=3 (saiu: {pinned})");
        TestContext.Check(parsed?["messages"]?[0]?["content"]?.GetValue<string>() == "acentuação",
            $"pino nao pode corromper o conteudo (saiu: {pinned})");

        // Texto que nao e' JSON segue intacto (o upstream responde o erro).
        string invalido = "nao-json";
        TestContext.Check(SlotPin.Apply(invalido, 1) == invalido,
            "pino nao pode reescrever body ilegivel");
        TestContext.Check(SlotPin.Apply(body, SlotPin.None) == body,
            "sem pino o body cru tem que voltar byte a byte");
        return Task.CompletedTask;
    }
}
