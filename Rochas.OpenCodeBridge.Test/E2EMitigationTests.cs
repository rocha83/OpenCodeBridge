using System.Text;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

/// <summary>E2E de mitigação de erro e carga (falha alta honesta, concorrência).</summary>
internal static class E2EMitigationTests
{
    public static async Task ErrorMitigation()
    {
        // Caminho de ERRO: nenhum request de modelo e' servido (a bridge responde 4xx
        // antes de montar o system), entao o portao de fluxo P1 nao julga este teste.
        TestContext.NoModelFlow = true;
        // Mitigação real: corpo malformado (sem input) deve falhar alto com 4xx, sem hang.
        var body = new JsonObject { ["model"] = "openai/Gemma4-E4B-Q4", ["stream"] = false };
        using var res = await TestContext.Http.PostAsync(TestContext.Bridge + "/v1/responses", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        string text = await res.Content.ReadAsStringAsync();
        TestContext.Check(!res.IsSuccessStatusCode, "corpo sem input falha alto, veio: " + (int)res.StatusCode);
        TestContext.Check(text.Length > 5, "erro com corpo útil");
    }

    public static async Task HealthyConcurrency()
    {
        // So' leitura de /api/status: nenhum request de modelo para o portao conferir.
        TestContext.NoModelFlow = true;
        // Mitigação de carga: 3 status paralelos, todos ok e mesmo modelo.
        var tasks = Enumerable.Range(0, 3).Select(_ => TestContext.GetObj(TestContext.Bridge + "/api/status")).ToArray();
        var all = await Task.WhenAll(tasks);
        TestContext.Check(all.All(s => s["status"]?.GetValue<string>() == "ok"), "3x status ok");
        TestContext.Check(all.All(s => s["model"]?.GetValue<string>() == TestContext.ServedModel), "3x mesmo modelo");
    }
}
