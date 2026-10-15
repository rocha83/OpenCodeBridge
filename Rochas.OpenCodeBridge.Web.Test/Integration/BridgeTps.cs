// Leitura de tok/s da bridge (llama.cpp devolve "timings" com predicted_per_second
// no chat completion sem stream). Serve p/ imprimir junto ao PASS nos testes ao vivo.
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Web.Test.Integration;

internal static class BridgeTps
{
    // Extrai "32.1 tok/s" do JSON da bridge; "" se ausente.
    internal static string Read(JsonNode json)
    {
        try
        {
            var timings = json["timings"]?.AsObject();
            if (timings?["predicted_per_second"] is JsonValue v && v.TryGetValue<double>(out double tps) && tps > 0)
                return $"{tps:F1} tok/s";
            if (timings?["predicted_ms"] is JsonValue ms && ms.TryGetValue<double>(out double msVal) && msVal > 0
                && json["usage"]?["completion_tokens"] is JsonValue ct && ct.TryGetValue<double>(out double toks) && toks > 0)
                return $"{toks / (msVal / 1000.0):F1} tok/s";
        }
        catch { }
        return "";
    }
}
