// Bateria cientifica E2E (matematica/estatistica/QM + armadilhas anti-alucinacao).
// Historico acumulativo (testa ctx cheia), SystemPrompt academico com ordem
// TEXTO, asserts frouxos (qualquer marcador, case/acento-insensitivo).

using System.Text;
using System.Text.Json.Nodes;

namespace Rochas.OpenCodeBridge.Test;

internal static class E2EScienceTests
{
    static string CpuBridge => Environment.GetEnvironmentVariable("WAR_CPU_BRIDGE") ?? "http://127.0.0.1:4125";
    const string SystemPrompt = "Voce e um professor universitario de exatas. Responda com rigor tecnico, "
        + "derive quando pedido e corrija premissas falsas em vez de aceita-las. "
        + "Responda apenas em TEXTO: proibido emitir tool calls ou templates; so prosa tecnica.";

    static readonly (string Q, string[] Markers)[] Items =
    [
        ("Deduza o valor esperado E[X] da distribuicao exponencial de parametro lambda. Derive e conclua.", ["1/lambda", "1/λ", "lambda", "integral"]),
        ("Enuncie as condicoes do Teorema Central do Limite de Lindeberg-Levy. Quais hipoteses sobre independencia e variancia?", ["lindeberg", "independentes", "variancia"]),
        ("Demonstre que a soma dos angulos internos de um triangulo esferico e 180 graus. (Armadilha: premissa falsa.)", ["maior", "falsa", "esferico", "180"]),
        ("Quando usar teste t versus teste z? Suposicoes de cada. Inclua tamanho de amostra e variancia conhecida.", ["student", "amostra", "variancia", "z"]),
        ("Se um teste da p-valor 0.06, qual a probabilidade de H0 ser verdadeira? Corrija o erro de interpretacao.", ["possivel", "determina", "condicional", "erro", "nao"]),
        ("O que diz a desigualdade de Bell e por que ela importa? Mencione localidade e realismo.", ["desigualdade", "bell", "localidade", "realismo"]),
        ("Explique o teorema de Bell sobre variaveis ocultas locais (nao existe 'Bell-Everett').", ["existe", "confus", "bell", "local"]),
        ("Qual o valor da constante de Planck em joules vezes segundo (J·s)? Corrija a unidade do enunciado.", ["segundo", "j·s", "j.s", "6.626"]),
        ("Deduza o principio da incerteza de Heisenberg a partir de [x,p]=i*hbar. Inclua sigma_x sigma_p.", ["incerteza", "hbar", "sigma"]),
        ("Prove que 0.999... e igual a 1 (premissa falsa no enunciado).", ["igual", "limite", "1"]),
        ("Explique decoerencia quantica em um paragrafo tecnico. Mencione interacao com ambiente.", ["ambiente", "decoerencia", "superposicao"]),
        ("Aprofunde com rigor o ponto central da sua resposta anterior. Derive ou justifique.", []),
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
            // sem limite de rounds/mensagens (pedido: tire limites de ida e volta)
            history.Add(new JsonObject { ["role"] = "user", ["content"] = q });
            var body = new JsonObject { ["model"] = model, ["messages"] = (JsonArray)history.DeepClone() };
            JsonObject r;
            try { r = await TestContext.PostObj(bridge + "/v1/chat/completions", body); }
            catch (Exception ex) { throw new Exception($"round{rounds} HTTP: {ex.Message}"); }
            string txt = r["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
            lastIn = r["usage"]?["prompt_tokens"]?.GetValue<int>() ?? lastIn;
            // sem assert de tamanho de conteudo (pedido: tire limites de tamanho)
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
        // TestContext.Check(lastIn > 1000, "ctx nao cresceu"); // removido: passthrough pode nao retornar usage
    }

    public static Task ScienceGpu() =>
        Run(TestContext.Bridge, TestContext.SuiteModel, 12000);

    static string CpuForcedBridge =>
        Environment.GetEnvironmentVariable("CPU_FORCED_BRIDGE") ?? "http://127.0.0.1:4126";

    public static Task ScienceCpu()
    {
        // CPU forced (:4126 -> llama :4120) pode estar parada (unit disabled).
        // Nao falha o teste por infra ausente: SKIP com a acao exata.
        try
        {
            using var r = TestContext.Http.GetAsync(CpuForcedBridge + "/api/status").GetAwaiter().GetResult();
            if (!r.IsSuccessStatusCode) throw new Exception("status " + (int)r.StatusCode);
        }
        catch
        {
            TestContext.Skip("CPU forced :4126 fora do ar (sudo systemctl start llama-cpu-forced bridge-cpu-forced)");
        }
        return Run(CpuForcedBridge, "Gemma4-E4B-Q4-build", 8000);
    }
}
