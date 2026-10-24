using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

// Unit do repasse de amostragem (--sampling-passthrough): o body que a bridge
// manda ao llama pode carregar repeat_penalty/dry_* que o cliente pediu; sem a
// flag nada muda. Sem HTTP e sem modelo (funcao pura).
internal static class UnitSamplingTests
{
    public static Task CopiesKnownNumericKeys()
    {
        var from = new JsonObject
        {
            ["repeat_penalty"] = 1.0,
            ["dry_multiplier"] = 0.0,
            ["dry_penalty_last_n"] = 1024,
            ["top_p"] = 0.9
        };
        var to = new JsonObject { ["temperature"] = 0.1 };
        global::Rochas.OpenCodeBridge.Program.CopySamplingParams(from, to);
        TestContext.Check(to["repeat_penalty"]?.GetValue<double>() == 1.0, "repasse repeat_penalty");
        TestContext.Check(to["dry_multiplier"]?.GetValue<double>() == 0.0, "repasse dry_multiplier");
        TestContext.Check(to["dry_penalty_last_n"]?.GetValue<int>() == 1024, "repasse dry_penalty_last_n");
        TestContext.Check(to["top_p"]?.GetValue<double>() == 0.9, "repasse top_p");
        TestContext.Check(to["temperature"]?.GetValue<double>() == 0.1, "nao sobrescreve temperature");
        return Task.CompletedTask;
    }

    public static Task IgnoresUnknownAndNonNumeric()
    {
        var from = new JsonObject
        {
            ["repeat_penalty"] = "1.1",          // string nao passa
            ["dry_multiplier"] = true,           // bool nao passa
            ["model"] = "qualquer",             // fora da whitelist
            ["messages"] = JsonValue.Create(1)  // fora da whitelist
        };
        var to = new JsonObject();
        global::Rochas.OpenCodeBridge.Program.CopySamplingParams(from, to);
        TestContext.Check(!to.ContainsKey("repeat_penalty"), "ignora valor nao numerico");
        TestContext.Check(!to.ContainsKey("dry_multiplier"), "ignora bool");
        TestContext.Check(!to.ContainsKey("model"), "ignora chave fora da whitelist");
        TestContext.Check(to.Count == 0, "body permanece intacto");
        return Task.CompletedTask;
    }

    public static Task WhitelistHasSamplingOnly()
    {
        var expected = new[]
        {
            "repeat_penalty", "repeat_last_n", "dry_multiplier", "dry_base",
            "dry_allowed_length", "dry_penalty_last_n", "top_p", "top_k",
            "min_p", "typical_p", "frequency_penalty", "presence_penalty",
            "seed", "xtc_probability", "xtc_threshold"
        };
        foreach (string key in expected)
        {
            var from = new JsonObject { [key] = 0.5 };
            var to = new JsonObject();
            global::Rochas.OpenCodeBridge.Program.CopySamplingParams(from, to);
            TestContext.Check(to.ContainsKey(key), "whitelist contem " + key);
        }
        var forbidden = new JsonObject { ["temperature"] = 0.5, ["messages"] = JsonValue.Create(1) };
        var sink = new JsonObject();
        global::Rochas.OpenCodeBridge.Program.CopySamplingParams(forbidden, sink);
        TestContext.Check(sink.Count == 0, "sem campos de mensagem/temperatura");
        return Task.CompletedTask;
    }
}
