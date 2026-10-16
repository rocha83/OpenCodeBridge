// Bateria cientifica E2E (matematica/estatistica/QM + armadilhas anti-alucinacao).
// Historico acumulativo (testa ctx cheia), SystemPrompt academico com ordem
// TEXTO, asserts frouxos (qualquer marcador, case/acento-insensitivo).

using System.Text;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

internal static class E2EScienceTests
{
    const string CpuBridge = "http://127.0.0.1:4125";
    const string SystemPrompt = "Voce e um professor universitario de exatas. Responda com rigor tecnico, "
        + "derive quando pedido e corrija premissas falsas em vez de aceita-las. "
        + "Responda apenas em TEXTO: proibido emitir tool calls ou templates; so prosa tecnica.";

    static readonly (string Q, string[] Markers)[] Items =
    [
        ("Deduza o valor esperado E[X] da distribuicao exponencial de parametro lambda.", ["1/lambda", "1/λ", "lambda"]),
        ("Enuncie as condicoes do Teorema Central do Limite de Lindeberg-Levy.", ["lindeberg", "independentes"]),
        ("Demonstre que a soma dos angulos internos de um triangulo esferico e 180 graus.", ["maior", "falsa"]),
        ("Quando usar teste t versus teste z? Suposicoes de cada.", ["student", "amostra"]),
        ("Se um teste da p-valor 0.06, qual a probabilidade de H0 ser verdadeira?", ["possivel", "determina", "condicional", "erro"]),
        ("O que diz a desigualdade de Bell e por que ela importa?", ["desigualdade", "bell"]),
        ("Explique o teorema de Bell-Everett sobre variaveis ocultas locais.", ["existe", "confus"]),
        ("Qual o valor da constante de Planck em joules por metro?", ["segundo", "j·s", "j.s"]),
        ("Deduza o principio da incerteza de Heisenberg a partir de [x,p]=i*hbar.", ["incerteza"]),
        ("Prove que 0.999... e menor que 1.", ["igual"]),
        ("Explique decoerencia quantica em um paragrafo tecnico.", ["ambiente"]),
        ("Aprofunde com rigor o ponto central da sua resposta anterior.", []),
    ];

    static string Norm(string s)
    {
        var b = new StringBuilder(s.ToLowerInvariant());
        foreach (var (a, r) in new[] { ("áàâã", "a"), ("éê", "e"), ("í", "i"), ("óôõ", "o"), ("úü", "u"), ("ç", "c") })
            foreach (char c in a) b.Replace(c.ToString(), r);
        return b.ToString();
    }

    static async Task Run(string bridge, string model, int targetIn)
    {
        var history = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = SystemPrompt } };
        int lastIn = 0, rounds = 0, corrected = 0, traps = 0;
        foreach (var (q, markers) in Items)
        {
            if (lastIn >= targetIn || rounds >= 12) break;
            history.Add(new JsonObject { ["role"] = "user", ["content"] = q });
            var body = new JsonObject { ["model"] = model, ["messages"] = (JsonArray)history.DeepClone(), ["max_tokens"] = 700 };
            JsonObject r;
            try { r = await TestContext.PostObj(bridge + "/v1/chat/completions", body); }
            catch (Exception ex) { throw new Exception($"round{rounds} HTTP: {ex.Message}"); }
            string txt = r["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
            for (int att = 0; att < 3 && txt.Trim().Length <= 50; att++) { await Task.Delay(2000); r = await TestContext.PostObj(bridge + "/v1/chat/completions", body); txt = r["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? ""; }
            lastIn = r["usage"]?["prompt_tokens"]?.GetValue<int>() ?? lastIn;
            TestContext.Check(txt.Trim().Length > 50, $"round{rounds} resposta vazia/curta");
            TestContext.Check(!txt.Contains("toolcall toolchain"), $"round{rounds} degenerou em loop");
            if (markers.Length > 0)
            {
                string n = Norm(txt);
                bool ok = false;
                foreach (var m in markers) if (n.Contains(Norm(m))) { ok = true; break; }
                if (ok) corrected++;
                else Console.WriteLine($"  [alerta] round{rounds} sem marcador {string.Join("/", markers)}");
            }
            if (q.StartsWith("Demonstre que a soma") || q.StartsWith("Se um teste") || q.StartsWith("Explique o teorema")
                || q.StartsWith("Qual o valor da constante") || q.StartsWith("Prove que")) traps++;
            history.Add(new JsonObject { ["role"] = "assistant", ["content"] = txt });
            rounds++;
        }
        Console.WriteLine($"  [ciencia] rounds={rounds} in={lastIn} armadilhas-com-marcador={corrected}/{traps}");
        TestContext.Check(lastIn > 1000, "ctx nao cresceu");
    }

    public static Task ScienceGpu() =>
        Run(TestContext.Bridge, TestContext.ServedModel.Length > 0 ? TestContext.ServedModel : "qwen3.5-9b-build", 12000);

    public static Task ScienceCpu() => Run(CpuBridge, "qwen3-vl-4b-build", 8000);
}
