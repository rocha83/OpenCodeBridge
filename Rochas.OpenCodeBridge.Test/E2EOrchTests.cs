using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

// E2E Orch (E4B role orch): segmenta um enunciado complexo em tarefas atomicas
// (JSON array) para leitura/execucao pelos Scripters (E4B) e Exec (E4B).
// Nao ha endpoint de decomposicao: a segmentacao ocorre via system prompt do Orch.
internal static class E2EOrchTests
{
    public static async Task Segment()
    {
        string model = TestContext.SuiteModel;
        if (!model.Contains("orch")) model = model.Replace("-build", "-orch").Replace("-plan", "-orch");
        if (!model.Contains("orch")) model += "-orch";
        Console.WriteLine($"[orch:segment] modelo {model} (bridge {TestContext.Bridge})");

        var history = new JsonArray(
            new JsonObject { ["role"]="system", ["content"]="Voce e o Orch (role orch, E4B). Segmenta o enunciado do usuario em tarefas atomicas (max 5) e responde SOMENTE com um JSON array: [{\"title\":\"...\",\"prompt\":\"...\"}]. Nao execute nada, apenas segmente." },
            new JsonObject { ["role"]="user", ["content"]="Dominio backend C# de reembolsos corporativos: entidades, regras de aprovacao por faixa de valor, banco SQL (DDL, indices, constraints), repositorios Dapper e servicos de aplicacao. Divida em tarefas por escopo." });

        var r = await TestContext.PostObj(TestContext.Bridge + "/v1/chat/completions",
            new JsonObject { ["model"]=model, ["temperature"]=0.3, ["stream"]=false, ["messages"]=history.DeepClone() });
        string content = r["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
        Console.WriteLine($"  [orch] resposta: {content[..Math.Min(300, content.Length)]}");

        // extrai JSON array
        int start = content.IndexOf('[');
        int end = content.LastIndexOf(']');
        TestContext.Check(start >= 0 && end > start, "Orch respondeu com JSON array de tarefas");
        string json = content[start..(end + 1)];
        var arr = JsonNode.Parse(json)?.AsArray() ?? throw new Exception("JSON invalido");
        TestContext.Check(arr.Count >= 2 && arr.Count <= 8, $"Orch segmentou em {arr.Count} tarefas (2-8)");
        foreach (var t in arr)
        {
            TestContext.Check(t?["title"] is not null && t?["prompt"] is not null, "tarefa com title+prompt");
            Console.WriteLine($"  [orch] tarefa: {t?["title"]}");
        }
        Console.WriteLine($"  [orch:segment] OK ({arr.Count} tarefas)");
    }
}